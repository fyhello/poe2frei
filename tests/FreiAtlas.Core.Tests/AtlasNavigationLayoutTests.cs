using System.Numerics;
using FreiAtlas.Core.Atlas;
using FreiAtlas.Core.Contracts;

namespace FreiAtlas.Core.Tests;

public sealed class AtlasNavigationLayoutTests
{
    [Fact]
    public void BuildRouteEdges_KeepsSegmentThatCrossesViewportWithBothEndpointsOutside()
    {
        var left = new AtlasGridPos(0, 0);
        var right = new AtlasGridPos(1, 0);
        var snapshot = Snapshot(
            [Node(left, "Left"), Node(right, "Right")],
            [Edge(left, right)]);
        var plan = Plan(routes: new HashSet<AtlasNavigationEdgeKey>
        {
            AtlasNavigationEdgeKey.Create(left, right)
        });
        var geometry = Geometry((left, new Vector2(-50, 100)), (right, new Vector2(850, 100)));

        var placement = Assert.Single(AtlasNavigationLayout.BuildRouteEdges(
            snapshot,
            plan,
            geometry,
            new AtlasViewport(800, 600)));

        Assert.Equal(new Vector2(-50, 100), placement.Start);
        Assert.Equal(new Vector2(850, 100), placement.End);
    }

    [Fact]
    public void BuildRouteEdges_DropsSegmentWhollyOutsideViewport()
    {
        var first = new AtlasGridPos(0, 0);
        var second = new AtlasGridPos(1, 0);
        var snapshot = Snapshot(
            [Node(first, "First"), Node(second, "Second")],
            [Edge(first, second)]);

        Assert.Empty(AtlasNavigationLayout.BuildRouteEdges(
            snapshot,
            Plan(routes: new HashSet<AtlasNavigationEdgeKey>
            {
                AtlasNavigationEdgeKey.Create(first, second)
            }),
            Geometry((first, new Vector2(-300, -300)), (second, new Vector2(-200, -200))),
            new AtlasViewport(800, 600)));
    }

    [Fact]
    public void BuildRouteEdges_DropsSegmentWhenEndpointFallsOutsideTrustedMargin()
    {
        var visible = new AtlasGridPos(0, 0);
        var staleOffscreen = new AtlasGridPos(1, 0);
        var snapshot = Snapshot(
            [Node(visible, "Visible"), Node(staleOffscreen, "Stale")],
            [Edge(visible, staleOffscreen)]);

        Assert.Empty(AtlasNavigationLayout.BuildRouteEdges(
            snapshot,
            Plan(routes: new HashSet<AtlasNavigationEdgeKey>
            {
                AtlasNavigationEdgeKey.Create(visible, staleOffscreen)
            }),
            Geometry(
                (visible, new Vector2(100, 100)),
                (staleOffscreen, new Vector2(1050, 100))),
            new AtlasViewport(800, 600),
            margin: 100f));
    }

    [Fact]
    public void BuildDirections_HidesTargetInsideViewport()
    {
        var target = new AtlasGridPos(1, 0);

        Assert.Empty(AtlasNavigationLayout.BuildDirections(
            Plan(directions: [new AtlasNavigationTarget(target, "Target")]),
            Geometry((target, new Vector2(400, 300))),
            new AtlasViewport(800, 600)));
    }

    [Fact]
    public void BuildDirections_PlacesOffscreenArrowOnInsetBoundaryTowardTarget()
    {
        var target = new AtlasGridPos(1, 0);

        var placement = Assert.Single(AtlasNavigationLayout.BuildDirections(
            Plan(directions: [new AtlasNavigationTarget(target, "Target")]),
            Geometry((target, new Vector2(1000, 300))),
            new AtlasViewport(800, 600)));

        Assert.Equal(754f, placement.ArrowTip.X, 3);
        Assert.Equal(300f, placement.ArrowTip.Y, 3);
        Assert.Equal(Vector2.UnitX, placement.UnitDirection);
        Assert.InRange(placement.LabelBounds.Left, 0f, 800f);
        Assert.InRange(placement.LabelBounds.Right, 0f, 800f);
    }

    [Fact]
    public void BuildDirections_SeparatesTargetsOnSameEdgeAndClampsLabels()
    {
        var first = new AtlasGridPos(1, 0);
        var second = new AtlasGridPos(2, 0);

        var placements = AtlasNavigationLayout.BuildDirections(
            Plan(directions:
            [
                new AtlasNavigationTarget(first, "A very long target map name"),
                new AtlasNavigationTarget(second, "Another very long target map name")
            ]),
            Geometry(
                (first, new Vector2(1000, 295)),
                (second, new Vector2(1000, 305))),
            new AtlasViewport(800, 600));

        Assert.Equal(2, placements.Count);
        var ordered = placements.OrderBy(item => item.ArrowTip.Y).ToArray();
        Assert.True(ordered[1].ArrowTip.Y - ordered[0].ArrowTip.Y >= 28f);
        Assert.All(placements, placement =>
        {
            Assert.InRange(placement.LabelBounds.Left, 0f, 800f);
            Assert.InRange(placement.LabelBounds.Right, 0f, 800f);
            Assert.InRange(placement.LabelBounds.Top, 0f, 600f);
            Assert.InRange(placement.LabelBounds.Bottom, 0f, 600f);
        });
    }

    [Fact]
    public void BuildDirections_SeparatesLongLabelsOnHorizontalEdge()
    {
        var first = new AtlasGridPos(1, 0);
        var second = new AtlasGridPos(2, 0);

        var placements = AtlasNavigationLayout.BuildDirections(
            Plan(directions:
            [
                new AtlasNavigationTarget(first, "A very long target map name"),
                new AtlasNavigationTarget(second, "Another very long target map name")
            ]),
            Geometry(
                (first, new Vector2(390, -200)),
                (second, new Vector2(410, -200))),
            new AtlasViewport(800, 600));

        Assert.Equal(2, placements.Count);
        Assert.False(placements[0].LabelBounds.IntersectsWith(
            placements[1].LabelBounds));
    }

    [Fact]
    public void BuildDirections_RejectsUnstableInvalidOrTooSmallGeometry()
    {
        var target = new AtlasGridPos(1, 0);
        var plan = Plan(directions: [new AtlasNavigationTarget(target, "Target")]);

        Assert.Empty(AtlasNavigationLayout.BuildDirections(
            plan,
            new AtlasLiveRenderGeometry(
                new Dictionary<AtlasGridPos, Vector2> { [target] = new(1000, 300) },
                AtlasProjection.Identity,
                IsStable: false),
            new AtlasViewport(800, 600)));
        Assert.Empty(AtlasNavigationLayout.BuildDirections(
            plan,
            Geometry((target, new Vector2(float.NaN, 300))),
            new AtlasViewport(800, 600)));
        Assert.Empty(AtlasNavigationLayout.BuildDirections(
            plan,
            Geometry((target, new Vector2(1000, 300))),
            new AtlasViewport(92, 600)));
    }

    private static AtlasNavigationPlan Plan(
        IReadOnlyList<AtlasNavigationTarget>? directions = null,
        IReadOnlySet<AtlasNavigationEdgeKey>? routes = null)
        => new(
            new HashSet<AtlasGridPos>(),
            directions ?? [],
            routes ?? new HashSet<AtlasNavigationEdgeKey>(),
            "plan");

    private static AtlasLiveRenderGeometry Geometry(
        params (AtlasGridPos Grid, Vector2 Position)[] positions)
        => new(
            positions.ToDictionary(item => item.Grid, item => item.Position),
            AtlasProjection.Identity,
            true);

    private static AtlasSnapshot Snapshot(
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
            "snapshot",
            true);

    private static AtlasNodeSnapshot Node(AtlasGridPos grid, string name)
        => new(
            grid,
            null,
            name,
            0f,
            0f,
            true,
            true,
            false,
            false,
            [],
            []);

    private static AtlasEdgeSnapshot Edge(AtlasGridPos from, AtlasGridPos to)
        => new(from, to, AtlasEdgeState.Unknown, AtlasEdgeColor.Gray);
}
