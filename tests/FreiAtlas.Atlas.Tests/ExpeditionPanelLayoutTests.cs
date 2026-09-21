using System.Collections.Immutable;
using System.Drawing;
using FreiAtlas.Core.Area;
using FreiAtlas.Platform.Windows.Overlay;
using FreiAtlas.Plugin.ExpeditionPanel;

namespace FreiAtlas.Atlas.Tests;

public sealed class ExpeditionPanelLayoutTests
{
    [Theory]
    [InlineData(4, 248)]
    [InlineData(8, 324)]
    [InlineData(10, 370)]
    public void Create_WidthFollowsLargestRecipeWithoutCompressingRunes(
        int size,
        int expectedWidth)
    {
        var layout = ExpeditionPanelLayoutCalculator.Create(
            Scene(maxRecipeSize: size),
            new Size(1920, 1080),
            scrollOffset: 0,
            panelScale: 1f);

        Assert.Equal(expectedWidth, layout.PanelSize.Width);
    }

    [Theory]
    [InlineData(0.75f, 186, 24, 22, 16, 1, 15, 5)]
    [InlineData(1.00f, 248, 32, 30, 22, 1, 20, 7)]
    [InlineData(1.50f, 372, 48, 45, 33, 2, 30, 10)]
    public void Create_ScalesAllPanelGeometryTogether(
        float scale,
        int expectedWidth,
        int expectedTitle,
        int expectedRow,
        int expectedRune,
        int expectedGap,
        int expectedReward,
        int expectedInset)
    {
        var metrics = ExpeditionPanelMetrics.Create(scale);
        var layout = ExpeditionPanelLayoutCalculator.Create(
            Scene(maxRecipeSize: 4),
            new Size(1920, 1080),
            0,
            scale);

        Assert.Equal(expectedWidth, layout.PanelSize.Width);
        Assert.Equal(expectedTitle, layout.TitleBounds.Height);
        Assert.Equal(expectedRow, Assert.Single(layout.Encounters).SummaryBounds.Height);
        Assert.Equal(expectedRune, metrics.RuneSize);
        Assert.Equal(expectedGap, metrics.RuneGap);
        Assert.Equal(expectedReward, metrics.RewardSize);
        Assert.Equal(expectedInset, metrics.GroupInset);
    }

    [Fact]
    public void Create_CollapsedEncounterShowsOnlyRecommendedSummary()
    {
        var layout = ExpeditionPanelLayoutCalculator.Create(
            Scene(encounterExpanded: false, recipeCount: 3),
            new Size(1920, 1080),
            0,
            1f);

        Assert.Equal(new Size(278, 62), layout.PanelSize);
        var encounter = Assert.Single(layout.Encounters);
        Assert.Equal(new Rectangle(0, 32, 278, 30), encounter.SummaryBounds);
        Assert.Empty(encounter.Recipes);
    }

    [Fact]
    public void Create_ExpandedEncounterKeepsSummaryAndInsetsCompleteRecipeList()
    {
        var layout = ExpeditionPanelLayoutCalculator.Create(
            Scene(encounterExpanded: true, recipeCount: 3),
            new Size(1920, 1080),
            0,
            1f);

        Assert.Equal(new Size(278, 152), layout.PanelSize);
        var encounter = Assert.Single(layout.Encounters);
        Assert.Equal(new Rectangle(0, 32, 278, 30), encounter.SummaryBounds);
        Assert.Equal(3, encounter.Recipes.Length);
        Assert.All(encounter.Recipes, row =>
        {
            Assert.Equal(7, row.Bounds.Left);
            Assert.Equal(278, row.Bounds.Right);
            Assert.Equal(30, row.Bounds.Height);
        });
    }

    [Fact]
    public void Create_WholePanelCollapsedKeepsStableDynamicWidthTitleOnly()
    {
        var layout = ExpeditionPanelLayoutCalculator.Create(
            Scene(panelExpanded: false, encounterExpanded: true, recipeCount: 8),
            new Size(1920, 1080),
            0,
            1f);

        Assert.Equal(new Size(278, 32), layout.PanelSize);
        Assert.Empty(layout.Encounters);
        Assert.Equal(Rectangle.Empty, layout.BodyBounds);
    }

    [Fact]
    public void Create_ManyRecipesUsesOneClippedBodyAndClampsScroll()
    {
        var layout = ExpeditionPanelLayoutCalculator.Create(
            Scene(encounterExpanded: true, recipeCount: 30),
            new Size(1920, 1080),
            10_000,
            1f);

        Assert.Equal(760, layout.PanelSize.Height);
        Assert.Equal(new Rectangle(0, 32, 278, 728), layout.BodyBounds);
        Assert.True(layout.ContentHeight > layout.BodyBounds.Height);
        Assert.Equal(layout.ContentHeight - layout.BodyBounds.Height, layout.ScrollOffset);
        Assert.Contains(
            layout.Encounters.SelectMany(item => item.Recipes),
            recipe => recipe.Bounds.Top < layout.BodyBounds.Top);
    }

    [Fact]
    public void Create_NarrowViewportKeepsPanelInsideAvailableWidth()
    {
        var layout = ExpeditionPanelLayoutCalculator.Create(
            Scene(maxRecipeSize: 10),
            new Size(360, 640),
            0,
            1f);

        Assert.Equal(328, layout.PanelSize.Width);
        Assert.True(layout.PanelSize.Height <= 584);
    }

    private static ExpeditionPanelScene Scene(
        bool panelExpanded = true,
        bool encounterExpanded = false,
        int recipeCount = 1,
        int maxRecipeSize = 6)
    {
        var recipes = Enumerable.Range(0, recipeCount)
            .Select(index => Row(index, maxRecipeSize))
            .ToImmutableArray();
        var recommended = recipes.IsDefaultOrEmpty ? null : recipes[0];
        var encounter = new ExpeditionPanelEncounter(
            "expedition:1",
            maxRecipeSize,
            AreaContentPhase.Available,
            encounterExpanded,
            recommended,
            ExpeditionPanelValueBand.Unknown,
            recipes);
        return new ExpeditionPanelScene(1, 1, panelExpanded, [encounter]);
    }

    private static ExpeditionPanelRecipeRow Row(int index, int size)
        => new(
            $"recipe-{index}",
            index,
            size,
            Enumerable.Range(0, size).ToImmutableArray(),
            [new AreaExpeditionReward("reward", "Reward", 1, true)],
            null,
            null,
            null,
            ExpeditionPanelValueBand.Unknown);
}
