using System.Collections.Immutable;
using FreiAtlas.Core.Area;
using FreiAtlas.Expedition;

namespace FreiAtlas.Expedition.Tests;

public sealed class ExpeditionValueCalculatorTests
{
    [Fact]
    public void Calculate_SumsRewardQuantitiesAndSelectsHighestRecipe()
    {
        var recipes = ImmutableArray.Create(
            Recipe(
                "lower",
                new AreaExpeditionReward("a", "Orb A", 3, true),
                new AreaExpeditionReward("b", "Orb B", 1, true)),
            Recipe(
                "highest",
                new AreaExpeditionReward("a", "Orb A", 2, true),
                new AreaExpeditionReward("c", "Orb C", 1, true)));
        var prices = new TestPriceBook(
            new Dictionary<string, decimal>
            {
                ["a"] = 0.5m,
                ["b"] = 1m,
                ["c"] = 6m
            },
            exaltedPerDivine: 400m);

        var result = ExpeditionValueCalculator.Calculate(recipes, prices);

        Assert.Equal(7m, result.BestTotalDivine);
        Assert.True(result.HasPricedRecipe);
        Assert.False(result.HasUnpricedReward);
    }

    [Fact]
    public void Calculate_MarksPartialCoverageWithoutDiscardingKnownMaximum()
    {
        var recipes = ImmutableArray.Create(
            Recipe("known", new AreaExpeditionReward("a", "Orb A", 2, true)),
            Recipe("unknown", new AreaExpeditionReward("missing", "Unknown", 1, true)));
        var prices = new TestPriceBook(
            new Dictionary<string, decimal> { ["a"] = 2m },
            exaltedPerDivine: 400m);

        var result = ExpeditionValueCalculator.Calculate(recipes, prices);

        Assert.Equal(4m, result.BestTotalDivine);
        Assert.True(result.HasUnpricedReward);
    }

    [Fact]
    public void Calculate_ReturnsNoValueWhenEveryRecipeIsUnpriced()
    {
        var result = ExpeditionValueCalculator.Calculate(
            [Recipe("unknown", new AreaExpeditionReward("missing", "Unknown", 1, true))],
            new TestPriceBook(new Dictionary<string, decimal>(), 400m));

        Assert.Null(result.BestTotalDivine);
        Assert.False(result.HasPricedRecipe);
        Assert.True(result.HasUnpricedReward);
    }

    [Theory]
    [InlineData(0.5, "200E")]
    [InlineData(0.501, "0.5D")]
    [InlineData(6.5, "6.5D")]
    public void Format_UsesInclusiveHalfDivineBoundaryAndExpectedUnits(
        double exalted,
        string expected)
    {
        var text = ExpeditionValueTextFormatter.Format(
            (decimal)exalted,
            400m,
            incomplete: false);

        Assert.Equal(expected, text);
    }

    [Fact]
    public void Format_AppendsIncompleteMarkerAfterFormattedValue()
    {
        Assert.Equal(
            "187E+",
            ExpeditionValueTextFormatter.Format(0.4675m, 400m, incomplete: true));
    }

    private static AreaExpeditionRecipe Recipe(
        string id,
        params AreaExpeditionReward[] rewards)
        => new(id, 1, rewards.Length, [], rewards.ToImmutableArray());

    private sealed class TestPriceBook(
        IReadOnlyDictionary<string, decimal> prices,
        decimal exaltedPerDivine) : IExpeditionPriceBook
    {
        public decimal ExaltedPerDivine => exaltedPerDivine;

        public bool TryGet(
            string itemId,
            string displayName,
            out decimal exaltedValue)
            => prices.TryGetValue(itemId, out exaltedValue);
    }
}
