using FreiAtlas.Core.Area;
using FreiAtlas.Core.Settings;
using FreiAtlas.Host;

namespace FreiAtlas.Host.Tests;

public sealed class ExpeditionOverlayVisibilityPolicyTests
{
    [Theory]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, true, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, false, true)]
    public void Evaluate_VerifiedOpenPanelKeepsTwoOptionsIndependent(
        bool showNative,
        bool autoHide,
        bool expectedNative,
        bool expectedStandalone)
    {
        var result = ExpeditionOverlayVisibilityPolicy.Evaluate(
            new AreaMapExpeditionPanelSettings(showNative, autoHide),
            VerifiedOpenPanel());

        Assert.Equal(expectedNative, result.ShowNativeSurface);
        Assert.Equal(expectedStandalone, result.ShowStandaloneSurface);
    }

    [Theory]
    [InlineData(AreaExpeditionRecipePanelAvailability.Unavailable, false)]
    [InlineData(AreaExpeditionRecipePanelAvailability.Unverified, true)]
    [InlineData(AreaExpeditionRecipePanelAvailability.Verified, false)]
    public void Evaluate_UntrustedOrClosedPanelNeverAutoHidesStandalone(
        AreaExpeditionRecipePanelAvailability availability,
        bool isOpen)
    {
        var result = ExpeditionOverlayVisibilityPolicy.Evaluate(
            new AreaMapExpeditionPanelSettings(true, true),
            Panel(availability, isOpen));

        Assert.False(result.ShowNativeSurface);
        Assert.True(result.ShowStandaloneSurface);
    }

    private static AreaExpeditionRecipePanelSnapshot VerifiedOpenPanel()
        => new(
            AreaExpeditionRecipePanelAvailability.Verified,
            true,
            new AreaUiRect(20, 70, 360, 390),
            new AreaUiRect(30, 100, 330, 200),
            "expedition:11:42",
            [new AreaExpeditionRecipePanelRow(
                "recipe",
                1,
                new AreaUiRect(30, 100, 330, 32),
                true)]);

    private static AreaExpeditionRecipePanelSnapshot Panel(
        AreaExpeditionRecipePanelAvailability availability,
        bool isOpen)
        => new(availability, isOpen, null, null, null, []);
}
