using FreiAtlas.Host;
using FreiAtlas.Core.Memory;
using FreiAtlas.Game;
using FreiAtlas.Platform.Windows.Windows;
using FreiAtlas.Platform.Windows.Overlay;
using FreiAtlas.Plugin.ExpeditionPanel;
using System.Drawing;

namespace FreiAtlas.Host.Tests;

public sealed class AtlasOverlayRunnerReliabilityTests
{
    [Fact]
    public void SampleAndObserve_ForwardsClosedSampleResult()
    {
        var expected = new FreiAtlas.Core.Atlas.AtlasSnapshot(
            DateTimeOffset.UnixEpoch,
            FreiAtlas.Core.Atlas.AtlasSnapshotStatus.Loading,
            0,
            0,
            [],
            [],
            null,
            FreiAtlas.Core.Atlas.AtlasProjection.Identity,
            "closed",
            false);
        FreiAtlas.Core.Atlas.AtlasSnapshot? observed = null;

        var result = AtlasOverlayRunner.SampleAndObserve(
            () => expected,
            snapshot => observed = snapshot);

        Assert.Same(expected, result);
        Assert.Same(expected, observed);
    }

    [Fact]
    public void ObserveSamplingTasks_FaultedAtlasLoopCancelsSessionAndPropagatesCause()
    {
        using var cancellation = new CancellationTokenSource();
        var failure = new InvalidOperationException("atlas sample failed");
        var atlasSampling = Task.FromException(failure);
        var areaSampling = Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            AtlasOverlayRunner.ObserveSamplingTasks(
                atlasSampling,
                areaSampling,
                cancellation));

        Assert.Same(failure, thrown);
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public void ObserveSamplingTasks_UnexpectedCompletionFailsInCurrentFrame()
    {
        using var cancellation = new CancellationTokenSource();
        var areaSampling = Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            AtlasOverlayRunner.ObserveSamplingTasks(
                Task.CompletedTask,
                areaSampling,
                cancellation));

        Assert.Contains("atlas", thrown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public void ObserveSamplingTasks_NormalSessionCancellationIsNotAFault()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        AtlasOverlayRunner.ObserveSamplingTasks(
            Task.CompletedTask,
            Task.CompletedTask,
            cancellation);
    }

    [Fact]
    public void RunSamplingSession_SamplingFailureAlwaysHidesAllFourSurfaces()
    {
        using var cancellation = new CancellationTokenSource();
        var failure = new InvalidOperationException("area sample failed");
        var atlasSampling = Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
        var areaSampling = Task.FromException(failure);
        var atlasHidden = false;
        var areaHidden = false;
        var expeditionPanelHidden = false;
        var nativeValueHidden = false;

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            AtlasOverlayRunner.RunSamplingSession(
                cancellation,
                atlasSampling,
                areaSampling,
                (sessionCancellation, atlas, area) =>
                {
                    AtlasOverlayRunner.ObserveSamplingTasks(
                        atlas,
                        area,
                        sessionCancellation);
                    return 0;
                },
                () => atlasHidden = true,
                () => areaHidden = true,
                () => expeditionPanelHidden = true,
                () => nativeValueHidden = true));

        Assert.Same(failure, thrown);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(atlasHidden);
        Assert.True(areaHidden);
        Assert.True(expeditionPanelHidden);
        Assert.True(nativeValueHidden);
    }

    [Fact]
    public void RunSamplingSession_HideFailureStillHidesRemainingSurfaces()
    {
        using var cancellation = new CancellationTokenSource();
        var areaHidden = false;
        var panelHidden = false;
        var nativeHidden = false;

        Assert.Throws<InvalidOperationException>(() =>
            AtlasOverlayRunner.RunSamplingSession(
                cancellation,
                Task.CompletedTask,
                Task.CompletedTask,
                static (_, _, _) => 0,
                static () => throw new InvalidOperationException("atlas hide failed"),
                () => areaHidden = true,
                () => panelHidden = true,
                () => nativeHidden = true));

        Assert.True(areaHidden);
        Assert.True(panelHidden);
        Assert.True(nativeHidden);
    }

    [Fact]
    public void SuppressOverlays_WhenAltIsDown_HidesAllFourSurfacesAndSkipsFrame()
    {
        var hideCounts = new int[4];

        var suppressed = AtlasOverlayRunner.SuppressOverlays(
            true,
            () => hideCounts[0]++,
            () => hideCounts[1]++,
            () => hideCounts[2]++,
            () => hideCounts[3]++);

        Assert.True(suppressed);
        Assert.Equal(new[] { 1, 1, 1, 1 }, hideCounts);
    }

    [Fact]
    public void SuppressOverlays_WhenAltIsUp_DoesNotHideAndContinuesFrame()
    {
        var hideCounts = new int[4];

        var suppressed = AtlasOverlayRunner.SuppressOverlays(
            false,
            () => hideCounts[0]++,
            () => hideCounts[1]++,
            () => hideCounts[2]++,
            () => hideCounts[3]++);

        Assert.False(suppressed);
        Assert.Equal(new[] { 0, 0, 0, 0 }, hideCounts);
    }

    [Fact]
    public void PumpNativeValueSurface_FailureDisablesOnlyNativeSurface()
    {
        var surface = new ThrowingNativeValueSurface();
        var enabled = true;

        var keepRunning = AtlasOverlayRunner.PumpNativeValueSurface(
            surface,
            ref enabled);
        var nextFrameKeepsRunning = AtlasOverlayRunner.PumpNativeValueSurface(
            surface,
            ref enabled);

        Assert.True(keepRunning);
        Assert.True(nextFrameKeepsRunning);
        Assert.False(enabled);
        Assert.Equal(1, surface.PumpCount);
        Assert.Equal(1, surface.HideCount);
    }

    [Fact]
    public void TryUpdateAreaViewport_SubmitsOnlyValidClientDimensionsAtClientOrigin()
    {
        using var memory = new EmptyProcessMemory();
        var provider = new AreaMapProviderService(memory);
        var invalid = Window(new Rectangle(10, 20, 0, 900));
        var minimized = Window(new Rectangle(10, 20, 1600, 900)) with
        {
            IsMinimized = true
        };
        var valid = Window(new Rectangle(10, 20, 1600, 900));

        Assert.False(AtlasOverlayRunner.TryUpdateAreaViewport(provider, null));
        Assert.False(AtlasOverlayRunner.TryUpdateAreaViewport(provider, invalid));
        Assert.False(AtlasOverlayRunner.TryUpdateAreaViewport(provider, minimized));
        Assert.True(AtlasOverlayRunner.TryUpdateAreaViewport(provider, valid));
        Assert.False(AtlasOverlayRunner.TryUpdateAreaViewport(provider, valid));
    }

    private static GameWindowSnapshot Window(Rectangle clientBounds)
        => new(
            1,
            1234,
            new Rectangle(0, 0, 1920, 1080),
            IsForeground: true,
            IsMinimized: false)
        {
            ClientBounds = clientBounds
        };

    private sealed class EmptyProcessMemory : IProcessMemory
    {
        public int ProcessId => 1234;

        public bool TryRead(nint address, Span<byte> destination) => false;
        public bool TryReadInt32(nint address, out int value) => Fail(out value);
        public bool TryReadInt64(nint address, out long value) => Fail(out value);
        public bool TryReadFloat(nint address, out float value) => Fail(out value);
        public bool TryReadPointer(nint address, out nint value) => Fail(out value);

        public bool TryReadUtf16(nint address, int maxChars, out string? value)
        {
            value = null;
            return false;
        }

        public void Dispose()
        {
        }

        private static bool Fail<T>(out T value)
        {
            value = default!;
            return false;
        }
    }

    private sealed class ThrowingNativeValueSurface
        : IExpeditionNativeValueSurface
    {
        public int PumpCount { get; private set; }

        public int HideCount { get; private set; }

        public bool PumpMessages()
        {
            PumpCount++;
            throw new InvalidOperationException("pump failed");
        }

        public void Render(
            Rectangle gameClientBounds,
            ExpeditionNativeValueScene scene)
            => throw new NotSupportedException();

        public void Hide() => HideCount++;

        public void RequestHide()
        {
        }

        public void Dispose()
        {
        }
    }
}
