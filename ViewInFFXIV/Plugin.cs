using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ViewInFFXIV.Diagnostics;
using ViewInFFXIV.Host;
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
        "/viewin create | join <w2g link> | play <video link> | pause | resume | seek <1:23|+10> | cc [on|off] | vol <0-100> | invite | show | hide | place | apply <code> | stop | bench";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;

    public Configuration Configuration { get; }

    public readonly WindowSystem WindowSystem = new("ViewInFFXIV");

    public HostClient Host { get; }

    public ScreenPlacement Placement { get; } = new();

    public ScreenRenderer Renderer { get; }

    public HighPerformance HighPerformance { get; }

    public Benchmark Benchmark { get; }

    private readonly RemoteWindow placementWindow;
    private readonly HighPerformanceWindow mainWindow;
    private readonly object videoLock = new();
    private SharedVideo? video;
    private uint lastTerritoryType;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        if (Configuration.Version < 3)
            PlacementPresets.MigrateFromV2(Configuration);
        Configuration.Version = 5;
        Configuration.SavedPlacements ??= [];

        lastTerritoryType = ClientState.TerritoryType;
        if (lastTerritoryType != 0)
            PlacementPresets.LoadActivePreset(Configuration, lastTerritoryType);

        Host = new HostClient(Log, PluginInterface.AssemblyLocation.DirectoryName ?? AppContext.BaseDirectory);

        Renderer = new ScreenRenderer(Configuration, ClientState, GameGui);
        Renderer.Initialize(PluginInterface, Log);

        HighPerformance = new HighPerformance(this);
        Benchmark = new Benchmark(this);

        mainWindow = new HighPerformanceWindow(this);
        WindowSystem.AddWindow(mainWindow);
        placementWindow = new RemoteWindow(this);
        WindowSystem.AddWindow(placementWindow);

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
        placementWindow.Dispose();
        Renderer.Dispose();
        Benchmark.Dispose();
        Host.Dispose();
        video?.Dispose();
        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(AliasCommandName);
    }

    public void ToggleMainUi() => mainWindow.Toggle();

    public void TogglePlacementUi() => placementWindow.Toggle();

    internal void ApplyUiHidePolicy()
    {
        PluginInterface.UiBuilder.DisableAutomaticUiHide = Configuration.KeepScreenWhenUiHidden;
    }

    public void ApplyShareCode(string raw)
    {
        if (!ScreenShareCode.TryImport(raw, Configuration, out var error))
        {
            ChatGui.PrintError($"{ChatPrefix}: {error}");
            return;
        }

        Configuration.Save();
        PlacementPresets.ApplyImportedShareCode(Configuration);
        Configuration.Save();
        ChatGui.Print("ViewInFFXIV screen applied from share code.", ChatPrefix);
        if (ClientState.TerritoryType != Configuration.ScreenTerritory)
        {
            ChatGui.Print(
                "You are in a different zone than this code. Housing interiors differ by ward — re-place if it looks wrong.",
                ChatPrefix);
        }
    }

    private void OnFramework(IFramework framework)
    {
        var territory = ClientState.TerritoryType;
        if (territory != lastTerritoryType)
        {
            PlacementPresets.OnTerritoryChanged(Configuration, lastTerritoryType, territory);
            lastTerritoryType = territory;
            Configuration.Save();
        }

        Host.Tick();
        HighPerformance.Tick();
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

        if (Benchmark.SkipRender)
            Renderer.Hide();
        else
            Renderer.Draw(video?.Handle, IsGameUiHidden());
        Benchmark.EndFrame();
    }

    private bool ShouldHidePluginWindows() => Configuration.KeepScreenWhenUiHidden && IsGameUiHidden();

    private static bool IsGameUiHidden() =>
        GameGui.GameUiHidden || ClientState.IsGPosing || PluginInterface.UiBuilder.CutsceneActive;

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
            case "create":
                HighPerformance.CreateRoom();
                ChatGui.Print("Creating a w2g room…", ChatPrefix);
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
                Placement.PlaceInFront(ClientState, ObjectTable, Configuration);
                ChatGui.Print("ViewInFFXIV screen placed in front of you.", ChatPrefix);
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
                ChatGui.Print("ViewInFFXIV closed its Brave.", ChatPrefix);
                break;
            default:
                ChatGui.Print(Usage, ChatPrefix);
                break;
        }
    }
}
