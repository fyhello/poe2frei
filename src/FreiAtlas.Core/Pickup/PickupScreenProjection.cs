using System.Numerics;

namespace FreiAtlas.Core.Pickup;

/// <summary>Projects ground-item world positions through the game's camera matrix.</summary>
public static class PickupScreenProjection
{
    private static readonly (int X, int Y)[] LocalOffsets =
    [
        (0, 0),
        (0, -18),
        (0, -36),
        (0, -54),
        (-18, -18),
        (18, -18),
        (-18, 0),
        (18, 0)
    ];

    public static bool TryProject(
        ReadOnlySpan<float> matrix,
        Vector3 worldPosition,
        Vector2 viewportSize,
        out Vector2 clientPosition)
    {
        clientPosition = default;
        if (matrix.Length < 16
            || !IsFinite(worldPosition)
            || !IsFinite(viewportSize)
            || viewportSize.X <= 0f
            || viewportSize.Y <= 0f)
            return false;

        for (var index = 0; index < 16; index++)
            if (!float.IsFinite(matrix[index])) return false;

        var clipX = worldPosition.X * matrix[0] + worldPosition.Y * matrix[4]
                    + worldPosition.Z * matrix[8] + matrix[12];
        var clipY = worldPosition.X * matrix[1] + worldPosition.Y * matrix[5]
                    + worldPosition.Z * matrix[9] + matrix[13];
        var clipW = worldPosition.X * matrix[3] + worldPosition.Y * matrix[7]
                    + worldPosition.Z * matrix[11] + matrix[15];
        if (!float.IsFinite(clipX) || !float.IsFinite(clipY)
            || !float.IsFinite(clipW) || clipW <= 0.001f)
            return false;

        var x = (clipX / clipW * 0.5f + 0.5f) * viewportSize.X;
        var y = (0.5f - clipY / clipW * 0.5f) * viewportSize.Y;
        if (!float.IsFinite(x) || !float.IsFinite(y)
            || x < 0f || y < 0f || x >= viewportSize.X || y >= viewportSize.Y)
            return false;

        clientPosition = new Vector2(x, y);
        return true;
    }

    public static IReadOnlyList<Vector2> CreateLocalCandidates(
        Vector2 projectedPosition,
        Vector2 viewportSize)
    {
        if (!IsFinite(projectedPosition) || !IsFinite(viewportSize)
            || viewportSize.X <= 0f || viewportSize.Y <= 0f
            || projectedPosition.X < 0f || projectedPosition.Y < 0f
            || projectedPosition.X >= viewportSize.X || projectedPosition.Y >= viewportSize.Y)
            return [];

        var points = new List<Vector2>(LocalOffsets.Length);
        var seen = new HashSet<(int X, int Y)>();
        foreach (var (offsetX, offsetY) in LocalOffsets)
        {
            var x = (int)MathF.Round(projectedPosition.X) + offsetX;
            var y = (int)MathF.Round(projectedPosition.Y) + offsetY;
            if (x < 0 || y < 0 || x >= viewportSize.X || y >= viewportSize.Y || !seen.Add((x, y)))
                continue;
            points.Add(new Vector2(x, y));
        }

        return points;
    }

    private static bool IsFinite(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
