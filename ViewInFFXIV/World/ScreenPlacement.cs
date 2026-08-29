using System.Numerics;
using Dalamud.Plugin.Services;

namespace ViewInFFXIV.World;

public static class PlacementLimits
{
    public const float MinDistance = 0.5f;
    public const float MaxDistance = 30f;
    public const float DefaultDistance = 4.5f;

    public const float MinHeight = 0f;
    public const float MaxHeight = 20f;
    public const float DefaultHeight = 1.35f;

    public const float MinStrafe = -25f;
    public const float MaxStrafe = 25f;

    public const float MinWidth = 0.5f;
    public const float MaxWidth = 20f;
    public const float DefaultWidth = 3.2f;

    public static void Clamp(Configuration config)
    {
        config.PlaceDistance = Math.Clamp(config.PlaceDistance, MinDistance, MaxDistance);
        config.PlaceHeight = Math.Clamp(config.PlaceHeight, MinHeight, MaxHeight);
        config.PlaceStrafe = Math.Clamp(config.PlaceStrafe, MinStrafe, MaxStrafe);
        config.ScreenWidth = Math.Clamp(config.ScreenWidth, MinWidth, MaxWidth);
        config.ScreenPitch = Math.Clamp(config.ScreenPitch, -MathF.PI / 2f, MathF.PI / 2f);
    }

    public static void ResetToDefaults(Configuration config)
    {
        config.PlaceDistance = DefaultDistance;
        config.PlaceHeight = DefaultHeight;
        config.PlaceStrafe = 0f;
        config.ScreenWidth = DefaultWidth;
        config.ScreenPitch = 0f;
        if (config.HasAnchor)
            config.ScreenYaw = config.AnchorYaw;
        if (config.HasAnchor)
            ScreenPlacement.ApplyLive(config);
    }

    public static void RemoveFromZone(Configuration config, uint territoryId)
    {
        PlacementPresets.RemoveActivePreset(config, territoryId);
    }
}

public sealed class ScreenPlacement
{
    public void PlaceInFront(IClientState client, IObjectTable objects, Configuration config)
    {
        var player = objects.LocalPlayer;
        if (player == null)
            return;

        config.AnchorX = player.Position.X;
        config.AnchorY = player.Position.Y;
        config.AnchorZ = player.Position.Z;
        config.AnchorYaw = player.Rotation;
        config.HasAnchor = true;
        config.ScreenYaw = player.Rotation;
        config.ScreenTerritory = client.TerritoryType;
        config.ScreenEnabled = true;
        ApplyLive(config);
        PlacementPresets.SaveActivePreset(config, client.TerritoryType);
        config.Save();
    }

    public static void EnsureAnchor(Configuration config)
    {
        if (config.HasAnchor || !config.ScreenEnabled)
            return;

        var forward = Forward(config.ScreenYaw);
        var origin = config.ScreenPosition
            - (forward * config.PlaceDistance)
            - (Vector3.UnitY * config.PlaceHeight);
        config.AnchorX = origin.X;
        config.AnchorY = origin.Y;
        config.AnchorZ = origin.Z;
        config.AnchorYaw = config.ScreenYaw;
        config.HasAnchor = true;
    }

    public static void ApplyLive(Configuration config)
    {
        if (!config.HasAnchor)
            return;

        var forward = Forward(config.AnchorYaw);
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, -forward));
        if (right.LengthSquared() < 0.01f)
            right = Vector3.UnitX;

        var pos = new Vector3(config.AnchorX, config.AnchorY, config.AnchorZ)
            + (forward * config.PlaceDistance)
            + (right * config.PlaceStrafe)
            + (Vector3.UnitY * config.PlaceHeight);
        config.ScreenX = pos.X;
        config.ScreenY = pos.Y;
        config.ScreenZ = pos.Z;
    }

    public static bool TryGetCorners(Configuration config, out Vector3 center, out Vector3 right, out Vector3 down)
    {
        center = config.ScreenPosition;
        var width = MathF.Max(0.5f, config.ScreenWidth);
        var height = width * 9f / 16f;
        var forward = Forward(config.ScreenYaw);
        var rightDir = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, -forward));
        if (rightDir.LengthSquared() < 0.01f)
            rightDir = Vector3.UnitX;

        var tilt = Quaternion.CreateFromAxisAngle(rightDir, config.ScreenPitch);
        var downDir = Vector3.Normalize(Vector3.Transform(-Vector3.UnitY, tilt));
        right = rightDir * width;
        down = downDir * height;
        return true;
    }

    public static Vector3 Forward(float yaw) => new(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
}
