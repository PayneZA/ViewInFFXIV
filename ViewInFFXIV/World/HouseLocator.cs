using FFXIVClientStructs.FFXIV.Client.Game;

namespace ViewInFFXIV.World;

/// <summary>Which house (or apartment, or FC room) you are inside: world, ward, plot and room in one number.</summary>
public static class HouseLocator
{
    /// <summary>The current house id, or 0 when not inside a house.</summary>
    public static unsafe ulong Current()
    {
        var housing = HousingManager.Instance();
        if (housing == null || !housing->IsInside())
            return 0;

        var id = housing->GetCurrentIndoorHouseId().Id;
        return id == ulong.MaxValue ? 0 : id;
    }

    /// <summary>"W12 P34", "W12 apartment 7", for spot names.</summary>
    public static string Describe(ulong id)
    {
        var house = new HouseId { Id = id };
        var ward = house.WardIndex + 1;
        return house.IsApartment
            ? $"W{ward} apartment {house.RoomNumber}"
            : house.RoomNumber > 0 ? $"W{ward} P{house.PlotIndex + 1} room {house.RoomNumber}" : $"W{ward} P{house.PlotIndex + 1}";
    }
}
