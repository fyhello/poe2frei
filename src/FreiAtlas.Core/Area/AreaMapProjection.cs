using System.Numerics;

namespace FreiAtlas.Core.Area;

public readonly record struct AreaMapLinearTransform(
    float M11,
    float M12,
    float M21,
    float M22)
{
    public static AreaMapLinearTransform Identity { get; } = new(1f, 0f, 0f, 1f);

    public Vector2 Transform(Vector2 value)
        => new(
            M11 * value.X + M12 * value.Y,
            M21 * value.X + M22 * value.Y);
}

public sealed record AreaMapProjectionParameters(
    AreaMapViewKind Kind,
    AreaMapLinearTransform Target,
    AreaMapLinearTransform Shift,
    Vector2 Bias);

public static class AreaMapProjection
{
    public static bool TryProject(
        Vector2 playerGrid,
        Vector2 targetGrid,
        AreaMapViewSnapshot view,
        AreaMapProjectionParameters parameters,
        out Vector2 clientPoint)
    {
        clientPoint = Vector2.Zero;
        if (view.Kind != parameters.Kind
            || !IsFinite(playerGrid)
            || !IsFinite(targetGrid)
            || !IsFinite(view.Shift)
            || !IsFinite(parameters.Bias)
            || !IsFinite(parameters.Target)
            || !IsFinite(parameters.Shift)
            || !float.IsFinite(view.Zoom)
            || view.Zoom <= 0f
            || view.Viewport is not { } viewport
            || !IsValid(viewport))
        {
            return false;
        }

        var anchor = new Vector2(
            viewport.X + viewport.Width / 2f,
            viewport.Y + viewport.Height / 2f)
            + parameters.Bias
            + parameters.Shift.Transform(view.Shift);
        var distanceScale = view.Zoom * viewport.Height;
        var targetDelta = parameters.Target.Transform(targetGrid - playerGrid);
        var projected = anchor + targetDelta * distanceScale;
        if (!IsFinite(anchor)
            || !float.IsFinite(distanceScale)
            || !IsFinite(targetDelta)
            || !IsFinite(projected))
        {
            return false;
        }

        clientPoint = projected;
        return true;
    }

    private static bool IsValid(AreaUiRect viewport)
        => float.IsFinite(viewport.X)
           && float.IsFinite(viewport.Y)
           && float.IsFinite(viewport.Width)
           && float.IsFinite(viewport.Height)
           && viewport.Width > 0f
           && viewport.Height > 0f;

    private static bool IsFinite(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool IsFinite(AreaMapLinearTransform value)
        => float.IsFinite(value.M11)
           && float.IsFinite(value.M12)
           && float.IsFinite(value.M21)
           && float.IsFinite(value.M22);
}
