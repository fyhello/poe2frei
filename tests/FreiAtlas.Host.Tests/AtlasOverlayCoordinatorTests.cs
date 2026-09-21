using System.Drawing;
using System.Numerics;
using FreiAtlas.Core.Atlas;
using FreiAtlas.Core.Contracts;
using FreiAtlas.Core.Settings;
using FreiAtlas.Host;
using FreiAtlas.Platform.Windows.Windows;

namespace FreiAtlas.Host.Tests;

public sealed class AtlasOverlayCoordinatorTests
{
    [Fact]
    public void Build_ReturnsFrameForForegroundStableOpenAtlas()
    {
        var frame = AtlasOverlayCoordinator.Build(
            Window(isForeground: true, isMinimized: false),
            Snapshot(isOpen: true, AtlasSnapshotStatus.Stable),
            Geometry());

        Assert.NotNull(frame);
        Assert.Equal(new Rectangle(108, 238, 1264, 674), frame!.ClientBounds);
        Assert.Single(frame.Labels);
        Assert.Equal("乾草原", frame.Labels[0].Text);
    }

    [Fact]
    public void Build_ProjectsAtlasEdgesIntoFrame()
    {
        var from = new AtlasGridPos(0, 0);
        var to = new AtlasGridPos(1, 0);
        var frame = AtlasOverlayCoordinator.Build(
            Window(isForeground: true, isMinimized: false),
            SnapshotWithEdge(from, to),
            new AtlasLiveRenderGeometry(
                new Dictionary<AtlasGridPos, Vector2>
                {
                    [from] = new(100, 200),
                    [to] = new(300, 200)
                },
                AtlasProjection.Identity));

        var edge = Assert.Single(frame!.Edges);
        Assert.Equal(new Vector2(100, 200), edge.Start);
        Assert.Equal(new Vector2(300, 200), edge.End);
        Assert.Equal(AtlasEdgeColor.Green, edge.Color);
    }

    [Fact]
    public void Build_HighlightsRegionEdgeFromScreenMouseOverClientLabel()
    {
        const uint regionKey = 0x6276CAA0u;
        var from = new AtlasGridPos(0, 0);
        var to = new AtlasGridPos(1, 0);
        var window = Window(isForeground: true, isMinimized: false);
        var snapshot = SnapshotWithEdge(from, to, regionKey, regionKey);
        var geometry = new AtlasLiveRenderGeometry(
            new Dictionary<AtlasGridPos, Vector2>
            {
                [from] = new(100, 200),
                [to] = new(300, 200)
            },
            AtlasProjection.Identity);
        var label = new AtlasLabelPlacement(from, "起点", new Vector2(100, 200));
        var panel = AtlasLabelPanelLayout.GetPanelBounds(label);
        var clientMouse = new Point(
            (int)(panel.Left + (panel.Width / 2f)),
            (int)(panel.Top + (panel.Height / 2f)));
        var screenMouse = new Point(
            window.ClientBounds.Left + clientMouse.X,
            window.ClientBounds.Top + clientMouse.Y);

        var hoveredFrame = AtlasOverlayCoordinator.Build(
            window,
            snapshot,
            geometry,
            screenMouse);
        var outsideFrame = AtlasOverlayCoordinator.Build(
            window,
            snapshot,
            geometry,
            new Point(window.ClientBounds.Right + 1, window.ClientBounds.Bottom + 1));
        var noMouseFrame = AtlasOverlayCoordinator.Build(window, snapshot, geometry);

        Assert.True(Assert.Single(hoveredFrame!.Edges).IsHighlighted);
        Assert.False(Assert.Single(outsideFrame!.Edges).IsHighlighted);
        Assert.False(Assert.Single(noMouseFrame!.Edges).IsHighlighted);
    }

    [Fact]
    public void Build_ProjectsFirstBatchContentIconsIntoFrame()
    {
        var grid = new AtlasGridPos(1, 2);
        var node = new AtlasNodeSnapshot(
            grid,
            "MapTest",
            "乾草原",
            100,
            200,
            true,
            false,
            false,
            false,
            [],
            [
                new NormalizedMapContent(
                    "map_boss",
                    "地图首领",
                    "content.map-boss",
                    "#E35D6A",
                    100,
                    new Dictionary<string, string>()),
                new NormalizedMapContent(
                    "expedition",
                    "先祖秘藏",
                    "content.expedition",
                    "#D6B449",
                    80,
                    new Dictionary<string, string>())
            ]);
        var snapshot = new AtlasSnapshot(
            DateTimeOffset.UnixEpoch,
            AtlasSnapshotStatus.Stable,
            1,
            0,
            [node],
            [],
            null,
            AtlasProjection.Identity,
            "content",
            true);

        var frame = AtlasOverlayCoordinator.Build(
            Window(isForeground: true, isMinimized: false),
            snapshot,
            Geometry());

        Assert.NotNull(frame);
        Assert.Equal(
            ["map_boss", "expedition"],
            frame!.ContentIcons.Select(icon => icon.ContentId));
        Assert.All(frame.ContentIcons, icon =>
            Assert.Equal(new Vector2(100, 200), icon.Anchor));
    }

    [Theory]
    [InlineData(AtlasNodeCategory.Completed, "Completed")]
    [InlineData(AtlasNodeCategory.Unlocked, "Unlocked")]
    [InlineData(AtlasNodeCategory.Locked, "Locked")]
    public void Build_MapNameVisibilityForEachCategoryIsIndependent(
        AtlasNodeCategory hiddenCategory,
        string hiddenName)
    {
        var snapshot = SnapshotWithCategorizedNodes();
        var settings = WithNodeVisibility(
            hiddenCategory,
            showMapNames: false,
            showConnections: true);

        var frame = AtlasOverlayCoordinator.Build(
            Window(isForeground: true, isMinimized: false),
            snapshot,
            GeometryFor(snapshot),
            settings);

        Assert.NotNull(frame);
        Assert.DoesNotContain(frame!.Labels, label => label.Text == hiddenName);
        Assert.Equal(
            Enum.GetValues<AtlasNodeCategory>().Length - 1,
            frame.Labels.Count);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Build_OrdinaryEdgeRequiresBothEndpointCategoriesToShowConnections(
        bool showUnlockedConnections,
        bool showLockedConnections,
        bool expectedVisible)
    {
        var from = new AtlasGridPos(0, 0);
        var to = new AtlasGridPos(1, 0);
        var snapshot = SnapshotWithEdge(from, to);
        var settings = WithConnectionVisibility(
            showUnlockedConnections,
            showLockedConnections);

        var frame = AtlasOverlayCoordinator.Build(
            Window(isForeground: true, isMinimized: false),
            snapshot,
            GeometryFor(snapshot),
            settings);

        Assert.NotNull(frame);
        Assert.Equal(expectedVisible ? 1 : 0, frame!.Edges.Count);
    }

    [Fact]
    public void Build_HighlightedRegionEdgeRemainsWhenOrdinaryConnectionsAreHidden()
    {
        const uint regionKey = 0x6276CAA0u;
        var from = new AtlasGridPos(0, 0);
        var to = new AtlasGridPos(1, 0);
        var window = Window(isForeground: true, isMinimized: false);
        var snapshot = SnapshotWithEdge(from, to, regionKey, regionKey);
        var geometry = GeometryFor(snapshot);
        var label = new AtlasLabelPlacement(from, "起点", new Vector2(100, 200));
        var panel = AtlasLabelPanelLayout.GetPanelBounds(label);
        var screenMouse = new Point(
            window.ClientBounds.Left + (int)(panel.Left + (panel.Width / 2f)),
            window.ClientBounds.Top + (int)(panel.Top + (panel.Height / 2f)));

        var frame = AtlasOverlayCoordinator.Build(
            window,
            snapshot,
            geometry,
            WithConnectionVisibility(false, false),
            screenMouse);

        Assert.True(Assert.Single(frame!.Edges).IsHighlighted);
    }

    [Fact]
    public void Build_ContentVisibilityFiltersOnlyMatchingReferenceIconId()
    {
        var snapshot = SnapshotWithContentIcons();
        var settings = AtlasDisplaySettings.Default with
        {
            ContentVisibility = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["AtlasIconContentMapBoss"] = false,
                ["AtlasIconContentExpedition"] = true
            }
        };

        var frame = AtlasOverlayCoordinator.Build(
            Window(isForeground: true, isMinimized: false),
            snapshot,
            GeometryFor(snapshot),
            settings);

        var icon = Assert.Single(frame!.ContentIcons);
        Assert.Equal("AtlasIconContentExpedition", icon.ReferenceIconId);
    }

    [Theory]
    [InlineData(AtlasNodeCategory.Completed, 0)]
    [InlineData(AtlasNodeCategory.Unlocked, 1)]
    [InlineData(AtlasNodeCategory.Locked, 2)]
    public void Build_MapContentVisibilityForEachCategoryIsIndependent(
        AtlasNodeCategory hiddenCategory,
        int hiddenGridX)
    {
        var snapshot = SnapshotWithCategorizedContentNodes();
        var settings = WithMapContentVisibility(hiddenCategory, false);

        var frame = AtlasOverlayCoordinator.Build(
            Window(isForeground: true, isMinimized: false),
            snapshot,
            GeometryFor(snapshot),
            settings);

        Assert.NotNull(frame);
        Assert.Equal(2, frame!.ContentIcons.Count);
        Assert.DoesNotContain(
            frame.ContentIcons,
            icon => icon.Grid == new AtlasGridPos(hiddenGridX, 0));
        Assert.Equal(3, frame.Labels.Count);
    }

    [Fact]
    public void Build_ContentRequiresBothTypeAndNodeStateVisibility()
    {
        var snapshot = SnapshotWithCategorizedContentNodes();
        var settings = WithMapContentVisibility(
            AtlasNodeCategory.Unlocked,
            false) with
        {
            ContentVisibility = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["AtlasIconContentMapBoss"] = false,
                ["AtlasIconContentExpedition"] = true
            }
        };

        var frame = AtlasOverlayCoordinator.Build(
            Window(isForeground: true, isMinimized: false),
            snapshot,
            GeometryFor(snapshot),
            settings);

        Assert.NotNull(frame);
        Assert.Empty(frame!.ContentIcons);
        Assert.Equal(3, frame.Labels.Count);
    }

    [Fact]
    public void Build_NavigationHighlightDoesNotForceHiddenMapContents()
    {
        var snapshot = SnapshotWithCategorizedContentNodes() with
        {
            CurrentGrid = new AtlasGridPos(0, 0)
        };
        var settings = WithMapContentVisibility(
            AtlasNodeCategory.Unlocked,
            false) with
        {
            Navigation = Navigation("Unlocked", highlight: true)
        };
        var geometry = GeometryFor(snapshot);
        var plan = AtlasNavigationPlanner.Build(snapshot, settings.Navigation);

        var frame = AtlasOverlayCoordinator.Build(
            Window(isForeground: true, isMinimized: false),
            snapshot,
            geometry,
            settings,
            plan,
            geometry);

        Assert.NotNull(frame);
        var label = Assert.Single(
            frame!.Labels,
            label => label.Grid == new AtlasGridPos(1, 0));
        Assert.True(label.IsNavigationHighlighted);
        Assert.DoesNotContain(
            frame.ContentIcons,
            icon => icon.Grid == new AtlasGridPos(1, 0));
    }

    [Fact]
    public void Build_HiddenNodeCategorySuppressesAllContentsOnSameNode()
    {
        var snapshot = SnapshotWithContentIcons();
        var settings = WithMapContentVisibility(
            AtlasNodeCategory.Unlocked,
            false);

        var frame = AtlasOverlayCoordinator.Build(
            Window(isForeground: true, isMinimized: false),
            snapshot,
            GeometryFor(snapshot),
            settings);

        Assert.NotNull(frame);
        Assert.Empty(frame!.ContentIcons);
        Assert.Equal(
            new AtlasGridPos(0, 0),
            Assert.Single(frame.Labels).Grid);
    }

    [Fact]
    public void Build_NavigationHighlightForcesInClientHiddenCategoryLabel()
    {
        var from = new AtlasGridPos(0, 0);
        var to = new AtlasGridPos(1, 0);
        var snapshot = SnapshotWithEdge(from, to) with { CurrentGrid = from };
        var settings = WithNodeVisibility(
            AtlasNodeCategory.Locked,
            showMapNames: false,
            showConnections: true) with
        {
            Navigation = Navigation("终点", highlight: true)
        };
        var geometry = GeometryFor(snapshot);
        var plan = AtlasNavigationPlanner.Build(snapshot, settings.Navigation);

        var frame = AtlasOverlayCoordinator.Build(
            Window(isForeground: true, isMinimized: false),
            snapshot,
            geometry,
            settings,
            plan,
            geometry);

        var label = Assert.Single(frame!.Labels, label => label.Grid == to);
        Assert.True(label.IsNavigationHighlighted);
    }

    [Fact]
    public void Build_NavigationHighlightDoesNotForceOffscreenHiddenCategoryLabel()
    {
        var from = new AtlasGridPos(0, 0);
        var to = new AtlasGridPos(1, 0);
        var snapshot = SnapshotWithEdge(from, to) with { CurrentGrid = from };
        var settings = WithNodeVisibility(
            AtlasNodeCategory.Locked,
            showMapNames: false,
            showConnections: true) with
        {
            Navigation = Navigation("终点", highlight: true)
        };
        var geometry = new AtlasLiveRenderGeometry(
            new Dictionary<AtlasGridPos, Vector2>
            {
                [from] = new(100f, 200f),
                [to] = new(1300f, 200f)
            },
            AtlasProjection.Identity);
        var plan = AtlasNavigationPlanner.Build(snapshot, settings.Navigation);

        var frame = AtlasOverlayCoordinator.Build(
            Window(isForeground: true, isMinimized: false),
            snapshot,
            geometry,
            settings,
            plan,
            geometry);

        Assert.DoesNotContain(frame!.Labels, label => label.Grid == to);
    }

    [Fact]
    public void Build_NavigationRouteRemainsWhenOrdinaryConnectionsAreHidden()
    {
        var from = new AtlasGridPos(0, 0);
        var to = new AtlasGridPos(1, 0);
        var snapshot = SnapshotWithEdge(from, to) with { CurrentGrid = from };
        var settings = WithConnectionVisibility(false, false) with
        {
            Navigation = Navigation("终点", route: true)
        };
        var geometry = GeometryFor(snapshot);
        var plan = AtlasNavigationPlanner.Build(snapshot, settings.Navigation);

        var frame = AtlasOverlayCoordinator.Build(
            Window(isForeground: true, isMinimized: false),
            snapshot,
            geometry,
            settings,
            plan,
            geometry);

        Assert.Empty(frame!.Edges);
        Assert.Single(frame.NavigationRouteEdges);
    }

    [Fact]
    public void Build_NavigationDirectionAppearsOnlyWhileTargetIsOffscreen()
    {
        var from = new AtlasGridPos(0, 0);
        var to = new AtlasGridPos(1, 0);
        var snapshot = SnapshotWithEdge(from, to) with { CurrentGrid = from };
        var settings = AtlasDisplaySettings.Default with
        {
            Navigation = Navigation("终点", direction: true)
        };
        var plan = AtlasNavigationPlanner.Build(snapshot, settings.Navigation);
        var onscreen = GeometryFor(snapshot);
        var offscreen = new AtlasLiveRenderGeometry(
            new Dictionary<AtlasGridPos, Vector2>
            {
                [from] = new(100f, 200f),
                [to] = new(1500f, 200f)
            },
            AtlasProjection.Identity);

        var onscreenFrame = AtlasOverlayCoordinator.Build(
            Window(isForeground: true, isMinimized: false),
            snapshot,
            onscreen,
            settings,
            plan,
            onscreen);
        var offscreenFrame = AtlasOverlayCoordinator.Build(
            Window(isForeground: true, isMinimized: false),
            snapshot,
            offscreen,
            settings,
            plan,
            offscreen);

        Assert.Empty(onscreenFrame!.Directions);
        Assert.Equal(to, Assert.Single(offscreenFrame!.Directions).Grid);
    }

    [Fact]
    public void Build_ChangingVisibilityPreservesCoordinatesOfRemainingItems()
    {
        var snapshot = SnapshotWithContentAndEdge();
        var geometry = new AtlasLiveRenderGeometry(
            snapshot.Nodes.ToDictionary(
                node => node.Grid,
                node => new Vector2(node.RelativeX, node.RelativeY)),
            new AtlasProjection(1.5f, 1.25f, 17f, 23f));
        var baseline = AtlasOverlayCoordinator.Build(
            Window(isForeground: true, isMinimized: false),
            snapshot,
            geometry,
            AtlasDisplaySettings.Default);
        var settings = WithNodeVisibility(
            AtlasNodeCategory.Completed,
            showMapNames: false,
            showConnections: true) with
        {
            ContentVisibility = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["AtlasIconContentMapBoss"] = false,
                ["AtlasIconContentExpedition"] = true
            }
        };

        var filtered = AtlasOverlayCoordinator.Build(
            Window(isForeground: true, isMinimized: false),
            snapshot,
            geometry,
            settings);

        Assert.NotNull(baseline);
        Assert.NotNull(filtered);
        Assert.Equal(
            Assert.Single(baseline!.Edges),
            Assert.Single(filtered!.Edges));
        Assert.Equal(
            baseline.Labels.Single(label => label.Grid == new AtlasGridPos(1, 0)).Anchor,
            Assert.Single(filtered.Labels).Anchor);
        Assert.Equal(
            baseline.ContentIcons.Single(icon =>
                icon.ReferenceIconId == "AtlasIconContentExpedition").Anchor,
            Assert.Single(filtered.ContentIcons).Anchor);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Build_HidesWhenWindowIsBackgroundOrMinimized(
        bool isForeground,
        bool isMinimized)
    {
        Assert.Null(AtlasOverlayCoordinator.Build(
            Window(isForeground, isMinimized),
            Snapshot(true, AtlasSnapshotStatus.Stable),
            Geometry()));
    }

    [Theory]
    [InlineData(false, AtlasSnapshotStatus.Stable)]
    [InlineData(true, AtlasSnapshotStatus.Loading)]
    [InlineData(true, AtlasSnapshotStatus.Rebuilding)]
    public void Build_HidesWithoutOpenStableSnapshot(
        bool isOpen,
        AtlasSnapshotStatus status)
    {
        Assert.Null(AtlasOverlayCoordinator.Build(
            Window(true, false),
            Snapshot(isOpen, status),
            Geometry()));
    }

    private static GameWindowSnapshot Window(
        bool isForeground,
        bool isMinimized)
        => new(
            123,
            55272,
            new Rectangle(100, 200, 1280, 720),
            isForeground,
            isMinimized)
        {
            ClientBounds = new Rectangle(108, 238, 1264, 674)
        };

    private static AtlasSnapshot Snapshot(
        bool isOpen,
        AtlasSnapshotStatus status)
    {
        var node = new AtlasNodeSnapshot(
            new AtlasGridPos(1, 2),
            "MapTest",
            "乾草原",
            100,
            200,
            true,
            false,
            false,
            false,
            [],
            []);

        return new AtlasSnapshot(
            DateTimeOffset.UnixEpoch,
            status,
            1,
            0,
            [node],
            [],
            null,
            AtlasProjection.Identity,
            "test",
            isOpen);
    }

    private static AtlasSnapshot SnapshotWithEdge(
        AtlasGridPos from,
        AtlasGridPos to,
        uint fromRegionKey = 0u,
        uint toRegionKey = 0u)
    {
        var nodes = new[]
        {
            new AtlasNodeSnapshot(
                from,
                "From",
                "起点",
                100,
                200,
                true,
                true,
                false,
                false,
                [],
                [])
            {
                RegionKey = fromRegionKey
            },
            new AtlasNodeSnapshot(
                to,
                "To",
                "终点",
                300,
                200,
                true,
                false,
                false,
                false,
                [],
                [])
            {
                RegionKey = toRegionKey
            }
        };
        var edge = new AtlasEdgeSnapshot(
            from,
            to,
            AtlasEdgeState.Reachable,
            AtlasEdgeColor.Green);

        return new AtlasSnapshot(
            DateTimeOffset.UnixEpoch,
            AtlasSnapshotStatus.Stable,
            nodes.Length,
            1,
            nodes,
            [edge],
            null,
            AtlasProjection.Identity,
            "edge",
            true);
    }

    private static AtlasSnapshot SnapshotWithCategorizedNodes()
    {
        var nodes = new[]
        {
            Node(new AtlasGridPos(0, 0), "Completed", 100f, true, true),
            Node(new AtlasGridPos(1, 0), "Unlocked", 300f, true, false),
            Node(new AtlasGridPos(2, 0), "Locked", 500f, false, false)
        };

        return StableSnapshot(nodes, []);
    }

    private static AtlasSnapshot SnapshotWithCategorizedContentNodes()
    {
        var completed = Node(
            new AtlasGridPos(0, 0),
            "Completed",
            100f,
            true,
            true) with
        {
            Contents = [Content("map_boss", "AtlasIconContentMapBoss", 100)]
        };
        var unlocked = Node(
            new AtlasGridPos(1, 0),
            "Unlocked",
            300f,
            true,
            false) with
        {
            Contents = [Content("expedition", "AtlasIconContentExpedition", 80)]
        };
        var locked = Node(
            new AtlasGridPos(2, 0),
            "Locked",
            500f,
            false,
            false) with
        {
            Contents = [Content("map_boss", "AtlasIconContentMapBoss", 100)]
        };

        return StableSnapshot([completed, unlocked, locked], []);
    }

    private static AtlasSnapshot SnapshotWithContentIcons()
    {
        var node = Node(new AtlasGridPos(0, 0), "Map", 100f, true, false) with
        {
            Contents =
            [
                Content("map_boss", "AtlasIconContentMapBoss", 100),
                Content("expedition", "AtlasIconContentExpedition", 80)
            ]
        };
        return StableSnapshot([node], []);
    }

    private static AtlasSnapshot SnapshotWithContentAndEdge()
    {
        var from = Node(new AtlasGridPos(0, 0), "Completed", 100f, true, true) with
        {
            Contents = [Content("map_boss", "AtlasIconContentMapBoss", 100)]
        };
        var to = Node(new AtlasGridPos(1, 0), "Unlocked", 300f, true, false) with
        {
            Contents = [Content("expedition", "AtlasIconContentExpedition", 80)]
        };
        var edge = new AtlasEdgeSnapshot(
            from.Grid,
            to.Grid,
            AtlasEdgeState.Reachable,
            AtlasEdgeColor.Green);
        return StableSnapshot([from, to], [edge]);
    }

    private static AtlasNodeSnapshot Node(
        AtlasGridPos grid,
        string name,
        float x,
        bool accessible,
        bool completed)
        => new(
            grid,
            name,
            name,
            x,
            200f,
            true,
            accessible,
            completed,
            false,
            [],
            []);

    private static NormalizedMapContent Content(
        string contentId,
        string referenceIconId,
        int priority)
        => new(
            contentId,
            contentId,
            $"content.{contentId}",
            "#FFFFFF",
            priority,
            new Dictionary<string, string>())
        {
            ReferenceIconId = referenceIconId
        };

    private static AtlasSnapshot StableSnapshot(
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
            "settings",
            true);

    private static AtlasLiveRenderGeometry GeometryFor(AtlasSnapshot snapshot)
        => new(
            snapshot.Nodes.ToDictionary(
                node => node.Grid,
                node => new Vector2(node.RelativeX, node.RelativeY)),
            AtlasProjection.Identity);

    private static AtlasDisplaySettings WithNodeVisibility(
        AtlasNodeCategory category,
        bool showMapNames,
        bool showConnections)
    {
        var nodes = AtlasDisplaySettings.Default.Nodes.ToDictionary();
        nodes[category] = new AtlasNodeCategoryVisibility(
            showMapNames,
            showConnections);
        return AtlasDisplaySettings.Default with { Nodes = nodes };
    }

    private static AtlasDisplaySettings WithConnectionVisibility(
        bool showUnlocked,
        bool showLocked)
    {
        var nodes = AtlasDisplaySettings.Default.Nodes.ToDictionary();
        nodes[AtlasNodeCategory.Unlocked] = nodes[AtlasNodeCategory.Unlocked] with
        {
            ShowConnections = showUnlocked
        };
        nodes[AtlasNodeCategory.Locked] = nodes[AtlasNodeCategory.Locked] with
        {
            ShowConnections = showLocked
        };
        return AtlasDisplaySettings.Default with { Nodes = nodes };
    }

    private static AtlasDisplaySettings WithMapContentVisibility(
        AtlasNodeCategory category,
        bool showMapContents)
    {
        var nodes = AtlasDisplaySettings.Default.Nodes.ToDictionary();
        nodes[category] = nodes[category] with
        {
            ShowMapContents = showMapContents
        };
        return AtlasDisplaySettings.Default with { Nodes = nodes };
    }

    private static AtlasNavigationSettings Navigation(
        string name,
        bool highlight = false,
        bool route = false,
        bool direction = false)
        => new(
            AtlasNavigationTargetMode.Nearest,
            new Dictionary<string, AtlasNavigationRule>(StringComparer.OrdinalIgnoreCase)
            {
                [name] = new(highlight, route, direction)
            });

    private static AtlasLiveRenderGeometry Geometry()
        => new(
            new Dictionary<AtlasGridPos, Vector2>
            {
                [new AtlasGridPos(1, 2)] = new Vector2(100, 200)
            },
            AtlasProjection.Identity);
}
