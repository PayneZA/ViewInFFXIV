using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ViewInFFXIV.World;
using ViewInFFXIV.Shared;

namespace ViewInFFXIV.Windows;

public sealed class RemoteWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string shareInput = "";
    private string newPresetName = "";

    public RemoteWindow(Plugin plugin)
        : base("ViewInFFXIV##ViewInFFXIVRemote")
    {
        this.plugin = plugin;
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
        var hasCaptureWindow = !string.IsNullOrWhiteSpace(config.CaptureProcess);
        var helperRunning = plugin.Host.HelperAlive;

        DrawHelperControls(config, helperRunning);

        ImGui.Separator();
        ImGui.TextUnformatted("Capture window");
        DrawSourceCombo(config, status, helperRunning);
        ImGui.TextDisabled("Pick a browser or Discord window. Audio stays in that app.");

        ImGui.Separator();
        ImGui.TextUnformatted("House screen — sliders move it live");
        DrawSavedSpots(config);
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
            {
                SyncActivePreset(config);
                config.Save();
            }
        }

        ImGui.TextDisabled("Anchor stays where you last placed. Re-place to move the origin.");

        var keepWhenHidden = config.KeepScreenWhenUiHidden;
        if (ImGui.Checkbox("Keep screen when UI hidden", ref keepWhenHidden))
        {
            config.KeepScreenWhenUiHidden = keepWhenHidden;
            config.Save();
            plugin.ApplyUiHidePolicy();
        }

        ImGui.TextDisabled("Scroll Lock, cutscenes, and GPose.");

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
            SyncActivePreset(config);
            config.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("Remove from zone"))
        {
            PlacementLimits.RemoveFromZone(config, Plugin.ClientState.TerritoryType);
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
        DrawPreview(hasCaptureWindow, helperRunning);
    }

    private void DrawSavedSpots(Configuration config)
    {
        var territoryId = Plugin.ClientState.TerritoryType;
        if (territoryId == 0)
        {
            ImGui.TextDisabled("Saved spots — enter a zone to save TV positions.");
            return;
        }

        ImGui.TextUnformatted($"Saved spots — territory {territoryId}");
        var territory = PlacementPresets.FindTerritory(config, territoryId);
        var presets = territory?.Presets ?? [];
        var names = presets.Select(p => p.Name).ToArray();
        var current = 0;
        if (territory != null && !string.IsNullOrWhiteSpace(territory.ActivePresetName))
        {
            current = Array.FindIndex(names, n =>
                n.Equals(territory.ActivePresetName, StringComparison.OrdinalIgnoreCase));
            if (current < 0)
                current = 0;
        }

        if (names.Length == 0)
        {
            ImGui.TextDisabled("No saved spots yet. Place the screen, then Save spot or New spot.");
        }
        else if (ImGui.Combo("Spot", ref current, names, names.Length))
        {
            PlacementPresets.SelectPreset(config, territoryId, names[current]);
            config.Save();
        }

        if (ImGui.Button("Save spot"))
        {
            PlacementPresets.SaveActivePreset(config, territoryId);
            config.Save();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(120f);
        ImGui.InputText("##newspot", ref newPresetName, 64);
        ImGui.SameLine();
        if (ImGui.Button("New spot"))
        {
            if (PlacementPresets.CreatePreset(config, territoryId, newPresetName))
            {
                newPresetName = "";
                config.Save();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Delete spot") && territory != null && !string.IsNullOrWhiteSpace(territory.ActivePresetName))
        {
            PlacementPresets.DeletePreset(config, territoryId, territory.ActivePresetName);
            config.Save();
        }

        ImGui.TextDisabled("Switch spots to move the TV between lounge, bedroom, etc.");
    }

    private void SyncActivePreset(Configuration config)
    {
        var territoryId = Plugin.ClientState.TerritoryType;
        if (territoryId != 0)
            PlacementPresets.SaveActivePreset(config, territoryId);
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
        var labels = new List<string> { "(none)" };
        var sources = new List<BrowserWindowInfo?> { null };
        var current = 0;
        var waiting = false;

        if (hasSavedCapture(config))
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

        if (hasSavedCapture(config) && !waiting)
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
        if (ImGui.Combo("Window", ref current, items, items.Length))
        {
            if (current <= 0)
                plugin.SetCaptureWindow(null);
            else if (sources[current] is { } chosen)
                plugin.SetCaptureWindow(chosen);
        }

        ImGui.SameLine();
        if (ImGui.Button("Refresh"))
            plugin.Host.Send(HostCommand.ListWindows());

        if (!helperRunning)
            ImGui.EndDisabled();
    }

    private static bool hasSavedCapture(Configuration config) =>
        !string.IsNullOrWhiteSpace(config.CaptureProcess);

    private void DrawStatus(HostStatus status, bool helperRunning)
    {
        if (!helperRunning)
        {
            ImGui.TextUnformatted("Helper: stopped");
            return;
        }

        var source = status.Source == "window"
            ? (string.IsNullOrEmpty(status.CapturedTitle) ? "browser" : status.CapturedTitle)
            : "none";
        ImGui.TextUnformatted(
            $"Helper: alive   " +
            $"Window: {source}   " +
            $"Capture: {(string.IsNullOrEmpty(status.Capture) ? "—" : status.Capture)}   " +
            $"FPS: {status.Fps:0}   " +
            $"{status.Width}x{status.Height}");
        if (!string.IsNullOrEmpty(status.Error))
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), status.Error);
        ImGui.TextDisabled(plugin.Renderer.UsingPictomancy
            ? "World draw: Pictomancy (occluded by walls)"
            : "World draw: WorldToScreen fallback");
    }

    private void DrawPreview(bool hasCaptureWindow, bool helperRunning)
    {
        if (!helperRunning)
        {
            ImGui.TextUnformatted("Helper stopped. Click Start helper to begin.");
            return;
        }

        var texture = plugin.Host.Texture;
        if (texture == null)
        {
            ImGui.TextUnformatted(hasCaptureWindow
                ? "No video frame yet. Fullscreen the video in that window (F11) if needed."
                : "Select a browser or Discord window above.");
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
        {
            SyncActivePreset(plugin.Configuration);
            plugin.Configuration.Save();
        }
        return changed;
    }
}
