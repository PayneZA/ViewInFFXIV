using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using InView.Host;
using InView.Shared;
using InView.Windows;
using InView.World;

namespace InView;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/inview";

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

    public readonly WindowSystem WindowSystem = new("InView");

    public HostClient Host { get; }

    public ScreenPlacement Placement { get; } = new();

    public ScreenRenderer Renderer { get; }

    private readonly RemoteWindow remote;
    private string lastSentSource = "";
    private long lastSentHwnd;
    private bool helperWasAlive;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.RoomUrl ??= IpcConstants.DefaultUrl;
        if (string.IsNullOrWhiteSpace(Configuration.CaptureSource))
            Configuration.CaptureSource = "webview";

        Host = new HostClient(
            Log,
            TextureProvider,
            PluginInterface.AssemblyLocation.DirectoryName ?? AppContext.BaseDirectory);

        Renderer = new ScreenRenderer(Configuration, ClientState, GameGui);
        Renderer.Initialize(PluginInterface, Log);

        remote = new RemoteWindow(this);
        WindowSystem.AddWindow(remote);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open InView. /inview place  /inview host  /inview hide  /inview apply <code>",
        });

        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        Framework.Update += OnFramework;

        PushHostState();
        Log.Information("InView loaded. Use {Command} to open the remote.", CommandName);
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
            ChatGui.PrintError($"InView: {error}");
            return;
        }

        Configuration.Save();
        ChatGui.Print("InView screen applied from share code.", "InView");
        if (ClientState.TerritoryType != Configuration.ScreenTerritory)
        {
            ChatGui.Print(
                "You are in a different zone than this code. Housing interiors differ by ward — re-place if it looks wrong.",
                "InView");
        }
    }

    private void OnFramework(IFramework framework)
    {
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
        WindowSystem.Draw();
        Renderer.Draw(Host.Texture);
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        if (trimmed.Equals("place", StringComparison.OrdinalIgnoreCase))
        {
            Placement.PlaceInFront(ClientState, ObjectTable, Configuration);
            ChatGui.Print("InView screen placed in front of you.", "InView");
            return;
        }

        if (trimmed.Equals("host", StringComparison.OrdinalIgnoreCase))
        {
            Host.ShowHostWindow();
            ChatGui.Print("InView host window shown (login / screen share).", "InView");
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
                ChatGui.Print("Usage: /inview apply IV1.…", "InView");
                return;
            }

            ApplyShareCode(code);
            return;
        }

        ToggleMainUi();
    }
}
