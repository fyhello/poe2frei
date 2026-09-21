using FreiAtlas.Core.Area;
using FreiAtlas.Game.Replay;
using FreiAtlas.Host;
using Xunit.Abstractions;

namespace FreiAtlas.Host.Tests;

public sealed class LiveAreaRecordTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Live")]
    public void AreaRecordProbe_RecordsAndReplaysMapViewToggle()
    {
        if (!TryReadSettings(out var processId, out var directory, out var seconds))
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(seconds));
        var runnerOutput = new StringWriter();

        var exitCode = new AreaCommandRunner(runnerOutput).RunRecord(
            processId,
            directory,
            cancellation.Token);

        output.WriteLine(runnerOutput.ToString());
        Assert.Equal(0, exitCode);
        using var replay = ReplayAreaMapProvider.Load(directory);
        var sawLargeHiddenMiniVisible = false;
        var sawLargeVisibleMiniHidden = false;
        while (replay.ReadNext())
        {
            var views = replay.Current.MapViews;
            if (views.LargeMap.Availability == AreaMapViewAvailability.Verified
                && views.MiniMap.Availability == AreaMapViewAvailability.Verified)
            {
                sawLargeHiddenMiniVisible |= !views.LargeMap.IsVisible
                                             && views.MiniMap.IsVisible;
                sawLargeVisibleMiniHidden |= views.LargeMap.IsVisible
                                             && !views.MiniMap.IsVisible;
            }
        }

        Assert.True(
            sawLargeHiddenMiniVisible,
            "Replay did not contain a Verified large-map-hidden/minimap-visible frame.");
        Assert.True(
            sawLargeVisibleMiniHidden,
            "Replay did not contain a Verified large-map-visible/minimap-hidden frame.");
    }

    [Fact]
    [Trait("Category", "Live")]
    public void AreaRecordProbe_RecordsAreaTransitionWithClearedLoadingState()
    {
        if (!TryReadSettings(out var processId, out var directory, out var seconds))
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(seconds));
        var runnerOutput = new StringWriter();

        var exitCode = new AreaCommandRunner(runnerOutput).RunRecord(
            processId,
            directory,
            cancellation.Token);

        output.WriteLine(runnerOutput.ToString());
        Assert.Equal(0, exitCode);
        using var replay = ReplayAreaMapProvider.Load(directory);
        var areaHashes = new HashSet<uint>();
        var sessionSequences = new HashSet<long>();
        var clearedLoadingFrames = 0;
        while (replay.ReadNext())
        {
            var snapshot = replay.Current;
            if (snapshot.Status is AreaMapSnapshotStatus.Stable
                or AreaMapSnapshotStatus.Degraded)
            {
                areaHashes.Add(snapshot.Area.AreaHash);
                sessionSequences.Add(snapshot.Area.SessionSequence);
            }

            if (snapshot.Status == AreaMapSnapshotStatus.Loading
                && snapshot.Entities.Count == 0
                && snapshot.Contents.Count == 0
                && snapshot.Landmarks.Count == 0
                && snapshot.Terrain is null
                && snapshot.MapViews == AreaMapViewsSnapshot.Unavailable)
            {
                clearedLoadingFrames++;
            }
        }

        output.WriteLine($"AreaHashes={string.Join(',', areaHashes.Order())}");
        output.WriteLine(
            $"SessionSequences={string.Join(',', sessionSequences.Order())}");
        output.WriteLine($"ClearedLoadingFrames={clearedLoadingFrames}");
        Assert.True(areaHashes.Count >= 2, "Replay did not contain two stable areas.");
        Assert.True(
            sessionSequences.Count >= 2,
            "Replay did not contain two stable area sessions.");
        Assert.True(
            clearedLoadingFrames >= 2,
            "Replay did not contain both initial and transition Loading clear states.");
    }

    private bool TryReadSettings(
        out int processId,
        out string directory,
        out int seconds)
    {
        var configuredPid = Environment.GetEnvironmentVariable("FREI_LIVE_PID");
        var configuredDirectory = Environment.GetEnvironmentVariable(
            "FREI_LIVE_AREA_RECORD_PATH");
        if (string.IsNullOrWhiteSpace(configuredPid)
            || string.IsNullOrWhiteSpace(configuredDirectory))
        {
            output.WriteLine(
                "Skipped: FREI_LIVE_PID and FREI_LIVE_AREA_RECORD_PATH were not both provided; no process was accessed.");
            processId = 0;
            directory = string.Empty;
            seconds = 0;
            return false;
        }

        Assert.True(
            int.TryParse(configuredPid, out processId) && processId > 0,
            "FREI_LIVE_PID must contain one positive process ID.");
        directory = configuredDirectory;
        seconds = ReadDurationSeconds();
        return true;
    }

    private static int ReadDurationSeconds()
    {
        var configured = Environment.GetEnvironmentVariable(
            "FREI_LIVE_AREA_RECORD_SECONDS");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return 15;
        }

        Assert.True(
            int.TryParse(configured, out var seconds)
            && seconds is >= 5 and <= 60,
            "FREI_LIVE_AREA_RECORD_SECONDS must be an integer from 5 through 60.");
        return seconds;
    }
}
