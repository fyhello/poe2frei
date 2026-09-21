using FreiAtlas.Platform.Windows.Overlay;
using FreiAtlas.Plugin.ExpeditionPanel;
using Vortice.DirectWrite;

namespace FreiAtlas.Atlas.Tests;

public sealed class ExpeditionNativeValueSurfaceTests
{
    [Fact]
    public void SurfaceUsesPassthroughWindowInteraction()
        => Assert.Equal(
            OverlayWindowInteraction.Passthrough,
            ExpeditionNativeValueSurface.InteractionMode);

    [Theory]
    [InlineData(ExpeditionPanelValueBand.Unknown)]
    [InlineData(ExpeditionPanelValueBand.UpToTwentyExalted)]
    [InlineData(ExpeditionPanelValueBand.UnderOneDivine)]
    [InlineData(ExpeditionPanelValueBand.OneToFiveDivine)]
    [InlineData(ExpeditionPanelValueBand.FiveToTenDivine)]
    [InlineData(ExpeditionPanelValueBand.TenToFiftyDivine)]
    [InlineData(ExpeditionPanelValueBand.FiftyPlusDivine)]
    public void SurfaceConsumesSharedValuePalette(ExpeditionPanelValueBand band)
        => Assert.Equal(
            ExpeditionValuePresentation.GetArgb(band),
            ExpeditionNativeValueSurface.ValueArgb(band));

    [Fact]
    public void ConfigureTextFormatRightAlignsSingleLineWithoutVerticalDrift()
    {
        using var factory = DWrite.DWriteCreateFactory<IDWriteFactory>(FactoryType.Shared);
        using var format = factory.CreateTextFormat(
            "Microsoft YaHei UI",
            null,
            FontWeight.SemiBold,
            FontStyle.Normal,
            FontStretch.Normal,
            15f,
            "zh-CN");

        ExpeditionNativeValueSurface.ConfigureTextFormat(format);

        Assert.Equal(TextAlignment.Trailing, format.TextAlignment);
        Assert.Equal(ParagraphAlignment.Center, format.ParagraphAlignment);
        Assert.Equal(WordWrapping.NoWrap, format.WordWrapping);
    }
}
