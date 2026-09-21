using System.Drawing;
using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Platform.Windows.Overlay;

namespace FreiAtlas.Atlas.Tests;

public sealed class AreaMapExpeditionTagLayoutTests
{
    [Theory]
    [InlineData(AreaMapViewKind.LargeMap, 13f, 13f)]
    [InlineData(AreaMapViewKind.MiniMap, 13f, 11.05f)]
    [InlineData(AreaMapViewKind.MiniMap, 9f, 9f)]
    public void ResolveFontSize_UsesSharedSettingAndMiniMapScale(
        AreaMapViewKind kind,
        float configured,
        float expected)
        => Assert.Equal(
            expected,
            AreaMapExpeditionTagLayoutCalculator.ResolveFontSize(kind, configured),
            2);

    [Theory]
    [InlineData(6, "6孔")]
    [InlineData(null, "?孔")]
    public void FormatHoleCount_UsesQuestionMarkFallback(int? holes, string expected)
        => Assert.Equal(
            expected,
            AreaMapExpeditionTagLayoutCalculator.FormatHoleCount(holes));

    [Fact]
    public void Create_KeepsHoleSegmentCenteredAndExpandsValueToTheRight()
    {
        var withoutValue = CreateLayout(null, new SizeF(0, 0));
        var withValue = CreateLayout("6.5D", new SizeF(34, 13));

        Assert.Equal(100f, CenterX(withoutValue.HoleBounds), 3);
        Assert.Equal(100f, CenterX(withValue.HoleBounds), 3);
        Assert.Equal(withoutValue.PanelBounds.Left, withValue.PanelBounds.Left, 3);
        Assert.True(withValue.PanelBounds.Right > withoutValue.PanelBounds.Right);
        Assert.Equal("6孔", withValue.HoleText);
        Assert.Equal("6.5D", withValue.ValueText);
    }

    [Fact]
    public void Create_ShiftsOnlyWhenIdealPanelCrossesViewport()
    {
        var layout = AreaMapExpeditionTagLayoutCalculator.Create(
            new RectangleF(10, 20, 120, 80),
            new Vector2(127, 60),
            "6孔",
            "6.5D",
            13f,
            new SizeF(24, 13),
            new SizeF(34, 13));

        Assert.Equal(129f, layout.PanelBounds.Right, 3);
        Assert.True(layout.PanelBounds.Left >= 10f);
        Assert.Equal(layout.PanelBounds, RectangleF.Intersect(
            layout.PanelBounds,
            layout.ClipBounds));
        Assert.NotEqual(127f, CenterX(layout.HoleBounds));
    }

    private static AreaMapExpeditionTagLayout CreateLayout(
        string? value,
        SizeF valueMeasurement)
        => AreaMapExpeditionTagLayoutCalculator.Create(
            new RectangleF(0, 0, 300, 200),
            new Vector2(100, 80),
            "6孔",
            value,
            13f,
            new SizeF(24, 13),
            valueMeasurement);

    private static float CenterX(RectangleF value)
        => value.Left + value.Width / 2f;
}
