using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ViewInFFXIV.Diagnostics;
using ViewInFFXIV.Host;
using ViewInFFXIV.Media;
using ViewInFFXIV.Shared;
using ViewInFFXIV.Windows;
using ViewInFFXIV.World;

namespace ViewInFFXIV;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/viewin";
    private const string AliasCommandName = "/viewinff";
    private const string ChatPrefix = "ViewInFFXIV";
    private const string Usage =
        "/viewin create | join <w2g link> | play <video link> | pause | resume | seek <1:23|+10> | cc [on|off] | vol <0-100> | search <words> | next | browser <auto|brave|chrome|edge> | invite | show | hide | place | screen | mode [game|ui] | apply <code> | stop | bench";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static INotificationManager Notifications { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;

    public Configuration Configuration { get; }

    public readonly WindowSystem WindowSystem = new("ViewInFFXIV");

    /// <summary>The screen on your UI, apart from the other windows: it stays with the wall screen's hide rules.</summary>
    private readonly WindowSystem screenWindows = new("ViewInFFXIVScreen");

    public HostClient Host { get; }

    public ScreenPlacement Placement { get; } = new();

    public ScreenRenderer Renderer { get; }

    public HighPerformance HighPerformance { get; }

    public Benchmark Benchmark { get; }

    public WatchQueue Queue { get; }

    public ThumbnailCache Thumbnails { get; }

    public SpatialAudio Audio { get; }

    public Playability Playability { get; } = new();

    public RenderTrace RenderTrace { get; } = new();

    public bool MainWindowOpen => mainWindow.IsOpen;

    /// <summary>The screen is on your UI and shown.</summary>
    public bool UiScreenShown => Configuration.ScreenMode == ScreenMode.OnUi && Configuration.UiScreenShown;

    internal UiScreenWindow UiScreen { get; }

    /// <summary>The house you are in (0 outside houses), as of this framework tick.</summary>
    public ulong CurrentHouse => lastHouse;

    private readonly MainWindow mainWindow;
    private readonly object videoLock = new();
    private SharedVideo? video;
    private uint lastTerritoryType;
    private ulong lastHouse;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        if (Configuration.Version < 3)
            PlacementPresets.MigrateFromV2(Configuration);
        Configuration.Version = 5;
        Configuration.SavedPlacements ??= [];

        lastTerritoryType = ClientState.TerritoryType;
        lastHouse = HouseLocator.Current();
        if (lastTerritoryType != 0)
            PlacementPresets.LoadActivePreset(Configuration, lastTerritoryType, lastHouse);

        Host = new HostClient(Log, PluginInterface.AssemblyLocation.DirectoryName ?? AppContext.BaseDirectory);

        Renderer = new ScreenRenderer(Configuration, ClientState, GameGui);
        Renderer.Initialize(PluginInterface, Log);

        HighPerformance = new HighPerformance(this);
        Benchmark = new Benchmark(this);
        Queue = new WatchQueue(this);
        Thumbnails = new ThumbnailCache(TextureProvider, Log);
        Audio = new SpatialAudio(this);

        mainWindow = new MainWindow(this);
        WindowSystem.AddWindow(mainWindow);
        UiScreen = new UiScreenWindow(this);
        screenWindows.AddWindow(UiScreen);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand) { HelpMessage = $"Open ViewInFFXIV. {Usage}" });
        CommandManager.AddHandler(AliasCommandName, new CommandInfo(OnCommand) { HelpMessage = "Same as /viewin.", ShowInHelp = false });

        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        Framework.Update += OnFramework;

        ApplyUiHidePolicy();
        Log.Information("ViewInFFXIV loaded. Use {Command} to open it.", CommandName);
    }

    /// <summary>The video texture shared by the helper, on the game's GPU (created on first use).</summary>
    public SharedVideo Video
    {
        get
        {
            lock (videoLock)
                return video ??= new SharedVideo(PluginInterface.UiBuilder.DeviceHandle, Log);
        }
    }

    public void Dispose()
    {
        Framework.Update -= OnFramework;
        PluginInterface.UiBuilder.Draw -= Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        WindowSystem.RemoveAllWindows();
        screenWindows.RemoveAllWindows();
        mainWindow.Dispose();
        Thumbnails.Dispose();
        RenderTrace.Dispose();
        Renderer.Dispose();
        Benchmark.Dispose();
        Host.Dispose();
        video?.Dispose();
        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(AliasCommandName);
    }

    public void ToggleMainUi() => mainWindow.Toggle();

    public void OpenScreenUi() => mainWindow.Open(MainWindow.Tab.Screen);

    /// <summary>Moves the TV between the wall (in game) and your UI.</summary>
    public void SetScreenMode(ScreenMode mode)
    {
        Configuration.ScreenMode = mode;
        if (mode == ScreenMode.OnUi)
            Configuration.UiScreenShown = true;
        Configuration.Save();
    }

    internal void ApplyUiHidePolicy()
    {
        PluginInterface.UiBuilder.DisableAutomaticUiHide = Configuration.KeepScreenWhenUiHidden;
    }

    /// <summary>Applies a VIF1 placement code as the "Imported" spot of its zone. Returns false if it is not one.</summary>
    public bool ApplyShareCode(string raw)
    {
        var shared = new Configuration();
        if (!ScreenShareCode.TryImport(raw, shared, out var sharedHouse, out var error))
        {
            Notify.Error("Couldn't use that screen spot", error);
            return false;
        }

        if (!PlacementPresets.ImportShared(Configuration, shared, sharedHouse, ClientState.TerritoryType, CurrentHouse))
        {
            Notify.Error("Couldn't save the screen spot", $"That zone already has {PlacementPresets.MaxPresetsPerTerritory} saved spots; delete one under Screen first.");
            return false;
        }

        Configuration.Save();
        var here = ClientState.TerritoryType == shared.ScreenTerritory && (sharedHouse == 0 || sharedHouse == CurrentHouse);
        if (Configuration.ScreenMode == ScreenMode.OnUi)
            Notify.Info("Got your friend's screen spot", "Your screen is on your UI; switch to In game under Screen to see it on their wall.");
        else if (here)
            Notify.Success("Screen placed where your friend has it");
        else if (sharedHouse != 0)
            Notify.Info("Got your friend's screen spot", $"It appears when you're in their house ({HouseLocator.Describe(sharedHouse)}).");
        else
            Notify.Info("Screen spot saved", "Pick it under Screen when you're in that zone.");
        return true;
    }

    private void OnFramework(IFramework framework)
    {
        // The house id arrives a moment after the zone during loading; either change re-picks the spot.
        var territory = ClientState.TerritoryType;
        var house = HouseLocator.Current();
        if (territory != lastTerritoryType || house != lastHouse)
        {
            if (PlacementPresets.IsDirty(Configuration, lastTerritoryType, lastHouse))
                Notify.Warning("Screen changes not saved", "You moved the screen but didn't save the spot, so it's back where it was saved.");
            PlacementPresets.OnTerritoryChanged(Configuration, lastTerritoryType, lastHouse, territory, house);
            lastTerritoryType = territory;
            lastHouse = house;
            Configuration.Save();
        }

        Host.Tick();
        HighPerformance.Tick();
        Queue.Tick(HighPerformance.Playback);
        Audio.Tick();
        RenderTrace.Tick();
        Renderer.FrameworkTicks++;
        if (FrameInjector.Rendering)
            Renderer.FrameworkOverlaps++;
    }

    private void Draw()
    {
        RenderTrace.OnDraw();
        if (HighPerformance.PendingClipboard is { } clip)
        {
            ImGui.SetClipboardText(clip);
            HighPerformance.PendingClipboard = null;
        }

        Benchmark.BeginFrame();

        // Pull the newest frame from the helper on the GPU (render thread, game's device context), but only
        // while something shows it: the wall screen in this zone, or the preview in the main window.
        var texture = Host.HelperAlive ? Host.Status.SharedTexture : "";
        var wanted = Renderer.WillDraw || UiScreenShown || mainWindow.IsOpen;
        if (wanted && !Benchmark.SkipVideo && (texture.Length > 0 || video != null))
            Video.Update(texture);
        Benchmark.VideoDone();

        if (!ShouldHidePluginWindows())
            WindowSystem.Draw();
        UiScreen.IsOpen = UiScreenShown;
        screenWindows.Draw();

        if (Benchmark.SkipRender)
            Renderer.Hide();
        else
            Renderer.Draw(video?.Handle, IsGameUiHidden());
        Benchmark.EndFrame();
    }

    private bool ShouldHidePluginWindows() => Configuration.KeepScreenWhenUiHidden && IsGameUiHidden();

    private static bool IsGameUiHidden() =>
        GameGui.GameUiHidden || ClientState.IsGPosing || PluginInterface.UiBuilder.CutsceneActive;

    private void SetBrowser(string id)
    {
        if (id != Browsers.Auto && Browsers.ById(id) is null)
        {
            ChatGui.Print($"Usage: /viewin browser auto | {string.Join(" | ", Browsers.All.Select(b => b.Id))}", ChatPrefix);
            return;
        }

        if (id != Browsers.Auto && Browsers.Find(Browsers.ById(id)!) == null)
        {
            ChatGui.Print($"{Browsers.ById(id)!.Name} is not installed.", ChatPrefix);
            return;
        }

        Configuration.Browser = id;
        Configuration.Save();
        var browser = HighPerformance.Browser;
        Notify.Info($"Browser: {(id == Browsers.Auto ? $"automatic ({browser?.Name ?? "none installed"})" : browser?.Name)}");
        HighPerformance.SwitchBrowser();
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        var space = trimmed.IndexOf(' ');
        var verb = (space < 0 ? trimmed : trimmed[..space]).ToLowerInvariant();
        var rest = space < 0 ? "" : trimmed[(space + 1)..].Trim();
        switch (verb)
        {
            case "":
                ToggleMainUi();
                break;
            case "start":
                HighPerformance.Start();
                break;
            case "create" or "party":
                HighPerformance.StartParty();
                break;
            case "join":
                HighPerformance.Join(rest.Length > 0 ? rest : Configuration.LastRoomUrl);
                break;
            case "play":
                if (YouTubeSearch.FindVideoId(rest) is { } videoId)
                    Queue.PlayNow(new VideoInfo { Id = videoId });
                else
                    HighPerformance.Play(rest);
                break;
            case "pause" or "resume" or "toggle":
                HighPerformance.Control(verb == "toggle" ? PlaybackCommands.Toggle : verb == "pause" ? "pause" : "play");
                break;
            case "seek":
                HighPerformance.SeekText(rest);
                break;
            case "cc" or "subs" or "subtitles":
                HighPerformance.Subtitles(rest.ToLowerInvariant() switch { "on" => true, "off" => false, _ => null });
                break;
            case "vol" or "volume":
                if (int.TryParse(rest, out var volume))
                    HighPerformance.Volume(volume);
                else
                    ChatGui.Print("Usage: /viewin vol 0-100", ChatPrefix);
                break;
            case "invite":
                HighPerformance.CopyInvite();
                break;
            case "show":
                HighPerformance.Show();
                break;
            case "hide":
                HighPerformance.Hide();
                break;
            case "name":
                HighPerformance.SendName();
                break;
            case "place":
                if (Configuration.ScreenMode == ScreenMode.OnUi)
                    SetScreenMode(ScreenMode.InGame);
                Placement.PlaceInFront(ClientState, ObjectTable, Configuration, CurrentHouse);
                Notify.Success("Screen placed in front of you");
                break;
            case "screen":
                OpenScreenUi();
                break;
            case "mode":
                var mode = rest.ToLowerInvariant() switch
                {
                    "game" or "world" or "wall" => ScreenMode.InGame,
                    "ui" => ScreenMode.OnUi,
                    _ => Configuration.ScreenMode == ScreenMode.InGame ? ScreenMode.OnUi : ScreenMode.InGame,
                };
                SetScreenMode(mode);
                Notify.Info(mode == ScreenMode.OnUi ? "Screen on your UI" : "Screen in game");
                break;
            case "browser":
                SetBrowser(rest.ToLowerInvariant());
                break;
            case "search" or "yt":
                if (rest.Length == 0)
                    ChatGui.Print("Usage: /viewin search <words>", ChatPrefix);
                else
                    mainWindow.Search(rest);
                break;
            case "next":
                if (!Queue.Next())
                    Notify.Info("Up next is empty");
                break;
            case "apply":
                if (rest.Length == 0)
                    ChatGui.Print("Usage: /viewin apply VIF1.…", ChatPrefix);
                else
                    ApplyShareCode(rest);
                break;
            case "rstat":
                ChatGui.Print(Renderer.Stats(), ChatPrefix);
                break;
            case "rdebug":
                if (rest == "overlay")
                    ScreenRenderer.ForceOverlay = !ScreenRenderer.ForceOverlay;
                else if (rest == "info")
                    WallRenderer.Pass.ShowInfo = !WallRenderer.Pass.ShowInfo;
                else
                    WallRenderer.Pass.IgnoreDepth = !WallRenderer.Pass.IgnoreDepth;
                ChatGui.Print($"Wall screen debug: ignore depth {WallRenderer.Pass.IgnoreDepth} · show scene info {WallRenderer.Pass.ShowInfo} · force overlay {ScreenRenderer.ForceOverlay}", ChatPrefix);
                break;
            case "rtrace":
                RenderTrace.Start();
                break;
            case "bench":
                Benchmark.Start(Video.Device);
                break;
            case "stop":
                HighPerformance.Stop();
                Notify.Info("Left the party", "The browser is closed.");
                break;
            default:
                ChatGui.Print(Usage, ChatPrefix);
                break;
        }
    }
}
