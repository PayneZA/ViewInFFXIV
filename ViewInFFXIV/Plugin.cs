using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ViewInFFXIV.Host;
using ViewInFFXIV.Shared;
using ViewInFFXIV.Windows;
using ViewInFFXIV.World;

namespace ViewInFFXIV;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/viewin";
    private const string ChatPrefix = "ViewInFFXIV";

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

    private readonly RemoteWindow remote;
    private string lastSentSource = "";
    private long lastSentHwnd;
    private bool helperWasAlive;
    private uint lastTerritoryType;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.RoomUrl ??= IpcConstants.DefaultUrl;
        if (string.IsNullOrWhiteSpace(Configuration.CaptureSource))
            Configuration.CaptureSource = "webview";

        if (Configuration.Version < 3)
            PlacementPresets.MigrateFromV2(Configuration);
        Configuration.Version = 3;
        Configuration.SavedPlacements ??= [];

        lastTerritoryType = ClientState.TerritoryType;
        if (lastTerritoryType != 0)
            PlacementPresets.LoadActivePreset(Configuration, lastTerritoryType);

        Host = new HostClient(
            Log,
            TextureProvider,
            PluginInterface.AssemblyLocation.DirectoryName ?? AppContext.BaseDirectory,
            Configuration.AutoStartHost);

        Renderer = new ScreenRenderer(Configuration, ClientState, GameGui);
        Renderer.Initialize(PluginInterface, Log);

        remote = new RemoteWindow(this);
        WindowSystem.AddWindow(remote);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open ViewInFFXIV. /viewin start|stop  /viewin place  /viewin host  /viewin hide  /viewin apply <code>",
        });

        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        Framework.Update += OnFramework;

        ApplyUiHidePolicy();

        if (Configuration.AutoStartHost)
            PushHostState();

        Log.Information("ViewInFFXIV loaded. Use {Command} to open the remote.", CommandName);
    }

    public void Dispose()
    {
        Framework.Update -= OnFramework;
        PluginInterface.UiBuilder.Draw -= Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        WindowSystem.RemoveAllWindows();
        remote.Dispose();
        Renderer.Dispose();
        Host.Dispose();
        CommandManager.RemoveHandler(CommandName);
    }

    public void ToggleMainUi() => remote.Toggle();

    internal void ApplyUiHidePolicy()
    {
        PluginInterface.UiBuilder.DisableAutomaticUiHide = Configuration.KeepScreenWhenUiHidden;
    }

    public void PushHostState()
    {
        if (Configuration.CaptureSource != "window")
        {
            Host.Send(HostCommand.Navigate(Configuration.RoomUrl));
            Host.Send(HostCommand.SetVolume(Configuration.Volume));
            Host.Send(HostCommand.SetHideChrome(Configuration.HideChrome));
        }

        lastSentSource = "";
        lastSentHwnd = 0;
        BindCaptureSource();
    }

    public void SetCaptureSource(string source, BrowserWindowInfo? window = null)
    {
        if (source == "window" && window != null)
        {
            Configuration.CaptureSource = "window";
            Configuration.CaptureProcess = window.Process;
            Configuration.CaptureTitle = window.Title;
            Host.Send(HostCommand.SetSource("window", window.Hwnd));
            lastSentSource = "window";
            lastSentHwnd = window.Hwnd;
        }
        else
        {
            Configuration.CaptureSource = "webview";
            Host.Send(HostCommand.Navigate(Configuration.RoomUrl));
            Host.Send(HostCommand.SetVolume(Configuration.Volume));
            Host.Send(HostCommand.SetHideChrome(Configuration.HideChrome));
            Host.Send(HostCommand.SetSource("webview"));
            lastSentSource = "webview";
            lastSentHwnd = 0;
        }

        Configuration.Save();
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
        if (Host.HelperAlive && !helperWasAlive)
        {
            lastSentSource = "";
            lastSentHwnd = 0;
            PushHostState();
        }

        helperWasAlive = Host.HelperAlive;
        BindCaptureSource();
    }

    private void BindCaptureSource()
    {
        if (!Host.HelperAlive)
            return;

        if (Configuration.CaptureSource != "window")
        {
            if (lastSentSource != "webview")
            {
                Host.Send(HostCommand.SetSource("webview"));
                lastSentSource = "webview";
                lastSentHwnd = 0;
            }

            return;
        }

        var windows = Host.Status.Windows ?? [];
        if (windows.Count == 0)
            return;

        var match = windows.Find(w =>
            w.Process.Equals(Configuration.CaptureProcess, StringComparison.OrdinalIgnoreCase)
            && w.Title == Configuration.CaptureTitle);
        match ??= windows.Find(w =>
            w.Process.Equals(Configuration.CaptureProcess, StringComparison.OrdinalIgnoreCase));
        if (match == null || match.Hwnd == 0)
            return;
        if (lastSentSource == "window" && lastSentHwnd == match.Hwnd)
            return;

        Host.Send(HostCommand.SetSource("window", match.Hwnd));
        lastSentSource = "window";
        lastSentHwnd = match.Hwnd;
    }

    private void Draw()
    {
        if (!ShouldHidePluginWindows())
            WindowSystem.Draw();

        Renderer.Draw(Host.Texture);
    }

    private bool ShouldHidePluginWindows()
    {
        if (!Configuration.KeepScreenWhenUiHidden)
            return false;

        return GameGui.GameUiHidden
            || ClientState.IsGPosing
            || PluginInterface.UiBuilder.CutsceneActive;
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        if (trimmed.Equals("start", StringComparison.OrdinalIgnoreCase))
        {
            Host.StartHost();
            ChatGui.Print("ViewInFFXIV helper started.", ChatPrefix);
            return;
        }

        if (trimmed.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            Host.StopHost();
            ChatGui.Print("ViewInFFXIV helper stopped.", ChatPrefix);
            return;
        }

        if (trimmed.Equals("place", StringComparison.OrdinalIgnoreCase))
        {
            Placement.PlaceInFront(ClientState, ObjectTable, Configuration);
            ChatGui.Print("ViewInFFXIV screen placed in front of you.", ChatPrefix);
            return;
        }

        if (trimmed.Equals("host", StringComparison.OrdinalIgnoreCase))
        {
            if (!Host.HelperAlive)
                Host.StartHost();
            Host.ShowHostWindow();
            ChatGui.Print("ViewInFFXIV host window shown (login / screen share).", ChatPrefix);
            return;
        }

        if (trimmed.Equals("hide", StringComparison.OrdinalIgnoreCase))
        {
            Host.HideHostWindow();
            return;
        }

        if (trimmed.StartsWith("apply", StringComparison.OrdinalIgnoreCase))
        {
            var code = trimmed.Length > 5 ? trimmed[5..].Trim() : "";
            if (string.IsNullOrEmpty(code))
            {
                ChatGui.Print("Usage: /viewin apply VIF1.…", ChatPrefix);
                return;
            }

            ApplyShareCode(code);
            return;
        }

        ToggleMainUi();
    }
}
