using System.Drawing;
using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Contracts;
using FreiAtlas.Platform.Windows.Overlay;
using FreiAtlas.Platform.Windows.Windows;

namespace FreiAtlas.Host.Tests;

public sealed class AreaProjectionProbeRunnerTests
{
    [Fact]
    public void Run_InvalidSnapshotHidesWithoutWritingOrRendering()
    {
        var calls = new List<string>();
        var surface = new TestSurface(calls, pumpResults: [true, false]);
        var trace = new TestTraceSink(calls);
        var runner = new AreaProjectionProbeRunner(TextWriter.Null);

        var exitCode = runner.Run(
            new TestAreaMapApi(AreaMapSnapshot.Loading(29368, "profile", 1)),
            ForegroundWindow,
            surface,
            trace,
            AreaProjectionProbeMode.Viewport,
            AreaProjectionProbeCalibration.CreateReferenceInitial(),
            CancellationToken.None,
            TimeSpan.Zero);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, surface.HideCount);
        Assert.Equal(0, surface.RenderCount);
        Assert.Equal(0, trace.WriteCount);
    }

    [Fact]
    public void Run_ValidFrameWritesTraceBeforeRendering()
    {
        var calls = new List<string>();
        var surface = new TestSurface(calls, pumpResults: [true, false]);
        var trace = new TestTraceSink(calls);
        var runner = new AreaProjectionProbeRunner(TextWriter.Null);

        var exitCode = runner.Run(
            new TestAreaMapApi(StableMiniSnapshot()),
            ForegroundWindow,
            surface,
            trace,
            AreaProjectionProbeMode.Targets,
            Calibration(),
            CancellationToken.None,
            TimeSpan.Zero);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, surface.RenderCount);
        Assert.Equal(1, trace.WriteCount);
        Assert.Equal("trace", calls[0]);
        Assert.Equal("render", calls[1]);
        var visual = Assert.Single(surface.Frames);
        var mark = Assert.Single(visual.Marks);
        Assert.Equal(AreaProjectionProbeVisualKind.Boss, mark.Kind);
        Assert.Equal("M-C01", mark.Label);
    }

    [Fact]
    public void Run_TraceIOExceptionHidesAndReturnsExplicitFailure()
    {
        var calls = new List<string>();
        var surface = new TestSurface(calls, pumpResults: [true, false]);
        var trace = new TestTraceSink(calls)
        {
            Failure = new IOException("disk-full")
        };
        using var output = new StringWriter();
        var runner = new AreaProjectionProbeRunner(output);

        var exitCode = runner.Run(
            new TestAreaMapApi(StableMiniSnapshot()),
            ForegroundWindow,
            surface,
            trace,
            AreaProjectionProbeMode.Targets,
            Calibration(),
            CancellationToken.None,
            TimeSpan.Zero);

        Assert.Equal(2, exitCode);
        Assert.Equal(0, surface.RenderCount);
        Assert.True(surface.HideCount >= 1);
        Assert.Contains("reason=area-projection-log-write-failed", output.ToString());
        Assert.Contains("error=IOException", output.ToString());
    }

    [Fact]
    public void Run_DerivedIOExceptionReportsStableBaseErrorContract()
    {
        var calls = new List<string>();
        var surface = new TestSurface(calls, pumpResults: [true]);
        var trace = new TestTraceSink(calls)
        {
            Failure = new DirectoryNotFoundException("missing-log-directory")
        };
        using var output = new StringWriter();
        var runner = new AreaProjectionProbeRunner(output);

        var exitCode = runner.Run(
            new TestAreaMapApi(StableMiniSnapshot()),
            ForegroundWindow,
            surface,
            trace,
            AreaProjectionProbeMode.Targets,
            Calibration(),
            CancellationToken.None,
            TimeSpan.Zero);

        Assert.Equal(2, exitCode);
        Assert.Contains("error=IOException", output.ToString());
        Assert.DoesNotContain("error=DirectoryNotFoundException", output.ToString());
    }

    [Fact]
    public void Run_GameWindowDisappearsHidesAndReturnsSuccess()
    {
        var calls = new List<string>();
        var surface = new TestSurface(calls, pumpResults: [true, true]);
        var trace = new TestTraceSink(calls);
        var runner = new AreaProjectionProbeRunner(TextWriter.Null);

        var exitCode = runner.Run(
            new TestAreaMapApi(StableMiniSnapshot()),
            () => null,
            surface,
            trace,
            AreaProjectionProbeMode.Targets,
            Calibration(),
            CancellationToken.None,
            TimeSpan.Zero);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, surface.HideCount);
        Assert.Equal(0, surface.RenderCount);
        Assert.Equal(0, trace.WriteCount);
    }

    [Fact]
    public void Run_PreCanceledTokenHidesAndReturnsSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = new List<string>();
        var surface = new TestSurface(calls, pumpResults: [true]);
        var runner = new AreaProjectionProbeRunner(TextWriter.Null);

        var exitCode = runner.Run(
            new TestAreaMapApi(StableMiniSnapshot()),
            ForegroundWindow,
            surface,
            new TestTraceSink(calls),
            AreaProjectionProbeMode.Viewport,
            Calibration(),
            cancellation.Token,
            TimeSpan.Zero);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, surface.HideCount);
        Assert.Equal(0, surface.RenderCount);
    }

    [Fact]
    public void CreateProbeFrameId_IsStableForTheSameAtomicSnapshot()
    {
        var snapshot = StableMiniSnapshot();

        var left = AreaProjectionProbeRunner.CreateProbeFrameId(snapshot);
        var right = AreaProjectionProbeRunner.CreateProbeFrameId(snapshot);

        Assert.Equal(left, right);
        Assert.StartsWith("1-", left, StringComparison.Ordinal);
        Assert.DoesNotContain("29368", left, StringComparison.Ordinal);
    }

    private static AreaMapSnapshot StableMiniSnapshot()
    {
        var largeMap = View(
            AreaMapViewKind.LargeMap,
            isVisible: false,
            new AreaUiRect(0, 0, 908, 600));
        var miniMap = View(
            AreaMapViewKind.MiniMap,
            isVisible: true,
            new AreaUiRect(0, 0, 200, 100)) with { Zoom = 1f };
        return new AreaMapSnapshot(
            DateTimeOffset.Parse("2026-08-01T10:30:38.4039012+00:00"),
            29368,
            "poe2-test-profile",
            AreaMapSnapshotStatus.Stable,
            new AreaIdentity(0xFE2A4CD3, "MapFlotsam", 79, 1),
            new AreaPlayerSnapshot("player", 97, Vector3.Zero, Vector2.Zero),
            [],
            [new AreaContentSnapshot(
                "candidate",
                "boss-candidate",
                "Boss",
                AreaContentKind.BossCandidate,
                AreaContentPhase.Unknown,
                Vector3.Zero,
                new Vector2(10, 0),
                1f,
                null,
                [])],
            [],
            null,
            new AreaMapViewsSnapshot(largeMap, miniMap),
            []);
    }

    private static AreaProjectionProbeCalibration Calibration()
        => new(
            new AreaMapProjectionParameters(
                AreaMapViewKind.LargeMap,
                new AreaMapLinearTransform(0.01f, 0, 0, 0.01f),
                AreaMapLinearTransform.Identity,
                Vector2.Zero),
            new AreaMapProjectionParameters(
                AreaMapViewKind.MiniMap,
                new AreaMapLinearTransform(0.01f, 0, 0, 0.01f),
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

    private static GameWindowSnapshot? ForegroundWindow()
        => new(
            1,
            29368,
            new Rectangle(100, 100, 908, 600),
            true,
            false)
        {
            ClientBounds = new Rectangle(100, 100, 908, 600)
        };

    private sealed class TestAreaMapApi : IAreaMapApi
    {
        public TestAreaMapApi(AreaMapSnapshot current)
        {
            Current = current;
        }

        public AreaMapSnapshot Current { get; }

        public event Action<AreaMapSnapshot>? SnapshotChanged
        {
            add { }
            remove { }
        }
    }

    private sealed class TestSurface : IAreaProjectionProbeSurface
    {
        private readonly List<string> _calls;
        private readonly Queue<bool> _pumpResults;

        public TestSurface(List<string> calls, IEnumerable<bool> pumpResults)
        {
            _calls = calls;
            _pumpResults = new Queue<bool>(pumpResults);
        }

        public int HideCount { get; private set; }

        public int RenderCount { get; private set; }

        public List<AreaProjectionProbeFrameVisual> Frames { get; } = [];

        public bool PumpMessages()
            => _pumpResults.Count > 0 && _pumpResults.Dequeue();

        public void Render(AreaProjectionProbeFrameVisual frame)
        {
            _calls.Add("render");
            RenderCount++;
            Frames.Add(frame);
        }

        public void Hide()
        {
            HideCount++;
        }

        public void RequestHide()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class TestTraceSink : IAreaProjectionProbeTraceSink
    {
        private readonly List<string> _calls;

        public TestTraceSink(List<string> calls)
        {
            _calls = calls;
        }

        public Exception? Failure { get; init; }

        public int WriteCount { get; private set; }

        public string OutputPath => "memory.ndjson";

        public void Write(AreaProjectionProbeTraceFrame frame)
        {
            _calls.Add("trace");
            WriteCount++;
            if (Failure is not null)
            {
                throw Failure;
            }
        }

        public void Dispose()
        {
        }
    }
}
