using FreiAtlas.Expedition;
using FreiAtlas.Settings;

namespace FreiAtlas.Settings.Tests;

public sealed class PriceAnnotationBuilderTests
{
    [Fact]
    public void BuildRows_UsesHalfDivineThresholdForAutoUnit()
    {
        var rows = PriceAnnotationBuilder.BuildRows(
            [
                new ExpeditionPriceEntry("divine", "神圣石", 0.6m),
                new ExpeditionPriceEntry("exalted", "崇高石", 0.4m)
            ],
            exaltedPerDivine: 400m,
            minExalted: 1m,
            unitMode: "auto");

        Assert.Equal("[0.6D]", rows[0].Price);
        Assert.Equal("[160E]", rows[1].Price);
    }

    [Fact]
    public void BuildRows_FiltersBelowMinimumExaltedValue()
    {
        var rows = PriceAnnotationBuilder.BuildRows(
            [
                new ExpeditionPriceEntry("visible", "可见", 0.004m),
                new ExpeditionPriceEntry("hidden", "隐藏", 0.002m)
            ],
            exaltedPerDivine: 400m,
            minExalted: 1m,
            unitMode: "auto");

        Assert.Single(rows);
        Assert.Equal("可见", rows[0].Name);
    }

    [Fact]
    public void BuildRows_FiltersByCategoryAndDeduplicatesByIdentity()
    {
        var rows = PriceAnnotationBuilder.BuildRows(
            [
                new ExpeditionPriceEntry("currency-1", "通货甲", 2m) { Category = "Currency" },
                new ExpeditionPriceEntry("currency-1", "通货甲副本", 3m) { Category = "Currency" },
                new ExpeditionPriceEntry("rune-1", "符文甲", 2m) { Category = "Runes" }
            ],
            exaltedPerDivine: 400m,
            minExalted: 1m,
            unitMode: "auto",
            categories: ["Currency"]);

        Assert.Single(rows);
        Assert.Equal("通货甲", rows[0].Name);
        Assert.Equal("[2D]", rows[0].Price);
    }

    [Fact]
    public void BuildRows_CarriesMetadataPathForCrossLanguageMatching()
    {
        var rows = PriceAnnotationBuilder.BuildRows(
            [new ExpeditionPriceEntry(
                "Metadata/Items/Currency/CurrencyModValues",
                "Divine Orb",
                1m)
            {
                MetadataPath = "Metadata/Items/Currency/CurrencyModValues"
            }],
            exaltedPerDivine: 400m,
            minExalted: 1m,
            unitMode: "auto");

        Assert.Single(rows);
        Assert.Equal("Metadata/Items/Currency/CurrencyModValues", rows[0].MetadataPath);
    }

    [Fact]
    public void BuildRows_CarriesCategoryIntoPriceRow()
    {
        var rows = PriceAnnotationBuilder.BuildRows(
            [new ExpeditionPriceEntry(
                "atziris-communion",
                "Atziri's Communion",
                575m)
            {
                Category = "LineageSupportGems",
                MetadataPath = "Metadata/Items/Gems/SupportGems/LineageAtziri"
            }],
            exaltedPerDivine: 700m,
            minExalted: 1m,
            unitMode: "auto",
            categories: ["LineageSupportGems"]);

        var row = Assert.Single(rows);
        Assert.Equal("Atziri's Communion", row.Name);
        Assert.Equal("LineageSupportGems", row.Category);
    }
}
