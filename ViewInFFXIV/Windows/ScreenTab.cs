using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using ViewInFFXIV.World;

namespace ViewInFFXIV.Windows;

/// <summary>The Screen tab: in game or on your UI; put the TV on your wall, fine-tune it, keep spots per room and share them.</summary>
internal sealed class ScreenTab
{
    private readonly Plugin plugin;
    private string shareInput = "";
    private string newSpotName = "";

    public ScreenTab(Plugin plugin)
    {
        this.plugin = plugin;
    }

    private Configuration Config => plugin.Configuration;

    private static uint Territory => Plugin.ClientState.TerritoryType;

    private ulong House => plugin.CurrentHouse;

    public void Draw()
    {
        DrawMode();
        if (Config.ScreenMode == ScreenMode.OnUi)
        {
            DrawUiScreen();
            return;
        }

        DrawPlacement();
        DrawSpots();
        DrawFineTune();
        DrawShare();
    }

    private void DrawMode()
    {
        Ui.Card("mode", width =>
        {
            ImGui.TextUnformatted("Where's the TV?");
            var half = (width - 8) / 2;
            ModeButton(ScreenMode.InGame, FontAwesomeIcon.Home, "In game", half);
            ImGui.SameLine();
            ModeButton(ScreenMode.OnUi, FontAwesomeIcon.Desktop, "On UI", half);
            Ui.Dim(Config.ScreenMode == ScreenMode.InGame
                ? "On a wall in the world, to watch together with the people in the room."
                : "A picture on your own screen that goes wherever you go. Only you see it here; friends keep their own.");
        });
    }

    private void ModeButton(ScreenMode mode, FontAwesomeIcon icon, string label, float width)
    {
        string text;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            text = icon.ToIconString();
        var selected = Config.ScreenMode == mode;
        var size = new Vector2(width, 34);
        var pos = ImGui.GetCursorScreenPos();
        var pressed = selected ? Ui.AccentButton($"##mode{mode}", size) : ImGui.Button($"##mode{mode}", size);

        // Icon and label centred together on the button.
        var dl = ImGui.GetWindowDrawList();
        var iconSize = ImGui.GetFontSize();
        var labelSize = ImGui.CalcTextSize(label);
        var total = iconSize + 8 + labelSize.X;
        var start = pos + new Vector2((width - total) / 2, 0);
        var color = selected ? new Vector4(0.067f, 0.067f, 0.086f, 1f) : Ui.Text;
        Ui.DrawIcon(dl, icon, start + new Vector2(iconSize / 2, size.Y / 2), iconSize, color);
        dl.AddText(start + new Vector2(iconSize + 8, (size.Y - labelSize.Y) / 2), Ui.Col(color), label);

        if (pressed && !selected)
            plugin.SetScreenMode(mode);
    }

    private void DrawUiScreen()
    {
        Ui.Card("uiscreen", width =>
        {
            var shown = Config.UiScreenShown;
            Ui.Pill(shown ? "● On your UI" : "Hidden", shown ? Ui.Good : Ui.TextDim);
            ImGui.TextWrapped("Drag the picture to move it and an edge to resize it. Hover it for play/pause, the seek bar and your volume.");

            // Showing a hidden picture is the thing to do here; hiding one is not, so only that gets the accent.
            var half = (width - 8) / 2;
            var label = shown ? "Hide screen" : "Show screen";
            if (shown ? ImGui.Button(label, new Vector2(half, 34)) : Ui.AccentButton(label, new Vector2(half, 34)))
            {
                Config.UiScreenShown = !shown;
                Config.Save();
            }

            ImGui.SameLine();
            if (ImGui.Button("Reset size and position", new Vector2(half, 34)))
            {
                Config.UiScreenShown = true;
                Config.Save();
                plugin.UiScreen.ResetPlacement();
            }

            var locked = Config.UiScreenLocked;
            if (ImGui.Checkbox("Lock in place", ref locked))
            {
                Config.UiScreenLocked = locked;
                Config.Save();
            }
        });
    }

    private void DrawPlacement()
    {
        Ui.Card("place", width =>
        {
            var here = plugin.Renderer.WillDraw;
            Ui.Pill(here ? "● On the wall here" : Config.HasAnchor && Config.ScreenTerritory == Territory ? "Hidden" : "Not placed in this zone",
                here ? Ui.Good : Ui.TextDim);

            ImGui.TextWrapped("Stand where you want to watch from, face the wall, and place the screen. Fine-tune below; save it as a spot when it looks right.");

            var half = (width - 8) / 2;
            if (Ui.AccentButton("Place in front of me", new Vector2(half, 34)))
                plugin.Placement.PlaceInFront(Plugin.ClientState, Plugin.ObjectTable, Config, House);
            ImGui.SameLine();
            if (ImGui.Button(here ? "Hide screen" : "Show screen", new Vector2(half, 34)))
                SetShown(!here);
        });
    }

    private void DrawSpots()
    {
        if (Territory == 0)
            return;

        Ui.Card("spots", width =>
        {
            ImGui.TextUnformatted("Saved spots");
            ImGui.SameLine();
            Ui.Dim("— lounge, bedroom, garden…");

            var territory = PlacementPresets.FindTerritory(Config, Territory);
            var presets = PlacementPresets.Candidates(territory, House);
            var active = PlacementPresets.Active(Config, Territory, House);
            var dirty = PlacementPresets.IsDirty(Config, Territory, House);
            var names = presets.Select(p => p.Name).ToArray();
            if (names.Length == 0)
            {
                Ui.Dim("Place the screen and it's saved as this house's first spot.");
            }
            else
            {
                var current = active == null ? -1 : Array.FindIndex(names, n => n.Equals(active.Name, StringComparison.OrdinalIgnoreCase));
                ImGui.SetNextItemWidth(width);
                if (ImGui.Combo("##spot", ref current, names, names.Length) && current >= 0)
                {
                    PlacementPresets.SelectPreset(Config, Territory, names[current], House);
                    Config.Save();
                }

                if (dirty && ImGui.IsItemHovered())
                    ImGui.SetTooltip("Switching spots drops your unsaved changes.");
            }

            // Nothing is saved until you say so: show what changed and the three ways out.
            if (dirty && active != null)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Ui.Warn);
                ImGui.TextUnformatted($"● The screen has moved since \"{active.Name}\" was saved.");
                ImGui.PopStyleColor();
                if (Ui.AccentButton($"Save to \"{active.Name}\""))
                {
                    PlacementPresets.SaveActivePreset(Config, Territory, House);
                    Config.Save();
                    Notify.Success($"Saved \"{active.Name}\"");
                }

                ImGui.SameLine();
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.UndoAlt, "Undo changes"))
                {
                    PlacementPresets.Revert(Config, Territory, House);
                    Config.Save();
                }
            }

            ImGui.SetNextItemWidth(width - 190);
            ImGui.InputTextWithHint("##newspot", "New spot name", ref newSpotName, 64);
            ImGui.SameLine();
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Plus, "Save as new spot") && PlacementPresets.CreatePreset(Config, Territory, newSpotName, House))
            {
                Notify.Success($"Saved \"{newSpotName.Trim()}\"");
                newSpotName = "";
                Config.Save();
            }

            if (active != null)
            {
                ImGui.SameLine();
                if (ImGuiComponents.IconButton("##delete", FontAwesomeIcon.TrashAlt))
                    ImGui.OpenPopup("delete-spot");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"Delete \"{active.Name}\"");
                if (Confirm("delete-spot", $"Delete the spot \"{active.Name}\"? This can't be undone.", "Delete"))
                {
                    PlacementPresets.DeletePreset(Config, Territory, active.Name, House);
                    Config.Save();
                }
            }

            if (House != 0)
                Ui.Dim($"Spots here belong to this house ({HouseLocator.Describe(House)}).");
        });
    }

    private void DrawFineTune()
    {
        Ui.Card("tune", width =>
        {
            ImGui.TextUnformatted("Fine-tune");
            ImGui.SameLine();
            Ui.Dim("— moves the screen live; saved spots only change when you save");
            ImGui.PushItemWidth(width - 110);

            var distance = Config.PlaceDistance;
            if (Slider("Distance", ref distance, PlacementLimits.MinDistance, PlacementLimits.MaxDistance, "%.2f yalms"))
            {
                Config.PlaceDistance = distance;
                ScreenPlacement.ApplyLive(Config);
            }

            var height = Config.PlaceHeight;
            if (Slider("Height", ref height, PlacementLimits.MinHeight, PlacementLimits.MaxHeight, "%.2f yalms"))
            {
                Config.PlaceHeight = height;
                ScreenPlacement.ApplyLive(Config);
            }

            var strafe = Config.PlaceStrafe;
            if (Slider("Left / right", ref strafe, PlacementLimits.MinStrafe, PlacementLimits.MaxStrafe, "%.2f yalms"))
            {
                Config.PlaceStrafe = strafe;
                ScreenPlacement.ApplyLive(Config);
            }

            var size = Config.ScreenWidth;
            if (Slider("Size", ref size, PlacementLimits.MinWidth, PlacementLimits.MaxWidth, "%.2f yalms wide"))
                Config.ScreenWidth = size;

            var yaw = Config.ScreenYaw * 180f / MathF.PI;
            if (Slider("Turn", ref yaw, -180f, 180f, "%.0f°"))
                Config.ScreenYaw = yaw * MathF.PI / 180f;

            var pitch = Config.ScreenPitch * 180f / MathF.PI;
            if (Slider("Tilt", ref pitch, -90f, 90f, "%.0f°"))
                Config.ScreenPitch = pitch * MathF.PI / 180f;

            ImGui.PopItemWidth();
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.UndoAlt, "Reset size and offsets"))
            {
                PlacementLimits.ResetToDefaults(Config);
                Config.Save();
            }

            var spot = PlacementPresets.Active(Config, Territory, House);
            if (spot != null)
            {
                ImGui.SameLine();
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Times, "Remove from this zone"))
                    ImGui.OpenPopup("remove-screen");
                if (Confirm("remove-screen", $"Take the screen down and delete the spot \"{spot.Name}\"? This can't be undone.", "Remove"))
                {
                    PlacementLimits.RemoveFromZone(Config, Territory, House);
                    Config.Save();
                }
            }
        });
    }

    private void DrawShare()
    {
        Ui.Card("share", width =>
        {
            var x0 = ImGui.GetCursorPosX();
            ImGui.TextUnformatted("Share the spot");
            Ui.Dim("Invites already include your spot. Use a code to send just the position.");
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Copy, "Copy spot code"))
            {
                ScreenPlacement.EnsureAnchor(Config);
                ImGui.SetClipboardText(ScreenShareCode.Export(Config, House));
                Config.Save();
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(MathF.Max(80, x0 + width - ImGui.GetCursorPosX() - 70));
            ImGui.InputTextWithHint("##share", "VIF1.…", ref shareInput, 256);
            ImGui.SameLine();
            if (ImGui.Button("Apply") && plugin.ApplyShareCode(shareInput))
                shareInput = "";
        });
    }

    private void SetShown(bool shown)
    {
        if (shown && !(Config.HasAnchor && Config.ScreenTerritory == Territory))
        {
            plugin.Placement.PlaceInFront(Plugin.ClientState, Plugin.ObjectTable, Config, House);
            return;
        }

        PlacementPresets.SetShown(Config, Territory, House, shown);
        Config.Save();
    }

    /// <summary>A small "are you sure" popup; true on the frame the user confirms.</summary>
    private static bool Confirm(string id, string message, string confirmLabel)
    {
        using var popup = Dalamud.Interface.Utility.Raii.ImRaii.Popup(id);
        if (!popup.Success)
            return false;

        ImGui.TextUnformatted(message);
        var confirmed = false;
        if (Ui.DangerButton(confirmLabel))
        {
            confirmed = true;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();
        return confirmed;
    }

    /// <summary>A slider that keeps the live placement (not the spot) when let go.</summary>
    private bool Slider(string label, ref float value, float min, float max, string format)
    {
        var changed = ImGui.SliderFloat(label, ref value, min, max, format);
        if (ImGui.IsItemDeactivatedAfterEdit())
            Config.Save();
        return changed;
    }
}
