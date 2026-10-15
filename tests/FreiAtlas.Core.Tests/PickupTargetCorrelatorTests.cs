using System.Numerics;
using FreiAtlas.Core.Pickup;

namespace FreiAtlas.Core.Tests;

public sealed class PickupTargetCorrelatorTests
{
    private static readonly Vector2 Player = new(100f, 100f);
    private static readonly Vector2 Viewport = new(2560f, 1600f);

    [Fact]
    public void Correlate_SingleUniqueItem_MatchesByNormalizedName()
    {
        var settings = new PickupSettings
        {
            Enabled = true,
            Whitelist = ["混沌石"],
            PickupRangeGrid = 10f
        };

        var candidate = new PickupItemCandidate(
            EntityId: 69,
            ItemName: "混沌石[67e]",
            ArtName: "CurrencyRerollRare",
            GridPosition: new(105f, 100f),
            DistanceToPlayer: 5f,
            WorldPosition: new(4000f, 4000f, 100f));

        var label = new GroundItemLabel(
            Address: 0x12345,
            RawText: "4x 混沌石[67e]",
            CleanText: "混沌石",
            Size: new(207f, 55f),
            RelativePosition: new(1200f, 400f),
            Center: new(1303.5f, 427.5f));

        var targets = PickupTargetCorrelator.Correlate(
            settings, Player, null, Viewport, [candidate], [label]);

        Assert.Single(targets);
        var target = targets[0];
        Assert.Equal(69u, target.EntityId);
        Assert.Equal("混沌石[67e]", target.ItemName);
        Assert.Equal(new Vector2(1303.5f, 427.5f), target.LabelCenter);
        Assert.Equal((nint)0x12345, target.LabelAddress);
    }

    [Fact]
    public void Correlate_MultipleSameNameItems_MatchesNearestScreenLabel()
    {
        var settings = new PickupSettings
        {
            Enabled = true,
            Whitelist = ["崇高石"],
            PickupRangeGrid = 10f
        };

        // 两个崇高石，一个在左侧 (Grid 102)，一个在右侧 (Grid 108)
        var nearLeft = new PickupItemCandidate(
            EntityId: 64,
            ItemName: "崇高石",
            ArtName: "CurrencyAddModToRare",
            GridPosition: new(102f, 100f),
            DistanceToPlayer: 2f,
            WorldPosition: new(1000f, 2000f, 0f));

        var nearRight = new PickupItemCandidate(
            EntityId: 65,
            ItemName: "崇高石",
            ArtName: "CurrencyAddModToRare",
            GridPosition: new(108f, 100f),
            DistanceToPlayer: 8f,
            WorldPosition: new(2000f, 2000f, 0f));

        var labelLeft = new GroundItemLabel(
            Address: 0x1001,
            RawText: "崇高石",
            CleanText: "崇高石",
            Size: new(154f, 55f),
            RelativePosition: new(1100f, 600f),
            Center: new(1177f, 627.5f));

        var labelRight = new GroundItemLabel(
            Address: 0x1002,
            RawText: "崇高石",
            CleanText: "崇高石",
            Size: new(154f, 55f),
            RelativePosition: new(1400f, 600f),
            Center: new(1477f, 627.5f));

        var targets = PickupTargetCorrelator.Correlate(
            settings, Player, null, Viewport, [nearLeft, nearRight], [labelLeft, labelRight]);

        Assert.Equal(2, targets.Count);
        // 距离更近的 nearLeft (2格) 优先匹配离中心最近的 labelLeft
        Assert.Equal(64u, targets[0].EntityId);
        Assert.Equal(65u, targets[1].EntityId);
    }

    [Fact]
    public void Correlate_IgnoresItemOutsideRange_AndIgnoresBlacklist()
    {
        var settings = new PickupSettings
        {
            Enabled = true,
            Whitelist = ["崇高石"],
            PickupRangeGrid = 5f
        };

        var farItem = new PickupItemCandidate(
            EntityId: 68,
            ItemName: "崇高石",
            ArtName: "CurrencyAddModToRare",
            GridPosition: new(120f, 100f),
            DistanceToPlayer: 20f);

        var blacklistedItem = new PickupItemCandidate(
            EntityId: 64,
            ItemName: "崇高石",
            ArtName: "CurrencyAddModToRare",
            GridPosition: new(102f, 100f),
            DistanceToPlayer: 2f);

        var label = new GroundItemLabel(
            Address: 0x1001,
            RawText: "崇高石",
            CleanText: "崇高石",
            Size: new(154f, 55f),
            RelativePosition: new(1100f, 600f),
            Center: new(1177f, 627.5f));

        var targets = PickupTargetCorrelator.Correlate(
            settings, Player, null, Viewport,
            [farItem, blacklistedItem],
            [label],
            blacklistedEntityIds: new HashSet<uint> { 64 });

        Assert.Empty(targets);
    }
}
