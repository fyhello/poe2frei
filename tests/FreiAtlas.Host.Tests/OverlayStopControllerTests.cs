using FreiAtlas.Host;

namespace FreiAtlas.Host.Tests;

public sealed class OverlayStopControllerTests
{
    [Fact]
    public void RequestStop_AfterRegister_InvokesCallbackOnlyOnce()
    {
        var controller = new OverlayStopController();
        var invocationCount = 0;
        controller.Register(() => invocationCount++);

        controller.RequestStop();
        controller.RequestStop();

        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public void Register_AfterRequestStop_InvokesCallbackImmediately()
    {
        var controller = new OverlayStopController();
        var invocationCount = 0;
        controller.RequestStop();

        controller.Register(() => invocationCount++);

        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public void Unregister_RemovesOnlyMatchingCallback()
    {
        var controller = new OverlayStopController();
        var invocationCount = 0;
        Action registered = () => invocationCount++;
        Action different = () => invocationCount += 10;
        controller.Register(registered);

        controller.Unregister(different);
        controller.RequestStop();

        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public void Unregister_MatchingCallbackPreventsInvocation()
    {
        var controller = new OverlayStopController();
        var invocationCount = 0;
        Action callback = () => invocationCount++;
        controller.Register(callback);

        controller.Unregister(callback);
        controller.RequestStop();

        Assert.Equal(0, invocationCount);
    }

    [Fact]
    public void RequestStop_InvokesEveryRegisteredCallbackOnlyOnce()
    {
        var controller = new OverlayStopController();
        var firstCount = 0;
        var secondCount = 0;
        controller.Register(() => firstCount++);
        controller.Register(() => secondCount++);

        controller.RequestStop();
        controller.RequestStop();

        Assert.Equal(1, firstCount);
        Assert.Equal(1, secondCount);
    }

    [Fact]
    public void Register_DuplicateCallbackDoesNotInvokeItTwice()
    {
        var controller = new OverlayStopController();
        var invocationCount = 0;
        Action callback = () => invocationCount++;
        controller.Register(callback);
        controller.Register(callback);

        controller.RequestStop();

        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public void Register_DuplicateAfterStopDoesNotInvokeItTwice()
    {
        var controller = new OverlayStopController();
        var invocationCount = 0;
        Action callback = () => invocationCount++;
        controller.RequestStop();

        controller.Register(callback);
        controller.Register(callback);

        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public void Unregister_RemovesOnlyTargetFromMultipleCallbacks()
    {
        var controller = new OverlayStopController();
        var firstCount = 0;
        var secondCount = 0;
        Action first = () => firstCount++;
        Action second = () => secondCount++;
        controller.Register(first);
        controller.Register(second);

        controller.Unregister(first);
        controller.RequestStop();

        Assert.Equal(0, firstCount);
        Assert.Equal(1, secondCount);
    }
}
