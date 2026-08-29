using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ViewInFFXIV.World;
using ViewInFFXIV.Shared;

namespace ViewInFFXIV.Windows;

public sealed class RemoteWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string url;
    private string shareInput = "";

    public RemoteWindow(Plugin plugin)
        : base("ViewInFFXIV##ViewInFFXIVRemote")
    {
        this.plugin = plugin;
        url = plugin.Configuration.RoomUrl;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 520),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = new Vector2(520, 720);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        var config = plugin.Configuration;
        var status = plugin.Host.Status;
        var capturingWindow = config.CaptureSource == "window";
        var helperRunning = plugin.Host.HelperAlive;

        DrawHelperControls(config, helperRunning);

        ImGui.Separator();
        DrawSourceCombo(config, status, helperRunning);
        ImGui.TextDisabled("Fullscreen the video in that window (F11), then capture it.");

        if (!capturingWindow)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("WatchTogether room — chat stays in /say.");

            ImGui.InputText("Room URL", ref url, 1024);
            if (ImGui.Button("Join"))
            {
                config.RoomUrl = string.IsNullOrWhiteSpace(url) ? IpcConstants.DefaultUrl : url.Trim();
                url = config.RoomUrl;
                config.Save();
                plugin.Host.Send(HostCommand.Navigate(config.RoomUrl));
            }

            ImGui.SameLine();
            if (ImGui.Button("Leave"))
            {
                url = IpcConstants.DefaultUrl;
                config.RoomUrl = url;
                config.Save();
                plugin.Host.Send(HostCommand.Navigate(url));
            }

            var volume = config.Volume;
            if (ImGui.SliderFloat("Volume", ref volume, 0f, 1f))
            {
                config.Volume = volume;
                config.Save();
                plugin.Host.Send(HostCommand.SetVolume(volume));
            }

            var hideChrome = config.HideChrome;
            if (ImGui.Checkbox("Hide WatchTogether chrome", ref hideChrome))
            {
                config.HideChrome = hideChrome;
                config.Save();
                plugin.Host.Send(HostCommand.SetHideChrome(hideChrome));
            }

            if (helperRunning)
            {
                if (ImGui.Button(status.WindowVisible ? "Hide host window" : "Show host window"))
                {
                    if (status.WindowVisible)
                        plugin.Host.HideHostWindow();
                    else
                        plugin.Host.ShowHostWindow();
                }

                ImGui.SameLine();
                ImGui.TextDisabled("Login / screen-share picker");
            }
        }
        else
        {
            ImGui.TextDisabled("Audio stays in that browser or Discord.");
        }

        ImGui.Separator();
        ImGui.TextUnformatted("House screen — sliders move it live");
        ScreenPlacement.EnsureAnchor(config);
        if (ImGui.Button("Place in front of me"))
            plugin.Placement.PlaceInFront(Plugin.ClientState, Plugin.ObjectTable, config);

        ImGui.SameLine();
        var enabled = config.ScreenEnabled;
        if (ImGui.Checkbox("Show screen", ref enabled))
        {
            config.ScreenEnabled = enabled;
            if (enabled && !config.HasAnchor)
                plugin.Placement.PlaceInFront(Plugin.ClientState, Plugin.ObjectTable, config);
            else
                config.Save();
        }

        ImGui.TextDisabled("Anchor stays where you last placed. Re-place to move the origin.");

        var distance = config.PlaceDistance;
        if (LiveSlider("Distance", ref distance, PlacementLimits.MinDistance, PlacementLimits.MaxDistance))
        {
            config.PlaceDistance = distance;
            ScreenPlacement.ApplyLive(config);
        }

        var height = config.PlaceHeight;
        if (LiveSlider("Height", ref height, PlacementLimits.MinHeight, PlacementLimits.MaxHeight))
        {
            config.PlaceHeight = height;
            ScreenPlacement.ApplyLive(config);
        }

        var strafe = config.PlaceStrafe;
        if (LiveSlider("Left / right", ref strafe, PlacementLimits.MinStrafe, PlacementLimits.MaxStrafe))
        {
            config.PlaceStrafe = strafe;
            ScreenPlacement.ApplyLive(config);
        }

        var yaw = config.ScreenYaw;
        if (LiveSlider("Yaw", ref yaw, -MathF.PI, MathF.PI))
            config.ScreenYaw = yaw;

        var pitch = config.ScreenPitch;
        if (LiveSlider("Pitch", ref pitch, -MathF.PI / 2f, MathF.PI / 2f))
            config.ScreenPitch = pitch;

        var width = config.ScreenWidth;
        if (LiveSlider("Width (yalms, 16:9)", ref width, PlacementLimits.MinWidth, PlacementLimits.MaxWidth))
            config.ScreenWidth = width;

        if (ImGui.Button("Reset to defaults"))
        {
            PlacementLimits.ResetToDefaults(config);
            config.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("Remove from zone"))
        {
            PlacementLimits.RemoveFromZone(config);
            config.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Share screen position");
        if (ImGui.Button("Copy share code"))
        {
            ScreenPlacement.EnsureAnchor(config);
            ImGui.SetClipboardText(ScreenShareCode.Export(config));
            config.Save();
        }

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.7f);
        ImGui.InputText("##share", ref shareInput, 256);
        ImGui.SameLine();
        if (ImGui.Button("Apply"))
            plugin.ApplyShareCode(shareInput);
        ImGui.TextDisabled("Tell-safe. Does not include the room URL.");

        ImGui.Separator();
        DrawStatus(status, helperRunning);
        DrawPreview(capturingWindow, helperRunning);
    }

    private void DrawHelperControls(Configuration config, bool helperRunning)
    {
        ImGui.TextUnformatted($"Helper: {(helperRunning ? "running" : "stopped")}");

        if (helperRunning)
        {
            if (ImGui.Button("Stop helper"))
                plugin.Host.StopHost();
        }
        else if (ImGui.Button("Start helper"))
        {
            plugin.Host.StartHost();
        }

        ImGui.SameLine();
        var autoStart = config.AutoStartHost;
        if (ImGui.Checkbox("Start helper with FFXIV", ref autoStart))
        {
            config.AutoStartHost = autoStart;
            config.Save();
        }
    }

    private void DrawSourceCombo(Configuration config, HostStatus status, bool helperRunning)
    {
        var windows = status.Windows ?? [];
        var labels = new List<string> { "Built-in" };
        var sources = new List<BrowserWindowInfo?> { null };
        var current = 0;
        var waiting = false;

        if (config.CaptureSource == "window")
        {
            var matchIndex = windows.FindIndex(w =>
                w.Process.Equals(config.CaptureProcess, StringComparison.OrdinalIgnoreCase)
                && w.Title == config.CaptureTitle);
            if (matchIndex < 0)
            {
                matchIndex = windows.FindIndex(w =>
                    w.Process.Equals(config.CaptureProcess, StringComparison.OrdinalIgnoreCase));
            }

            if (matchIndex < 0)
            {
                waiting = true;
                labels.Add($"{BrowserCatalog.Label(config.CaptureProcess, config.CaptureTitle)} (closed)");
                sources.Add(null);
                current = 1;
            }
        }

        foreach (var window in windows)
        {
            labels.Add(BrowserCatalog.Label(window.Process, window.Title));
            sources.Add(window);
        }

        if (config.CaptureSource == "window" && !waiting)
        {
            var liveIndex = windows.FindIndex(w =>
                w.Process.Equals(config.CaptureProcess, StringComparison.OrdinalIgnoreCase)
                && w.Title == config.CaptureTitle);
            if (liveIndex < 0)
            {
                liveIndex = windows.FindIndex(w =>
                    w.Process.Equals(config.CaptureProcess, StringComparison.OrdinalIgnoreCase));
            }

            if (liveIndex >= 0)
                current = liveIndex + 1;
        }

        if (current < 0 || current >= labels.Count)
            current = 0;

        if (!helperRunning)
            ImGui.BeginDisabled();

        var items = labels.ToArray();
        if (ImGui.Combo("Source", ref current, items, items.Length))
        {
            if (current <= 0)
                plugin.SetCaptureSource("webview");
            else if (sources[current] is { } chosen)
                plugin.SetCaptureSource("window", chosen);
        }

        ImGui.SameLine();
        if (ImGui.Button("Refresh"))
            plugin.Host.Send(HostCommand.ListWindows());

        if (!helperRunning)
            ImGui.EndDisabled();
    }

    private void DrawStatus(HostStatus status, bool helperRunning)
    {
        if (!helperRunning)
        {
            ImGui.TextUnformatted("Helper: stopped");
            return;
        }

        var source = status.Source == "window"
            ? (string.IsNullOrEmpty(status.CapturedTitle) ? "browser" : status.CapturedTitle)
            : (status.Loaded ? "loaded" : "loading");
        ImGui.TextUnformatted(
            $"Helper: alive   " +
            $"{(status.Source == "window" ? "Window" : "WebView")}: {source}   " +
            $"Capture: {(string.IsNullOrEmpty(status.Capture) ? "—" : status.Capture)}   " +
            $"FPS: {status.Fps:0}   " +
            $"{status.Width}x{status.Height}");
        if (!string.IsNullOrEmpty(status.Error))
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), status.Error);
        if (status.Source != "window")
            ImGui.TextDisabled(status.Url);
        ImGui.TextDisabled(plugin.Renderer.UsingPictomancy
            ? "World draw: Pictomancy (occluded by walls)"
            : "World draw: WorldToScreen fallback");
    }

    private void DrawPreview(bool capturingWindow, bool helperRunning)
    {
        if (!helperRunning)
        {
            ImGui.TextUnformatted("Helper stopped. Click Start helper to begin.");
            return;
        }

        var texture = plugin.Host.Texture;
        if (texture == null)
        {
            ImGui.TextUnformatted(capturingWindow
                ? "No video frame yet. Fullscreen the video in that window (F11)."
                : "No video frame yet. Join a room, or Show host window if the page needs login.");
            return;
        }

        var avail = ImGui.GetContentRegionAvail();
        var aspect = texture.Height > 0 ? texture.Width / (float)texture.Height : 16f / 9f;
        var w = MathF.Max(160f, avail.X);
        var h = w / aspect;
        if (h > 280f)
        {
            h = 280f;
            w = h * aspect;
        }

        ImGui.TextUnformatted("Preview");
        ImGui.Image(texture.Handle, new Vector2(w, h));
    }

    private bool LiveSlider(string label, ref float value, float min, float max)
    {
        var changed = ImGui.SliderFloat(label, ref value, min, max);
        if (ImGui.IsItemDeactivatedAfterEdit())
            plugin.Configuration.Save();
        return changed;
    }
}
