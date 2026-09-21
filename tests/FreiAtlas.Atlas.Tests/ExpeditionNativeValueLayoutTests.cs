using System.Drawing;
using FreiAtlas.Core.Area;
using FreiAtlas.Platform.Windows.Overlay;

namespace FreiAtlas.Atlas.Tests;

public sealed class ExpeditionNativeValueLayoutTests
{
    [Fact]
    public void Create_PlacesMeasuredTextAfterRecipeRowWithoutHorizontalClipping()
    {
        var layout = ExpeditionNativeValueLayoutCalculator.Create(
            new AreaUiRect(31.5f, 99f, 333f, 31.5f),
            new AreaUiRect(31.5f, 99f, 333f, 346.5f),
            new Size(1280, 720),
            measuredTextWidth: 42f);

        Assert.NotNull(layout);
        Assert.True(layout.Value.TextBounds.Left > 364.5f);
        Assert.True(layout.Value.ClipBounds.Right >= layout.Value.TextBounds.Right);
        Assert.Equal(99f, layout.Value.ClipBounds.Top, 2);
    }

    [Theory]
    [InlineData(12f, 9f)]
    [InlineData(31.5f, 15.12f)]
    [InlineData(80f, 24f)]
    public void FontSize_FollowsRowHeightWithinStableLimits(
        float rowHeight,
        float expected)
        => Assert.Equal(
            expected,
            ExpeditionNativeValueLayoutCalculator.FontSize(rowHeight),
            2);

    [Fact]
    public void UniformFontSize_UsesShortestValidRowHeight()
    {
        var fontSize = ExpeditionNativeValueLayoutCalculator.UniformFontSize(
        [
            new AreaUiRect(27.475f, 86.35f, 290.45f, 47.1f),
            new AreaUiRect(27.475f, 133.45f, 290.45f, 27.475f)
        ]);

        Assert.Equal(13.188f, fontSize, 3);
    }

    [Fact]
    public void Create_SharedFontKeepsValueColumnAlignedAcrossDifferentRowHeights()
    {
        const float fontSize = 13.188f;
        var tall = ExpeditionNativeValueLayoutCalculator.Create(
            new AreaUiRect(27.475f, 86.35f, 290.45f, 47.1f),
            new AreaUiRect(27.475f, 86.35f, 290.45f, 129.525f),
            new Size(1015, 628),
            30f,
            fontSize);
        var shortRow = ExpeditionNativeValueLayoutCalculator.Create(
            new AreaUiRect(27.475f, 133.45f, 290.45f, 27.475f),
            new AreaUiRect(27.475f, 86.35f, 290.45f, 129.525f),
            new Size(1015, 628),
            30f,
            fontSize);

        Assert.NotNull(tall);
        Assert.NotNull(shortRow);
        Assert.Equal(fontSize, tall.Value.FontSize, 3);
        Assert.Equal(tall.Value.TextBounds.Left, shortRow.Value.TextBounds.Left, 3);
    }

    [Fact]
    public void Create_HalfVisibleRowKeepsFullTextBoundsButClipsVertically()
    {
        var layout = ExpeditionNativeValueLayoutCalculator.Create(
            new AreaUiRect(30, 80, 330, 32),
            new AreaUiRect(30, 96, 330, 200),
            new Size(1280, 720),
            40f);

        Assert.NotNull(layout);
        Assert.Equal(80f, layout.Value.TextBounds.Top);
        Assert.Equal(96f, layout.Value.ClipBounds.Top);
    }

    [Fact]
    public void Create_ClipsAgainstClientAndReturnsNullWhenRowMissesList()
    {
        var clipped = ExpeditionNativeValueLayoutCalculator.Create(
            new AreaUiRect(30, -10, 200, 32),
            new AreaUiRect(0, -20, 400, 100),
            new Size(320, 200),
            40f);

        Assert.NotNull(clipped);
        Assert.Equal(0f, clipped.Value.ClipBounds.Top);
        Assert.Equal(320f, clipped.Value.ClipBounds.Right);
        Assert.Null(ExpeditionNativeValueLayoutCalculator.Create(
            new AreaUiRect(30, 300, 330, 32),
            new AreaUiRect(30, 96, 330, 200),
            new Size(1280, 720),
            40f));
    }

    [Theory]
    [InlineData(float.NaN, 20f)]
    [InlineData(100f, 0f)]
    [InlineData(100f, -1f)]
    public void Create_RejectsInvalidRowGeometry(float width, float height)
        => Assert.Null(ExpeditionNativeValueLayoutCalculator.Create(
            new AreaUiRect(10, 10, width, height),
            new AreaUiRect(0, 0, 500, 500),
            new Size(500, 500),
            40f));

    [Fact]
    public void Create_LongTextUsesAtMostThirtyTwoPercentOfRowWidth()
    {
        var layout = ExpeditionNativeValueLayoutCalculator.Create(
            new AreaUiRect(10, 20, 200, 30),
            new AreaUiRect(0, 0, 500, 500),
            new Size(500, 500),
            measuredTextWidth: 1000f);

        Assert.NotNull(layout);
        Assert.Equal(64f, layout.Value.TextBounds.Width, 2);
    }

    [Fact]
    public void Create_EqualRowsKeepSameRightAnchorAtDifferentVerticalPositions()
    {
        var first = ExpeditionNativeValueLayoutCalculator.Create(
            new AreaUiRect(10, 20, 200, 30),
            new AreaUiRect(0, 0, 500, 500),
            new Size(500, 500),
            40f);
        var second = ExpeditionNativeValueLayoutCalculator.Create(
            new AreaUiRect(10, 80, 200, 30),
            new AreaUiRect(0, 0, 500, 500),
            new Size(500, 500),
            40f);

        Assert.Equal(first!.Value.TextBounds.Right, second!.Value.TextBounds.Right);
    }
}
