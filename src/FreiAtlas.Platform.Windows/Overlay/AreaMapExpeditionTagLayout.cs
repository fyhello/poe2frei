using System.Drawing;
using System.Globalization;
using System.Numerics;
using FreiAtlas.Core.Area;

namespace FreiAtlas.Platform.Windows.Overlay;

internal sealed record AreaMapExpeditionTagLayout(
    string HoleText,
    string? ValueText,
    float FontSize,
    RectangleF PanelBounds,
    RectangleF HoleBounds,
    RectangleF? ValueBounds,
    RectangleF ClipBounds);

internal static class AreaMapExpeditionTagLayoutCalculator
{
    private const float MiniMapScale = 0.85f;
    private const float MinimumFontSize = 9f;
    private const float ReferenceFontSize = 13f;
    private const float ReferenceHoleWidth = 44f;
    private const float HorizontalPadding = 7f;
    private const float VerticalPadding = 7f;
    private const float ValueGap = 7f;
    private const float BorderWidth = 2f;

    internal static float ResolveFontSize(AreaMapViewKind kind, float configured)
        => kind == AreaMapViewKind.MiniMap
            ? MathF.Max(MinimumFontSize, configured * MiniMapScale)
            : configured;

    internal static string FormatHoleCount(int? holeCount)
        => holeCount is >= 1 and <= 16
            ? holeCount.Value.ToString(CultureInfo.InvariantCulture) + "孔"
            : "?孔";

    internal static AreaMapExpeditionTagLayout Create(
        RectangleF viewport,
        Vector2 center,
        string holeText,
        string? valueText,
        float fontSize,
        SizeF holeMeasurement,
        SizeF valueMeasurement)
    {
        if (viewport.Width <= BorderWidth || viewport.Height <= BorderWidth)
        {
            throw new ArgumentOutOfRangeException(nameof(viewport));
        }

        var scale = fontSize / ReferenceFontSize;
        var horizontalPadding = HorizontalPadding * scale;
        var verticalPadding = VerticalPadding * scale;
        var holeWidth = MathF.Max(
            ReferenceHoleWidth * scale,
            holeMeasurement.Width + horizontalPadding * 2f);
        var valueWidth = string.IsNullOrWhiteSpace(valueText)
            ? 0f
            : ValueGap * scale + valueMeasurement.Width + horizontalPadding;
        var borderInset = BorderWidth / 2f;
        var availableWidth = MathF.Max(0f, viewport.Width - BorderWidth);
        var availableHeight = MathF.Max(0f, viewport.Height - BorderWidth);
        var panelWidth = MathF.Min(holeWidth + valueWidth, availableWidth);
        var contentHeight = MathF.Max(holeMeasurement.Height, valueMeasurement.Height);
        var panelHeight = MathF.Min(
            contentHeight + verticalPadding * 2f,
            availableHeight);
        var ideal = new RectangleF(
            center.X - holeWidth / 2f,
            center.Y - panelHeight / 2f,
            panelWidth,
            panelHeight);
        var minimumLeft = viewport.Left + borderInset;
        var maximumLeft = MathF.Max(
            minimumLeft,
            viewport.Right - panelWidth - borderInset);
        var minimumTop = viewport.Top + borderInset;
        var maximumTop = MathF.Max(
            minimumTop,
            viewport.Bottom - panelHeight - borderInset);
        var left = Math.Clamp(ideal.Left, minimumLeft, maximumLeft);
        var top = Math.Clamp(ideal.Top, minimumTop, maximumTop);
        var panel = new RectangleF(left, top, panelWidth, panelHeight);
        var offsetX = panel.Left - ideal.Left;
        var hole = new RectangleF(
            center.X - holeWidth / 2f + offsetX,
            panel.Top,
            MathF.Min(holeWidth, panel.Width),
            panel.Height);
        RectangleF? value = valueWidth <= 0f
            ? null
            : new RectangleF(
                hole.Right + ValueGap * scale,
                panel.Top,
                MathF.Max(0f, panel.Right - hole.Right - ValueGap * scale),
                panel.Height);
        return new AreaMapExpeditionTagLayout(
            holeText,
            string.IsNullOrWhiteSpace(valueText) ? null : valueText,
            fontSize,
            panel,
            hole,
            value,
            viewport);
    }
}
