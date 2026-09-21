using System.Numerics;
using FreiAtlas.Core.Contracts;

namespace FreiAtlas.Core.Atlas;

public readonly record struct AtlasContentIconPlacement(
    AtlasGridPos Grid,
    string ContentId,
    string DisplayName,
    string Color,
    string ReferenceIconId,
    Vector2 Anchor);

public static class AtlasContentIconLayout
{
    private static readonly IReadOnlySet<string> FirstBatchContentIds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "map_boss",
            "expedition"
        };

    public static IReadOnlyList<AtlasContentIconPlacement> Build(
        AtlasSnapshot snapshot,
        AtlasLiveRenderGeometry geometry,
        AtlasViewport viewport,
        float margin = 100f)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!viewport.IsValid
            || !float.IsFinite(margin)
            || margin < 0f)
        {
            return Array.Empty<AtlasContentIconPlacement>();
        }

        var placements = new List<AtlasContentIconPlacement>();
        foreach (var node in snapshot.Nodes)
        {
            if (node.Contents is null || node.Contents.Count == 0)
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
                || !IsWithinViewport(anchor, viewport, margin))
            {
                continue;
            }

            foreach (var content in node.Contents
                         .Where(content => FirstBatchContentIds.Contains(
                             content.ContentId))
                         .OrderByDescending(content => content.Priority)
                         .ThenBy(content => content.ContentId,
                             StringComparer.OrdinalIgnoreCase)
                         .GroupBy(content => content.ContentId,
                             StringComparer.OrdinalIgnoreCase)
                         .Select(group => group.First()))
            {
                placements.Add(new AtlasContentIconPlacement(
                    node.Grid,
                    content.ContentId,
                    content.DisplayName,
                    content.DefaultColor,
                    content.ReferenceIconId,
                    anchor));
            }
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
