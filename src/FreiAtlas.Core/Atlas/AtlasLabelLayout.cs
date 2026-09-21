using FreiAtlas.Core.Contracts;
using System.Numerics;

namespace FreiAtlas.Core.Atlas;

public readonly record struct AtlasLabelPlacement(
    AtlasGridPos Grid,
    string Text,
    Vector2 Anchor,
    bool IsNavigationHighlighted = false);

public static class AtlasLabelLayout
{
    public static IReadOnlyList<AtlasLabelPlacement> Build(
        AtlasSnapshot snapshot,
        AtlasLiveRenderGeometry geometry,
        AtlasViewport viewport,
        float margin = 100f)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!viewport.IsValid || !float.IsFinite(margin) || margin < 0f)
        {
            return Array.Empty<AtlasLabelPlacement>();
        }

        var placements = new List<AtlasLabelPlacement>(snapshot.Nodes.Count);
        foreach (var node in snapshot.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.DisplayName))
            {
                continue;
            }

            var livePosition = default(Vector2);
            var hasLivePosition = geometry.NodePositions is not null
                                  && geometry.NodePositions.TryGetValue(
                                      node.Grid,
                                      out livePosition);
            var position = hasLivePosition
                ? livePosition
                : new Vector2(node.RelativeX, node.RelativeY);

            if (!geometry.Projection.TryProject(position, out var anchor)
                || anchor.X < -margin
                || anchor.X > viewport.Width + margin
                || anchor.Y < -margin
                || anchor.Y > viewport.Height + margin)
            {
                continue;
            }

            placements.Add(new AtlasLabelPlacement(node.Grid, node.DisplayName, anchor));
        }

        return placements;
    }

}
