using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using ViewInFFXIV.Host;
using ViewInFFXIV.Media;
using ViewInFFXIV.Shared;

namespace ViewInFFXIV.Windows;

/// <summary>
/// The ViewInFFXIV window. The Watch tab follows where you are: get Brave → start or join a party → watch
/// (live picture, controls, search, Up next). Screen and Settings hold everything else.
/// </summary>
public sealed partial class MainWindow : Window, IDisposable
{
    public enum Tab
    {
        Watch,
        Screen,
        Settings,
    }

    private enum ListView
    {
        Results,
        UpNext,
        Recent,
        Favourites,
    }

    private const int WatchAgainCount = 5;

    private readonly Plugin plugin;
    private readonly ScreenTab screenTab;
    private readonly IFontHandle titleFont;
    private Tab? selectTab;
    private ListView listView = ListView.Recent;

    private string joinInput = "";
    private string searchInput = "";
    private volatile List<VideoInfo>? results;
    private volatile string? searchError;
    private volatile bool searching;
    private string lastQuery = "";
    private int searchCount;
    private CancellationTokenSource? searchCts;

    private readonly PlayerControls controls;

    private BrowserInfo? browser;
    private DateTime browserCheckedUtc = DateTime.MinValue;
    private string? clipboardInvite;
    private DateTime clipboardCheckedUtc;

    public MainWindow(Plugin plugin)
        : base("ViewInFFXIV##ViewInFFXIVMain")
    {
        this.plugin = plugin;
        screenTab = new ScreenTab(plugin);
        controls = new PlayerControls(plugin);
        titleFont = Plugin.PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.Axis18));
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(460, 520),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = new Vector2(600, 760);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    private HighPerformance Party => plugin.HighPerformance;

    private WatchQueue Queue => plugin.Queue;

    public void Open(Tab tab)
    {
        IsOpen = true;
        selectTab = tab;
    }

    /// <summary>Opens the Watch tab and searches YouTube (or plays a pasted link).</summary>
    public void Search(string text)
    {
        Open(Tab.Watch);
        searchInput = text;
        Submit(text.Trim());
    }

    public void Dispose()
    {
        searchCts?.Cancel();
        titleFont.Dispose();
    }

    public override void PreDraw() => Ui.PushStyle();

    public override void PostDraw() => Ui.PopStyle();

    public override void Draw()
    {
        DrawHeader();

        using var bar = Dalamud.Interface.Utility.Raii.ImRaii.TabBar("##tabs");
        if (!bar.Success)
            return;

        if (TabItem(" Watch ", Tab.Watch))
        {
            DrawWatch();
            ImGui.EndTabItem();
        }

        if (TabItem(" Screen ", Tab.Screen))
        {
            screenTab.Draw();
            ImGui.EndTabItem();
        }

        if (TabItem(" Settings ", Tab.Settings))
        {
            DrawSettings();
            ImGui.EndTabItem();
        }

        selectTab = null;
    }

    private bool TabItem(string label, Tab tab)
    {
        var flags = selectTab == tab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        return ImGui.BeginTabItem(label, flags);
    }

    // ───────────────────────────── header ─────────────────────────────

    private void DrawHeader()
    {
        var status = Party.Status;
        var playback = Party.Playback;
        var helper = plugin.Host.HelperAlive;

        var twitch = Twitch.Active;
        if (twitch)
            Ui.Pill(Twitch.Starting ? "◌ Twitch…" : Twitch.Status.Playing ? "● TWITCH" : "● Twitch · offline", Twitch.Status.Playing ? Ui.Live : Ui.TextDim, filled: Twitch.Status.Playing);
        else if (Party.Connecting)
            Ui.Pill($"◌ {Party.ConnectingActivity}…", Ui.Warn);
        else if (!helper || status.State == "off")
            Ui.Pill("○ Off", Ui.TextDim);
        else if (status.State is "starting" or "busy" && status.Activity.Length > 0)
            Ui.Pill($"◌ {status.Activity}…", Ui.Warn);
        else if (playback is { Paused: false })
            Ui.Pill("● LIVE", Ui.Live, filled: true);
        else if (status.RoomUrl.Length > 0)
            Ui.Pill("● In a party", Ui.Good);
        else if (status.State == "error")
            Ui.Pill("● Needs attention", Ui.Live);
        else
            Ui.Pill("● Ready", Ui.Good);

        var room = status.RoomUrl.Length > 0;
        if (!room && !twitch)
        {
            ImGui.Spacing();
            return;
        }

        const string inviteLabel = "Invite friends";
        var inviteWidth = ImGui.CalcTextSize(inviteLabel).X + 20;
        if (room)
        {
            ImGui.SameLine();
            if (ImGuiComponents.IconButton("##room", FontAwesomeIcon.Users))
                ImGui.OpenPopup("room");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Room");
        }

        // The mini remote's switch, lit while it's shown.
        ImGui.SameLine();
        var remoteShown = plugin.Configuration.RemoteShown;
        if (remoteShown)
            ImGui.PushStyleColor(ImGuiCol.Text, Ui.Accent);
        if (ImGuiComponents.IconButton("##remote", FontAwesomeIcon.TabletAlt))
            plugin.ToggleRemote();
        if (remoteShown)
            ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(remoteShown ? "Hide the mini remote" : "Show the mini remote: small controls you can drag anywhere");

        if (!room)
        {
            ImGui.Spacing();
            return;
        }

        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, ImGui.GetContentRegionAvail().X - inviteWidth));
        if (Ui.AccentButton(inviteLabel))
            Party.CopyInvite();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copies the room link and your screen spot.\nFriends paste it into the Join box (or /viewin join).");

        DrawRoomPopup(status);
        ImGui.Spacing();
    }

    private void DrawRoomPopup(ManagedBrowserStatus status)
    {
        using var popup = Dalamud.Interface.Utility.Raii.ImRaii.Popup("room");
        if (!popup.Success)
            return;

        ImGui.TextUnformatted("Your watch party");
        Ui.Dim(status.RoomUrl);
        Ui.Dim($"You appear as {Party.ResolveName() ?? "(log in to a character)"}.");
        ImGui.Separator();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.UserFriends, "Copy invite (link + screen spot)"))
            Party.CopyInvite();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Link, "Copy link only"))
            ImGui.SetClipboardText(status.RoomUrl);
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.ExternalLinkAlt, status.Shown ? "Hide the browser" : "Open the browser (w2g)"))
        {
            if (status.Shown)
                Party.Hide();
            else
                Party.Show();
        }

        ImGui.Separator();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.SignOutAlt, "Leave the party"))
        {
            Party.Stop();
            ImGui.CloseCurrentPopup();
        }
    }

    // ───────────────────────────── watch tab ─────────────────────────────

    private void DrawWatch()
    {
        DrawProblems();

        if (NoBrowser())
        {
            DrawGetBrowser();
            return;
        }

        if (Twitch.Active)
        {
            DrawTwitch();
            return;
        }

        var status = Party.Status;
        if (!Party.Active || status.RoomUrl.Length == 0)
        {
            if (Party.Connecting || (Party.Active && status.State is "starting" or "busy"))
            {
                DrawStarting();
            }
            else
            {
                DrawWelcome();
                DrawTwitchWelcome();
                DrawWatchAgain();
            }

            return;
        }

        DrawPlayer();
        DrawSearchBox();
        DrawLists();
    }

    private void DrawProblems()
    {
        var problems = new List<string>();
        if (!string.IsNullOrEmpty(Party.Status.Error))
            problems.Add(Party.Status.Error);
        if (!string.IsNullOrEmpty(plugin.Host.Status.Error))
            problems.Add(plugin.Host.Status.Error!);
        if (plugin.Host.Status.SharedTexture.Length > 0 && plugin.Video.Error is { } videoError)
            problems.Add(videoError);
        if (!string.IsNullOrEmpty(Twitch.Status.Error))
            problems.Add(Twitch.Status.Error);
        if (Party.Playback is { PlayerError.Length: > 0 } broken)
            problems.Add($"YouTube says: {broken.PlayerError}");
        if (problems.Count == 0)
            return;

        Ui.Card("problems", _ =>
        {
            foreach (var problem in problems.Distinct())
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Ui.Warn);
                ImGui.TextWrapped(problem);
                ImGui.PopStyleColor();
            }
        }, color: Ui.Warn with { W = 0.12f });
    }

    /// <summary>The browser to use, re-checked every few seconds (someone may be installing one right now).</summary>
    private bool NoBrowser()
    {
        if (DateTime.UtcNow - browserCheckedUtc > TimeSpan.FromSeconds(5))
        {
            browser = Party.Browser;
            browserCheckedUtc = DateTime.UtcNow;
        }

        return browser == null;
    }

    private void DrawGetBrowser()
    {
        Ui.Card("browser-missing", width =>
        {
            HeroIcon(FontAwesomeIcon.Download, width);
            Title("One thing first: Brave", width);
            ImGui.TextWrapped(
                "ViewInFFXIV is made for the Brave browser. It runs its own hidden copy with its own profile (your normal " +
                "browser is never touched), and Brave's built-in ad blocking keeps ads off your wall.");
            Ui.Dim("Google Chrome and Microsoft Edge also work, best effort only: the experience and support aren't guaranteed.");
            if (Ui.AccentButton("Get Brave", new Vector2(width, 40)))
                Util.OpenLink(Browsers.BraveDownloadUrl);
            Ui.Dim("Install it, then come back here; this page notices on its own.");
        });
    }

    private void DrawWelcome()
    {
        Ui.Card("welcome", width =>
        {
            HeroIcon(FontAwesomeIcon.Tv, width);
            Title("Watch together, right on your wall", width);
            ImGui.PushStyleColor(ImGuiCol.Text, Ui.TextDim);
            ImGui.TextWrapped("Start a party and send the invite: everyone sees the same video, in sync, on a screen in your house.");
            ImGui.PopStyleColor();
            ImGui.Spacing();

            var rejoin = plugin.Configuration.LastRoomUrl;
            if (Party.Dropped && rejoin.Length > 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Ui.Warn);
                ImGui.TextWrapped("Your watch party stopped unexpectedly. Rejoin to get back in with your friends.");
                ImGui.PopStyleColor();
                if (Ui.AccentButton("Rejoin your last party", new Vector2(width, 44)))
                    Party.Join(rejoin);
                ImGui.Spacing();
            }

            if (Party.Dropped && rejoin.Length > 0 ? ImGui.Button("Start a new watch party", new Vector2(width, 36)) : Ui.AccentButton("Start a watch party", new Vector2(width, 44)))
                Party.StartParty();

            ImGui.Spacing();
            Ui.Centered("or join a friend's party", Ui.TextDim);

            var clipboardInvite = ClipboardInvite();

            ImGui.SetNextItemWidth(width - 80);
            var enter = ImGui.InputTextWithHint("##join", clipboardInvite != null ? "Invite found on your clipboard — press Join" : "Paste their invite here", ref joinInput, 1024, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            if (ImGui.Button("Join", new Vector2(72, 0)) || enter)
            {
                var text = joinInput.Length > 0 ? joinInput : clipboardInvite ?? "";
                if (Party.Join(text))
                    joinInput = "";
            }

            if (rejoin.Length > 0 && !Party.Dropped)
            {
                ImGui.Spacing();
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.History, "Rejoin your last party"))
                    Party.Join(rejoin);
            }

            ImGui.Spacing();
            Ui.Dim($"You'll appear as {Party.ResolveName() ?? "your character name"}.");
            if (browser is { BlocksAds: false } other)
            {
                Ui.Dim($"Using {other.Name} (best effort): ViewInFFXIV is made for Brave.");
                ImGui.SameLine();
                if (ImGui.SmallButton("Get Brave"))
                    Util.OpenLink(Browsers.BraveDownloadUrl);
            }
        });
    }

    /// <summary>Under the welcome card: what you watched lately. Play starts a party with it; + lines it up.</summary>
    private void DrawWatchAgain()
    {
        if (Queue.History.Count == 0)
            return;

        ImGui.TextUnformatted("Watch again");
        ImGui.SameLine();
        Ui.Dim("— play one to start a party with it");

        // A short pick of the latest few, not the whole history (that is the Recent list in a party).
        for (var i = 0; i < Math.Min(Queue.History.Count, WatchAgainCount); i++)
        {
            var video = Queue.History[i];
            VideoRow(video, i, () =>
            {
                if (ActionButton("play", FontAwesomeIcon.Play, "Start a party with this video\n(puts the screen up and copies the invite)"))
                    Party.StartParty(video);
                ImGui.SameLine();
                var queued = Queue.Contains(video.Id);
                if (ActionButton("add", queued ? FontAwesomeIcon.Check : FontAwesomeIcon.Plus, queued ? "In Up next" : "Add to Up next") && !queued)
                    Queue.Add(video);
                ImGui.SameLine();
                StarButton(video);
            }, 3, onDoubleClick: () => Party.StartParty(video));
        }
    }

    private void DrawStarting()
    {
        Ui.Card("starting", width =>
        {
            ImGui.Dummy(new Vector2(0, 12));
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (width / 2) - 18);
            Ui.Spinner(18);
            ImGui.Dummy(new Vector2(0, 4));
            Ui.Centered(Party.ConnectingActivity + "…");
            if (browser == Browsers.Brave)
                Ui.Centered("The very first start downloads Brave's ad-block lists (about 30 seconds).", Ui.TextDim);
            ImGui.Dummy(new Vector2(0, 12));
        });
    }

    private void DrawPlayer()
    {
        var playback = Party.Playback;
        var avail = ImGui.GetContentRegionAvail().X;
        var maxHeight = MathF.Max(160, ImGui.GetWindowHeight() * 0.42f);
        var size = new Vector2(avail, avail * 9f / 16f);
        if (size.Y > maxHeight)
            size = new Vector2(maxHeight * 16f / 9f, maxHeight);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ((avail - size.X) / 2));
        var picture = ImGui.GetCursorScreenPos();
        Ui.RoundedImage(plugin.Picture, size, 10f, FontAwesomeIcon.Tv);
        if (playback == null)
        {
            var dl = ImGui.GetWindowDrawList();
            const string hint = "Nothing playing yet — search below or paste a link";
            var textSize = ImGui.CalcTextSize(hint);
            dl.AddText(picture + new Vector2((size.X - textSize.X) / 2, (size.Y * 0.5f) + 26), Ui.Col(Ui.TextDim), hint);
            return;
        }

        using (titleFont.Push())
            ImGui.TextUnformatted(Ui.Fit(playback.Title, avail));

        var meta = new List<string>();
        if (playback.Author.Length > 0)
            meta.Add(playback.Author);
        if (playback.VideoHeight > 0)
            meta.Add($"{playback.VideoHeight}p");
        if (playback.Duration <= 0)
            meta.Add("live");
        Ui.Dim(string.Join("  ·  ", meta));

        controls.DrawSeek(playback, avail);
        controls.DrawTransport(playback);
    }

    // ───────────────────────────── search + lists ─────────────────────────────

    private void DrawSearchBox()
    {
        ImGui.Spacing();
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.SetNextItemWidth(width - 44 - 36);
        var enter = ImGui.InputTextWithHint("##search", "Search YouTube or paste a link…", ref searchInput, 512, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if (ImGuiComponents.IconButton("##go", FontAwesomeIcon.Search) || enter)
            Submit(searchInput.Trim());
        ImGui.SameLine();
        DrawTwitchPopupButton();
    }

    private void Submit(string text)
    {
        if (text.Length == 0)
            return;

        if (TwitchChannel.IsLink(text))
        {
            if (Twitch.Watch(text))
                searchInput = "";
            return;
        }

        if (YouTubeSearch.FindVideoId(text) is { } id)
        {
            Queue.PlayNow(new VideoInfo { Id = id });
            searchInput = "";
            return;
        }

        if (UrlRegex().Match(text) is { Success: true } url)
        {
            Party.Play(url.Value);
            searchInput = "";
            return;
        }

        searchCts?.Cancel();
        var cts = searchCts = new CancellationTokenSource();
        searchCount++;
        lastQuery = text;
        searching = true;
        searchError = null;
        listView = ListView.Results;
        _ = Task.Run(async () =>
        {
            try
            {
                var found = await YouTubeSearch.SearchAsync(text, cts.Token).ConfigureAwait(false);
                if (cts.IsCancellationRequested)
                    return;
                results = found;
                searchError = found.Count == 0 ? $"Nothing found for \"{text}\"." : null;
            }
            catch (Exception ex) when (!cts.IsCancellationRequested)
            {
                Plugin.Log.Warning(ex, "YouTube search failed");
                searchError = "YouTube search didn't answer. Paste a video link instead.";
                results = [];
            }
            finally
            {
                if (!cts.IsCancellationRequested)
                    searching = false;
            }
        });
    }

    private void DrawLists()
    {
        var hasResults = results != null || searching;
        if (!hasResults && listView == ListView.Results)
            listView = Queue.UpNext.Count > 0 ? ListView.UpNext : ListView.Recent;

        if (hasResults && Segment(searching ? "Searching…" : $"Results ({results?.Count ?? 0})", ListView.Results))
            listView = ListView.Results;
        if (hasResults)
            ImGui.SameLine();
        if (Segment($"Up next ({Queue.UpNext.Count})", ListView.UpNext))
            listView = ListView.UpNext;
        ImGui.SameLine();
        if (Segment("Recent", ListView.Recent))
            listView = ListView.Recent;
        ImGui.SameLine();
        if (Segment($"Favourites ({Queue.Favourites.Count})", ListView.Favourites))
            listView = ListView.Favourites;

        // Each list (and each new search) scrolls on its own, from the top.
        var height = MathF.Max(170, ImGui.GetContentRegionAvail().Y);
        var id = listView == ListView.Results ? $"##results{searchCount}" : $"##list{listView}";
        using var child = Dalamud.Interface.Utility.Raii.ImRaii.Child(id, new Vector2(0, height));
        if (!child.Success)
            return;

        switch (listView)
        {
            case ListView.Results:
                DrawResults();
                break;
            case ListView.UpNext:
                DrawUpNext();
                break;
            case ListView.Recent:
                DrawRecent();
                break;
            case ListView.Favourites:
                DrawFavourites();
                break;
        }
    }

    private bool Segment(string label, ListView view)
    {
        var selected = listView == view;
        ImGui.PushStyleColor(ImGuiCol.Button, selected ? Ui.Surface3 : Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Text, selected ? Ui.Text : Ui.TextDim);
        var pressed = ImGui.Button(label);
        ImGui.PopStyleColor(2);
        return pressed;
    }

    private void DrawResults()
    {
        if (searching)
        {
            ImGui.Dummy(new Vector2(0, 16));
            ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X / 2) - 12);
            Ui.Spinner(12);
            Ui.Centered($"Searching for \"{lastQuery}\"…", Ui.TextDim);
            return;
        }

        if (searchError != null)
            Ui.Dim(searchError);

        var list = results ?? [];
        for (var i = 0; i < list.Count; i++)
        {
            var video = list[i];
            VideoRow(video, i, () =>
            {
                if (ActionButton("play", FontAwesomeIcon.Play, "Play now for everyone"))
                    Queue.PlayNow(video);
                ImGui.SameLine();
                var queued = Queue.Contains(video.Id);
                if (ActionButton("add", queued ? FontAwesomeIcon.Check : FontAwesomeIcon.Plus, queued ? "In Up next" : "Add to Up next") && !queued)
                    Queue.Add(video);
                ImGui.SameLine();
                StarButton(video);
            }, 3, onDoubleClick: () => Queue.PlayNow(video));
        }
    }

    private void DrawUpNext()
    {
        var auto = plugin.Configuration.AutoAdvance;
        if (ImGui.Checkbox("Play the next one when a video ends", ref auto))
        {
            plugin.Configuration.AutoAdvance = auto;
            plugin.Configuration.Save();
        }

        if (Queue.UpNext.Count == 0)
        {
            Ui.Dim("Nothing queued. Use + on a search result to line videos up.");
            return;
        }

        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 50);
        if (ImGui.SmallButton("Clear"))
            Queue.Clear();

        for (var i = 0; i < Queue.UpNext.Count; i++)
        {
            var index = i;
            var video = Queue.UpNext[i];
            VideoRow(video, i, () =>
            {
                if (ActionButton("play", FontAwesomeIcon.Play, "Play now"))
                {
                    Queue.Remove(index);
                    Queue.PlayNow(video);
                }

                ImGui.SameLine();
                if (ActionButton("up", FontAwesomeIcon.ArrowUp, "Move up"))
                    Queue.Move(index, -1);
                ImGui.SameLine();
                if (ActionButton("down", FontAwesomeIcon.ArrowDown, "Move down"))
                    Queue.Move(index, 1);
                ImGui.SameLine();
                if (ActionButton("remove", FontAwesomeIcon.Times, "Remove"))
                    Queue.Remove(index);
            }, 4);
        }
    }

    private void DrawRecent()
    {
        if (Queue.History.Count == 0)
        {
            Ui.Dim("Videos you watch show up here.");
            return;
        }

        for (var i = 0; i < Queue.History.Count; i++)
        {
            var video = Queue.History[i];
            VideoRow(video, i, () =>
            {
                if (ActionButton("play", FontAwesomeIcon.Play, "Play again"))
                    Queue.PlayNow(video);
                ImGui.SameLine();
                var queued = Queue.Contains(video.Id);
                if (ActionButton("add", queued ? FontAwesomeIcon.Check : FontAwesomeIcon.Plus, queued ? "In Up next" : "Add to Up next") && !queued)
                    Queue.Add(video);
                ImGui.SameLine();
                StarButton(video);
            }, 3, onDoubleClick: () => Queue.PlayNow(video));
            if (i >= 24)
                break;
        }
    }

    private void DrawFavourites()
    {
        if (Queue.Favourites.Count == 0)
        {
            Ui.Dim("Star a video to keep it here.");
            return;
        }

        for (var i = 0; i < Queue.Favourites.Count; i++)
        {
            var video = Queue.Favourites[i];
            VideoRow(video, i, () =>
            {
                if (ActionButton("play", FontAwesomeIcon.Play, "Play now for everyone"))
                    Queue.PlayNow(video);
                ImGui.SameLine();
                var queued = Queue.Contains(video.Id);
                if (ActionButton("add", queued ? FontAwesomeIcon.Check : FontAwesomeIcon.Plus, queued ? "In Up next" : "Add to Up next") && !queued)
                    Queue.Add(video);
                ImGui.SameLine();
                StarButton(video);
            }, 3, onDoubleClick: () => Queue.PlayNow(video));
        }
    }

    /// <summary>Thumbnail, title, details, and action buttons on the right.</summary>
    private void VideoRow(VideoInfo video, int index, Action actions, int actionCount, Action? onDoubleClick = null)
    {
        const float thumbHeight = 56f;
        const float thumbWidth = thumbHeight * 16f / 9f;
        const float actionSize = 30f;
        ImGui.PushID($"{video.Id}{index}");

        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var actionsWidth = (actionCount * actionSize) + ((actionCount - 1) * ImGui.GetStyle().ItemSpacing.X) + 6;
        var hit = ImGui.InvisibleButton("##row", new Vector2(MathF.Max(1, width - actionsWidth), thumbHeight));
        var hovered = ImGui.IsItemHovered();
        if (hovered && onDoubleClick != null && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            onDoubleClick();
        _ = hit;

        var dl = ImGui.GetWindowDrawList();
        if (hovered)
            dl.AddRectFilled(start - new Vector2(4, 3), start + new Vector2(width, thumbHeight + 3), Ui.Col(Ui.Surface2), 8f);

        var thumb = plugin.Thumbnails.Get(video.Id);
        ImGui.SetCursorScreenPos(start);
        Ui.RoundedImage(thumb?.Handle, new Vector2(thumbWidth, thumbHeight), 6f, FontAwesomeIcon.Film);
        if (video.IsLive || video.Duration.Length > 0)
        {
            var label = video.IsLive ? "LIVE" : video.Duration;
            var size = ImGui.CalcTextSize(label);
            var corner = start + new Vector2(thumbWidth - size.X - 8, thumbHeight - size.Y - 4);
            dl.AddRectFilled(corner - new Vector2(3, 1), corner + size + new Vector2(3, 1), Ui.Col(video.IsLive ? Ui.Live : new Vector4(0, 0, 0, 0.75f)), 4f);
            dl.AddText(corner, Ui.Col(Vector4.One), label);
        }

        // Videos YouTube won't let play in a watch party are greyed out and say why, before anyone clicks them.
        var playable = plugin.Playability.Peek(video.Id);
        var blocked = playable is Playability.State.Blocked or Playability.State.Gone;
        if (blocked)
            dl.AddRectFilled(start, start + new Vector2(thumbWidth, thumbHeight), Ui.Col(new Vector4(0, 0, 0, 0.6f)), 6f);

        var textX = start.X + thumbWidth + 10;
        var textWidth = width - thumbWidth - 10 - actionsWidth;
        dl.AddText(new Vector2(textX, start.Y + 6), Ui.Col(blocked ? Ui.TextDim : Ui.Text), Ui.Fit(video.Title.Length > 0 ? video.Title : "YouTube video", textWidth));
        var meta = blocked
            ? playable == Playability.State.Blocked ? "Can't play in a watch party (owner disabled embedding)" : "Private or removed"
            : string.Join("  ·  ", new[] { video.Channel, video.Views, video.Age }.Where(s => s.Length > 0));
        dl.AddText(new Vector2(textX, start.Y + 6 + ImGui.GetTextLineHeightWithSpacing()), Ui.Col(blocked ? Ui.Warn : Ui.TextDim), Ui.Fit(meta, textWidth));
        if (blocked && hovered)
            ImGui.SetTooltip(Playability.Reason(playable));

        ImGui.SetCursorScreenPos(new Vector2(start.X + width - actionsWidth + 6, start.Y + ((thumbHeight - actionSize) / 2)));
        actions();

        ImGui.SetCursorScreenPos(start + new Vector2(0, thumbHeight + 8));
        ImGui.Dummy(Vector2.Zero);
        ImGui.PopID();
    }

    private static bool ActionButton(string id, FontAwesomeIcon icon, string tooltip, bool accent = false) =>
        Ui.RoundIconButton($"##{id}", icon, 30f, accent: accent, tooltip: tooltip);

    /// <summary>The star on a video row: filled in the accent colour when it's one of your favourites.</summary>
    private void StarButton(VideoInfo video)
    {
        var starred = Queue.IsFavourite(video.Id);
        if (ActionButton("star", FontAwesomeIcon.Star, starred ? "Remove from Favourites" : "Add to Favourites", accent: starred))
            Queue.ToggleFavourite(video);
    }

    // ───────────────────────────── settings tab ─────────────────────────────

    /// <summary>Settings that only apply to the screen on a wall.</summary>
    private static void DrawWallSettings(Configuration config, float width)
    {
        var behind = config.DrawBehindGameUi;
        if (ImGui.Checkbox("Keep chat bubbles, name plates and the HUD on top of the screen", ref behind))
        {
            config.DrawBehindGameUi = behind;
            config.Save();
        }

        if (config.DrawBehindGameUi)
        {
            ImGui.Indent();
            var hideWorldUi = config.HideWorldUiOnScreen;
            if (ImGui.Checkbox("Hide name plates and item markers over the screen", ref hideWorldUi))
            {
                config.HideWorldUiOnScreen = hideWorldUi;
                config.Save();
            }

            Ui.Dim("Chat bubbles and the HUD stay on top either way.");
            ImGui.Unindent();
        }

        ImGui.TextUnformatted("Covers furniture in front of the screen up to");
        var cover = config.ScreenCoverDepth;
        ImGui.SetNextItemWidth(width);
        if (ImGui.SliderFloat("##cover", ref cover, 0f, 1f, "%.2f yalms"))
            config.ScreenCoverDepth = cover;
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save();
        Ui.Dim("For a screen hung over furniture or set into a wall. People always pass in front of it.");
    }

    private void DrawSettings()
    {
        var config = plugin.Configuration;
        Ui.Card("playback", _ =>
        {
            ImGui.TextUnformatted("Playback");
            var subs = config.SubtitlesOn;
            if (ImGui.Checkbox("English subtitles on every video", ref subs))
            {
                config.SubtitlesOn = subs;
                config.Save();
                if (Party.Playback != null)
                    Party.Subtitles(subs);
            }

            var auto = config.AutoAdvance;
            if (ImGui.Checkbox("Play the next video in Up next when one ends", ref auto))
            {
                config.AutoAdvance = auto;
                config.Save();
            }

            Ui.Dim("Volume and subtitles are just for you; play, pause and seek are for everyone in the party.");

            var remote = config.RemoteShown;
            if (ImGui.Checkbox("Mini remote: a small control bar you can drag anywhere", ref remote))
                plugin.ToggleRemote();
            Ui.Dim("Only during a party; it fades while you're not using it. Also the remote button at the top, or /viewin remote.");
        });

        Ui.Card("sound", width =>
        {
            ImGui.TextUnformatted("Sound");
            var spatial = config.SpatialAudio;
            if (ImGui.Checkbox("Sound comes from the screen", ref spatial))
            {
                config.SpatialAudio = spatial;
                config.Save();
            }

            Ui.Dim(config.ScreenMode == ScreenMode.OnUi
                ? "The screen is on your UI, so the sound plays at your own volume."
                : "Quieter as you walk away, left/right with the camera, silent once you leave the screen's zone.");
            if (config.SpatialAudio)
            {
                var range = config.HearingRange;
                ImGui.SetNextItemWidth(width - 140);
                if (ImGui.SliderFloat("Hearing range", ref range, 5f, 60f, "%.0f yalms"))
                    config.HearingRange = range;
                if (ImGui.IsItemDeactivatedAfterEdit())
                    config.Save();
                if (Party.Playback != null)
                    Ui.Dim($"Right now: {plugin.Audio.Gain * 100:0}% of your volume.");
            }
        });

        Ui.Card("display", width =>
        {
            ImGui.TextUnformatted("Screen");
            if (config.ScreenMode == ScreenMode.InGame)
                DrawWallSettings(config, width);

            var keep = config.KeepScreenWhenUiHidden;
            if (ImGui.Checkbox("Keep the screen when the game UI is hidden (Scroll Lock, cutscenes, GPose)", ref keep))
            {
                config.KeepScreenWhenUiHidden = keep;
                config.Save();
                plugin.ApplyUiHidePolicy();
            }
        });

        Ui.Card("browser", width =>
        {
            ImGui.TextUnformatted("Browser");
            Ui.Dim("ViewInFFXIV runs its own hidden copy of the browser, with its own profile; the sound plays from it.");

            var options = new List<(string Id, string Label)> { (Browsers.Auto, $"Automatic ({Party.Browser?.Name ?? "none installed"})") };
            options.AddRange(Browsers.All.Where(b => Browsers.Find(b) != null).Select(b => (b.Id, b.Name + (b == Browsers.Brave ? " — recommended" : " — best effort"))));
            var index = Math.Max(0, options.FindIndex(o => o.Id == config.Browser));
            ImGui.SetNextItemWidth(width);
            if (ImGui.BeginCombo("##browser", options[index].Label))
            {
                foreach (var option in options)
                {
                    if (ImGui.Selectable(option.Label, option.Id == config.Browser))
                    {
                        config.Browser = option.Id;
                        config.Save();
                        browserCheckedUtc = DateTime.MinValue;
                    }
                }

                ImGui.EndCombo();
            }

            var status = Party.Status;
            var chosen = Party.Browser;
            if (Party.Active && chosen != null && status.Browser.Length > 0 && status.Browser != chosen.Name)
            {
                Ui.Dim($"Running {status.Browser} right now.");
                ImGui.SameLine();
                if (ImGui.SmallButton($"Switch to {chosen.Name}"))
                    Party.SwitchBrowser();
            }

            if (chosen != null && chosen != Browsers.Brave)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Ui.Warn);
                ImGui.TextWrapped($"ViewInFFXIV is made for Brave. {chosen.Name} works on a best-effort basis: the experience and support aren't guaranteed, and without Brave's ad blocking you will see ads.");
                ImGui.PopStyleColor();
            }

            Ui.Dim("Made for Brave; Chrome and Edge best effort. Firefox isn't supported.");

            if (Party.Active)
            {
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.ExternalLinkAlt, status.Shown ? "Hide the browser" : "Open the browser"))
                {
                    if (status.Shown)
                        Party.Hide();
                    else
                        Party.Show();
                }

                ImGui.SameLine();
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Stop, "Close the browser"))
                    Party.Stop();
            }
        });

        Ui.Card("diagnostics", _ =>
        {
            // A tree node, not a collapsing header: a header spans the whole window and pokes out of the card.
            if (!ImGui.TreeNodeEx("Diagnostics", ImGuiTreeNodeFlags.NoTreePushOnOpen))
                return;

            var host = plugin.Host.Status;
            var video = plugin.Video;
            ImGui.TextUnformatted($"Game {ImGui.GetIO().Framerate:0} fps · wall {video.Fps:0} fps · captured {host.ArrivedFps:0} fps · dropped {host.SkippedFps:0}/s");
            ImGui.TextUnformatted($"Renderer: {plugin.Renderer.Describe()} · capture GPU: {host.Capture}");
            if (Party.Playback is { } pb)
            {
                var dropped = pb.DecodedFrames > 0 ? 100f * pb.DroppedFrames / pb.DecodedFrames : 0f;
                ImGui.TextUnformatted($"Decode: {(pb.Codec.Length > 0 ? pb.Codec : "?")} · {pb.VideoHeight}p · dropped {pb.DroppedFrames} of {pb.DecodedFrames} ({dropped:0.#}%)");
            }

            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.ChartLine, "Run benchmark (25 s, results in chat)"))
                plugin.Benchmark.Start(plugin.Video.Device);
        });

        Ui.Card("about", _ =>
        {
            Ui.Dim($"ViewInFFXIV {Plugin.PluginInterface.Manifest.AssemblyVersion} · not affiliated with Square Enix, Watch2Gether, YouTube or the browser makers.");
        });
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private void HeroIcon(FontAwesomeIcon icon, float width)
    {
        var pos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(width, 64));
        var center = pos + new Vector2(width / 2, 32);
        var dl = ImGui.GetWindowDrawList();
        dl.AddCircleFilled(center, 30, Ui.Col(Ui.Accent with { W = 0.16f }), 40);
        Ui.DrawIcon(dl, icon, center, 26, Ui.Accent);
    }

    private void Title(string text, float width)
    {
        using (titleFont.Push())
        {
            var size = ImGui.CalcTextSize(text);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (width - size.X) / 2));
            ImGui.TextUnformatted(text);
        }
    }

    /// <summary>A w2g invite on the clipboard, offered in the Join box (checked once a second).</summary>
    private string? ClipboardInvite()
    {
        if (DateTime.UtcNow - clipboardCheckedUtc < TimeSpan.FromSeconds(1))
            return clipboardInvite;

        clipboardCheckedUtc = DateTime.UtcNow;
        try
        {
            var text = ImGui.GetClipboardText();
            clipboardInvite = text != null && HighPerformance.FindW2gLink(text) != null ? text : null;
        }
        catch
        {
            clipboardInvite = null;
        }

        return clipboardInvite;
    }

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();
}
