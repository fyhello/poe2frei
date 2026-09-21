using FreiAtlas.Core.Atlas;

namespace FreiAtlas.Core.Tests;

public sealed class AtlasGraphTests
{
    [Fact]
    public void AddNode_UsesGridPositionAsStableKey()
    {
        var graph = new AtlasGraph();
        graph.AddNode(new AtlasNodeSnapshot(
            new AtlasGridPos(1, 2),
            "MapA",
            "地图 A",
            10,
            20,
            false,
            true,
            false,
            false,
            [],
            []));
        graph.AddNode(new AtlasNodeSnapshot(
            new AtlasGridPos(1, 2),
            "MapA",
            "地图 A 更新",
            11,
            21,
            true,
            true,
            false,
            false,
            [],
            []));

        Assert.Single(graph.Nodes);
        Assert.Equal("地图 A 更新", graph.Nodes[new AtlasGridPos(1, 2)].DisplayName);
    }

    [Fact]
    public void AddEdge_NormalizesDirectionAndRemovesDuplicates()
    {
        var graph = new AtlasGraph();
        var a = new AtlasGridPos(1, 2);
        var b = new AtlasGridPos(3, 4);

        graph.AddEdge(new AtlasEdgeSnapshot(a, b, AtlasEdgeState.Known, AtlasEdgeColor.Red));
        graph.AddEdge(new AtlasEdgeSnapshot(b, a, AtlasEdgeState.Reachable, AtlasEdgeColor.Green));

        var edge = Assert.Single(graph.Edges);
        Assert.Equal(a, edge.From);
        Assert.Equal(b, edge.To);
        Assert.Equal(AtlasEdgeState.Reachable, edge.State);
        Assert.Equal(AtlasEdgeColor.Green, edge.RenderColor);
    }
}
