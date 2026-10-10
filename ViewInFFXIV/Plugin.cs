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
        "/viewin create | join <w2g link> | play <video link> | pause | resume | seek <1:23|+10> | cc [on|off] | vol <0-100> | search <words> | next | browser <auto|brave|chrome|edge> | invite | show | hide | place | screen | apply <code> | stop | bench";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;

    public Configuration Configuration { get; }

    public readonly WindowSystem WindowSystem = new("ViewInFFXIV");

    public HostClient Host { get; }

    public ScreenPlacement Placement { get; } = new();

    public ScreenRenderer Renderer { get; }

    public HighPerformance HighPerformance { get; }

    public Benchmark Benchmark { get; }

    public WatchQueue Queue { get; }

    public ThumbnailCache Thumbnails { get; }

    public SpatialAudio Audio { get; }

    public bool MainWindowOpen => mainWindow.IsOpen;

    /// <summary>The house you are in (0 outside houses), as of this framework tick.</summary>
    public ulong CurrentHouse => lastHouse;

    private readonly ScreenOverlay screenOverlay;

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
        screenOverlay = new ScreenOverlay(this);

        mainWindow = new MainWindow(this);
        WindowSystem.AddWindow(mainWindow);

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
        mainWindow.Dispose();
        Thumbnails.Dispose();
        Renderer.Dispose();
        Benchmark.Dispose();
        Host.Dispose();
        video?.Dispose();
        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(AliasCommandName);
    }

    public void ToggleMainUi() => mainWindow.Toggle();

    public void OpenScreenUi() => mainWindow.Open(MainWindow.Tab.Screen);

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
            ChatGui.PrintError($"{ChatPrefix}: {error}");
            return false;
        }

        if (!PlacementPresets.ImportShared(Configuration, shared, sharedHouse, ClientState.TerritoryType, CurrentHouse))
        {
            ChatGui.PrintError($"{ChatPrefix}: that zone already has {PlacementPresets.MaxPresetsPerTerritory} saved spots; delete one first.");
            return false;
        }

        Configuration.Save();
        var here = ClientState.TerritoryType == shared.ScreenTerritory && (sharedHouse == 0 || sharedHouse == CurrentHouse);
        ChatGui.Print(
            here
                ? "Screen placed where your friend has it."
                : sharedHouse != 0
                    ? $"Got your friend's screen spot; it appears when you're in their house ({HouseLocator.Describe(sharedHouse)})."
                    : "Screen spot saved for its zone; pick it under Screen when you're there.",
            ChatPrefix);
        return true;
    }

    private void OnFramework(IFramework framework)
    {
        // The house id arrives a moment after the zone during loading; either change re-picks the spot.
        var territory = ClientState.TerritoryType;
        var house = HouseLocator.Current();
        if (territory != lastTerritoryType || house != lastHouse)
        {
            PlacementPresets.OnTerritoryChanged(Configuration, lastTerritoryType, lastHouse, territory, house);
            lastTerritoryType = territory;
            lastHouse = house;
            Configuration.Save();
        }

        Host.Tick();
        HighPerformance.Tick();
        Queue.Tick(HighPerformance.Playback);
        Audio.Tick();
    }

    private void Draw()
    {
        if (HighPerformance.PendingClipboard is { } clip)
        {
            ImGui.SetClipboardText(clip);
            HighPerformance.PendingClipboard = null;
        }

        Benchmark.BeginFrame();

        // Pull the newest frame from the helper on the GPU (render thread, game's device context), but only
        // while something shows it: the wall screen in this zone, or the preview in the main window.
        var texture = Host.HelperAlive ? Host.Status.SharedTexture : "";
        var wanted = Renderer.WillDraw || mainWindow.IsOpen;
        if (wanted && !Benchmark.SkipVideo && (texture.Length > 0 || video != null))
            Video.Update(texture);
        Benchmark.VideoDone();

        if (!ShouldHidePluginWindows())
            WindowSystem.Draw();
        if (!IsGameUiHidden())
            screenOverlay.Draw();

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
            ChatGui.PrintError($"{ChatPrefix}: {Browsers.ById(id)!.Name} is not installed.");
            return;
        }

        Configuration.Browser = id;
        Configuration.Save();
        var browser = HighPerformance.Browser;
        ChatGui.Print($"Browser: {(id == Browsers.Auto ? $"automatic ({browser?.Name ?? "none installed"})" : browser?.Name)}.", ChatPrefix);
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
                ChatGui.Print("Starting a watch party…", ChatPrefix);
                break;
            case "join":
                HighPerformance.Join(rest.Length > 0 ? rest : Configuration.LastRoomUrl);
                break;
            case "play":
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
                    ChatGui.PrintError($"{ChatPrefix}: /viewin vol 0-100");
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
                Placement.PlaceInFront(ClientState, ObjectTable, Configuration, CurrentHouse);
                ChatGui.Print("ViewInFFXIV screen placed in front of you.", ChatPrefix);
                break;
            case "screen":
                OpenScreenUi();
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
                    ChatGui.Print("Up next is empty.", ChatPrefix);
                break;
            case "apply":
                if (rest.Length == 0)
                    ChatGui.Print("Usage: /viewin apply VIF1.…", ChatPrefix);
                else
                    ApplyShareCode(rest);
                break;
            case "bench":
                Benchmark.Start(Video.Device);
                break;
            case "stop":
                HighPerformance.Stop();
                ChatGui.Print("Left the party and closed the browser.", ChatPrefix);
                break;
            default:
                ChatGui.Print(Usage, ChatPrefix);
                break;
        }
    }
}
