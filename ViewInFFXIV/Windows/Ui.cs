using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace ViewInFFXIV.Windows;

/// <summary>ViewInFFXIV's look: colours, window style and the custom widgets the windows are built from.</summary>
internal static class Ui
{
    public static readonly Vector4 Accent = Rgb(0x9B7BFF);
    public static readonly Vector4 AccentHover = Rgb(0xB39CFF);
    public static readonly Vector4 AccentActive = Rgb(0x8262F0);
    public static readonly Vector4 Background = Rgb(0x141419, 0.97f);
    public static readonly Vector4 Surface = Rgb(0x1D1D25);
    public static readonly Vector4 Surface2 = Rgb(0x272731);
    public static readonly Vector4 Surface3 = Rgb(0x32323E);
    public static readonly Vector4 Surface4 = Rgb(0x474758);
    public static readonly Vector4 Text = Rgb(0xEEEEF3);
    public static readonly Vector4 TextDim = Rgb(0x9C9CB0);
    public static readonly Vector4 Live = Rgb(0xFF4D6A);
    public static readonly Vector4 Good = Rgb(0x4ADE80);
    public static readonly Vector4 Warn = Rgb(0xFBBF24);

    private const int StyleVars = 11;
    private const int StyleColors = 29;

    /// <summary>Pushes the ViewInFFXIV window style (pair with <see cref="PopStyle"/>).</summary>
    public static void PushStyle()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 12f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 10f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 7f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 10f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 7f);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 7f);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14, 12));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10, 6));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 8));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

        ImGui.PushStyleColor(ImGuiCol.WindowBg, Background);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Surface);
        ImGui.PushStyleColor(ImGuiCol.Border, Surface3);
        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, TextDim);
        ImGui.PushStyleColor(ImGuiCol.TitleBg, Background);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, Surface);
        ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed, Background);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Surface2);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Surface3);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Surface3);
        ImGui.PushStyleColor(ImGuiCol.Button, Surface2);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Surface3);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccentActive);
        ImGui.PushStyleColor(ImGuiCol.Header, Surface2);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Surface3);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, Surface3);
        ImGui.PushStyleColor(ImGuiCol.Tab, Surface);
        ImGui.PushStyleColor(ImGuiCol.TabHovered, Surface3);
        ImGui.PushStyleColor(ImGuiCol.TabActive, Surface2);
        ImGui.PushStyleColor(ImGuiCol.TabUnfocused, Surface);
        ImGui.PushStyleColor(ImGuiCol.TabUnfocusedActive, Surface2);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, Accent);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, AccentHover);
        ImGui.PushStyleColor(ImGuiCol.CheckMark, Accent);
        ImGui.PushStyleColor(ImGuiCol.Separator, Surface3);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.TextSelectedBg, Accent with { W = 0.35f });
    }

    public static void PopStyle()
    {
        ImGui.PopStyleColor(StyleColors);
        ImGui.PopStyleVar(StyleVars);
    }

    public static uint Col(Vector4 color) => ImGui.GetColorU32(color);

    /// <summary>A rounded panel behind <paramref name="body"/>, which gets the inner width. Cards cannot nest.</summary>
    public static void Card(string id, Action<float> body, float padding = 12f, Vector4? color = null)
    {
        ImGui.PushID(id);
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var inner = width - (2 * padding);

        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);
        ImGui.SetCursorScreenPos(start + new Vector2(padding, padding));
        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + inner);
        body(inner);
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();
        var end = new Vector2(start.X + width, ImGui.GetItemRectMax().Y + padding);

        dl.ChannelsSetCurrent(0);
        dl.AddRectFilled(start, end, Col(color ?? Surface), 10f);
        dl.ChannelsMerge();

        ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y + ImGui.GetStyle().ItemSpacing.Y));
        ImGui.Dummy(Vector2.Zero);
        ImGui.PopID();
    }

    /// <summary>A small rounded label, e.g. "● LIVE".</summary>
    public static void Pill(string text, Vector4 color, bool filled = false)
    {
        var size = ImGui.CalcTextSize(text) + new Vector2(16, 6);
        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + size, Col(filled ? color : color with { W = 0.16f }), size.Y / 2);
        dl.AddText(pos + new Vector2(8, 3), Col(filled ? Rgb(0x111116) : color), text);
        ImGui.Dummy(size);
    }

    public static bool AccentButton(string label, Vector2 size = default)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Accent);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AccentHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccentActive);
        ImGui.PushStyleColor(ImGuiCol.Text, Rgb(0x111116));
        var pressed = ImGui.Button(label, size);
        ImGui.PopStyleColor(4);
        return pressed;
    }

    /// <summary>A circular button with a FontAwesome icon (transport controls).</summary>
    public static bool RoundIconButton(string id, FontAwesomeIcon icon, float diameter, bool accent = false, string? tooltip = null, bool enabled = true)
    {
        var pos = ImGui.GetCursorScreenPos();
        var pressed = ImGui.InvisibleButton(id, new Vector2(diameter, diameter)) && enabled;
        var hovered = enabled && ImGui.IsItemHovered();
        var active = enabled && ImGui.IsItemActive();
        if (hovered && tooltip != null)
            ImGui.SetTooltip(tooltip);

        var center = pos + new Vector2(diameter / 2);
        var fill = accent
            ? active ? AccentActive : hovered ? AccentHover : Accent
            : active ? Surface3 : hovered ? Surface3 : Surface2;
        if (!enabled)
            fill = fill with { W = 0.4f };
        var dl = ImGui.GetWindowDrawList();
        dl.AddCircleFilled(center, diameter / 2, Col(fill), 32);
        DrawIcon(dl, icon, center, diameter * 0.4f, accent ? Rgb(0x111116) : enabled ? Text : TextDim, icon == FontAwesomeIcon.Play ? 0.08f : 0f);
        return pressed;
    }

    /// <summary>Draws an icon centred on <paramref name="center"/> at the given pixel size.</summary>
    public static void DrawIcon(ImDrawListPtr dl, FontAwesomeIcon icon, Vector2 center, float size, Vector4 color, float nudgeX = 0f)
    {
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            var text = icon.ToIconString();
            var font = ImGui.GetFont();
            var scale = size / ImGui.GetFontSize();
            var textSize = ImGui.CalcTextSize(text) * scale;
            dl.AddText(font, size, center - (textSize / 2) + new Vector2(nudgeX * size, 0), Col(color), text);
        }
    }

    /// <summary>
    /// A thin seek bar. Shows <paramref name="fraction"/>; while dragged, <paramref name="drag"/> holds the target.
    /// Returns true on the frame the user lets go (seek to <paramref name="drag"/>'s last value).
    /// </summary>
    public static bool SeekBar(string id, float fraction, float width, ref float? drag, out float? hover)
    {
        const float height = 18f;
        var pos = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton(id, new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        var mouse = Math.Clamp((ImGui.GetIO().MousePos.X - pos.X) / width, 0f, 1f);
        hover = hovered || active ? mouse : null;

        var released = false;
        if (active)
            drag = mouse;
        else if (drag != null)
            released = true;

        var shown = Math.Clamp(drag ?? fraction, 0f, 1f);
        var thick = hovered || active ? 6f : 4f;
        var y = pos.Y + (height / 2);
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(new Vector2(pos.X, y - (thick / 2)), new Vector2(pos.X + width, y + (thick / 2)), Col(Surface3), thick);
        if (hovered && !active)
            dl.AddRectFilled(new Vector2(pos.X, y - (thick / 2)), new Vector2(pos.X + (width * mouse), y + (thick / 2)), Col(Surface4), thick);
        dl.AddRectFilled(new Vector2(pos.X, y - (thick / 2)), new Vector2(pos.X + (width * shown), y + (thick / 2)), Col(Accent), thick);
        if (hovered || active)
            dl.AddCircleFilled(new Vector2(pos.X + (width * shown), y), 7f, Col(Text), 20);
        return released;
    }

    public static void Spinner(float radius, Vector4? color = null)
    {
        var pos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(radius * 2));
        var center = pos + new Vector2(radius);
        var t = (float)ImGui.GetTime() * 5f;
        var dl = ImGui.GetWindowDrawList();
        dl.PathArcTo(center, radius - 2, t, t + (MathF.PI * 1.4f), 28);
        dl.PathStroke(Col(color ?? Accent), ImDrawFlags.None, 3f);
    }

    /// <summary>An image (or a dark placeholder with an icon) with rounded corners.</summary>
    public static void RoundedImage(ImTextureID? texture, Vector2 size, float rounding, FontAwesomeIcon placeholder = FontAwesomeIcon.Film)
    {
        var pos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(size);
        var dl = ImGui.GetWindowDrawList();
        if (texture is { } image)
        {
            dl.AddImageRounded(image, pos, pos + size, Vector2.Zero, Vector2.One, Col(Vector4.One), rounding);
        }
        else
        {
            dl.AddRectFilled(pos, pos + size, Col(Surface2), rounding);
            DrawIcon(dl, placeholder, pos + (size / 2), MathF.Min(size.Y * 0.3f, 40f), TextDim);
        }
    }

    /// <summary>Shortens text with "…" to fit a width.</summary>
    public static string Fit(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width)
            return text;
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (ImGui.CalcTextSize(text[..mid] + "…").X <= width)
                lo = mid;
            else
                hi = mid - 1;
        }

        return text[..lo].TrimEnd() + "…";
    }

    /// <summary>Text centred in the available width.</summary>
    public static void Centered(string text, Vector4? color = null)
    {
        var width = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (ImGui.GetContentRegionAvail().X - width) / 2));
        if (color is { } c)
            ImGui.TextColored(c, text);
        else
            ImGui.TextUnformatted(text);
    }

    public static void Dim(string text) => ImGui.TextColored(TextDim, text);

    private static Vector4 Rgb(uint rgb, float alpha = 1f) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);
}
