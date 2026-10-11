using System.Text.RegularExpressions;

namespace ViewInFFXIV.Shared;

/// <summary>Twitch channel names, typed or pasted as a link.</summary>
public static partial class TwitchChannel
{
    /// <summary>
    /// The channel login in "monstercat", "twitch.tv/monstercat", "https://www.twitch.tv/monstercat/videos" or
    /// "player.twitch.tv/?channel=monstercat" (lower case), or null when the text is none of these.
    /// </summary>
    public static string? Parse(string? text)
    {
        text = text?.Trim() ?? "";
        var match = PlayerLinkRegex().Match(text);
        if (!match.Success)
            match = ChannelLinkRegex().Match(text);
        if (!match.Success)
            match = BareRegex().Match(text);
        if (!match.Success)
            return null;

        var login = match.Groups["login"].Value.ToLowerInvariant();
        // Twitch pages that are not channels.
        return login is "directory" or "videos" or "search" or "settings" or "downloads" or "p" or "jobs" or "turbo" or "subscriptions" or "inventory" or "drops" or "wallet"
            ? null
            : login;
    }

    /// <summary>The text names a Twitch link (as opposed to a bare word, which could be a YouTube search).</summary>
    public static bool IsLink(string? text) =>
        text != null && (PlayerLinkRegex().IsMatch(text) || ChannelLinkRegex().IsMatch(text));

    [GeneratedRegex(@"player\.twitch\.tv/\?(?:\S*&)?channel=(?<login>[A-Za-z0-9_]{2,25})", RegexOptions.IgnoreCase)]
    private static partial Regex PlayerLinkRegex();

    [GeneratedRegex(@"(?:^|[\s/])(?:www\.|m\.)?twitch\.tv/(?<login>[A-Za-z0-9_]{2,25})(?=$|[/?#\s])", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelLinkRegex();

    [GeneratedRegex(@"^@?(?<login>[A-Za-z0-9_]{2,25})$")]
    private static partial Regex BareRegex();
}
