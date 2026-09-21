using System.Numerics;
using FreiAtlas.Core.Atlas;

namespace FreiAtlas.Core.Tests;

public sealed class AtlasRegionRevealTests
{
    private const uint SelectedRegionKey = 0x6276CAA0u;

    [Fact]
    public void Select_HitLabelSelectsEveryGridWithSameNonZeroRegionKey()
    {
        var a = new AtlasGridPos(0, 0);
        var b = new AtlasGridPos(1, 0);
        var c = new AtlasGridPos(2, 0);
        var snapshot = CreateSnapshot(
            [CreateNode(a, SelectedRegionKey), CreateNode(b, SelectedRegionKey), CreateNode(c, 7u)],
            [CreateEdge(a, b), CreateEdge(b, c)]);
        AtlasLabelPlacement[] labels =
        [
            new(a, "A", new Vector2(100f, 200f)),
            new(b, "B", new Vector2(200f, 200f)),
            new(c, "C", new Vector2(300f, 200f))
        ];
        var panel = AtlasLabelPanelLayout.GetPanelBounds(labels[0]);
        var mouse = new Vector2(
            panel.Left + (panel.Width / 2f),
            panel.Top + (panel.Height / 2f));

        var selection = AtlasRegionReveal.Select(snapshot, labels, mouse);

        Assert.NotNull(selection);
        Assert.Equal(SelectedRegionKey, selection.RegionKey);
        Assert.Equal(2, selection.Grids.Count);
        Assert.Contains(a, selection.Grids);
        Assert.Contains(b, selection.Grids);
        Assert.DoesNotContain(c, selection.Grids);
        Assert.True(AtlasRegionReveal.IsInternalEdge(snapshot, a, b, selection));
        Assert.False(AtlasRegionReveal.IsInternalEdge(snapshot, b, c, selection));
    }

    [Fact]
    public void Select_OverlappingLabelsUsesLastDrawnLabel()
    {
        var a = new AtlasGridPos(0, 0);
        var c = new AtlasGridPos(2, 0);
        var snapshot = CreateSnapshot(
            [CreateNode(a, SelectedRegionKey), CreateNode(c, 7u)],
            []);
        AtlasLabelPlacement[] labels =
        [
            new(a, "A", new Vector2(100f, 200f)),
            new(c, "C", new Vector2(100f, 200f))
        ];
        var panel = AtlasLabelPanelLayout.GetPanelBounds(labels[0]);
        var mouse = new Vector2(panel.Left + 1f, panel.Top + 1f);

        var selection = AtlasRegionReveal.Select(snapshot, labels, mouse);

        Assert.NotNull(selection);
        Assert.Equal(7u, selection.RegionKey);
        Assert.Contains(c, selection.Grids);
        Assert.DoesNotContain(a, selection.Grids);
    }

    [Fact]
    public void Select_ZeroRegionKeyDoesNotReveal()
    {
        var grid = new AtlasGridPos(0, 0);
        var snapshot = CreateSnapshot([CreateNode(grid, 0u)], []);
        var label = new AtlasLabelPlacement(
            grid,
            "Unknown",
            new Vector2(100f, 200f));
        var panel = AtlasLabelPanelLayout.GetPanelBounds(label);
        var mouse = new Vector2(panel.Left + 1f, panel.Top + 1f);

        Assert.Null(AtlasRegionReveal.Select(snapshot, [label], mouse));
    }

    [Fact]
    public void Select_MouseOutsidePanelDoesNotReveal()
    {
        var grid = new AtlasGridPos(0, 0);
        var snapshot = CreateSnapshot([CreateNode(grid, SelectedRegionKey)], []);
        var label = new AtlasLabelPlacement(
            grid,
            "A",
            new Vector2(100f, 200f));
        var panel = AtlasLabelPanelLayout.GetPanelBounds(label);
        var mouse = new Vector2(panel.Right + 1f, panel.Bottom + 1f);

        Assert.Null(AtlasRegionReveal.Select(snapshot, [label], mouse));
    }

    [Fact]
    public void Select_UsesFontSizeAdjustedPanelBounds()
    {
        var grid = new AtlasGridPos(0, 0);
        var snapshot = CreateSnapshot(
            [CreateNode(grid, SelectedRegionKey)],
            []);
        var label = new AtlasLabelPlacement(
            grid,
            "大字号地图",
            new Vector2(100f, 200f));
        var largePanel = AtlasLabelPanelLayout.GetPanelBounds(label, 30f);
        var defaultPanel = AtlasLabelPanelLayout.GetPanelBounds(label);
        var mouse = new Vector2(
            largePanel.Left + (largePanel.Width / 2f),
            largePanel.Bottom - 1f);

        Assert.True(mouse.Y > defaultPanel.Bottom);
        Assert.NotNull(AtlasRegionReveal.Select(
            snapshot,
            [label],
            mouse,
            30f));
    }

    private static AtlasSnapshot CreateSnapshot(
        IReadOnlyList<AtlasNodeSnapshot> nodes,
        IReadOnlyList<AtlasEdgeSnapshot> edges)
        => new(
            DateTimeOffset.UnixEpoch,
            AtlasSnapshotStatus.Stable,
            nodes.Count,
            edges.Count,
            nodes,
            edges,
            null,
            AtlasProjection.Identity,
            "test");

    private static AtlasNodeSnapshot CreateNode(
        AtlasGridPos grid,
        uint regionKey)
        => new(
            grid,
            null,
            $"Map {grid.X}",
            grid.X * 100f,
            200f,
            true,
            true,
            false,
            false,
            [],
            [])
        {
            RegionKey = regionKey
        };

    private static AtlasEdgeSnapshot CreateEdge(
        AtlasGridPos from,
        AtlasGridPos to)
        => new(from, to, AtlasEdgeState.Known, AtlasEdgeColor.Gray);
}
