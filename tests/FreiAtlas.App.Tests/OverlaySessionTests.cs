using FreiAtlas.App.Runtime;
using FreiAtlas.Host;

namespace FreiAtlas.App.Tests;

public sealed class OverlaySessionTests
{
    [Fact]
    public void RequestStopCore_HidesBeforeCancellationIsObservable()
    {
        using var cancellation = new CancellationTokenSource();
        var stopController = new OverlayStopController();
        bool? cancellationObservedByHide = null;
        stopController.Register(() =>
            cancellationObservedByHide = cancellation.IsCancellationRequested);

        OverlaySession.RequestStopCore(cancellation, stopController);

        Assert.False(cancellationObservedByHide);
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public void RequestStopCore_CancelsWhenHideThrows()
    {
        using var cancellation = new CancellationTokenSource();
        var stopController = new OverlayStopController();
        stopController.Register(() => throw new InvalidOperationException("hide"));

        Assert.Throws<InvalidOperationException>(() =>
            OverlaySession.RequestStopCore(cancellation, stopController));

        Assert.True(cancellation.IsCancellationRequested);
    }
}
