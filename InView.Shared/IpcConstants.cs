namespace InView.Shared;

public static class IpcConstants
{
    public const string DefaultUrl = "https://watchtogether.watch/";
    public const int CaptureWidth = 1280;
    public const int CaptureHeight = 720;
    public const int MaxWidth = 1920;
    public const int MaxHeight = 1080;
    public const int CaptureIntervalMs = 33;
    public const int MaxNativeWidth = 7680;
    public const int MaxNativeHeight = 2160;
    public const uint FrameMagic = 0x564E5749; // 'IWNV'

    public static string PipeName => $"InView.Ipc.{Sanitize(Environment.UserName)}";

    public static string FrameMapName => $"InView.Frames.{Sanitize(Environment.UserName)}";

    public static string MutexName => $"InView.Host.{Sanitize(Environment.UserName)}";

    private static string Sanitize(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');
        return value.Replace(' ', '_');
    }
}
