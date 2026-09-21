using FreiAtlas.Plugin.ExpeditionPanel;

namespace FreiAtlas.Plugin.ExpeditionPanel.Tests;

public sealed class ExpeditionValuePresentationTests
{
    [Theory]
    [InlineData(0.05, 400, ExpeditionPanelValueBand.UpToTwentyExalted, 0xFF707A80u)]
    [InlineData(0.050025, 400, ExpeditionPanelValueBand.UnderOneDivine, 0xFFF2F2F2u)]
    [InlineData(1, 400, ExpeditionPanelValueBand.OneToFiveDivine, 0xFF39D98Au)]
    [InlineData(5, 400, ExpeditionPanelValueBand.FiveToTenDivine, 0xFFFFD166u)]
    [InlineData(10, 400, ExpeditionPanelValueBand.TenToFiftyDivine, 0xFFC084FCu)]
    [InlineData(50, 400, ExpeditionPanelValueBand.FiftyPlusDivine, 0xFFFF5C5Cu)]
    public void SharedPresentationUsesConfirmedBoundariesAndPalette(
        double totalDivine,
        double exaltedPerDivine,
        ExpeditionPanelValueBand expectedBand,
        uint expectedArgb)
    {
        var band = ExpeditionValuePresentation.Classify(
            (decimal)totalDivine,
            (decimal)exaltedPerDivine);

        Assert.Equal(expectedBand, band);
        Assert.Equal(expectedArgb, ExpeditionValuePresentation.GetArgb(band));
    }

    [Fact]
    public void UnknownUsesSharedPlaceholderColor()
        => Assert.Equal(
            0xFF50585Du,
            ExpeditionValuePresentation.GetArgb(ExpeditionPanelValueBand.Unknown));
}
