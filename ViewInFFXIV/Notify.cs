using Dalamud.Interface.ImGuiNotification;

namespace ViewInFFXIV;

/// <summary>
/// Short notices (party ready, errors) show as Dalamud notifications in the corner. Information worth reading at
/// your own pace, like what an invite contains and how friends use it, goes to the chat box as an echo
/// (<see cref="Chat"/>), where it stays. A notice's title is the whole notification; its explanation, if it has
/// one, goes to chat, because notifications cut longer text off. The same notice within a few seconds shows once.
/// </summary>
internal static class Notify
{
    private static readonly TimeSpan Repeat = TimeSpan.FromSeconds(8);
    private static readonly Dictionary<string, DateTime> Recent = new();

    public static void Success(string title, string? text = null) => Show(NotificationType.Success, title, text);

    public static void Info(string title, string? text = null) => Show(NotificationType.Info, title, text);

    public static void Warning(string title, string? text = null) => Show(NotificationType.Warning, title, text);

    public static void Error(string title, string? text = null) => Show(NotificationType.Error, title, text);

    /// <summary>Prints to your chat box as an Echo line (only you see it).</summary>
    public static void Chat(string text) =>
        Plugin.ChatGui.Print(new Dalamud.Game.Text.XivChatEntry
        {
            Type = Dalamud.Game.Text.XivChatType.Echo,
            Message = $"[ViewInFFXIV] {text}",
        });

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
            Content = title,
            InitialDuration = type == NotificationType.Error ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(4),
        });
        if (text != null)
            Chat($"{title}. {text}");
    }
}
