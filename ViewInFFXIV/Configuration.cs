using System.Numerics;
using Dalamud.Configuration;

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
    public int Version { get; set; } = 3;

    public bool AutoStartHost { get; set; }

    public string RoomUrl { get; set; } = "https://watchtogether.watch/";

    public float Volume { get; set; } = 1f;

    public bool HideChrome { get; set; } = true;

    public string CaptureSource { get; set; } = "webview";

    public string CaptureProcess { get; set; } = "";

    public string CaptureTitle { get; set; } = "";

    public List<TerritoryPresets> SavedPlacements { get; set; } = [];

    public bool ScreenEnabled { get; set; }

    public bool KeepScreenWhenUiHidden { get; set; }

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

    public Vector3 ScreenPosition => new(ScreenX, ScreenY, ScreenZ);

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
