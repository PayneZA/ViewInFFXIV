using System.Net.Http;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace ViewInFFXIV.Media;

/// <summary>YouTube thumbnails (320x180), downloaded in the background and kept while they are on screen.</summary>
public sealed class ThumbnailCache : IDisposable
{
    private const int MaxEntries = 150;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly ITextureProvider textures;
    private readonly IPluginLog log;
    private readonly Dictionary<string, Entry> entries = new();
    private readonly object sync = new();
    private bool disposed;

    public ThumbnailCache(ITextureProvider textures, IPluginLog log)
    {
        this.textures = textures;
        this.log = log;
    }

    /// <summary>The thumbnail, or null while it loads (or if it failed). Starts the download on first use.</summary>
    public IDalamudTextureWrap? Get(string videoId)
    {
        if (videoId.Length == 0)
            return null;

        lock (sync)
        {
            if (disposed)
                return null;

            if (!entries.TryGetValue(videoId, out var entry))
            {
                Evict();
                entry = new Entry();
                entries[videoId] = entry;
                _ = LoadAsync(videoId, entry);
            }

            entry.LastUsed = Environment.TickCount64;
            return entry.Wrap;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            foreach (var entry in entries.Values)
                entry.Wrap?.Dispose();
            entries.Clear();
        }
    }

    private async Task LoadAsync(string videoId, Entry entry)
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync($"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg").ConfigureAwait(false);
            var wrap = await textures.CreateFromImageAsync(bytes, $"ViewInFFXIV thumbnail {videoId}").ConfigureAwait(false);
            lock (sync)
            {
                if (disposed || !entries.TryGetValue(videoId, out var current) || current != entry)
                {
                    wrap.Dispose();
                    return;
                }

                entry.Wrap = wrap;
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Thumbnail {Id} failed", videoId);
        }
    }

    /// <summary>Drops the least recently drawn thumbnails once the cache is full.</summary>
    private void Evict()
    {
        if (entries.Count < MaxEntries)
            return;

        foreach (var (id, entry) in entries.OrderBy(e => e.Value.LastUsed).Take(entries.Count - MaxEntries + 20).ToList())
        {
            entry.Wrap?.Dispose();
            entries.Remove(id);
        }
    }

    private sealed class Entry
    {
        public IDalamudTextureWrap? Wrap { get; set; }

        public long LastUsed { get; set; }
    }
}
