using System.Numerics;
using FreiAtlas.Core.Atlas;
using FreiAtlas.Core.Contracts;

namespace FreiAtlas.Core.Tests;

public sealed class AtlasContentIconLayoutTests
{
    [Fact]
    public void Build_ReturnsOnlyFirstBatchContentInPriorityOrder()
    {
        var grid = new AtlasGridPos(1, 2);
        var snapshot = CreateSnapshot(
            CreateNode(
                grid,
                "乾草原",
                100,
                200,
                Content("breach", "裂隙", "#B47CFF", 70),
                Content(
                    "expedition",
                    "先祖秘藏",
                    "#D6B449",
                    80,
                    "AtlasIconContentExpedition"),
                Content(
                    "map_boss",
                    "地图首领",
                    "#E35D6A",
                    100,
                    "AtlasIconContentMapBoss")));
        var geometry = new AtlasLiveRenderGeometry(
            new Dictionary<AtlasGridPos, Vector2> { [grid] = new(300, 400) },
            new AtlasProjection(0.5f, 0.5f, 0, 0));

        var placements = AtlasContentIconLayout.Build(
            snapshot,
            geometry,
            new AtlasViewport(800, 600));

        Assert.Collection(
            placements,
            boss =>
            {
                Assert.Equal(grid, boss.Grid);
                Assert.Equal("map_boss", boss.ContentId);
                Assert.Equal("地图首领", boss.DisplayName);
                Assert.Equal("#E35D6A", boss.Color);
                Assert.Equal("AtlasIconContentMapBoss", boss.ReferenceIconId);
                Assert.Equal(new Vector2(150, 200), boss.Anchor);
            },
            expedition =>
            {
                Assert.Equal("expedition", expedition.ContentId);
                Assert.Equal("先祖秘藏", expedition.DisplayName);
                Assert.Equal("#D6B449", expedition.Color);
                Assert.Equal("AtlasIconContentExpedition", expedition.ReferenceIconId);
                Assert.Equal(new Vector2(150, 200), expedition.Anchor);
            });
    }

    [Fact]
    public void Build_FallsBackToSnapshotPositionAndDropsUnsupportedOrFarContent()
    {
        var keptGrid = new AtlasGridPos(2, 0);
        var snapshot = CreateSnapshot(
            CreateNode(new AtlasGridPos(0, 0), "无内容", 10, 10),
            CreateNode(new AtlasGridPos(1, 0), "远处", 2000, 2000,
                Content("map_boss", "地图首领", "#E35D6A", 100)),
            CreateNode(keptGrid, "快照节点", 100, 120,
                Content("breach", "裂隙", "#B47CFF", 70),
                Content("expedition", "先祖秘藏", "#D6B449", 80)));
        var geometry = new AtlasLiveRenderGeometry(
            new Dictionary<AtlasGridPos, Vector2>(),
            AtlasProjection.Identity);

        var placements = AtlasContentIconLayout.Build(
            snapshot,
            geometry,
            new AtlasViewport(800, 600));

        var placement = Assert.Single(placements);
        Assert.Equal(keptGrid, placement.Grid);
        Assert.Equal("expedition", placement.ContentId);
        Assert.Equal(new Vector2(100, 120), placement.Anchor);
    }

    private static AtlasSnapshot CreateSnapshot(params AtlasNodeSnapshot[] nodes)
        => new(
            DateTimeOffset.UnixEpoch,
            AtlasSnapshotStatus.Stable,
            nodes.Length,
            0,
            nodes,
            [],
            null,
            AtlasProjection.Identity,
            "icons");

    private static AtlasNodeSnapshot CreateNode(
        AtlasGridPos grid,
        string displayName,
        float relativeX,
        float relativeY,
        params NormalizedMapContent[] contents)
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
            contents);

    private static NormalizedMapContent Content(
        string contentId,
        string displayName,
        string color,
        int priority,
        string referenceIconId = "")
        => new(
            contentId,
            displayName,
            $"content.{contentId}",
            color,
            priority,
            new Dictionary<string, string>())
        {
            ReferenceIconId = referenceIconId
        };
}
