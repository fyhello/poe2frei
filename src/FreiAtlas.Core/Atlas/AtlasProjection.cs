using System.Numerics;

namespace FreiAtlas.Core.Atlas;

public readonly record struct AtlasViewport(float Width, float Height)
{
    public bool IsValid
        => float.IsFinite(Width)
           && float.IsFinite(Height)
           && Width > 0f
           && Height > 0f;
}

public readonly record struct AtlasProjection(
    float ScaleX,
    float ScaleY,
    float OffsetX,
    float OffsetY,
    float UiScale = 1f,
    float AtlasZoom = 1f)
{
    public static AtlasProjection Identity => new(1f, 1f, 0f, 0f);

    public static AtlasProjection FromCanvas(
        AtlasViewport viewport,
        Vector2 canvasRelativePosition,
        float canvasScale,
        float atlasZoom,
        Vector2 nodeSize)
    {
        var uiScale = viewport.IsValid
            ? viewport.Height / 1600f
            : 1f;
        var safeCanvasScale = IsPositiveFinite(canvasScale) ? canvasScale : 1f;
        var safeAtlasZoom = IsPositiveFinite(atlasZoom) ? atlasZoom : 1f;
        // Atlas node RelativePos already includes the live pan. When the canvas and
        // node report the same scale, the canvas value is the node scale repeated,
        // not another projection multiplier.
        var effectiveCanvasScale = MathF.Abs(safeCanvasScale - safeAtlasZoom) <= 0.01f
            ? 1f
            : safeCanvasScale;
        var factor = uiScale * effectiveCanvasScale * safeAtlasZoom;

        return new AtlasProjection(
            factor,
            factor,
            0f,
            0f,
            uiScale,
            safeAtlasZoom);
    }

    public Vector2 Project(Vector2 relative)
    {
        return new Vector2(
            relative.X * ScaleX + OffsetX,
            relative.Y * ScaleY + OffsetY);
    }

    public bool TryProject(Vector2 relative, out Vector2 screen)
    {
        if (!float.IsFinite(relative.X) || !float.IsFinite(relative.Y))
        {
            screen = default;
            return false;
        }

        screen = Project(relative);
        return float.IsFinite(screen.X) && float.IsFinite(screen.Y);
    }

    private static bool IsPositiveFinite(float value)
        => float.IsFinite(value) && value > 0f;

    private static bool IsFinite(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y);
}
