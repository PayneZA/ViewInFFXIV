namespace ViewInFFXIV.World;

public static class PlacementPresets
{
    public const int MaxPresetsPerTerritory = 12;
    public const string DefaultPresetName = "Default";
    public const string ImportedPresetName = "Imported";

    public static TerritoryPresets? FindTerritory(Configuration config, uint territoryId) =>
        config.SavedPlacements.Find(t => t.TerritoryId == territoryId);

    public static TerritoryPresets GetOrCreateTerritory(Configuration config, uint territoryId)
    {
        var existing = FindTerritory(config, territoryId);
        if (existing != null)
            return existing;

        existing = new TerritoryPresets { TerritoryId = territoryId };
        config.SavedPlacements.Add(existing);
        return existing;
    }

    public static PlacementPreset? FindPreset(TerritoryPresets territory, string name) =>
        territory.Presets.Find(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static void LoadActivePreset(Configuration config, uint territoryId)
    {
        config.ScreenTerritory = territoryId;
        var territory = FindTerritory(config, territoryId);
        if (territory == null || string.IsNullOrWhiteSpace(territory.ActivePresetName))
        {
            ClearLivePlacement(config, territoryId);
            return;
        }

        var preset = FindPreset(territory, territory.ActivePresetName);
        if (preset == null)
        {
            ClearLivePlacement(config, territoryId);
            return;
        }

        ApplyPresetToConfig(config, territoryId, preset);
    }

    public static void SaveActivePreset(Configuration config, uint territoryId)
    {
        if (territoryId == 0)
            return;

        config.ScreenTerritory = territoryId;
        var territory = GetOrCreateTerritory(config, territoryId);
        var name = string.IsNullOrWhiteSpace(territory.ActivePresetName)
            ? DefaultPresetName
            : territory.ActivePresetName;

        var preset = FindPreset(territory, name);
        if (preset == null)
        {
            if (territory.Presets.Count >= MaxPresetsPerTerritory)
                return;

            preset = new PlacementPreset { Name = name };
            territory.Presets.Add(preset);
        }

        territory.ActivePresetName = preset.Name;
        CopyConfigToPreset(config, preset);
    }

    public static bool SelectPreset(Configuration config, uint territoryId, string name)
    {
        if (territoryId == 0 || string.IsNullOrWhiteSpace(name))
            return false;

        var territory = FindTerritory(config, territoryId);
        var preset = territory != null ? FindPreset(territory, name) : null;
        if (territory == null || preset == null)
            return false;

        SaveActivePreset(config, territoryId);
        territory.ActivePresetName = preset.Name;
        ApplyPresetToConfig(config, territoryId, preset);
        return true;
    }

    public static bool CreatePreset(Configuration config, uint territoryId, string name)
    {
        if (territoryId == 0)
            return false;

        name = name.Trim();
        if (string.IsNullOrEmpty(name))
            return false;

        SaveActivePreset(config, territoryId);
        var territory = GetOrCreateTerritory(config, territoryId);
        if (FindPreset(territory, name) != null)
            return false;
        if (territory.Presets.Count >= MaxPresetsPerTerritory)
            return false;

        var preset = new PlacementPreset { Name = name };
        CopyConfigToPreset(config, preset);
        territory.Presets.Add(preset);
        territory.ActivePresetName = preset.Name;
        return true;
    }

    public static bool DeletePreset(Configuration config, uint territoryId, string name)
    {
        var territory = FindTerritory(config, territoryId);
        if (territory == null || string.IsNullOrWhiteSpace(name))
            return false;

        var preset = FindPreset(territory, name);
        if (preset == null)
            return false;

        territory.Presets.Remove(preset);
        if (territory.Presets.Count == 0)
        {
            config.SavedPlacements.Remove(territory);
            ClearLivePlacement(config, territoryId);
            return true;
        }

        if (territory.ActivePresetName.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            var next = territory.Presets[0];
            territory.ActivePresetName = next.Name;
            ApplyPresetToConfig(config, territoryId, next);
        }

        return true;
    }

    public static void OnTerritoryChanged(Configuration config, uint previousTerritoryId, uint newTerritoryId)
    {
        if (previousTerritoryId != 0 && previousTerritoryId != newTerritoryId)
            SaveActivePreset(config, previousTerritoryId);

        if (newTerritoryId == 0)
        {
            ClearLivePlacement(config, 0);
            return;
        }

        LoadActivePreset(config, newTerritoryId);
    }

    public static void RemoveActivePreset(Configuration config, uint territoryId)
    {
        if (territoryId == 0)
        {
            ClearLivePlacement(config, 0);
            return;
        }

        var territory = FindTerritory(config, territoryId);
        if (territory == null || string.IsNullOrWhiteSpace(territory.ActivePresetName))
        {
            ClearLivePlacement(config, territoryId);
            return;
        }

        DeletePreset(config, territoryId, territory.ActivePresetName);
    }

    public static void MigrateFromV2(Configuration config)
    {
        if (config.Version >= 3)
            return;

        if (config.ScreenTerritory == 0 || !config.HasAnchor)
            return;

        var territory = GetOrCreateTerritory(config, config.ScreenTerritory);
        if (territory.Presets.Count > 0)
            return;

        var preset = new PlacementPreset { Name = DefaultPresetName };
        CopyConfigToPreset(config, preset);
        territory.Presets.Add(preset);
        territory.ActivePresetName = preset.Name;
    }

    public static void ApplyImportedShareCode(Configuration config)
    {
        if (config.ScreenTerritory == 0)
            return;

        var territory = GetOrCreateTerritory(config, config.ScreenTerritory);
        var preset = !string.IsNullOrWhiteSpace(territory.ActivePresetName)
            ? FindPreset(territory, territory.ActivePresetName)
            : null;

        if (preset == null)
        {
            preset = FindPreset(territory, ImportedPresetName);
            if (preset == null && territory.Presets.Count >= MaxPresetsPerTerritory)
            {
                SaveActivePreset(config, config.ScreenTerritory);
                return;
            }

            if (preset == null)
            {
                preset = new PlacementPreset { Name = ImportedPresetName };
                territory.Presets.Add(preset);
            }
        }

        territory.ActivePresetName = preset.Name;
        CopyConfigToPreset(config, preset);
    }

    private static void ClearLivePlacement(Configuration config, uint territoryId)
    {
        config.ScreenTerritory = territoryId;
        config.ScreenEnabled = false;
        config.HasAnchor = false;
        config.ScreenX = 0f;
        config.ScreenY = 0f;
        config.ScreenZ = 0f;
        config.AnchorX = 0f;
        config.AnchorY = 0f;
        config.AnchorZ = 0f;
        config.AnchorYaw = 0f;
    }

    private static void ApplyPresetToConfig(Configuration config, uint territoryId, PlacementPreset preset)
    {
        config.ScreenTerritory = territoryId;
        config.ScreenEnabled = preset.Enabled;
        config.ScreenX = preset.ScreenX;
        config.ScreenY = preset.ScreenY;
        config.ScreenZ = preset.ScreenZ;
        config.ScreenYaw = preset.ScreenYaw;
        config.ScreenPitch = preset.ScreenPitch;
        config.ScreenWidth = preset.ScreenWidth;
        config.PlaceDistance = preset.PlaceDistance;
        config.PlaceHeight = preset.PlaceHeight;
        config.PlaceStrafe = preset.PlaceStrafe;
        config.HasAnchor = preset.HasAnchor;
        config.AnchorX = preset.AnchorX;
        config.AnchorY = preset.AnchorY;
        config.AnchorZ = preset.AnchorZ;
        config.AnchorYaw = preset.AnchorYaw;

        if (config.HasAnchor)
            ScreenPlacement.ApplyLive(config);
    }

    private static void CopyConfigToPreset(Configuration config, PlacementPreset preset)
    {
        ScreenPlacement.EnsureAnchor(config);
        preset.Enabled = config.ScreenEnabled;
        preset.ScreenX = config.ScreenX;
        preset.ScreenY = config.ScreenY;
        preset.ScreenZ = config.ScreenZ;
        preset.ScreenYaw = config.ScreenYaw;
        preset.ScreenPitch = config.ScreenPitch;
        preset.ScreenWidth = config.ScreenWidth;
        preset.PlaceDistance = config.PlaceDistance;
        preset.PlaceHeight = config.PlaceHeight;
        preset.PlaceStrafe = config.PlaceStrafe;
        preset.HasAnchor = config.HasAnchor;
        preset.AnchorX = config.AnchorX;
        preset.AnchorY = config.AnchorY;
        preset.AnchorZ = config.AnchorZ;
        preset.AnchorYaw = config.AnchorYaw;
    }
}
