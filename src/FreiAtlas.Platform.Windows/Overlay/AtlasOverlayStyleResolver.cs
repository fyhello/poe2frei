using System.Globalization;
using FreiAtlas.Core.Settings;
using Vortice.Mathematics;

namespace FreiAtlas.Platform.Windows.Overlay;

[Flags]
internal enum AtlasOverlayResourceChanges
{
    None = 0,
    EdgeBrushes = 1 << 0,
    HighlightBrush = 1 << 1,
    TextBrush = 1 << 2,
    PanelBrush = 1 << 3,
    TextFormat = 1 << 4,
    All = EdgeBrushes | HighlightBrush | TextBrush | PanelBrush | TextFormat
}

internal readonly record struct AtlasOverlayStyle(
    Color4 ReachableEdgeColor,
    Color4 LockedEdgeColor,
    Color4 HighlightColor,
    Color4 TextColor,
    Color4 PanelColor,
    Color4 NavigationPanelColor,
    float EdgeWidth,
    float HighlightWidth,
    float FontSize,
    bool ShowLabelBackground);

internal static class AtlasOverlayStyleResolver
{
    public static AtlasOverlayStyle Resolve(AtlasDisplaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new AtlasOverlayStyle(
            ToColor4(settings.Edges.ReachableColor, settings.Edges.Opacity),
            ToColor4(settings.Edges.LockedColor, settings.Edges.Opacity),
            ToColor4(settings.Highlight.Color, settings.Highlight.Opacity),
            ToColor4(settings.Labels.TextColor, 1f),
            ToColor4(
                settings.Labels.BackgroundColor,
                settings.Labels.BackgroundOpacity),
            ToColor4(
                settings.Labels.BackgroundColor,
                MathF.Max(settings.Labels.BackgroundOpacity, 0.9f)),
            settings.Edges.Width,
            settings.Highlight.Width,
            settings.Labels.FontSize,
            settings.Labels.ShowBackground);
    }

    public static Color4 ToColor4(string rgbHex, float opacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rgbHex);
        if (rgbHex.Length != 7 || rgbHex[0] != '#')
        {
            throw new ArgumentException(
                "Color must use #RRGGBB format.",
                nameof(rgbHex));
        }

        if (!float.IsFinite(opacity) || opacity < 0f || opacity > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(opacity));
        }

        try
        {
            return new Color4(
                ParseByte(rgbHex.AsSpan(1, 2)) / 255f,
                ParseByte(rgbHex.AsSpan(3, 2)) / 255f,
                ParseByte(rgbHex.AsSpan(5, 2)) / 255f,
                opacity);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "Color must use #RRGGBB format.",
                nameof(rgbHex),
                exception);
        }
    }

    public static AtlasOverlayResourceChanges GetResourceChanges(
        AtlasOverlayStyle? previous,
        AtlasOverlayStyle current)
    {
        if (previous is not { } old)
        {
            return AtlasOverlayResourceChanges.All;
        }

        var changes = AtlasOverlayResourceChanges.None;
        if (!old.ReachableEdgeColor.Equals(current.ReachableEdgeColor)
            || !old.LockedEdgeColor.Equals(current.LockedEdgeColor))
        {
            changes |= AtlasOverlayResourceChanges.EdgeBrushes;
        }

        if (!old.HighlightColor.Equals(current.HighlightColor))
        {
            changes |= AtlasOverlayResourceChanges.HighlightBrush;
        }

        if (!old.TextColor.Equals(current.TextColor))
        {
            changes |= AtlasOverlayResourceChanges.TextBrush;
        }

        if (!old.PanelColor.Equals(current.PanelColor)
            || !old.NavigationPanelColor.Equals(current.NavigationPanelColor))
        {
            changes |= AtlasOverlayResourceChanges.PanelBrush;
        }

        if (old.FontSize != current.FontSize)
        {
            changes |= AtlasOverlayResourceChanges.TextFormat;
        }

        return changes;
    }

    private static byte ParseByte(ReadOnlySpan<char> value)
        => byte.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
