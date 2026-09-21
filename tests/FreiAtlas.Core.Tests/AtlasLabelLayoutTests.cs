using FreiAtlas.Core.Atlas;
using FreiAtlas.Core.Contracts;
using System.Numerics;

namespace FreiAtlas.Core.Tests;

public sealed class AtlasLabelLayoutTests
{
    [Fact]
    public void Build_UsesLivePositionAndProjection()
    {
        var grid = new AtlasGridPos(1, 2);
        var snapshot = CreateSnapshot(CreateNode(grid, "乾草原", 10, 20));
        var geometry = new AtlasLiveRenderGeometry(
            new Dictionary<AtlasGridPos, Vector2> { [grid] = new(100, 200) },
            new AtlasProjection(0.5f, 0.5f, 0, 0));

        var placement = Assert.Single(AtlasLabelLayout.Build(
            snapshot,
            geometry,
            new AtlasViewport(800, 600)));

        Assert.Equal(grid, placement.Grid);
        Assert.Equal("乾草原", placement.Text);
        Assert.Equal(new Vector2(50, 100), placement.Anchor);
    }

    [Fact]
    public void Build_FallsBackToStableSnapshotPosition()
    {
        var grid = new AtlasGridPos(3, 4);
        var snapshot = CreateSnapshot(CreateNode(grid, "快照节点", 80, 120));
        var geometry = new AtlasLiveRenderGeometry(
            new Dictionary<AtlasGridPos, Vector2>(),
            AtlasProjection.Identity);

        var placement = Assert.Single(AtlasLabelLayout.Build(
            snapshot,
            geometry,
            new AtlasViewport(800, 600)));

        Assert.Equal(new Vector2(80, 120), placement.Anchor);
    }

    [Fact]
    public void Build_UsesLivePositionForHiddenNode()
    {
        var hiddenGrid = new AtlasGridPos(1, 1);
        var snapshot = CreateSnapshot(
            CreateNode(new AtlasGridPos(0, 0), null, 100, 100),
            CreateNode(new AtlasGridPos(1, 0), null, 200, 100),
            CreateNode(new AtlasGridPos(0, 1), null, 100, 200),
            CreateNode(hiddenGrid, "隐藏地图", 200, 200) with
            {
                IsVisible = false,
                IsDiscovered = false
            });
        var geometry = new AtlasLiveRenderGeometry(
            new Dictionary<AtlasGridPos, Vector2>
            {
                [new AtlasGridPos(0, 0)] = new(140, 70),
                [new AtlasGridPos(1, 0)] = new(240, 70),
                [new AtlasGridPos(0, 1)] = new(140, 170),
                [hiddenGrid] = new(200, 200)
            },
            AtlasProjection.Identity);

        var placement = Assert.Single(AtlasLabelLayout.Build(
            snapshot,
            geometry,
            new AtlasViewport(800, 600)));

        Assert.Equal(new Vector2(200, 200), placement.Anchor);
    }

    [Fact]
    public void Build_DropsBlankInvalidAndFarOutsideLabels()
    {
        var keptGrid = new AtlasGridPos(4, 0);
        var snapshot = CreateSnapshot(
            CreateNode(new AtlasGridPos(1, 0), " ", 10, 10),
            CreateNode(new AtlasGridPos(2, 0), "无效", float.NaN, 20),
            CreateNode(new AtlasGridPos(3, 0), "太远", 2000, 2000),
            CreateNode(keptGrid, "保留", -90, 100));
        var geometry = new AtlasLiveRenderGeometry(
            new Dictionary<AtlasGridPos, Vector2>(),
            AtlasProjection.Identity);

        var placement = Assert.Single(AtlasLabelLayout.Build(
            snapshot,
            geometry,
            new AtlasViewport(800, 600),
            margin: 100));

        Assert.Equal(keptGrid, placement.Grid);
        Assert.Equal("保留", placement.Text);
    }

    private static AtlasSnapshot CreateSnapshot(params AtlasNodeSnapshot[] nodes)
        => new(
            DateTimeOffset.Parse("2026-07-30T00:00:00+08:00"),
            AtlasSnapshotStatus.Stable,
            nodes.Length,
            0,
            nodes,
            [],
            null,
            AtlasProjection.Identity,
            "stable");

    private static AtlasNodeSnapshot CreateNode(
        AtlasGridPos grid,
        string? displayName,
        float relativeX,
        float relativeY)
        => new(
            grid,
            null,
            displayName,
            relativeX,
            relativeY,
            true,
            true,
            false,
            false,
            [],
            []);
}
