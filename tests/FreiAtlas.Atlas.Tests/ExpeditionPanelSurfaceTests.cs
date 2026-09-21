using System.Collections.Immutable;
using System.Drawing;
using FreiAtlas.Core.Area;
using FreiAtlas.Platform.Windows.Overlay;
using FreiAtlas.Plugin.ExpeditionPanel;

namespace FreiAtlas.Atlas.Tests;

public sealed class ExpeditionPanelSurfaceTests
{
    [Fact]
    public void HitTest_PrioritizesPanelToggleAndUsesRemainingTitleForDrag()
    {
        var layout = Layout();

        Assert.Equal(
            ExpeditionPanelHitKind.PanelToggle,
            ExpeditionPanelSurface.HitTest(layout, new Point(20, 20)).Kind);
        Assert.Equal(
            ExpeditionPanelHitKind.Title,
            ExpeditionPanelSurface.HitTest(layout, new Point(180, 20)).Kind);
    }

    [Fact]
    public void HitTest_ReturnsEncounterIdentityOnlyForVisibleHeader()
    {
        var layout = Layout();

        var hit = ExpeditionPanelSurface.HitTest(layout, new Point(100, 45));

        Assert.Equal(ExpeditionPanelHitKind.EncounterHeader, hit.Kind);
        Assert.Equal("expedition:1", hit.EncounterId);
        Assert.Equal(
            ExpeditionPanelHitKind.Body,
            ExpeditionPanelSurface.HitTest(layout, new Point(100, 75)).Kind);
        Assert.Equal(
            ExpeditionPanelHitKind.None,
            ExpeditionPanelSurface.HitTest(layout, new Point(500, 500)).Kind);
    }

    [Theory]
    [InlineData(ExpeditionPanelValueBand.Unknown, 0xFF50585Du)]
    [InlineData(ExpeditionPanelValueBand.UpToTwentyExalted, 0xFF707A80u)]
    [InlineData(ExpeditionPanelValueBand.UnderOneDivine, 0xFFF2F2F2u)]
    [InlineData(ExpeditionPanelValueBand.OneToFiveDivine, 0xFF39D98Au)]
    [InlineData(ExpeditionPanelValueBand.FiveToTenDivine, 0xFFFFD166u)]
    [InlineData(ExpeditionPanelValueBand.TenToFiftyDivine, 0xFFC084FCu)]
    [InlineData(ExpeditionPanelValueBand.FiftyPlusDivine, 0xFFFF5C5Cu)]
    public void ValueBandColorUsesConfirmedPalette(
        ExpeditionPanelValueBand band,
        uint expected)
        => Assert.Equal(expected, ExpeditionValuePresentation.GetArgb(band));

    [Theory]
    [InlineData(0.75f)]
    [InlineData(1.00f)]
    [InlineData(1.50f)]
    public void HitTest_UsesScaledTitleAndSummaryGeometry(float panelScale)
    {
        var layout = Layout(panelScale);
        var titlePoint = Center(layout.TitleBounds);
        var summaryPoint = Center(Assert.Single(layout.Encounters).SummaryBounds);

        Assert.Equal(
            ExpeditionPanelHitKind.Title,
            ExpeditionPanelSurface.HitTest(layout, titlePoint).Kind);
        Assert.Equal(
            ExpeditionPanelHitKind.EncounterHeader,
            ExpeditionPanelSurface.HitTest(layout, summaryPoint).Kind);
    }

    private static ExpeditionPanelLayout Layout(float panelScale = 1f)
    {
        var row = new ExpeditionPanelRecipeRow(
            "recipe",
            1,
            6,
            [0, 1],
            [new AreaExpeditionReward("reward", "Reward", 1, true)],
            null,
            null,
            null,
            ExpeditionPanelValueBand.Unknown);
        var encounter = new ExpeditionPanelEncounter(
            "expedition:1",
            6,
            AreaContentPhase.Available,
            true,
            row,
            ExpeditionPanelValueBand.Unknown,
            [row]);
        return ExpeditionPanelLayoutCalculator.Create(
            new ExpeditionPanelScene(1, 1, true, [encounter]),
            new Size(1920, 1080),
            0,
            panelScale);
    }

    private static Point Center(Rectangle bounds)
        => new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
}
