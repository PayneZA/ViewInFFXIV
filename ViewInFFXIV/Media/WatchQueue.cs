using ViewInFFXIV.Shared;

namespace ViewInFFXIV.Media;

/// <summary>
/// Your "Up next" list and the recently watched history. When the room's video ends, the next one in your list
/// starts for everyone. Both are remembered between sessions.
/// </summary>
public sealed class WatchQueue
{
    private const int MaxHistory = 40;

    private readonly Plugin plugin;
    private string lastVideoId = "";
    private string advancedFrom = "";

    // The video you started yourself. When a video ends, only the person who started it plays the next one,
    // so two friends with their own Up next lists don't both start something at once.
    private string startedByMe = "";

    public WatchQueue(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public List<VideoInfo> UpNext => plugin.Configuration.UpNext;

    public List<VideoInfo> History => plugin.Configuration.History;

    /// <summary>Plays the video for everyone, unless YouTube does not let it play in a watch party.</summary>
    public void PlayNow(VideoInfo video) => Start(video, fromUpNext: false);

    private void Start(VideoInfo video, bool fromUpNext)
    {
        var check = plugin.Playability.Check(video.Id);
        if (check.IsCompleted)
            Continue(video, check.Result, fromUpNext);
        else
            check.ContinueWith(t => Plugin.Framework.RunOnFrameworkThread(() => Continue(video, t.Result, fromUpNext)));
    }

    private void Continue(VideoInfo video, Playability.Result result, bool fromUpNext)
    {
        if (result.State is Playability.State.Blocked or Playability.State.Gone)
        {
            // Sending it would put w2g's error page on everyone's wall and stop what they are watching.
            var name = video.Title.Length > 0 ? $"\"{video.Title}\"" : "this video";
            if (fromUpNext)
            {
                Notify.Warning($"Skipped {name}", Playability.Reason(result.State));
                Next();
            }
            else
            {
                Notify.Warning(video.Title.Length > 0 ? video.Title : "This video", Playability.Reason(result.State));
            }

            return;
        }

        // A pasted link has no details yet; the check brought the title and channel.
        if (video.Title.Length == 0 && result.Title.Length > 0)
            video = new VideoInfo { Id = video.Id, Title = result.Title, Channel = result.Channel };

        startedByMe = video.Id;
        plugin.HighPerformance.Play(video.Url);
        Remember(video);
    }

    public void Add(VideoInfo video)
    {
        UpNext.RemoveAll(v => v.Id == video.Id);
        UpNext.Add(video);
        plugin.Configuration.Save();
    }

    public bool Contains(string videoId) => UpNext.Exists(v => v.Id == videoId);

    /// <summary>Plays the first video in Up next. Returns false when the list is empty.</summary>
    public bool Next()
    {
        if (UpNext.Count == 0)
            return false;

        var next = UpNext[0];
        UpNext.RemoveAt(0);
        plugin.Configuration.Save();
        Start(next, fromUpNext: true);
        return true;
    }

    public void Remove(int index)
    {
        if (index < 0 || index >= UpNext.Count)
            return;
        UpNext.RemoveAt(index);
        plugin.Configuration.Save();
    }

    public void Move(int index, int delta)
    {
        var target = index + delta;
        if (index < 0 || index >= UpNext.Count || target < 0 || target >= UpNext.Count)
            return;
        (UpNext[index], UpNext[target]) = (UpNext[target], UpNext[index]);
        plugin.Configuration.Save();
    }

    public void Clear()
    {
        UpNext.Clear();
        plugin.Configuration.Save();
    }

    /// <summary>Every framework tick: records what the room plays and moves on when a video ends.</summary>
    public void Tick(PlaybackStatus? playback)
    {
        if (playback == null || playback.VideoId.Length == 0)
            return;

        // Anyone in the room can start a video; remember it once its title is known.
        if (playback.VideoId != lastVideoId && playback.Title.Length > 0)
        {
            lastVideoId = playback.VideoId;
            Remember(new VideoInfo
            {
                Id = playback.VideoId,
                Title = playback.Title,
                Channel = playback.Author,
                Duration = playback.Duration > 0 ? Clock(playback.Duration) : "",
            });
        }

        // The channel name can arrive a moment after the title.
        if (playback.Author.Length > 0 && History.Count > 0 && History[0].Id == playback.VideoId && History[0].Channel.Length == 0)
        {
            History[0].Channel = playback.Author;
            plugin.Configuration.Save();
        }

        if (playback.Ended && playback.VideoId == startedByMe && playback.VideoId != advancedFrom
            && plugin.Configuration.AutoAdvance && UpNext.Count > 0)
        {
            advancedFrom = playback.VideoId;
            Next();
        }
    }

    private void Remember(VideoInfo video)
    {
        var existing = History.Find(v => v.Id == video.Id);
        History.RemoveAll(v => v.Id == video.Id);

        // Keep the richer details from search results when the player only knows the title.
        if (existing != null)
        {
            video = new VideoInfo
            {
                Id = video.Id,
                Title = video.Title.Length > 0 ? video.Title : existing.Title,
                Channel = video.Channel.Length > 0 ? video.Channel : existing.Channel,
                Duration = video.Duration.Length > 0 ? video.Duration : existing.Duration,
                Views = video.Views.Length > 0 ? video.Views : existing.Views,
                Age = video.Age.Length > 0 ? video.Age : existing.Age,
            };
        }

        History.Insert(0, video);
        if (History.Count > MaxHistory)
            History.RemoveRange(MaxHistory, History.Count - MaxHistory);
        plugin.Configuration.Save();
    }

    public static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
