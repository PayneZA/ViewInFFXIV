using System.Numerics;
using Dalamud.Configuration;
using ViewInFFXIV.Media;

namespace ViewInFFXIV;

[Serializable]
public sealed class PlacementPreset
{
    public string Name { get; set; } = "";

    public bool Enabled { get; set; }

    public float ScreenX { get; set; }

    public float ScreenY { get; set; }

    public float ScreenZ { get; set; }

    public float ScreenYaw { get; set; }

    public float ScreenPitch { get; set; }

    public float ScreenWidth { get; set; } = 3.2f;

    public float PlaceDistance { get; set; } = 4.5f;

    public float PlaceHeight { get; set; } = 1.35f;

    public float PlaceStrafe { get; set; }

    public bool HasAnchor { get; set; }

    public float AnchorX { get; set; }

    public float AnchorY { get; set; }

    public float AnchorZ { get; set; }

    public float AnchorYaw { get; set; }

    /// <summary>The house this spot belongs to (see <see cref="World.HouseLocator"/>); 0 = any house of the zone.</summary>
    public ulong HouseId { get; set; }

    /// <summary>When the spot was last shown (UTC ticks), to bring back the one you used last in a house.</summary>
    public long LastUsed { get; set; }
}

[Serializable]
public sealed class TerritoryPresets
{
    public uint TerritoryId { get; set; }

    public string ActivePresetName { get; set; } = "";

    public List<PlacementPreset> Presets { get; set; } = [];
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 5;

    public List<TerritoryPresets> SavedPlacements { get; set; } = [];

    public bool ScreenEnabled { get; set; }

    public bool KeepScreenWhenUiHidden { get; set; }

    /// <summary>Draw the screen in the game's own UI layer so chat bubbles, name plates and the HUD stay on top.</summary>
    public bool DrawBehindGameUi { get; set; } = true;

    public uint ScreenTerritory { get; set; }

    public float ScreenX { get; set; }

    public float ScreenY { get; set; }

    public float ScreenZ { get; set; }

    public float ScreenYaw { get; set; }

    public float ScreenPitch { get; set; }

    public float ScreenWidth { get; set; } = 3.2f;

    public float PlaceDistance { get; set; } = 4.5f;

    public float PlaceHeight { get; set; } = 1.35f;

    public float PlaceStrafe { get; set; }

    public bool HasAnchor { get; set; }

    public float AnchorX { get; set; }

    public float AnchorY { get; set; }

    public float AnchorZ { get; set; }

    public float AnchorYaw { get; set; }

    /// <summary>English subtitles wanted (applied to every video instead of YouTube's default).</summary>
    public bool SubtitlesOn { get; set; }

    /// <summary>YouTube volume, 0-100 (yours only, not synced to the room).</summary>
    public int Volume { get; set; } = 100;

    public string LastRoomUrl { get; set; } = "";

    /// <summary>Your "Up next" list; the first one starts when the room's video ends.</summary>
    public List<VideoInfo> UpNext { get; set; } = [];

    /// <summary>Recently watched, newest first.</summary>
    public List<VideoInfo> History { get; set; } = [];

    public bool AutoAdvance { get; set; } = true;

    /// <summary>Show play/pause/seek on the TV when you point at it.</summary>
    public bool ScreenControls { get; set; } = true;

    /// <summary>Sound comes from the TV: fades with distance, pans with the camera, silent outside its zone.</summary>
    public bool SpatialAudio { get; set; } = true;

    /// <summary>Yalms past the screen over which the sound fades out.</summary>
    public float HearingRange { get; set; } = 20f;

    public Vector3 ScreenPosition => new(ScreenX, ScreenY, ScreenZ);

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
