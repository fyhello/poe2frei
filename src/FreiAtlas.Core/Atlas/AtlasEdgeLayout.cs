using FreiAtlas.Core.Contracts;
using System.Numerics;

namespace FreiAtlas.Core.Atlas;

public readonly record struct AtlasEdgePlacement(
    AtlasGridPos From,
    AtlasGridPos To,
    Vector2 Start,
    Vector2 End,
    AtlasEdgeColor Color,
    bool IsHighlighted = false);

public static class AtlasEdgeLayout
{
    public static IReadOnlyList<AtlasEdgePlacement> Build(
        AtlasSnapshot snapshot,
        AtlasLiveRenderGeometry geometry,
        AtlasViewport viewport,
        float margin = 100f)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!viewport.IsValid
            || !geometry.IsStable
            || !float.IsFinite(margin)
            || margin < 0f
            || geometry.NodePositions is null)
        {
            return Array.Empty<AtlasEdgePlacement>();
        }

        var placements = new List<AtlasEdgePlacement>(snapshot.Edges.Count);
        foreach (var edge in snapshot.Edges)
        {
            if (!geometry.NodePositions.TryGetValue(edge.From, out var from)
                || !geometry.NodePositions.TryGetValue(edge.To, out var to)
                || !geometry.Projection.TryProject(from, out var start)
                || !geometry.Projection.TryProject(to, out var end)
                || !IsWithinViewport(start, viewport, margin)
                || !IsWithinViewport(end, viewport, margin))
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

    private static bool IsWithinViewport(
        Vector2 point,
        AtlasViewport viewport,
        float margin)
        => point.X >= -margin
           && point.X <= viewport.Width + margin
           && point.Y >= -margin
           && point.Y <= viewport.Height + margin;
}
