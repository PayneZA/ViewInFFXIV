namespace ViewInFFXIV.World;

/// <summary>
/// Saved screen spots, per zone and per house. Houses of the same size in the same district share a zone, so a
/// spot remembers the house it was made in (<see cref="PlacementPreset.HouseId"/>) and only shows up there;
/// spots from before that (house 0) work in any house of the zone. Every function takes the house the player
/// is in (0 outside houses) from the caller, because during a zone change the game already reports the new one.
/// </summary>
public static class PlacementPresets
{
    public const int MaxPresetsPerTerritory = 24;
    public const string DefaultPresetName = "Default";

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

    /// <summary>The spots that apply in this house: its own, plus ones not tied to a house.</summary>
    public static List<PlacementPreset> Candidates(TerritoryPresets? territory, ulong house) =>
        territory?.Presets.Where(p => Fits(p, house)).ToList() ?? [];

    /// <summary>The spot in use here, if any.</summary>
    public static PlacementPreset? Active(Configuration config, uint territoryId, ulong house)
    {
        var territory = FindTerritory(config, territoryId);
        var preset = territory == null ? null : FindPreset(territory, territory.ActivePresetName);
        return preset != null && Fits(preset, house) ? preset : null;
    }

    /// <summary>Entering a zone or house: show the spot you last used here (or none).</summary>
    public static void LoadActivePreset(Configuration config, uint territoryId, ulong house)
    {
        config.ScreenTerritory = territoryId;
        var territory = FindTerritory(config, territoryId);
        var candidates = Candidates(territory, house);
        // A spot made in this very house wins over older spots that are not tied to a house.
        var preset = candidates.Where(p => p.HouseId != 0).MaxBy(p => p.LastUsed)
            ?? Active(config, territoryId, house)
            ?? candidates.MaxBy(p => p.LastUsed);
        if (territory == null || preset == null)
        {
            ClearLivePlacement(config, territoryId);
            return;
        }

        Activate(territory, preset);
        ApplyPresetToConfig(config, territoryId, preset);
    }

    /// <summary>Writes the live placement into the spot in use here, creating one if there is none.</summary>
    public static void SaveActivePreset(Configuration config, uint territoryId, ulong house)
    {
        if (territoryId == 0)
            return;

        config.ScreenTerritory = territoryId;
        var territory = GetOrCreateTerritory(config, territoryId);
        var preset = Active(config, territoryId, house);
        if (preset == null)
        {
            if (territory.Presets.Count >= MaxPresetsPerTerritory)
                return;

            preset = new PlacementPreset { Name = UniqueName(territory, DefaultPresetName), HouseId = house };
            territory.Presets.Add(preset);
        }

        Activate(territory, preset);
        CopyConfigToPreset(config, preset);
    }

    /// <summary>Placing the screen by hand in a house ties the spot in use to that house.</summary>
    public static void BindActive(Configuration config, uint territoryId, ulong house)
    {
        if (house != 0 && Active(config, territoryId, house) is { HouseId: 0 } preset)
            preset.HouseId = house;
    }

    public static bool SelectPreset(Configuration config, uint territoryId, string name, ulong house)
    {
        if (territoryId == 0 || string.IsNullOrWhiteSpace(name))
            return false;

        var territory = FindTerritory(config, territoryId);
        var preset = territory != null ? FindPreset(territory, name) : null;
        if (territory == null || preset == null || !Fits(preset, house))
            return false;

        SaveActivePreset(config, territoryId, house);
        Activate(territory, preset);
        ApplyPresetToConfig(config, territoryId, preset);
        return true;
    }

    public static bool CreatePreset(Configuration config, uint territoryId, string name, ulong house)
    {
        if (territoryId == 0)
            return false;

        name = name.Trim();
        if (string.IsNullOrEmpty(name))
            return false;

        SaveActivePreset(config, territoryId, house);
        var territory = GetOrCreateTerritory(config, territoryId);
        if (FindPreset(territory, name) != null || territory.Presets.Count >= MaxPresetsPerTerritory)
            return false;

        var preset = new PlacementPreset { Name = name, HouseId = house };
        CopyConfigToPreset(config, preset);
        territory.Presets.Add(preset);
        Activate(territory, preset);
        return true;
    }

    public static bool DeletePreset(Configuration config, uint territoryId, string name, ulong house)
    {
        var territory = FindTerritory(config, territoryId);
        var preset = territory == null || string.IsNullOrWhiteSpace(name) ? null : FindPreset(territory, name);
        if (territory == null || preset == null)
            return false;

        territory.Presets.Remove(preset);
        if (territory.Presets.Count == 0)
            config.SavedPlacements.Remove(territory);

        // Show the next spot of this house, or nothing.
        if (territory.ActivePresetName.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            territory.ActivePresetName = "";
            LoadActivePreset(config, territoryId, house);
        }

        return true;
    }

    public static void OnTerritoryChanged(Configuration config, uint previousTerritoryId, ulong previousHouse, uint newTerritoryId, ulong newHouse)
    {
        if (previousTerritoryId != 0 && (previousTerritoryId != newTerritoryId || previousHouse != newHouse) && config.ScreenTerritory == previousTerritoryId && config.HasAnchor)
            SaveActivePreset(config, previousTerritoryId, previousHouse);

        if (newTerritoryId == 0)
        {
            ClearLivePlacement(config, 0);
            return;
        }

        LoadActivePreset(config, newTerritoryId, newHouse);
    }

    public static void RemoveActivePreset(Configuration config, uint territoryId, ulong house)
    {
        if (Active(config, territoryId, house) is { } preset)
            DeletePreset(config, territoryId, preset.Name, house);
        else
            ClearLivePlacement(config, territoryId);
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

    /// <summary>
    /// Stores a shared placement (decoded into <paramref name="shared"/>) as a spot of its zone, tied to the
    /// house it came from. It becomes the active spot, and the screen moves, only when you are in that same house
    /// (or the code has no house). Your own spots are never overwritten. Returns false when the zone is full.
    /// </summary>
    public static bool ImportShared(Configuration config, Configuration shared, ulong sharedHouse, uint currentTerritory, ulong currentHouse)
    {
        var territoryId = shared.ScreenTerritory;
        if (territoryId == 0)
            return false;

        var here = currentTerritory == territoryId && (sharedHouse == 0 || sharedHouse == currentHouse);
        if (here)
            SaveActivePreset(config, territoryId, currentHouse);

        var territory = GetOrCreateTerritory(config, territoryId);
        var name = sharedHouse == 0 ? "Imported" : $"Party spot ({HouseLocator.Describe(sharedHouse)})";
        var preset = (sharedHouse != 0 ? territory.Presets.Find(p => p.HouseId == sharedHouse && p.Name.StartsWith("Party spot", StringComparison.Ordinal)) : null)
            ?? FindPreset(territory, name);
        if (preset == null)
        {
            if (territory.Presets.Count >= MaxPresetsPerTerritory)
                return false;
            preset = new PlacementPreset { Name = UniqueName(territory, name) };
            territory.Presets.Add(preset);
        }

        CopyConfigToPreset(shared, preset);
        preset.HouseId = sharedHouse;
        if (here)
        {
            Activate(territory, preset);
            ApplyPresetToConfig(config, territoryId, preset);
        }
        else if (sharedHouse != 0)
        {
            // Picked automatically when you walk into that house.
            preset.LastUsed = DateTime.UtcNow.Ticks;
        }

        return true;
    }

    private static bool Fits(PlacementPreset preset, ulong house) => preset.HouseId == 0 || preset.HouseId == house;

    private static void Activate(TerritoryPresets territory, PlacementPreset preset)
    {
        territory.ActivePresetName = preset.Name;
        preset.LastUsed = DateTime.UtcNow.Ticks;
    }

    private static string UniqueName(TerritoryPresets territory, string name)
    {
        var candidate = name;
        for (var i = 2; FindPreset(territory, candidate) != null; i++)
            candidate = $"{name} {i}";
        return candidate;
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
