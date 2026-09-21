using System.Numerics;

namespace FreiAtlas.Core.Atlas;

public static class AtlasLivePositionFilter
{
    private const float SevereDeltaLimit = 2_000f;

    public static bool TryStabilize(
        IReadOnlyDictionary<AtlasGridPos, Vector2> previous,
        IReadOnlyDictionary<AtlasGridPos, Vector2> current,
        out IReadOnlyDictionary<AtlasGridPos, Vector2> stabilized,
        float tolerance = 2f,
        float minimumInlierRatio = 0.75f)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var deltas = current
            .Where(pair => previous.ContainsKey(pair.Key))
            .Select(pair => pair.Value - previous[pair.Key])
            .Where(delta => float.IsFinite(delta.X) && float.IsFinite(delta.Y))
            .ToArray();

        if (deltas.Length < 4)
        {
            stabilized = new Dictionary<AtlasGridPos, Vector2>(current);
            return true;
        }

        var medianX = Median(deltas.Select(delta => delta.X));
        var medianY = Median(deltas.Select(delta => delta.Y));
        var inlierCount = deltas.Count(
            delta => MathF.Abs(delta.X - medianX) <= tolerance
                     && MathF.Abs(delta.Y - medianY) <= tolerance);
        var requiredInliers = Math.Max(
            3,
            (int)MathF.Ceiling(deltas.Length * minimumInlierRatio));
        if (inlierCount < requiredInliers)
        {
            stabilized = new Dictionary<AtlasGridPos, Vector2>(current);
            return false;
        }

        var result = new Dictionary<AtlasGridPos, Vector2>(current.Count);
        foreach (var (grid, position) in current)
        {
            if (previous.TryGetValue(grid, out var oldPosition))
            {
                var delta = position - oldPosition;
                if (MathF.Abs(delta.X - medianX) > tolerance
                    || MathF.Abs(delta.Y - medianY) > tolerance)
                {
                    // Only repair an implausibly large stale read. A real atlas pan can
                    // affect a small on-screen subset while culled nodes remain unchanged;
                    // replacing every non-median delta makes that valid movement disappear.
                    if (MathF.Abs(delta.X) > SevereDeltaLimit
                        || MathF.Abs(delta.Y) > SevereDeltaLimit)
                    {
                        result[grid] = oldPosition + new Vector2(medianX, medianY);
                        continue;
                    }
                }
            }

            result[grid] = position;
        }

        stabilized = result;
        return true;
    }

    private static float Median(IEnumerable<float> values)
    {
        var ordered = values.Order().ToArray();
        return ordered[ordered.Length / 2];
    }
}
