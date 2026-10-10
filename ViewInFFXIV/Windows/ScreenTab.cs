using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using ViewInFFXIV.World;

namespace ViewInFFXIV.Windows;

/// <summary>The Screen tab: put the TV on your wall, fine-tune it, keep spots per room and share them.</summary>
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
        DrawPlacement();
        DrawSpots();
        DrawFineTune();
        DrawShare();
    }

    private void DrawPlacement()
    {
        Ui.Card("place", width =>
        {
            var here = plugin.Renderer.WillDraw;
            Ui.Pill(here ? "● On the wall here" : Config.HasAnchor && Config.ScreenTerritory == Territory ? "Hidden" : "Not placed in this zone",
                here ? Ui.Good : Ui.TextDim);

            ImGui.TextWrapped("Stand where you want to watch from, face the wall, and place the screen. Sliders below move it live.");

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
            var names = presets.Select(p => p.Name).ToArray();
            if (names.Length == 0)
            {
                Ui.Dim("Place the screen, then save the spot to switch between rooms later.");
            }
            else
            {
                var current = active == null ? -1 : Array.FindIndex(names, n => n.Equals(active.Name, StringComparison.OrdinalIgnoreCase));
                ImGui.SetNextItemWidth(width);
                if (ImGui.Combo("##spot", ref current, names, names.Length) && current >= 0)
                {
                    PlacementPresets.SelectPreset(Config, Territory, names[current], House);
                    PlacementPresets.BindActive(Config, Territory, House);
                    Config.Save();
                }
            }

            if (House != 0)
                Ui.Dim($"Spots here belong to this house ({HouseLocator.Describe(House)}).");

            ImGui.SetNextItemWidth(width - 190);
            ImGui.InputTextWithHint("##newspot", "New spot name", ref newSpotName, 64);
            ImGui.SameLine();
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Plus, "Save as new") && PlacementPresets.CreatePreset(Config, Territory, newSpotName, House))
            {
                newSpotName = "";
                Config.Save();
            }

            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.TrashAlt) && active != null)
            {
                PlacementPresets.DeletePreset(Config, Territory, active.Name, House);
                Config.Save();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Delete this spot");
        });
    }

    private void DrawFineTune()
    {
        Ui.Card("tune", width =>
        {
            ImGui.TextUnformatted("Fine-tune");
            ImGui.PushItemWidth(width - 110);

            var distance = Config.PlaceDistance;
            if (Slider("Distance", ref distance, PlacementLimits.MinDistance, PlacementLimits.MaxDistance, "%.2f y"))
            {
                Config.PlaceDistance = distance;
                ScreenPlacement.ApplyLive(Config);
            }

            var height = Config.PlaceHeight;
            if (Slider("Height", ref height, PlacementLimits.MinHeight, PlacementLimits.MaxHeight, "%.2f y"))
            {
                Config.PlaceHeight = height;
                ScreenPlacement.ApplyLive(Config);
            }

            var strafe = Config.PlaceStrafe;
            if (Slider("Left / right", ref strafe, PlacementLimits.MinStrafe, PlacementLimits.MaxStrafe, "%.2f y"))
            {
                Config.PlaceStrafe = strafe;
                ScreenPlacement.ApplyLive(Config);
            }

            var size = Config.ScreenWidth;
            if (Slider("Size", ref size, PlacementLimits.MinWidth, PlacementLimits.MaxWidth, "%.2f y wide"))
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
                SyncSpot();
                Config.Save();
            }

            ImGui.SameLine();
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Times, "Remove from this zone"))
            {
                PlacementLimits.RemoveFromZone(Config, Territory, House);
                Config.Save();
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

        Config.ScreenEnabled = shown;
        SyncSpot();
        Config.Save();
    }

    private void SyncSpot()
    {
        if (Territory != 0)
            PlacementPresets.SaveActivePreset(Config, Territory, House);
    }

    /// <summary>A slider that saves the spot when let go.</summary>
    private bool Slider(string label, ref float value, float min, float max, string format)
    {
        var changed = ImGui.SliderFloat(label, ref value, min, max, format);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            SyncSpot();
            Config.Save();
        }

        return changed;
    }
}
