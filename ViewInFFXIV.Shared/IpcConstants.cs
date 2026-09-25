namespace ViewInFFXIV.Shared;

public static class IpcConstants
{
    /// <summary>Size of the shared video texture (and of the video area the helper fits Brave to).</summary>
    public const int MaxWidth = 1920;
    public const int MaxHeight = 1080;

    public static string PipeName => $"ViewInFFXIV.Ipc.{Instance}";

    public static string MutexName => $"ViewInFFXIV.Host.{Instance}";

    /// <summary>Prefix of the named D3D11 shared texture; the helper appends its process id and a counter.</summary>
    public static string SharedTexturePrefix => $"ViewInFFXIV.Video.{Instance}";

    /// <summary>
    /// Per-user names. VIEWINFFXIV_INSTANCE lets a developer run a second, separate helper next to the game's.
    /// </summary>
    private static string Instance =>
        Sanitize(Environment.UserName) + (Environment.GetEnvironmentVariable("VIEWINFFXIV_INSTANCE") is { Length: > 0 } dev ? "." + Sanitize(dev) : "");

    private static string Sanitize(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');
        return value.Replace(' ', '_');
    }
}
