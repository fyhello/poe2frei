using FreiAtlas.Core.Settings;

namespace FreiAtlas.Host.Tests;

public sealed class AltOverlaySuppressionControllerTests
{
    [Fact]
    public void HoldToHide_FollowsCurrentAltState()
    {
        var controller = new AltOverlaySuppressionController();

        Assert.False(controller.ShouldSuppress(
            AltOverlayMode.HoldToHide,
            isAltDown: false,
            isGameForeground: true));
        Assert.True(controller.ShouldSuppress(
            AltOverlayMode.HoldToHide,
            isAltDown: true,
            isGameForeground: true));
        Assert.False(controller.ShouldSuppress(
            AltOverlayMode.HoldToHide,
            isAltDown: false,
            isGameForeground: true));
    }

    [Fact]
    public void ToggleOnPress_TogglesOnlyAfterCompleteCycles()
    {
        var controller = new AltOverlaySuppressionController();

        Assert.False(controller.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: false,
            isGameForeground: true));
        Assert.False(controller.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: true,
            isGameForeground: true));
        Assert.True(controller.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: false,
            isGameForeground: true));
        Assert.True(controller.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: true,
            isGameForeground: true));
        Assert.False(controller.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: false,
            isGameForeground: true));
    }

    [Fact]
    public void ToggleOnPress_InitialHeldAltReleaseDoesNotToggle()
    {
        var controller = new AltOverlaySuppressionController();

        Assert.False(controller.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: true,
            isGameForeground: true));
        Assert.False(controller.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: false,
            isGameForeground: true));
    }

    [Fact]
    public void ToggleOnPress_BackgroundReleaseIsIgnoredWithoutDelayedToggle()
    {
        var controller = new AltOverlaySuppressionController();

        Assert.False(controller.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: false,
            isGameForeground: true));
        Assert.False(controller.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: true,
            isGameForeground: true));
        Assert.False(controller.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: false,
            isGameForeground: false));
        Assert.False(controller.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: false,
            isGameForeground: true));
        Assert.False(controller.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: true,
            isGameForeground: true));
        Assert.True(controller.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: false,
            isGameForeground: true));
    }

    [Fact]
    public void ToggleOnPress_HiddenLatchSurvivesBackgroundReleaseWithoutDelayedToggle()
    {
        var controller = new AltOverlaySuppressionController();

        Assert.False(controller.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: false,
            isGameForeground: true));
        Assert.False(controller.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: true,
            isGameForeground: true));
        Assert.True(controller.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: false,
            isGameForeground: true));

        Assert.True(controller.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: true,
            isGameForeground: true));
        Assert.True(controller.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: false,
            isGameForeground: false));
        Assert.True(controller.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: false,
            isGameForeground: true));

        Assert.True(controller.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: true,
            isGameForeground: true));
        Assert.False(controller.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: false,
            isGameForeground: true));
    }

    [Fact]
    public void ModeChange_ClearsLatchAndUsesHeldKeyAsBaseline()
    {
        var controller = new AltOverlaySuppressionController();

        Assert.False(controller.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: false,
            isGameForeground: true));
        Assert.False(controller.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: true,
            isGameForeground: true));
        Assert.True(controller.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: false,
            isGameForeground: true));

        Assert.True(controller.ShouldSuppress(
            AltOverlayMode.HoldToHide,
            isAltDown: true,
            isGameForeground: true));
        Assert.False(controller.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: true,
            isGameForeground: true));
        Assert.False(controller.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: false,
            isGameForeground: true));
    }

    [Fact]
    public void NewSession_DoesNotInheritHiddenLatch()
    {
        var firstSession = new AltOverlaySuppressionController();

        Assert.False(firstSession.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: false,
            isGameForeground: true));
        Assert.False(firstSession.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: true,
            isGameForeground: true));
        Assert.True(firstSession.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: false,
            isGameForeground: true));

        var secondSession = new AltOverlaySuppressionController();

        Assert.False(secondSession.ShouldSuppress(
            AltOverlayMode.ToggleOnPress,
            isAltDown: false,
            isGameForeground: true));
    }
}
