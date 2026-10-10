using Dalamud.Interface.ImGuiNotification;

namespace ViewInFFXIV;

/// <summary>
/// Things that happen (party ready, invite copied, errors) show as Dalamud notifications in the corner, not in chat.
/// Chat is kept for replies to commands you typed. The same message within a few seconds shows once.
/// </summary>
internal static class Notify
{
    private static readonly TimeSpan Repeat = TimeSpan.FromSeconds(8);
    private static readonly Dictionary<string, DateTime> Recent = new();

    public static void Success(string title, string? text = null) => Show(NotificationType.Success, title, text);

    public static void Info(string title, string? text = null) => Show(NotificationType.Info, title, text);

    public static void Warning(string title, string? text = null) => Show(NotificationType.Warning, title, text);

    public static void Error(string title, string? text = null) => Show(NotificationType.Error, title, text);

    private static void Show(NotificationType type, string title, string? text)
    {
        var now = DateTime.UtcNow;
        var key = $"{type}|{title}|{text}";
        lock (Recent)
        {
            if (Recent.TryGetValue(key, out var shown) && now - shown < Repeat)
                return;
            if (Recent.Count > 64)
                Recent.Clear();
            Recent[key] = now;
        }

        Plugin.Notifications.AddNotification(new Notification
        {
            Type = type,
            Title = text == null ? null : title,
            Content = text ?? title,
            InitialDuration = type == NotificationType.Error ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(4),
        });
    }
}
