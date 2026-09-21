using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Game.Replay;
using FreiAtlas.Host;
using FreiAtlas.Platform.Windows.Overlay;
using FreiAtlas.Platform.Windows.Windows;

namespace FreiAtlas.Host.Tests;

public sealed class AreaCommandRunnerTests
{
    [Fact]
    public void RunProbe_UsesOnlyExplicitPidAndDisposesProviderLifetime()
    {
        var output = new StringWriter();
        var source = new TestAreaSamplingApi(
            Snapshot(DateTimeOffset.Parse("2026-08-01T01:02:03Z")) with
            {
                ProcessId = 42123
            },
            Snapshot(DateTimeOffset.Parse("2026-08-01T01:02:04Z")) with
            {
                ProcessId = 42123
            });
        var lifetime = new TestLifetime();
        var factory = new TestAreaSamplingProviderFactory(source, lifetime);
        var runner = new AreaCommandRunner(output, factory);

        var exitCode = runner.RunProbe(42123);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            [new AreaSamplingProviderRequest(42123, false)],
            factory.Requests);
        Assert.True(lifetime.IsDisposed);
        Assert.Contains("pid=42123", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("area=MapOne", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RunProbe_WhenExplicitPidCannotAttach_ReportsFailureWithoutFallback()
    {
        var output = new StringWriter();
        var factory = new RejectingAreaSamplingProviderFactory(
            "read-only-process-attach-failed");
        var runner = new AreaCommandRunner(output, factory);

        var exitCode = runner.RunProbe(99991);

        Assert.Equal(2, exitCode);
        Assert.Equal(
            [new AreaSamplingProviderRequest(99991, false)],
            factory.Requests);
        Assert.Equal(
            $"pid=99991{Environment.NewLine}"
            + $"area=unavailable{Environment.NewLine}"
            + $"reason=read-only-process-attach-failed{Environment.NewLine}",
            output.ToString());
    }

    [Fact]
    public void RunWatch_ReportsOnlyDeclaredStateChangesForExplicitPid()
    {
        var first = Snapshot(DateTimeOffset.Parse("2026-08-01T01:02:03Z"));
        var output = new StringWriter();
        var source = new TestAreaSamplingApi(
            first,
            first with { CapturedAt = first.CapturedAt.AddMilliseconds(33) },
            first with
            {
                CapturedAt = first.CapturedAt.AddMilliseconds(66),
                Player = first.Player! with { WorldPosition = new Vector3(5, 6, 7) }
            },
            Snapshot(
                first.CapturedAt.AddMilliseconds(99),
                AreaContentPhase.Completed));
        var lifetime = new TestLifetime();
        var factory = new TestAreaSamplingProviderFactory(source, lifetime);
        var runner = new AreaCommandRunner(output, factory);

        var exitCode = runner.RunWatch(30744);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            [new AreaSamplingProviderRequest(30744, false)],
            factory.Requests);
        Assert.True(lifetime.IsDisposed);
        Assert.Equal(2, CountOccurrences(output.ToString(), "pid=30744"));
    }

    [Fact]
    public void RunWatch_DoesNotReportWhenEntityCollectionIsOnlyReordered()
    {
        var first = Snapshot(DateTimeOffset.Parse("2026-08-01T01:02:03Z")) with
        {
            Entities = [Entity(10), Entity(11)]
        };
        var reordered = first with
        {
            CapturedAt = first.CapturedAt.AddMilliseconds(33),
            Entities = [Entity(11), Entity(10)]
        };
        var output = new StringWriter();
        var source = new TestAreaSamplingApi(first, reordered);
        var factory = new TestAreaSamplingProviderFactory(
            source,
            new TestLifetime());
        var runner = new AreaCommandRunner(output, factory);

        var exitCode = runner.RunWatch(30744);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, CountOccurrences(output.ToString(), "pid=30744"));
    }

    [Fact]
    public void RunExpeditionProbe_RequestsResearchModeAndFormatsStableSnapshot()
    {
        var snapshot = ExpeditionResearchSnapshot();
        var output = new StringWriter();
        var source = new TestAreaSamplingApi(snapshot, snapshot);
        var lifetime = new TestLifetime();
        var factory = new TestAreaSamplingProviderFactory(source, lifetime);
        var runner = new AreaCommandRunner(output, factory);

        var exitCode = runner.RunExpeditionProbe(30744);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            [new AreaSamplingProviderRequest(30744, true)],
            factory.Requests);
        Assert.True(lifetime.IsDisposed);
        var text = output.ToString();
        Assert.Contains("expeditionResearch=count:2", text, StringComparison.Ordinal);
        Assert.Contains(
            "expedition=entity:957;instance:expedition:1:957;world:10,20,0;grid:1,2;phase:Available;completed:false",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "research=entity:957;source:StateMachine;key:Research.StateMachine.+0x04;value:1;confidence:0.1",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("key=Completed", text, StringComparison.Ordinal);
        Assert.True(
            text.IndexOf("expedition=entity:957", StringComparison.Ordinal)
            < text.IndexOf("expedition=entity:959", StringComparison.Ordinal));
    }

    [Fact]
    public void RunExpeditionProbe_WhenSnapshotIsNotReady_ReturnsFailure()
    {
        var loading = AreaMapSnapshot.Loading(30744, "poe2-test-profile", 1);
        var output = new StringWriter();
        var source = new TestAreaSamplingApi(loading, loading);
        var factory = new TestAreaSamplingProviderFactory(
            source,
            new TestLifetime());
        var runner = new AreaCommandRunner(output, factory);

        var exitCode = runner.RunExpeditionProbe(30744);

        Assert.Equal(2, exitCode);
        Assert.Equal(
            $"pid=30744{Environment.NewLine}"
            + $"area=unavailable{Environment.NewLine}"
            + $"reason=expedition-research-snapshot-not-ready{Environment.NewLine}",
            output.ToString());
    }

    [Fact]
    public void RunExpeditionProbe_WhenValidSnapshotHasNoExpedition_ReportsNone()
    {
        var snapshot = Snapshot(DateTimeOffset.Parse("2026-08-01T01:02:03Z"));
        var output = new StringWriter();
        var source = new TestAreaSamplingApi(snapshot, snapshot);
        var runner = new AreaCommandRunner(
            output,
            new TestAreaSamplingProviderFactory(source, new TestLifetime()));

        var exitCode = runner.RunExpeditionProbe(30744);

        Assert.Equal(0, exitCode);
        Assert.Contains(
            "expeditionResearch=none",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void RunRecord_RecordsEveryPublishedSnapshotAndReportsFrameCount()
    {
        var directory = CreateDirectory();
        try
        {
            var output = new StringWriter();
            var source = new TestAreaSamplingApi(
                Snapshot(DateTimeOffset.Parse("2026-08-01T01:02:03Z")),
                Snapshot(
                    DateTimeOffset.Parse("2026-08-01T01:02:04Z"),
                    AreaContentPhase.Completed));
            var runner = new AreaCommandRunner(output);

            var exitCode = runner.RunRecord(source, directory, CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Equal(
                2,
                File.ReadLines(Path.Combine(directory, "frames.ndjson")).Count());
            Assert.Contains("frames=2", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("areaRecord=", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void RunRecord_WhenCancelled_FinalizesFourLoadableFiles()
    {
        var directory = CreateDirectory();
        try
        {
            var source = new TestAreaSamplingApi(
                Snapshot(DateTimeOffset.Parse("2026-08-01T01:02:03Z")),
                Snapshot(DateTimeOffset.Parse("2026-08-01T01:02:04Z")));
            var runner = new AreaCommandRunner(new StringWriter());
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var exitCode = runner.RunRecord(
                source,
                directory,
                cancellation.Token);

            Assert.Equal(0, exitCode);
            Assert.Equal(
                ["frames.ndjson", "manifest.json", "terrain.json", "unknown.json"],
                Directory.GetFiles(directory)
                    .Select(path => Path.GetFileName(path)!)
                    .Order(StringComparer.Ordinal)
                    .ToArray());
            using var replay = ReplayAreaMapProvider.Load(directory);
            Assert.Equal(2, replay.FrameCount);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void RunRecord_RequestsDefaultProviderModeForExplicitPid()
    {
        var directory = CreateDirectory();
        try
        {
            var snapshot = Snapshot(DateTimeOffset.Parse("2026-08-01T01:02:03Z"));
            var source = new TestAreaSamplingApi(snapshot, snapshot);
            var factory = new TestAreaSamplingProviderFactory(
                source,
                new TestLifetime());
            var runner = new AreaCommandRunner(new StringWriter(), factory);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var exitCode = runner.RunRecord(
                30744,
                directory,
                cancellation.Token);

            Assert.Equal(0, exitCode);
            Assert.Equal(
                [new AreaSamplingProviderRequest(30744, false)],
                factory.Requests);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void HostRuntime_AreaReplayRoutesToRunnerAndReportsFinalState()
    {
        var directory = CreateDirectory();
        try
        {
            using (var recorder = new AreaSnapshotRecorder(directory))
            {
                recorder.Record(Snapshot(DateTimeOffset.Parse("2026-08-01T01:02:03Z")));
                recorder.Record(Snapshot(
                    DateTimeOffset.Parse("2026-08-01T01:02:04Z"),
                    AreaContentPhase.Completed));
            }

            var output = new StringWriter();
            var runtime = new HostRuntime(new AreaCommandRunner(output));
            var options = new HostOptions(null, false, false, null)
            {
                AreaReplayPath = directory
            };

            var exitCode = runtime.Run(options);

            Assert.Equal(0, exitCode);
            var text = output.ToString();
            Assert.Contains("areaReplay=", text, StringComparison.Ordinal);
            Assert.Contains("frames=2", text, StringComparison.Ordinal);
            Assert.Contains("replayed=2", text, StringComparison.Ordinal);
            Assert.Contains("bossPhase=Completed", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void HostRuntime_AreaExpeditionProbeRoutesToResearchRunner()
    {
        var snapshot = ExpeditionResearchSnapshot();
        var output = new StringWriter();
        var source = new TestAreaSamplingApi(snapshot, snapshot);
        var factory = new TestAreaSamplingProviderFactory(
            source,
            new TestLifetime());
        var runtime = new HostRuntime(new AreaCommandRunner(output, factory));
        var options = new HostOptions(30744, false, false, null)
        {
            AreaExpeditionProbe = true
        };

        var exitCode = runtime.Run(options);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            [new AreaSamplingProviderRequest(30744, true)],
            factory.Requests);
        Assert.Contains(
            "expeditionResearch=count:2",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void RunRecord_WhenSnapshotPersistenceFails_ReturnsFailure()
    {
        var directory = CreateDirectory();
        try
        {
            var output = new StringWriter();
            var source = new TestAreaSamplingApi(
                Snapshot(DateTimeOffset.Parse("2026-08-01T01:02:03Z")),
                Snapshot(DateTimeOffset.Parse("2026-08-01T01:02:04Z")) with
                {
                    ProfileId = "different-profile"
                })
            {
                IsolateConsumerFailures = true
            };
            var runner = new AreaCommandRunner(output);

            var exitCode = runner.RunRecord(source, directory, CancellationToken.None);

            Assert.Equal(2, exitCode);
            Assert.Contains(
                "reason=area-record-write-failed",
                output.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void RunProjectionProbe_StartsProviderAndCancelsItWhenSurfaceStops()
    {
        var snapshot = Snapshot(DateTimeOffset.Parse("2026-08-01T01:02:03Z"));
        var source = new TestAreaSamplingApi(snapshot, snapshot)
        {
            HoldUntilCancelled = true
        };
        var surface = new ProjectionProbeSurface(pumpResults: [false]);
        var trace = new ProjectionProbeTraceSink();
        var runner = new AreaCommandRunner(TextWriter.Null);

        var exitCode = runner.RunProjectionProbe(
            source,
            () => null,
            surface,
            trace,
            AreaProjectionProbeMode.Viewport,
            CancellationToken.None,
            TimeSpan.Zero);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, source.SampleWorldCount);
        Assert.Equal(1, source.SampleRealtimeCount);
        Assert.Equal(1, source.StartCount);
        Assert.True(source.StartObservedCancellation);
        Assert.Equal(1, surface.HideCount);
        Assert.Equal(0, trace.WriteCount);
    }

    [Fact]
    public void RunProjectionProbe_WhenExplicitPidCannotAttach_DoesNotCreateLogDirectory()
    {
        var directory = CreateDirectory();
        var output = new StringWriter();
        var factory = new RejectingAreaSamplingProviderFactory(
            "read-only-process-attach-failed");
        var runner = new AreaCommandRunner(output, factory);

        var exitCode = runner.RunProjectionProbe(
            99991,
            AreaProjectionProbeMode.Viewport,
            directory,
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Equal(
            [new AreaSamplingProviderRequest(99991, false)],
            factory.Requests);
        Assert.False(Directory.Exists(directory));
        Assert.Contains(
            "reason=read-only-process-attach-failed",
            output.ToString(),
            StringComparison.Ordinal);
    }

    private static AreaMapSnapshot Snapshot(
        DateTimeOffset capturedAt,
        AreaContentPhase bossPhase = AreaContentPhase.Available)
        => new(
            capturedAt,
            30744,
            "poe2-test-profile",
            AreaMapSnapshotStatus.Stable,
            new AreaIdentity(0xA1, "MapOne", 81, 1),
            new AreaPlayerSnapshot("LiveHero", 95, Vector3.One, Vector2.One),
            [],
            [new AreaContentSnapshot(
                "boss:1:31",
                "map_boss",
                "Replay Boss",
                AreaContentKind.Boss,
                bossPhase,
                Vector3.Zero,
                Vector2.Zero,
                0.95f,
                31,
                [])],
            [],
            new AreaTerrainSnapshot(1, 1, new byte[] { 1 }, ["Metadata/Terrain/One"]),
            AreaMapViewsSnapshot.Unavailable,
            []);

    private static AreaEntitySnapshot Entity(uint entityId)
        => new(
            entityId,
            $"Metadata/Test/{entityId}",
            $"Entity {entityId}",
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
            []);

    private static AreaMapSnapshot ExpeditionResearchSnapshot()
    {
        var capturedAt = DateTimeOffset.Parse("2026-08-01T01:02:03Z");
        var entities = new[]
        {
            ExpeditionEntity(959, complete: true),
            ExpeditionEntity(957, complete: false)
        };
        return Snapshot(capturedAt) with
        {
            Entities = entities,
            Contents =
            [
                ExpeditionContent(
                    959,
                    AreaContentPhase.Completed,
                    new Vector3(30, 40, 0),
                    new Vector2(3, 4),
                    "2"),
                ExpeditionContent(
                    957,
                    AreaContentPhase.Available,
                    new Vector3(10, 20, 0),
                    new Vector2(1, 2),
                    "1")
            ]
        };
    }

    private static AreaEntitySnapshot ExpeditionEntity(uint entityId, bool complete)
        => new(
            entityId,
            "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter",
            "Expedition Encounter",
            AreaEntityCategory.Object,
            Vector3.Zero,
            Vector2.Zero,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster,
            0,
            0,
            true,
            complete,
            AreaChestState.NotApplicable,
            []);

    private static AreaContentSnapshot ExpeditionContent(
        uint entityId,
        AreaContentPhase phase,
        Vector3 world,
        Vector2 grid,
        string researchValue)
        => new(
            $"expedition:1:{entityId}",
            "expedition",
            "Expedition Encounter",
            AreaContentKind.Expedition,
            phase,
            world,
            grid,
            0.9f,
            entityId,
            [
                new AreaContentEvidence(
                    "StateMachine",
                    "Completed",
                    phase == AreaContentPhase.Completed ? "true" : "false",
                    0.9f),
                new AreaContentEvidence(
                    "StateMachine",
                    "Research.StateMachine.+0x04",
                    researchValue,
                    0.1f)
            ]);

    private static string CreateDirectory()
        => Path.Combine(
            Path.GetTempPath(),
            "frei-area-host-tests",
            Guid.NewGuid().ToString("N"));

    private static int CountOccurrences(string value, string expected)
        => value.Split(expected, StringSplitOptions.None).Length - 1;

    private sealed class TestAreaSamplingApi : IAreaSamplingApi
    {
        private readonly Queue<AreaMapSnapshot> _snapshots;

        public TestAreaSamplingApi(params AreaMapSnapshot[] snapshots)
        {
            _snapshots = new Queue<AreaMapSnapshot>(snapshots);
            Current = AreaMapSnapshot.Loading(0, "poe2-test-profile", 0);
        }

        public AreaMapSnapshot Current { get; private set; }

        public bool IsolateConsumerFailures { get; init; }

        public bool HoldUntilCancelled { get; init; }

        public int SampleWorldCount { get; private set; }

        public int SampleRealtimeCount { get; private set; }

        public int StartCount { get; private set; }

        public bool StartObservedCancellation { get; private set; }

        public event Action<AreaMapSnapshot>? SnapshotChanged;

        public AreaMapSnapshot SampleWorld()
        {
            SampleWorldCount++;
            return PublishNext();
        }

        public AreaMapSnapshot SampleRealtime()
        {
            SampleRealtimeCount++;
            return PublishNext();
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            cancellationToken.ThrowIfCancellationRequested();
            while (_snapshots.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PublishNext();
            }

            if (!HoldUntilCancelled)
            {
                return;
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                StartObservedCancellation = true;
                throw;
            }
        }

        private AreaMapSnapshot PublishNext()
        {
            Current = _snapshots.Dequeue();
            try
            {
                SnapshotChanged?.Invoke(Current);
            }
            catch when (IsolateConsumerFailures)
            {
            }

            return Current;
        }
    }

    private sealed class TestAreaSamplingProviderFactory : IAreaSamplingProviderFactory
    {
        private readonly IAreaSamplingApi _provider;
        private readonly IDisposable _lifetime;

        public TestAreaSamplingProviderFactory(
            IAreaSamplingApi provider,
            IDisposable lifetime)
        {
            _provider = provider;
            _lifetime = lifetime;
        }

        public List<AreaSamplingProviderRequest> Requests { get; } = [];

        public bool TryCreate(
            AreaSamplingProviderRequest request,
            out IAreaSamplingApi? provider,
            out IDisposable? lifetime,
            out string reason)
        {
            Requests.Add(request);
            provider = _provider;
            lifetime = _lifetime;
            reason = string.Empty;
            return true;
        }
    }

    private sealed class TestLifetime : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class RejectingAreaSamplingProviderFactory
        : IAreaSamplingProviderFactory
    {
        private readonly string _reason;

        public RejectingAreaSamplingProviderFactory(string reason)
        {
            _reason = reason;
        }

        public List<AreaSamplingProviderRequest> Requests { get; } = [];

        public bool TryCreate(
            AreaSamplingProviderRequest request,
            out IAreaSamplingApi? provider,
            out IDisposable? lifetime,
            out string reason)
        {
            Requests.Add(request);
            provider = null;
            lifetime = null;
            reason = _reason;
            return false;
        }
    }

    private sealed class ProjectionProbeSurface : IAreaProjectionProbeSurface
    {
        private readonly Queue<bool> _pumpResults;

        public ProjectionProbeSurface(IEnumerable<bool> pumpResults)
        {
            _pumpResults = new Queue<bool>(pumpResults);
        }

        public int HideCount { get; private set; }

        public bool PumpMessages()
            => _pumpResults.Count > 0 && _pumpResults.Dequeue();

        public void Render(AreaProjectionProbeFrameVisual frame)
        {
        }

        public void Hide() => HideCount++;

        public void RequestHide()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class ProjectionProbeTraceSink : IAreaProjectionProbeTraceSink
    {
        public string OutputPath => "memory.ndjson";

        public int WriteCount { get; private set; }

        public void Write(AreaProjectionProbeTraceFrame frame)
            => WriteCount++;

        public void Dispose()
        {
        }
    }
}
