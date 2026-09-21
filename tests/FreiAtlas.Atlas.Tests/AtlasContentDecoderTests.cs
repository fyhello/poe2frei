using FreiAtlas.Atlas.Content;

namespace FreiAtlas.Atlas.Tests;

public sealed class AtlasContentDecoderTests
{
    [Theory]
    [InlineData("PowerfulMapBoss", "map_boss")]
    [InlineData("MapBoss", "map_boss")]
    [InlineData("Breach", "breach")]
    [InlineData("BreachCity", "breach")]
    [InlineData("Delirium", "delirium")]
    [InlineData("DeliriumGigaMirror", "delirium")]
    [InlineData("RitualLocusts", "ritual")]
    [InlineData("Abyss", "abyss")]
    [InlineData("Incursion", "incursion")]
    [InlineData("Expedition", "expedition")]
    [InlineData("ExpeditionLogbook", "expedition")]
    [InlineData("Grand Expedition", "expedition")]
    [InlineData("死境探險", "expedition")]
    [InlineData("死境探险", "expedition")]
    [InlineData("大型探險", "expedition")]
    [InlineData("大型探险", "expedition")]
    [InlineData("AzmeriSpiritBossPossessed", "map_boss")]
    [InlineData("强力地图首领", "map_boss")]
    [InlineData("地图首领", "map_boss")]
    [InlineData("強大地圖頭目", "map_boss")]
    [InlineData("地圖頭目", "map_boss")]
    [InlineData("致命地圖頭目", "map_boss")]
    public void Decode_KnownRawCode_ReturnsNormalizedContent(
        string rawCode,
        string expectedId)
    {
        var result = AtlasContentDecoder.Decode(rawCode);

        Assert.Equal(expectedId, result.ContentId);
    }

    [Theory]
    [InlineData("AtlasNormalMapsWithBreach3")]
    [InlineData("AtlasLeagueDeliriumOuterNode6")]
    [InlineData("AtlasLeagueBreachOuterNode7")]
    public void Decode_LayoutTemplateCode_ReturnsUnknownWithoutDisplayLabel(
        string rawCode)
    {
        var result = AtlasContentDecoder.Decode(rawCode);

        Assert.Equal("unknown", result.ContentId);
        Assert.Equal(string.Empty, result.DisplayName);
    }

    [Fact]
    public void Decode_UnknownCode_PreservesRawValue()
    {
        var result = AtlasContentDecoder.Decode("NewContentCode");

        Assert.Equal("unknown", result.ContentId);
        Assert.Contains("NewContentCode", result.Attributes.Values);
    }

    [Fact]
    public void Decode_DntContent_IsNotPublishedAsUserContent()
    {
        Assert.True(ContentDictionary.IsNoiseCode(
            "[DNT] Breach City - Not Shown to Players"));
    }

    [Fact]
    public void Decode_KnownContent_PreservesReferenceIconAndDescription()
    {
        var result = AtlasContentDecoder.Decode("PowerfulMapBoss");

        Assert.Equal("AtlasIconContentMapBoss", result.ReferenceIconId);
        Assert.Contains("Map Boss", result.Description);
    }

    [Theory]
    [InlineData("Abyss", "AtlasIconContentAbyss")]
    [InlineData("Incursion", "AtlasIconContentIncursion")]
    public void Decode_CurrentStatContent_UsesEmbeddedMetadata(
        string rawCode,
        string expectedIconId)
    {
        var result = AtlasContentDecoder.Decode(rawCode);

        Assert.Equal(expectedIconId, result.ReferenceIconId);
        Assert.NotEmpty(result.Description);
    }

    [Fact]
    public void Decode_ExpeditionContent_UsesExpeditionMetadata()
    {
        var result = AtlasContentDecoder.Decode("Expedition");

        Assert.Equal("expedition", result.ContentId);
        Assert.Equal("先祖秘藏", result.DisplayName);
        Assert.Equal("#D6B449", result.DefaultColor);
        Assert.Equal("AtlasIconContentExpedition", result.ReferenceIconId);
    }

    [Fact]
    public void InferFromNodeSignals_IconOnlyBoss_ReturnsMapBoss()
    {
        var result = AtlasContentDecoder.InferFromNodeSignals(
            iconType: 6,
            rawContentValue: 0,
            rawFlags: 0x10);

        Assert.NotNull(result);
        Assert.Equal("map_boss", result.ContentId);
        Assert.Equal("atlas-icon-type", result.Attributes["sourceField"]);
        Assert.Equal("0.8", result.Attributes["confidence"]);
    }

    [Theory]
    [InlineData(0, 0u, 0x10)]
    [InlineData(6, 123u, 0x10)]
    [InlineData(6, 0u, 0x00)]
    public void InferFromNodeSignals_NonFallbackPattern_ReturnsNull(
        int iconType,
        uint rawContentValue,
        byte rawFlags)
    {
        Assert.Null(AtlasContentDecoder.InferFromNodeSignals(
            iconType,
            rawContentValue,
            rawFlags));
    }
}
