using FreiAtlas.Core.Atlas;
using FreiAtlas.Core.Settings;

namespace FreiAtlas.Core.Tests;

public sealed class AtlasNavigationPlannerTests
{
    [Fact]
    public void Build_NearestUsesShortestReachableTargetAndPath()
    {
        var start = new AtlasGridPos(0, 0);
        var middle = new AtlasGridPos(1, 0);
        var near = new AtlasGridPos(2, 0);
        var far = new AtlasGridPos(5, 0);
        var snapshot = Snapshot(
            start,
            [Node(start, "Start"), Node(middle, "Middle"), Node(near, "Target"), Node(far, "target")],
            [Edge(start, middle), Edge(middle, near), Edge(near, far)]);

        var plan = AtlasNavigationPlanner.Build(
            snapshot,
            Settings(AtlasNavigationTargetMode.Nearest, " target ", true, true, true));

        Assert.Equal([near], plan.HighlightTargets);
        Assert.Equal(near, Assert.Single(plan.DirectionTargets).Grid);
        Assert.Equal(2, plan.RouteEdges.Count);
        Assert.Contains(AtlasNavigationEdgeKey.Create(start, middle), plan.RouteEdges);
        Assert.Contains(AtlasNavigationEdgeKey.Create(middle, near), plan.RouteEdges);
    }

    [Fact]
    public void Build_NearestBreaksEqualDistanceTieByGrid()
    {
        var start = new AtlasGridPos(0, 0);
        var lowerGrid = new AtlasGridPos(1, -1);
        var higherGrid = new AtlasGridPos(1, 1);
        var snapshot = Snapshot(
            start,
            [Node(start, "Start"), Node(higherGrid, "Target"), Node(lowerGrid, "target")],
            [Edge(start, higherGrid), Edge(start, lowerGrid)]);

        var plan = AtlasNavigationPlanner.Build(
            snapshot,
            Settings(AtlasNavigationTargetMode.Nearest, "Target", true, false, false));

        Assert.Equal([lowerGrid], plan.HighlightTargets);
    }

    [Fact]
    public void Build_NearestUsesSquaredGridDistanceWhenAllTargetsAreUnreachable()
    {
        var start = new AtlasGridPos(0, 0);
        var farther = new AtlasGridPos(5, 0);
        var nearer = new AtlasGridPos(2, 1);
        var snapshot = Snapshot(
            start,
            [Node(start, "Start"), Node(farther, "Target"), Node(nearer, "target")],
            []);

        var plan = AtlasNavigationPlanner.Build(
            snapshot,
            Settings(AtlasNavigationTargetMode.Nearest, "Target", true, true, true));

        Assert.Equal([nearer], plan.HighlightTargets);
        Assert.Equal(nearer, Assert.Single(plan.DirectionTargets).Grid);
        Assert.Empty(plan.RouteEdges);
    }

    [Fact]
    public void Build_AllIncludesEveryTargetAndDeduplicatesSharedRouteEdges()
    {
        var start = new AtlasGridPos(0, 0);
        var shared = new AtlasGridPos(1, 0);
        var first = new AtlasGridPos(2, -1);
        var second = new AtlasGridPos(2, 1);
        var snapshot = Snapshot(
            start,
            [Node(start, "Start"), Node(shared, "Middle"), Node(first, "Target"), Node(second, "target")],
            [Edge(start, shared), Edge(shared, first), Edge(shared, second)]);

        var plan = AtlasNavigationPlanner.Build(
            snapshot,
            Settings(AtlasNavigationTargetMode.All, "TARGET", true, true, true));

        Assert.Equal([first, second], plan.HighlightTargets.OrderBy(grid => grid.Y));
        Assert.Equal(2, plan.DirectionTargets.Count);
        Assert.Equal(3, plan.RouteEdges.Count);
        Assert.Contains(AtlasNavigationEdgeKey.Create(start, shared), plan.RouteEdges);
    }

    [Fact]
    public void Build_HideCompletedMapsExcludesCompletedTargetsFromAllFeatures()
    {
        var start = new AtlasGridPos(0, 0);
        var completed = new AtlasGridPos(1, 0);
        var incomplete = new AtlasGridPos(2, 0);
        var snapshot = Snapshot(
            start,
            [
                Node(start, "Start"),
                Node(completed, "Target", completed: true),
                Node(incomplete, "target")
            ],
            [Edge(start, completed), Edge(completed, incomplete)]);

        var plan = AtlasNavigationPlanner.Build(
            snapshot,
            Settings(
                AtlasNavigationTargetMode.All,
                "Target",
                true,
                true,
                true,
                hideCompletedMaps: true));

        Assert.Equal([incomplete], plan.HighlightTargets);
        Assert.Equal(incomplete, Assert.Single(plan.DirectionTargets).Grid);
        Assert.Equal(2, plan.RouteEdges.Count);
    }

    [Fact]
    public void Build_ShowCompletedMapsKeepsCompletedTargetsAvailable()
    {
        var start = new AtlasGridPos(0, 0);
        var target = new AtlasGridPos(1, 0);
        var snapshot = Snapshot(
            start,
            [Node(start, "Start"), Node(target, "Target", completed: true)],
            [Edge(start, target)]);

        var plan = AtlasNavigationPlanner.Build(
            snapshot,
            Settings(
                AtlasNavigationTargetMode.Nearest,
                "Target",
                true,
                true,
                true,
                hideCompletedMaps: false));

        Assert.Equal([target], plan.HighlightTargets);
        Assert.Equal(target, Assert.Single(plan.DirectionTargets).Grid);
        Assert.Single(plan.RouteEdges);
    }

    [Fact]
    public void Build_RouteCanTraverseLockedUndiscoveredNode()
    {
        var start = new AtlasGridPos(0, 0);
        var locked = new AtlasGridPos(1, 0);
        var target = new AtlasGridPos(2, 0);
        var snapshot = Snapshot(
            start,
            [
                Node(start, "Start"),
                Node(locked, "Locked", accessible: false, discovered: false),
                Node(target, "Target")
            ],
            [Edge(start, locked), Edge(locked, target)]);

        var plan = AtlasNavigationPlanner.Build(
            snapshot,
            Settings(AtlasNavigationTargetMode.Nearest, "Target", false, true, false));

        Assert.Equal(2, plan.RouteEdges.Count);
    }

    [Fact]
    public void Build_MissingCurrentGridKeepsTargetsButProducesNoRoute()
    {
        var first = new AtlasGridPos(2, 0);
        var second = new AtlasGridPos(1, 0);
        var snapshot = Snapshot(
            null,
            [Node(first, "Target"), Node(second, "target")],
            [Edge(first, second)]);

        var plan = AtlasNavigationPlanner.Build(
            snapshot,
            Settings(AtlasNavigationTargetMode.Nearest, "Target", true, true, true));

        Assert.Equal([second], plan.HighlightTargets);
        Assert.Equal(second, Assert.Single(plan.DirectionTargets).Grid);
        Assert.Empty(plan.RouteEdges);
    }

    [Fact]
    public void Build_InputSignatureIgnoresUnrelatedSnapshotState()
    {
        var start = new AtlasGridPos(0, 0);
        var target = new AtlasGridPos(1, 0);
        var baseline = Snapshot(
            start,
            [Node(start, "Start"), Node(target, "Target")],
            [Edge(start, target)],
            signature: "first");
        var changed = baseline with
        {
            Signature = "second",
            Nodes = baseline.Nodes.Select(node => node with
            {
                IsCompleted = true,
                Contents = [new NormalizedMapContent("boss", "Boss", "boss", "#FFFFFF", 1, new Dictionary<string, string>())]
            }).ToArray()
        };
        var settings = Settings(
            AtlasNavigationTargetMode.Nearest,
            "Target",
            true,
            true,
            true,
            hideCompletedMaps: false);

        Assert.Equal(
            AtlasNavigationPlanner.Build(baseline, settings).InputSignature,
            AtlasNavigationPlanner.Build(changed, settings).InputSignature);
        Assert.Equal(
            AtlasNavigationPlanner.CreateInputSignature(baseline, settings),
            AtlasNavigationPlanner.CreateInputSignature(changed, settings));
    }

    [Fact]
    public void Build_InputSignatureTracksCompletionWhenFilteringCompletedMaps()
    {
        var start = new AtlasGridPos(0, 0);
        var target = new AtlasGridPos(1, 0);
        var baseline = Snapshot(
            start,
            [Node(start, "Start"), Node(target, "Target")],
            [Edge(start, target)]);
        var changed = baseline with
        {
            Nodes = baseline.Nodes.Select(node => node.Grid == target
                ? node with { IsCompleted = true }
                : node).ToArray()
        };
        var settings = Settings(
            AtlasNavigationTargetMode.Nearest,
            "Target",
            true,
            true,
            true,
            hideCompletedMaps: true);

        Assert.NotEqual(
            AtlasNavigationPlanner.CreateInputSignature(baseline, settings),
            AtlasNavigationPlanner.CreateInputSignature(changed, settings));
    }

    private static AtlasNavigationSettings Settings(
        AtlasNavigationTargetMode mode,
        string name,
        bool highlight,
        bool route,
        bool direction,
        bool hideCompletedMaps = true)
        => new(mode, new Dictionary<string, AtlasNavigationRule>(StringComparer.OrdinalIgnoreCase)
        {
            [name] = new(highlight, route, direction)
        }, hideCompletedMaps);

    private static AtlasSnapshot Snapshot(
        AtlasGridPos? current,
        IReadOnlyList<AtlasNodeSnapshot> nodes,
        IReadOnlyList<AtlasEdgeSnapshot> edges,
        string signature = "snapshot")
        => new(
            DateTimeOffset.UnixEpoch,
            AtlasSnapshotStatus.Stable,
            nodes.Count,
            edges.Count,
            nodes,
            edges,
            current,
            AtlasProjection.Identity,
            signature,
            true);

    private static AtlasNodeSnapshot Node(
        AtlasGridPos grid,
        string name,
        bool accessible = true,
        bool? discovered = true,
        bool completed = false)
        => new(
            grid,
            null,
            name,
            grid.X,
            grid.Y,
            true,
            accessible,
            completed,
            false,
            [],
            [],
            discovered);

    private static AtlasEdgeSnapshot Edge(AtlasGridPos from, AtlasGridPos to)
        => new(from, to, AtlasEdgeState.Unknown, AtlasEdgeColor.Gray);
}
