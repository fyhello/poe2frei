using System.Drawing;
using System.Numerics;

namespace FreiAtlas.Core.Atlas;

public static class AtlasLabelPanelLayout
{
    public const float DefaultFontSize = 13f;
    public const float AnchorOffsetX = 5f;
    public const float AnchorOffsetY = 7f;
    public const float LabelOffsetY = 8f;
    public const float LabelHeight = 20f;

    public static Vector2 AdjustAnchor(Vector2 anchor)
        => new(anchor.X + AnchorOffsetX, anchor.Y + AnchorOffsetY);

    public static float GetWidth(string text)
        => GetWidth(text, DefaultFontSize);

    public static float GetWidth(string text, float fontSize)
    {
        ArgumentNullException.ThrowIfNull(text);
        ValidateFontSize(fontSize);

        var scale = fontSize / DefaultFontSize;
        return Math.Clamp(
            (text.Length * 14f + 12f) * scale,
            48f,
            220f * Math.Max(1f, scale));
    }

    public static float GetHeight(float fontSize)
    {
        ValidateFontSize(fontSize);
        return Math.Max(
            LabelHeight,
            MathF.Ceiling(fontSize * 1.2f + 4f));
    }

    public static float GetTop(float adjustedAnchorY)
        => adjustedAnchorY + LabelOffsetY;

    public static RectangleF GetPanelBounds(AtlasLabelPlacement label)
        => GetPanelBounds(label, DefaultFontSize);

    public static RectangleF GetPanelBounds(
        AtlasLabelPlacement label,
        float fontSize)
    {
        var width = GetWidth(label.Text, fontSize);
        var anchor = AdjustAnchor(label.Anchor);
        return new RectangleF(
            anchor.X - (width / 2f),
            GetTop(anchor.Y),
            width,
            GetHeight(fontSize));
    }

    private static void ValidateFontSize(float fontSize)
    {
        if (!float.IsFinite(fontSize) || fontSize <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        }
    }
}
