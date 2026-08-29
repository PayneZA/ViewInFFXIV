using System.Text;

namespace ViewInFFXIV.World;

public static class ScreenShareCode
{
    public const string Prefix = "VIF1.";
    private const string LegacyPrefix = "IV1.";
    private const byte Version = 1;

    public static string Export(Configuration config)
    {
        ScreenPlacement.EnsureAnchor(config);
        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Version);
            writer.Write(config.ScreenTerritory);
            writer.Write(config.ScreenX);
            writer.Write(config.ScreenY);
            writer.Write(config.ScreenZ);
            writer.Write(config.ScreenYaw);
            writer.Write(config.ScreenPitch);
            writer.Write(config.ScreenWidth);
            writer.Write(config.PlaceDistance);
            writer.Write(config.PlaceHeight);
            writer.Write(config.PlaceStrafe);
            writer.Write(config.HasAnchor);
            writer.Write(config.AnchorX);
            writer.Write(config.AnchorY);
            writer.Write(config.AnchorZ);
            writer.Write(config.AnchorYaw);
        }

        return Prefix + ToBase64Url(ms.ToArray());
    }

    public static bool TryImport(string raw, Configuration config, out string error)
    {
        error = "";
        if (!TryExtractBytes(raw, out var bytes))
        {
            error = "Not a ViewInFFXIV share code.";
            return false;
        }

        try
        {
            using var reader = new BinaryReader(new MemoryStream(bytes));
            var version = reader.ReadByte();
            if (version != Version)
            {
                error = $"Unsupported share code version ({version}).";
                return false;
            }

            config.ScreenTerritory = reader.ReadUInt32();
            config.ScreenX = reader.ReadSingle();
            config.ScreenY = reader.ReadSingle();
            config.ScreenZ = reader.ReadSingle();
            config.ScreenYaw = WrapAngle(reader.ReadSingle());
            config.ScreenPitch = Math.Clamp(reader.ReadSingle(), -MathF.PI / 2f, MathF.PI / 2f);
            config.ScreenWidth = Math.Clamp(reader.ReadSingle(), PlacementLimits.MinWidth, PlacementLimits.MaxWidth);
            config.PlaceDistance = Math.Clamp(reader.ReadSingle(), PlacementLimits.MinDistance, PlacementLimits.MaxDistance);
            config.PlaceHeight = Math.Clamp(reader.ReadSingle(), PlacementLimits.MinHeight, PlacementLimits.MaxHeight);
            config.PlaceStrafe = Math.Clamp(reader.ReadSingle(), PlacementLimits.MinStrafe, PlacementLimits.MaxStrafe);
            config.HasAnchor = reader.ReadBoolean();
            config.AnchorX = reader.ReadSingle();
            config.AnchorY = reader.ReadSingle();
            config.AnchorZ = reader.ReadSingle();
            config.AnchorYaw = WrapAngle(reader.ReadSingle());
            config.ScreenEnabled = true;
            if (config.HasAnchor)
                ScreenPlacement.ApplyLive(config);
            else
                ScreenPlacement.EnsureAnchor(config);
            return true;
        }
        catch
        {
            error = "Share code is truncated or invalid.";
            return false;
        }
    }

    private static bool TryExtractBytes(string raw, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var vifStart = raw.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
        var legacyStart = raw.IndexOf(LegacyPrefix, StringComparison.OrdinalIgnoreCase);
        var start = vifStart >= 0 ? vifStart : legacyStart;
        if (start < 0)
            return false;

        var tokenPrefix = vifStart >= 0 && (legacyStart < 0 || vifStart <= legacyStart)
            ? Prefix
            : LegacyPrefix;

        var end = start + tokenPrefix.Length;
        while (end < raw.Length && !char.IsWhiteSpace(raw[end]))
            end++;
        var token = raw[start..end];

        if (token.Length <= tokenPrefix.Length)
            return false;

        try
        {
            bytes = FromBase64Url(token[tokenPrefix.Length..]);
            return bytes.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string ToBase64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string payload)
    {
        var s = payload.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2:
                s += "==";
                break;
            case 3:
                s += "=";
                break;
        }

        return Convert.FromBase64String(s);
    }

    private static float WrapAngle(float radians)
    {
        while (radians > MathF.PI)
            radians -= MathF.Tau;
        while (radians < -MathF.PI)
            radians += MathF.Tau;
        return radians;
    }
}
