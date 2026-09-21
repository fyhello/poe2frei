using FreiAtlas.Host;
using FreiAtlas.Core.Area;
using System.Numerics;

namespace FreiAtlas.Host.Tests;

public sealed class HostRuntimeTests
{
    [Fact]
    public void AreaFormatter_ReportsStructuredSnapshotSummary()
    {
        var snapshot = new AreaMapSnapshot(
            DateTimeOffset.UtcNow,
            30744,
            "poe2-test-profile",
            AreaMapSnapshotStatus.Stable,
            new AreaIdentity(0x1234, "MapPlantation", 79, 4),
            new AreaPlayerSnapshot(
                "测试角色",
                95,
                new Vector3(10, 20, 0),
                new Vector2(1, 2)),
            [
                new AreaEntitySnapshot(
                    10,
                    "Metadata/Monsters/Test",
                    "Monster",
                    AreaEntityCategory.Monster,
                    Vector3.Zero,
                    Vector2.Zero,
                    AreaEntityDisposition.Hostile,
                    AreaEntityRarity.Normal,
                    100,
                    100,
                    false,
                    false,
                    AreaChestState.NotApplicable,
                    []),
                new AreaEntitySnapshot(
                    11,
                    "Metadata/NPC/Test",
                    "Npc",
                    AreaEntityCategory.Npc,
                    Vector3.Zero,
                    Vector2.Zero,
                    AreaEntityDisposition.Friendly,
                    AreaEntityRarity.NonMonster,
                    0,
                    0,
                    false,
                    false,
                    AreaChestState.NotApplicable,
                    [])
            ],
            [new AreaContentSnapshot(
                "boss:4:1",
                "map_boss",
                "地图头目",
                AreaContentKind.Boss,
                AreaContentPhase.Available,
                Vector3.Zero,
                Vector2.Zero,
                1f,
                1,
                [])],
            [],
            new AreaTerrainSnapshot(4, 5, new byte[20], ["tiles/a"]),
            new AreaMapViewsSnapshot(
                new AreaMapViewSnapshot(
                    AreaMapViewKind.LargeMap,
                    AreaMapViewAvailability.Unverified,
                    true,
                    Vector2.Zero,
                    1f,
                    0f,
                    false,
                    new AreaUiRect(0, 0, 900, 600),
                    0.8f),
                new AreaMapViewSnapshot(
                    AreaMapViewKind.MiniMap,
                    AreaMapViewAvailability.Unverified,
                    true,
                    Vector2.Zero,
                    0.5f,
                    0f,
                    false,
                    new AreaUiRect(20, 20, 200, 150),
                    0.8f)),
            []);

        var output = AreaCommandFormatter.FormatSnapshot(snapshot);

        Assert.Contains("pid=30744", output);
        Assert.Contains("area=MapPlantation", output);
        Assert.Contains("boss=1", output);
        Assert.Contains("playerWorld=10,20,0", output);
        Assert.Contains("playerGrid=1,2", output);
        Assert.Contains("entityMonster=1", output);
        Assert.Contains("entityNpc=1", output);
        Assert.Contains("terrain=4x5", output);
        Assert.Contains(
            output,
            line => line.StartsWith("miniMap=Unverified/visible", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_AtlasProbeWithoutPid_ReturnsFailure()
    {
        var runtime = new HostRuntime();

        var exitCode = runtime.Run(new HostOptions(
            ProcessId: null,
            AtlasProbe: true,
            AtlasOverlay: false,
            ReplayPath: null));

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void Run_AtlasOverlayWithoutPid_ReturnsFailure()
    {
        var runtime = new HostRuntime();

        var exitCode = runtime.Run(new HostOptions(
            ProcessId: null,
            AtlasProbe: false,
            AtlasOverlay: true,
            ReplayPath: null));

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void Run_AtlasProbeWithExplicitMissingPid_RoutesToReadOnlyAttach()
    {
        var runtime = new HostRuntime();

        var exitCode = runtime.Run(new HostOptions(
            ProcessId: int.MaxValue,
            AtlasProbe: true,
            AtlasOverlay: false,
            ReplayPath: null));

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void Run_AreaProjectionProbePassesExactArgumentsAndCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        (int Pid, AreaProjectionProbeMode Mode, string Path, CancellationToken Token)? call = null;
        var runtime = new HostRuntime(
            new AreaCommandRunner(TextWriter.Null),
            (pid, mode, path, token) =>
            {
                call = (pid, mode, path, token);
                return 17;
            });
        var options = new HostOptions(29368, false, false, null)
        {
            AreaProjectionProbeMode = AreaProjectionProbeMode.Targets,
            ProjectionLogPath = "data/area-map/projection-current"
        };

        var exitCode = runtime.Run(options, cancellation.Token);

        Assert.Equal(17, exitCode);
        Assert.Equal(29368, call?.Pid);
        Assert.Equal(AreaProjectionProbeMode.Targets, call?.Mode);
        Assert.Equal("data/area-map/projection-current", call?.Path);
        Assert.Equal(cancellation.Token, call?.Token);
    }

    [Fact]
    public void Usage_ContainsCompleteAreaProjectionProbeCommand()
    {
        Assert.Contains(
            "--pid <pid> --area-projection-probe <viewport|targets> --projection-log <directory>",
            HostRuntime.Usage,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_RejectsNullAreaRunnerExplicitly()
    {
        Assert.Throws<ArgumentNullException>(() => new HostRuntime(null!));
    }
}
