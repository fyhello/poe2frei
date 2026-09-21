using System.Drawing;
using System.Numerics;
using FreiAtlas.Core.Contracts;

namespace FreiAtlas.Core.Atlas;

public readonly record struct AtlasDirectionPlacement(
    AtlasGridPos Grid,
    string Text,
    Vector2 ArrowTip,
    Vector2 UnitDirection,
    RectangleF LabelBounds);

public static class AtlasNavigationLayout
{
    public static IReadOnlyList<AtlasEdgePlacement> BuildRouteEdges(
        AtlasSnapshot snapshot,
        AtlasNavigationPlan plan,
        AtlasLiveRenderGeometry geometry,
        AtlasViewport viewport,
        float margin = 100f)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(plan);
        if (!viewport.IsValid
            || !geometry.IsStable
            || geometry.NodePositions is null
            || !float.IsFinite(margin)
            || margin < 0f)
        {
            return Array.Empty<AtlasEdgePlacement>();
        }

        var edgeByKey = snapshot.Edges
            .GroupBy(edge => AtlasNavigationEdgeKey.Create(edge.From, edge.To))
            .ToDictionary(group => group.Key, group => group.First());
        var bounds = new RectangleF(
            -margin,
            -margin,
            viewport.Width + (margin * 2f),
            viewport.Height + (margin * 2f));
        var placements = new List<AtlasEdgePlacement>(plan.RouteEdges.Count);
        foreach (var key in plan.RouteEdges
                     .OrderBy(edge => edge.First.X)
                     .ThenBy(edge => edge.First.Y)
                     .ThenBy(edge => edge.Second.X)
                     .ThenBy(edge => edge.Second.Y))
        {
            if (!edgeByKey.TryGetValue(key, out var edge)
                || !geometry.NodePositions.TryGetValue(edge.From, out var from)
                || !geometry.NodePositions.TryGetValue(edge.To, out var to)
                || !geometry.Projection.TryProject(from, out var start)
                || !geometry.Projection.TryProject(to, out var end)
                || !IsInside(start, bounds)
                || !IsInside(end, bounds))
            {
                continue;
            }

            placements.Add(new AtlasEdgePlacement(
                edge.From,
                edge.To,
                start,
                end,
                edge.RenderColor));
        }

        return placements;
    }

    public static IReadOnlyList<AtlasDirectionPlacement> BuildDirections(
        AtlasNavigationPlan plan,
        AtlasLiveRenderGeometry geometry,
        AtlasViewport viewport,
        float inset = 46f,
        float minimumSpacing = 28f)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!viewport.IsValid
            || !geometry.IsStable
            || geometry.NodePositions is null
            || !float.IsFinite(inset)
            || inset < 0f
            || !float.IsFinite(minimumSpacing)
            || minimumSpacing < 0f
            || viewport.Width <= inset * 2f
            || viewport.Height <= inset * 2f)
        {
            return Array.Empty<AtlasDirectionPlacement>();
        }

        var candidates = new List<DirectionCandidate>();
        var center = new Vector2(viewport.Width / 2f, viewport.Height / 2f);
        foreach (var target in plan.DirectionTargets)
        {
            if (!geometry.NodePositions.TryGetValue(target.Grid, out var position)
                || !geometry.Projection.TryProject(position, out var projected)
                || IsInside(projected, viewport))
            {
                continue;
            }

            var delta = projected - center;
            if (!float.IsFinite(delta.X)
                || !float.IsFinite(delta.Y)
                || delta.LengthSquared() <= float.Epsilon)
            {
                continue;
            }

            var direction = Vector2.Normalize(delta);
            if (!TryIntersectInsetBounds(
                    center,
                    direction,
                    viewport,
                    inset,
                    out var tip,
                    out var edge))
            {
                continue;
            }

            candidates.Add(new DirectionCandidate(target, tip, direction, edge));
        }

        var adjusted = new List<DirectionCandidate>(candidates.Count);
        foreach (var group in candidates.GroupBy(candidate => candidate.Edge))
        {
            var vertical = group.Key is DirectionEdge.Left or DirectionEdge.Right;
            var minimum = inset;
            var maximum = (vertical ? viewport.Height : viewport.Width) - inset;
            var ordered = group
                .OrderBy(candidate => vertical ? candidate.Tip.Y : candidate.Tip.X)
                .ThenBy(candidate => candidate.Target.Grid.X)
                .ThenBy(candidate => candidate.Target.Grid.Y)
                .ToArray();
            var coordinates = Spread(
                ordered.Select(candidate => vertical ? candidate.Tip.Y : candidate.Tip.X).ToArray(),
                minimum,
                maximum,
                minimumSpacing);
            for (var index = 0; index < ordered.Length; index++)
            {
                var tip = vertical
                    ? ordered[index].Tip with { Y = coordinates[index] }
                    : ordered[index].Tip with { X = coordinates[index] };
                adjusted.Add(ordered[index] with { Tip = tip });
            }
        }

        var labeled = adjusted
            .Select(candidate => new LabeledDirectionCandidate(
                candidate,
                CreateLabelBounds(candidate, viewport)))
            .ToArray();
        foreach (var group in labeled.GroupBy(candidate => candidate.Candidate.Edge))
        {
            PackLabelBounds(group.ToArray(), group.Key, viewport);
        }

        return labeled
            .OrderBy(candidate => candidate.Candidate.Target.Grid.X)
            .ThenBy(candidate => candidate.Candidate.Target.Grid.Y)
            .Select(candidate => new AtlasDirectionPlacement(
                candidate.Candidate.Target.Grid,
                candidate.Candidate.Target.DisplayName,
                candidate.Candidate.Tip,
                candidate.Candidate.Direction,
                candidate.LabelBounds))
            .ToArray();
    }

    private static void PackLabelBounds(
        IReadOnlyList<LabeledDirectionCandidate> candidates,
        DirectionEdge edge,
        AtlasViewport viewport)
    {
        const float gap = 4f;
        var vertical = edge is DirectionEdge.Left or DirectionEdge.Right;
        var ordered = candidates
            .OrderBy(candidate => vertical
                ? candidate.LabelBounds.Top
                : candidate.LabelBounds.Left)
            .ThenBy(candidate => candidate.Candidate.Target.Grid.X)
            .ThenBy(candidate => candidate.Candidate.Target.Grid.Y)
            .ToArray();
        var starts = ordered
            .Select(candidate => vertical
                ? candidate.LabelBounds.Top
                : candidate.LabelBounds.Left)
            .ToArray();
        var sizes = ordered
            .Select(candidate => vertical
                ? candidate.LabelBounds.Height
                : candidate.LabelBounds.Width)
            .ToArray();
        var packed = PackStarts(
            starts,
            sizes,
            vertical ? viewport.Height : viewport.Width,
            gap);
        for (var index = 0; index < ordered.Length; index++)
        {
            var bounds = ordered[index].LabelBounds;
            var maximum = Math.Max(
                0f,
                (vertical ? viewport.Height : viewport.Width) - sizes[index]);
            var packedStart = Math.Clamp(packed[index], 0f, maximum);
            ordered[index].LabelBounds = vertical
                ? bounds with { Y = packedStart }
                : bounds with { X = packedStart };
        }
    }

    private static float[] PackStarts(
        IReadOnlyList<float> starts,
        IReadOnlyList<float> sizes,
        float extent,
        float gap)
    {
        var result = new float[starts.Count];
        if (result.Length == 0)
        {
            return result;
        }

        result[0] = Math.Clamp(starts[0], 0f, Math.Max(0f, extent - sizes[0]));
        for (var index = 1; index < result.Length; index++)
        {
            result[index] = Math.Max(
                Math.Clamp(starts[index], 0f, Math.Max(0f, extent - sizes[index])),
                result[index - 1] + sizes[index - 1] + gap);
        }

        if (result[^1] + sizes[^1] > extent)
        {
            result[^1] = Math.Max(0f, extent - sizes[^1]);
            for (var index = result.Length - 2; index >= 0; index--)
            {
                result[index] = Math.Min(
                    result[index],
                    result[index + 1] - sizes[index] - gap);
            }
        }

        return result;
    }

    private static RectangleF CreateLabelBounds(
        DirectionCandidate candidate,
        AtlasViewport viewport)
    {
        var width = AtlasLabelPanelLayout.GetWidth(candidate.Target.DisplayName);
        var height = AtlasLabelPanelLayout.GetHeight(
            AtlasLabelPanelLayout.DefaultFontSize);
        const float gap = 12f;
        var left = candidate.Edge switch
        {
            DirectionEdge.Left => candidate.Tip.X + gap,
            DirectionEdge.Right => candidate.Tip.X - gap - width,
            _ => candidate.Tip.X - (width / 2f)
        };
        var top = candidate.Edge switch
        {
            DirectionEdge.Top => candidate.Tip.Y + gap,
            DirectionEdge.Bottom => candidate.Tip.Y - gap - height,
            _ => candidate.Tip.Y - (height / 2f)
        };
        left = Math.Clamp(left, 0f, Math.Max(0f, viewport.Width - width));
        top = Math.Clamp(top, 0f, Math.Max(0f, viewport.Height - height));
        return new RectangleF(left, top, Math.Min(width, viewport.Width), Math.Min(height, viewport.Height));
    }

    private static float[] Spread(
        IReadOnlyList<float> coordinates,
        float minimum,
        float maximum,
        float spacing)
    {
        if (coordinates.Count == 0)
        {
            return [];
        }

        var result = new float[coordinates.Count];
        result[0] = Math.Clamp(coordinates[0], minimum, maximum);
        for (var index = 1; index < result.Length; index++)
        {
            result[index] = Math.Max(
                Math.Clamp(coordinates[index], minimum, maximum),
                result[index - 1] + spacing);
        }

        if (result[^1] > maximum)
        {
            result[^1] = maximum;
            for (var index = result.Length - 2; index >= 0; index--)
            {
                result[index] = Math.Min(result[index], result[index + 1] - spacing);
            }

            if (result[0] < minimum)
            {
                var availableSpacing = result.Length == 1
                    ? 0f
                    : (maximum - minimum) / (result.Length - 1);
                for (var index = 0; index < result.Length; index++)
                {
                    result[index] = minimum + (index * availableSpacing);
                }
            }
        }

        return result;
    }

    private static bool TryIntersectInsetBounds(
        Vector2 origin,
        Vector2 direction,
        AtlasViewport viewport,
        float inset,
        out Vector2 tip,
        out DirectionEdge edge)
    {
        var candidates = new List<(float T, DirectionEdge Edge)>(2);
        if (direction.X < 0f)
        {
            candidates.Add(((inset - origin.X) / direction.X, DirectionEdge.Left));
        }
        else if (direction.X > 0f)
        {
            candidates.Add((((viewport.Width - inset) - origin.X) / direction.X, DirectionEdge.Right));
        }

        if (direction.Y < 0f)
        {
            candidates.Add(((inset - origin.Y) / direction.Y, DirectionEdge.Top));
        }
        else if (direction.Y > 0f)
        {
            candidates.Add((((viewport.Height - inset) - origin.Y) / direction.Y, DirectionEdge.Bottom));
        }

        var hit = candidates
            .Where(candidate => float.IsFinite(candidate.T) && candidate.T >= 0f)
            .OrderBy(candidate => candidate.T)
            .ThenBy(candidate => candidate.Edge)
            .FirstOrDefault();
        if (!float.IsFinite(hit.T) || hit.T < 0f || candidates.Count == 0)
        {
            tip = default;
            edge = default;
            return false;
        }

        tip = origin + (direction * hit.T);
        edge = hit.Edge;
        return float.IsFinite(tip.X) && float.IsFinite(tip.Y);
    }

    private static bool IsInside(Vector2 point, AtlasViewport viewport)
        => point.X >= 0f
           && point.X <= viewport.Width
           && point.Y >= 0f
           && point.Y <= viewport.Height;

    private static bool IsInside(Vector2 point, RectangleF bounds)
        => point.X >= bounds.Left
           && point.X <= bounds.Right
           && point.Y >= bounds.Top
           && point.Y <= bounds.Bottom;

    private enum DirectionEdge
    {
        Left,
        Top,
        Right,
        Bottom
    }

    private sealed record DirectionCandidate(
        AtlasNavigationTarget Target,
        Vector2 Tip,
        Vector2 Direction,
        DirectionEdge Edge);

    private sealed record LabeledDirectionCandidate(
        DirectionCandidate Candidate,
        RectangleF InitialLabelBounds)
    {
        public RectangleF LabelBounds { get; set; } = InitialLabelBounds;
    }
}
