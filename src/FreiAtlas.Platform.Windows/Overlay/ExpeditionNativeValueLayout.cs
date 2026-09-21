using System.Drawing;
using FreiAtlas.Core.Area;

namespace FreiAtlas.Platform.Windows.Overlay;

internal readonly record struct ExpeditionNativeValueLayout(
    RectangleF TextBounds,
    RectangleF ClipBounds,
    float FontSize);

internal static class ExpeditionNativeValueLayoutCalculator
{
    public static float FontSize(float rowHeight)
        => float.IsFinite(rowHeight)
            ? Math.Clamp(rowHeight * 0.48f, 9f, 24f)
            : 9f;

    public static float UniformFontSize(IEnumerable<AreaUiRect> rowBounds)
    {
        ArgumentNullException.ThrowIfNull(rowBounds);
        var minimumHeight = float.PositiveInfinity;
        foreach (var row in rowBounds)
        {
            if (row is not null
                && float.IsFinite(row.Height)
                && row.Height > 0f)
            {
                minimumHeight = Math.Min(minimumHeight, row.Height);
            }
        }

        return FontSize(minimumHeight);
    }

    public static ExpeditionNativeValueLayout? Create(
        AreaUiRect rowBounds,
        AreaUiRect listClipBounds,
        Size clientSize,
        float measuredTextWidth,
        float? sharedFontSize = null)
    {
        ArgumentNullException.ThrowIfNull(rowBounds);
        ArgumentNullException.ThrowIfNull(listClipBounds);
        if (!IsValid(rowBounds)
            || !IsValid(listClipBounds)
            || clientSize.Width <= 0
            || clientSize.Height <= 0
            || !float.IsFinite(measuredTextWidth)
            || measuredTextWidth < 0f
            || sharedFontSize is { } suppliedFontSize
                && (!float.IsFinite(suppliedFontSize) || suppliedFontSize <= 0f))
        {
            return null;
        }

        var row = ToRectangle(rowBounds);
        var list = ToRectangle(listClipBounds);
        var client = new RectangleF(0f, 0f, clientSize.Width, clientSize.Height);
        var visibleRow = RectangleF.Intersect(RectangleF.Intersect(row, list), client);
        if (visibleRow.Width <= 0f || visibleRow.Height <= 0f)
        {
            return null;
        }

        var fontSize = sharedFontSize ?? FontSize(rowBounds.Height);
        var horizontalGap = fontSize * 1.50f;
        var minimumWidth = fontSize * 2.80f;
        var desiredWidth = Math.Max(
            minimumWidth,
            measuredTextWidth + fontSize * 0.50f);
        var left = rowBounds.X + rowBounds.Width + horizontalGap;
        var valueWidth = Math.Min(
            Math.Min(desiredWidth, rowBounds.Width * 0.32f),
            client.Right - left);
        if (valueWidth <= 0f)
        {
            return null;
        }
        var textBounds = new RectangleF(
            left,
            rowBounds.Y,
            valueWidth,
            rowBounds.Height);
        var clip = new RectangleF(
            client.Left,
            visibleRow.Top,
            client.Width,
            visibleRow.Height);

        return new ExpeditionNativeValueLayout(textBounds, clip, fontSize);
    }

    private static bool IsValid(AreaUiRect bounds)
        => float.IsFinite(bounds.X)
            && float.IsFinite(bounds.Y)
            && float.IsFinite(bounds.Width)
            && float.IsFinite(bounds.Height)
            && bounds.Width > 0f
            && bounds.Height > 0f;

    private static RectangleF ToRectangle(AreaUiRect bounds)
        => new(bounds.X, bounds.Y, bounds.Width, bounds.Height);
}
