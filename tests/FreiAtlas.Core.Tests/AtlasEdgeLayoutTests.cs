using FreiAtlas.Core.Atlas;
using FreiAtlas.Core.Contracts;
using System.Numerics;

namespace FreiAtlas.Core.Tests;

public sealed class AtlasEdgeLayoutTests
{
    [Fact]
    public void Build_ProjectsLiveEndpointsAndPreservesEdgeColor()
    {
        var from = new AtlasGridPos(0, 0);
        var to = new AtlasGridPos(1, 0);
        var snapshot = CreateSnapshot(
            CreateNode(from, 10, 20),
            CreateNode(to, 30, 20),
            new AtlasEdgeSnapshot(
                from,
                to,
                AtlasEdgeState.Reachable,
                AtlasEdgeColor.Green));
        var geometry = new AtlasLiveRenderGeometry(
            new Dictionary<AtlasGridPos, Vector2>
            {
                [from] = new(100, 200),
                [to] = new(200, 200)
            },
            new AtlasProjection(0.5f, 0.5f, 0f, 0f));

        var placement = Assert.Single(AtlasEdgeLayout.Build(
            snapshot,
            geometry,
            new AtlasViewport(800, 600)));

        Assert.Equal(from, placement.From);
        Assert.Equal(to, placement.To);
        Assert.Equal(new Vector2(50, 100), placement.Start);
        Assert.Equal(new Vector2(100, 100), placement.End);
        Assert.Equal(AtlasEdgeColor.Green, placement.Color);
    }

    [Fact]
    public void Build_SkipsEdgeWhenEndpointCannotBeProjected()
    {
        var from = new AtlasGridPos(0, 0);
        var to = new AtlasGridPos(1, 0);
        var snapshot = CreateSnapshot(
            CreateNode(from, 10, 20),
            CreateNode(to, 30, 20),
            new AtlasEdgeSnapshot(
                from,
                to,
                AtlasEdgeState.Locked,
                AtlasEdgeColor.Red));
        var geometry = new AtlasLiveRenderGeometry(
            new Dictionary<AtlasGridPos, Vector2>
            {
                [from] = new(100, 200)
            },
            AtlasProjection.Identity);

        Assert.Empty(AtlasEdgeLayout.Build(
            snapshot,
            geometry,
            new AtlasViewport(800, 600)));
    }

    private static AtlasSnapshot CreateSnapshot(
        AtlasNodeSnapshot from,
        AtlasNodeSnapshot to,
        AtlasEdgeSnapshot edge)
        => new(
            DateTimeOffset.UnixEpoch,
            AtlasSnapshotStatus.Stable,
            2,
            1,
            [from, to],
            [edge],
            null,
            AtlasProjection.Identity,
            "test");

    private static AtlasNodeSnapshot CreateNode(
        AtlasGridPos grid,
        float relativeX,
        float relativeY)
        => new(
            grid,
            null,
            $"地图 {grid.X},{grid.Y}",
            relativeX,
            relativeY,
            true,
            true,
            false,
            false,
            [],
            []);
}
