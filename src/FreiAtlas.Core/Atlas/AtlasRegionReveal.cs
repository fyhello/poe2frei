using System.Numerics;

namespace FreiAtlas.Core.Atlas;

public sealed record AtlasRegionSelection(
    uint RegionKey,
    IReadOnlySet<AtlasGridPos> Grids);

public static class AtlasRegionReveal
{
    public static AtlasRegionSelection? Select(
        AtlasSnapshot snapshot,
        IReadOnlyList<AtlasLabelPlacement> labels,
        Vector2 clientMouse,
        float fontSize = AtlasLabelPanelLayout.DefaultFontSize)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(labels);

        if (!float.IsFinite(clientMouse.X) || !float.IsFinite(clientMouse.Y))
        {
            return null;
        }

        for (var index = labels.Count - 1; index >= 0; index--)
        {
            var label = labels[index];
            if (!AtlasLabelPanelLayout.GetPanelBounds(label, fontSize).Contains(
                    clientMouse.X,
                    clientMouse.Y))
            {
                continue;
            }

            var regionKey = snapshot.Nodes
                .FirstOrDefault(node => node.Grid == label.Grid)
                ?.RegionKey ?? 0u;
            if (regionKey == 0u)
            {
                return null;
            }

            IReadOnlySet<AtlasGridPos> grids = snapshot.Nodes
                .Where(node => node.RegionKey == regionKey)
                .Select(node => node.Grid)
                .ToHashSet();
            return new AtlasRegionSelection(regionKey, grids);
        }

        return null;
    }

    public static bool IsInternalEdge(
        AtlasSnapshot snapshot,
        AtlasGridPos from,
        AtlasGridPos to,
        AtlasRegionSelection? selection)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (selection is null || selection.RegionKey == 0u)
        {
            return false;
        }

        return selection.Grids.Contains(from)
               && selection.Grids.Contains(to);
    }
}
