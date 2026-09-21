using System.Numerics;
using FreiAtlas.Core.Atlas;
using FreiAtlas.Core.Contracts;
using FreiAtlas.Core.Settings;
using FreiAtlas.Host;

namespace FreiAtlas.Host.Tests;

public sealed class AtlasNavigationRenderStateTests
{
    [Fact]
    public void ResolvePlan_ReusesPlanWhenOnlyUnrelatedNodeStateChanges()
    {
        var start = Node(new AtlasGridPos(0, 0), "Start", completed: false);
        var target = Node(new AtlasGridPos(1, 0), "Target", completed: false);
        var baseline = Snapshot(start, target);
        var changed = Snapshot(start with { IsCompleted = true }, target) with
        {
            Signature = "unrelated-state-changed"
        };
        var state = new AtlasNavigationRenderState();
        var settings = Navigation(hideCompletedMaps: false);

        var first = state.ResolvePlan(baseline, settings);
        var second = state.ResolvePlan(changed, settings);

        Assert.Same(first, second);
    }

    [Fact]
    public void ResolvePlan_RebuildsWhenTargetCompletesWhileFilteringCompletedMaps()
    {
        var start = Node(new AtlasGridPos(0, 0), "Start", completed: false);
        var target = Node(new AtlasGridPos(1, 0), "Target", completed: false);
        var baseline = Snapshot(start, target);
        var changed = Snapshot(start, target with { IsCompleted = true });
        var state = new AtlasNavigationRenderState();
        var settings = Navigation(hideCompletedMaps: true);

        var first = state.ResolvePlan(baseline, settings);
        var second = state.ResolvePlan(changed, settings);

        Assert.NotSame(first, second);
        Assert.Single(first.HighlightTargets);
        Assert.Empty(second.HighlightTargets);
        Assert.Empty(second.RouteEdges);
        Assert.Empty(second.DirectionTargets);
    }

    [Fact]
    public void ResolvePlan_RebuildsWhenNavigationInputChanges()
    {
        var start = Node(new AtlasGridPos(0, 0), "Start", completed: false);
        var target = Node(new AtlasGridPos(1, 0), "Target", completed: false);
        var snapshot = Snapshot(start, target);
        var state = new AtlasNavigationRenderState();

        var first = state.ResolvePlan(snapshot, Navigation());
        var second = state.ResolvePlan(
            snapshot with { CurrentGrid = target.Grid },
            Navigation());

        Assert.NotSame(first, second);
    }

    [Fact]
    public void ObserveGeometry_RetainsLastStableGeometryForFifteenFrames()
    {
        var state = new AtlasNavigationRenderState();
        var stable = Geometry(isStable: true);
        var unstable = Geometry(isStable: false);

        Assert.Equal(stable, state.ObserveGeometry(atlasActive: true, stable));
        for (var frame = 1; frame <= 15; frame++)
        {
            Assert.Equal(
                stable,
                state.ObserveGeometry(atlasActive: true, unstable));
        }

        Assert.Null(state.ObserveGeometry(atlasActive: true, unstable));
    }

    [Fact]
    public void ObserveGeometry_InactiveAtlasClearsGraceImmediately()
    {
        var state = new AtlasNavigationRenderState();
        var stable = Geometry(isStable: true);

        Assert.Equal(stable, state.ObserveGeometry(atlasActive: true, stable));
        Assert.Null(state.ObserveGeometry(atlasActive: false, stable));
        Assert.Null(state.ObserveGeometry(atlasActive: true, geometry: null));
    }

    private static AtlasNavigationSettings Navigation(
        bool hideCompletedMaps = true)
        => new(
            AtlasNavigationTargetMode.Nearest,
            new Dictionary<string, AtlasNavigationRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["Target"] = new(true, true, true)
            },
            hideCompletedMaps);

    private static AtlasSnapshot Snapshot(
        AtlasNodeSnapshot start,
        AtlasNodeSnapshot target)
        => new(
            DateTimeOffset.UnixEpoch,
            AtlasSnapshotStatus.Stable,
            2,
            1,
            [start, target],
            [new AtlasEdgeSnapshot(
                start.Grid,
                target.Grid,
                AtlasEdgeState.Reachable,
                AtlasEdgeColor.Green)],
            start.Grid,
            AtlasProjection.Identity,
            "snapshot",
            true);

    private static AtlasNodeSnapshot Node(
        AtlasGridPos grid,
        string name,
        bool completed)
        => new(
            grid,
            name,
            name,
            grid.X * 100f,
            grid.Y * 100f,
            true,
            true,
            completed,
            false,
            [],
            []);

    private static AtlasLiveRenderGeometry Geometry(bool isStable)
        => new(
            new Dictionary<AtlasGridPos, Vector2>
            {
                [new AtlasGridPos(0, 0)] = new(100f, 100f)
            },
            AtlasProjection.Identity,
            isStable);
}
