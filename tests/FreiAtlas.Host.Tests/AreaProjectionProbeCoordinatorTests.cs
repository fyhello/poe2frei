using System.Drawing;
using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Platform.Windows.Windows;

namespace FreiAtlas.Host.Tests;

public sealed class AreaProjectionProbeCoordinatorTests
{
    private static readonly AreaUiRect MiniViewport =
        new(283.875f, 187.5f, 340.5f, 225f);

    [Fact]
    public void Build_ViewportModePublishesSelectedViewGeometryWithoutTargets()
    {
        var snapshot = Snapshot(
            AreaMapSnapshotStatus.Stable,
            areaHash: 0xFE2A4CD3,
            player: Player(),
            largeMap: View(
                AreaMapViewKind.LargeMap,
                isVisible: false,
                new AreaUiRect(0, 0, 908, 600)),
            miniMap: View(
                AreaMapViewKind.MiniMap,
                isVisible: true,
                MiniViewport));

        var frame = AreaProjectionProbeCoordinator.Build(
            ForegroundWindow(),
            snapshot,
            AreaProjectionProbeMode.Viewport,
            AreaProjectionProbeCalibration.CreateReferenceInitial(),
            "1-100");

        Assert.NotNull(frame);
        Assert.Equal(AreaMapViewKind.MiniMap, frame.ViewKind);
        Assert.Equal(MiniViewport, frame.Viewport);
        Assert.True(frame.DrawViewportGuide);
        Assert.Empty(frame.Marks);
        Assert.Equal("1-100", frame.ProbeFrameId);
        Assert.Equal(snapshot.Area.AreaHash, frame.Trace.AreaHash);
        Assert.Equal(snapshot.Player!.GridPosition, frame.Trace.PlayerGrid);
    }

    [Fact]
    public void Build_AcceptsDegradedSnapshotWhenRealtimeInputsAreValid()
    {
        var snapshot = Snapshot(
            AreaMapSnapshotStatus.Degraded,
            areaHash: 0xFE2A4CD3,
            player: Player(),
            largeMap: View(
                AreaMapViewKind.LargeMap,
                isVisible: true,
                new AreaUiRect(0, 0, 908, 600)),
            miniMap: View(
                AreaMapViewKind.MiniMap,
                isVisible: false,
                MiniViewport));

        var frame = AreaProjectionProbeCoordinator.Build(
            ForegroundWindow(),
            snapshot,
            AreaProjectionProbeMode.Viewport,
            AreaProjectionProbeCalibration.CreateReferenceInitial(),
            "1-101");

        Assert.NotNull(frame);
        Assert.Equal(AreaMapViewKind.LargeMap, frame.ViewKind);
    }

    [Fact]
    public void Build_RejectsInvalidWindowOrSnapshotState()
    {
        var validSnapshot = ValidMiniSnapshot();
        var calibration = AreaProjectionProbeCalibration.CreateReferenceInitial();
        var invalidWindows = new GameWindowSnapshot?[]
        {
            null,
            ForegroundWindow() with { IsForeground = false },
            ForegroundWindow() with { IsMinimized = true },
            ForegroundWindow() with { ClientBounds = new Rectangle(0, 0, 0, 600) }
        };

        foreach (var window in invalidWindows)
        {
            Assert.Null(AreaProjectionProbeCoordinator.Build(
                window,
                validSnapshot,
                AreaProjectionProbeMode.Viewport,
                calibration,
                "1-102"));
        }

        var invalidSnapshots = new AreaMapSnapshot?[]
        {
            null,
            validSnapshot with { Status = AreaMapSnapshotStatus.Loading },
            validSnapshot with { Status = AreaMapSnapshotStatus.Detached },
            validSnapshot with { Area = validSnapshot.Area with { AreaHash = 0 } },
            validSnapshot with { Player = null }
        };

        foreach (var snapshot in invalidSnapshots)
        {
            Assert.Null(AreaProjectionProbeCoordinator.Build(
                ForegroundWindow(),
                snapshot,
                AreaProjectionProbeMode.Viewport,
                calibration,
                "1-103"));
        }
    }

    [Fact]
    public void Build_RequiresExactlyOneVerifiedVisibleView()
    {
        var valid = ValidMiniSnapshot();
        var calibration = AreaProjectionProbeCalibration.CreateReferenceInitial();
        var neither = valid with
        {
            MapViews = new AreaMapViewsSnapshot(
                valid.MapViews.LargeMap,
                valid.MapViews.MiniMap with { IsVisible = false })
        };
        var both = valid with
        {
            MapViews = new AreaMapViewsSnapshot(
                valid.MapViews.LargeMap with { IsVisible = true },
                valid.MapViews.MiniMap)
        };
        var unverified = valid with
        {
            MapViews = new AreaMapViewsSnapshot(
                valid.MapViews.LargeMap,
                valid.MapViews.MiniMap with
                {
                    Availability = AreaMapViewAvailability.Unverified
                })
        };

        Assert.Null(Build(neither, calibration));
        Assert.Null(Build(both, calibration));
        Assert.Null(Build(unverified, calibration));
    }

    [Fact]
    public void Build_RejectsInvalidViewGeometryZoomOrMiniMapRotation()
    {
        var valid = ValidMiniSnapshot();
        var calibration = AreaProjectionProbeCalibration.CreateReferenceInitial();
        var invalidMiniMaps = new[]
        {
            valid.MapViews.MiniMap with { Viewport = null },
            valid.MapViews.MiniMap with
            {
                Viewport = new AreaUiRect(0, 0, float.NaN, 225)
            },
            valid.MapViews.MiniMap with { Zoom = 0 },
            valid.MapViews.MiniMap with { Shift = new Vector2(float.NaN, 0) },
            valid.MapViews.MiniMap with { RotationRadians = 0.5f },
            valid.MapViews.MiniMap with { RotatesWithPlayer = true }
        };

        foreach (var miniMap in invalidMiniMaps)
        {
            var snapshot = valid with
            {
                MapViews = new AreaMapViewsSnapshot(
                    valid.MapViews.LargeMap,
                    miniMap)
            };
            Assert.Null(Build(snapshot, calibration));
        }
    }

    [Fact]
    public void Build_RejectsUnknownViewKindWithoutThrowing()
    {
        var valid = ValidMiniSnapshot();
        var invalidKind = valid.MapViews.MiniMap with
        {
            Kind = (AreaMapViewKind)99
        };
        var snapshot = valid with
        {
            MapViews = new AreaMapViewsSnapshot(
                valid.MapViews.LargeMap,
                invalidKind)
        };

        var exception = Record.Exception(() => Build(
            snapshot,
            AreaProjectionProbeCalibration.CreateReferenceInitial()));

        Assert.Null(exception);
        Assert.Null(Build(
            snapshot,
            AreaProjectionProbeCalibration.CreateReferenceInitial()));
    }

    [Fact]
    public void CreateReferenceInitial_WrapsVerifiedCoreProfile()
    {
        var profile = AreaMapProjectionProfile.Verified;
        var calibration = AreaProjectionProbeCalibration.CreateReferenceInitial();

        Assert.Same(profile.LargeMap, calibration.LargeMap);
        Assert.Same(profile.MiniMap, calibration.MiniMap);
        Assert.Equal(
            profile.MiniMapVisibleCenterSafeInsetRatio,
            calibration.MiniMapVisibleCenterSafeInsetRatio);
    }

    [Fact]
    public void Build_MiniMapSafeInsetUsesTheCalibrationRatio()
    {
        var viewport = new AreaUiRect(0, 0, 200, 100);
        var snapshot = Snapshot(
            AreaMapSnapshotStatus.Stable,
            areaHash: 0xFE2A4CD3,
            player: Player(Vector2.Zero),
            largeMap: View(
                AreaMapViewKind.LargeMap,
                isVisible: false,
                new AreaUiRect(0, 0, 400, 200)),
            miniMap: View(AreaMapViewKind.MiniMap, isVisible: true, viewport) with
            {
                Zoom = 1f
            },
            contents:
            [
                Content(
                    "left",
                    AreaContentKind.Boss,
                    AreaContentPhase.Available,
                    new Vector2(-100, 0))
            ]);
        var calibration = Calibration(
            miniTarget: new AreaMapLinearTransform(0.01f, 0, 0, 0.01f),
            largeTarget: AreaMapLinearTransform.Identity) with
        {
            MiniMapVisibleCenterSafeInsetRatio = 0.2f
        };

        var frame = AreaProjectionProbeCoordinator.Build(
            ForegroundWindow(),
            snapshot,
            AreaProjectionProbeMode.Targets,
            calibration,
            "1-200");

        Assert.NotNull(frame);
        Assert.Equal(new Vector2(20, 50), Assert.Single(frame.Marks).Center);
    }

    [Fact]
    public void Build_TargetsModeProjectsOnlySupportedContentAndPreservesPhase()
    {
        var viewport = new AreaUiRect(0, 0, 200, 100);
        var snapshot = Snapshot(
            AreaMapSnapshotStatus.Stable,
            areaHash: 0xFE2A4CD3,
            player: Player(Vector2.Zero),
            largeMap: View(
                AreaMapViewKind.LargeMap,
                isVisible: false,
                new AreaUiRect(0, 0, 400, 200)),
            miniMap: View(
                AreaMapViewKind.MiniMap,
                isVisible: true,
                viewport) with { Zoom = 1f },
            contents:
            [
                Content("boss", AreaContentKind.Boss, AreaContentPhase.Available, Vector2.Zero),
                Content("candidate", AreaContentKind.BossCandidate, AreaContentPhase.Unknown, new Vector2(10, 0)),
                Content("expedition", AreaContentKind.Expedition, AreaContentPhase.Selected, new Vector2(0, 10)),
                Content("unknown", AreaContentKind.Unknown, AreaContentPhase.Unknown, Vector2.Zero),
                Content("ritual", AreaContentKind.Ritual, AreaContentPhase.Active, Vector2.Zero),
                Content("outside", AreaContentKind.Boss, AreaContentPhase.Completed, new Vector2(110, 0))
            ]);
        var calibration = Calibration(
            miniTarget: new AreaMapLinearTransform(0.01f, 0, 0, 0.01f),
            largeTarget: new AreaMapLinearTransform(0.02f, 0, 0, 0.02f));

        var frame = AreaProjectionProbeCoordinator.Build(
            ForegroundWindow(),
            snapshot,
            AreaProjectionProbeMode.Targets,
            calibration,
            "1-200");

        Assert.NotNull(frame);
        Assert.False(frame.DrawViewportGuide);
        Assert.Collection(
            frame.Marks,
            mark =>
            {
                Assert.Equal("boss", mark.TargetId);
                Assert.Equal(AreaContentKind.Boss, mark.ContentKind);
                Assert.Equal(AreaContentPhase.Available, mark.Phase);
                Assert.Equal(new Vector2(100, 50), mark.Center);
                Assert.Equal("M-B01", mark.Label);
            },
            mark =>
            {
                Assert.Equal("candidate", mark.TargetId);
                Assert.Equal(AreaContentKind.BossCandidate, mark.ContentKind);
                Assert.Equal(AreaContentPhase.Unknown, mark.Phase);
                Assert.Equal(new Vector2(110, 50), mark.Center);
                Assert.Equal("M-C02", mark.Label);
            },
            mark =>
            {
                Assert.Equal("expedition", mark.TargetId);
                Assert.Equal(AreaContentKind.Expedition, mark.ContentKind);
                Assert.Equal(AreaContentPhase.Selected, mark.Phase);
                Assert.Equal(new Vector2(100, 60), mark.Center);
                Assert.Equal("M-E03", mark.Label);
            });
        Assert.Equal(frame.Marks.Count, frame.Trace.Targets.Count);
        Assert.DoesNotContain(frame.Marks, mark => mark.TargetId == "outside");
    }

    [Fact]
    public void Build_TargetsModeClampsMiniMapBoundaryCentersAndRejectsRawOutsideCenter()
    {
        var viewport = new AreaUiRect(0, 0, 200, 100);
        var snapshot = Snapshot(
            AreaMapSnapshotStatus.Stable,
            areaHash: 0xFE2A4CD3,
            player: Player(Vector2.Zero),
            largeMap: View(
                AreaMapViewKind.LargeMap,
                isVisible: false,
                new AreaUiRect(0, 0, 400, 200)),
            miniMap: View(AreaMapViewKind.MiniMap, isVisible: true, viewport) with
            {
                Zoom = 1f
            },
            contents:
            [
                Content("left", AreaContentKind.Boss, AreaContentPhase.Available, new Vector2(-100, 0)),
                Content("right", AreaContentKind.Boss, AreaContentPhase.Available, new Vector2(100, 0)),
                Content("top", AreaContentKind.Expedition, AreaContentPhase.Available, new Vector2(0, -50)),
                Content("bottom", AreaContentKind.Expedition, AreaContentPhase.Available, new Vector2(0, 50)),
                Content("above", AreaContentKind.Expedition, AreaContentPhase.Available, new Vector2(0, -51))
            ]);
        var calibration = Calibration(
            miniTarget: new AreaMapLinearTransform(0.01f, 0, 0, 0.01f),
            largeTarget: AreaMapLinearTransform.Identity);

        var frame = AreaProjectionProbeCoordinator.Build(
            ForegroundWindow(),
            snapshot,
            AreaProjectionProbeMode.Targets,
            calibration,
            "1-201");

        Assert.NotNull(frame);
        Assert.Equal(
            new[] { "left", "right", "top", "bottom" },
            frame.Marks.Select(mark => mark.TargetId));
        Assert.Equal(new Vector2(8, 50), frame.Marks[0].Center);
        Assert.Equal(new Vector2(192, 50), frame.Marks[1].Center);
        Assert.Equal(new Vector2(100, 8), frame.Marks[2].Center);
        Assert.Equal(new Vector2(100, 92), frame.Marks[3].Center);
        Assert.Equal(
            frame.Marks.Select(mark => mark.Center),
            frame.Trace.Targets.Select(target => target.PredictedClientPoint));
    }

    [Fact]
    public void Build_TargetsModeKeepsLargeMapBoundaryCentersUnclamped()
    {
        var viewport = new AreaUiRect(0, 0, 200, 100);
        var snapshot = Snapshot(
            AreaMapSnapshotStatus.Stable,
            areaHash: 0xFE2A4CD3,
            player: Player(Vector2.Zero),
            largeMap: View(AreaMapViewKind.LargeMap, isVisible: true, viewport) with
            {
                Zoom = 1f
            },
            miniMap: View(
                AreaMapViewKind.MiniMap,
                isVisible: false,
                new AreaUiRect(0, 0, 200, 100)),
            contents:
            [
                Content("left", AreaContentKind.Boss, AreaContentPhase.Available, new Vector2(-100, 0)),
                Content("right", AreaContentKind.Boss, AreaContentPhase.Available, new Vector2(100, 0))
            ]);
        var calibration = Calibration(
            miniTarget: AreaMapLinearTransform.Identity,
            largeTarget: new AreaMapLinearTransform(0.01f, 0, 0, 0.01f));

        var frame = AreaProjectionProbeCoordinator.Build(
            ForegroundWindow(),
            snapshot,
            AreaProjectionProbeMode.Targets,
            calibration,
            "1-202");

        Assert.NotNull(frame);
        Assert.Equal(new Vector2(0, 50), frame.Marks[0].Center);
        Assert.Equal(new Vector2(200, 50), frame.Marks[1].Center);
    }

    [Fact]
    public void Build_TargetsModeUsesOnlySelectedViewParameters()
    {
        var miniViewport = new AreaUiRect(0, 0, 200, 100);
        var largeViewport = new AreaUiRect(0, 0, 400, 200);
        var miniView = View(
            AreaMapViewKind.MiniMap,
            isVisible: true,
            miniViewport) with { Shift = new Vector2(2, 3), Zoom = 1f };
        var largeView = View(
            AreaMapViewKind.LargeMap,
            isVisible: false,
            largeViewport) with { Shift = new Vector2(2, 3), Zoom = 1f };
        var contents = new[]
        {
            Content("boss", AreaContentKind.Boss, AreaContentPhase.Available, new Vector2(10, 0))
        };
        var miniSnapshot = Snapshot(
            AreaMapSnapshotStatus.Stable,
            0xFE2A4CD3,
            Player(Vector2.Zero),
            largeView,
            miniView,
            contents);
        var largeSnapshot = miniSnapshot with
        {
            MapViews = new AreaMapViewsSnapshot(
                largeView with { IsVisible = true },
                miniView with { IsVisible = false })
        };
        var calibration = new AreaProjectionProbeCalibration(
            new AreaMapProjectionParameters(
                AreaMapViewKind.LargeMap,
                new AreaMapLinearTransform(0.02f, 0, 0, 0.02f),
                new AreaMapLinearTransform(2, 0, 0, 2),
                new Vector2(5, 0)),
            new AreaMapProjectionParameters(
                AreaMapViewKind.MiniMap,
                new AreaMapLinearTransform(0.01f, 0, 0, 0.01f),
                AreaMapLinearTransform.Identity,
                Vector2.Zero));

        var miniFrame = AreaProjectionProbeCoordinator.Build(
            ForegroundWindow(),
            miniSnapshot,
            AreaProjectionProbeMode.Targets,
            calibration,
            "1-202");
        var largeFrame = AreaProjectionProbeCoordinator.Build(
            ForegroundWindow(),
            largeSnapshot,
            AreaProjectionProbeMode.Targets,
            calibration,
            "1-203");

        Assert.NotNull(miniFrame);
        Assert.NotNull(largeFrame);
        Assert.Equal(new Vector2(112, 53), miniFrame.Marks.Single().Center);
        Assert.Equal(new Vector2(249, 106), largeFrame.Marks.Single().Center);
        Assert.Equal(AreaMapViewKind.MiniMap, miniFrame.Trace.Parameters.Kind);
        Assert.Equal(AreaMapViewKind.LargeMap, largeFrame.Trace.Parameters.Kind);
    }

    private static AreaProjectionProbeFrame? Build(
        AreaMapSnapshot snapshot,
        AreaProjectionProbeCalibration calibration)
        => AreaProjectionProbeCoordinator.Build(
            ForegroundWindow(),
            snapshot,
            AreaProjectionProbeMode.Viewport,
            calibration,
            "1-104");

    private static AreaMapSnapshot ValidMiniSnapshot()
        => Snapshot(
            AreaMapSnapshotStatus.Stable,
            areaHash: 0xFE2A4CD3,
            player: Player(),
            largeMap: View(
                AreaMapViewKind.LargeMap,
                isVisible: false,
                new AreaUiRect(0, 0, 908, 600)),
            miniMap: View(
                AreaMapViewKind.MiniMap,
                isVisible: true,
                MiniViewport));

    private static AreaMapSnapshot Snapshot(
        AreaMapSnapshotStatus status,
        uint areaHash,
        AreaPlayerSnapshot? player,
        AreaMapViewSnapshot largeMap,
        AreaMapViewSnapshot miniMap,
        IReadOnlyList<AreaContentSnapshot>? contents = null)
        => new(
            DateTimeOffset.Parse("2026-08-01T10:30:38.4039012+00:00"),
            29368,
            "poe2-test-profile",
            status,
            new AreaIdentity(areaHash, "MapFlotsam", 79, 1),
            player,
            [],
            contents ?? [],
            [],
            null,
            new AreaMapViewsSnapshot(largeMap, miniMap),
            []);

    private static AreaPlayerSnapshot Player()
        => Player(new Vector2(963.2456f, 1214.7452f));

    private static AreaPlayerSnapshot Player(Vector2 gridPosition)
        => new(
            "player",
            97,
            new Vector3(10470.061f, 13203.752f, -587.8245f),
            gridPosition);

    private static AreaContentSnapshot Content(
        string id,
        AreaContentKind kind,
        AreaContentPhase phase,
        Vector2 gridPosition)
        => new(
            id,
            id,
            id,
            kind,
            phase,
            Vector3.Zero,
            gridPosition,
            1f,
            null,
            []);

    private static AreaProjectionProbeCalibration Calibration(
        AreaMapLinearTransform miniTarget,
        AreaMapLinearTransform largeTarget)
        => new(
            new AreaMapProjectionParameters(
                AreaMapViewKind.LargeMap,
                largeTarget,
                AreaMapLinearTransform.Identity,
                Vector2.Zero),
            new AreaMapProjectionParameters(
                AreaMapViewKind.MiniMap,
                miniTarget,
                AreaMapLinearTransform.Identity,
                Vector2.Zero));

    private static AreaMapViewSnapshot View(
        AreaMapViewKind kind,
        bool isVisible,
        AreaUiRect viewport)
        => new(
            kind,
            AreaMapViewAvailability.Verified,
            isVisible,
            Vector2.Zero,
            1.5f,
            0f,
            false,
            viewport,
            0.85f);

    private static GameWindowSnapshot ForegroundWindow()
        => new(
            1,
            29368,
            new Rectangle(100, 100, 908, 600),
            true,
            false)
        {
            ClientBounds = new Rectangle(100, 100, 908, 600)
        };
}
