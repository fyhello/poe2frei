using FreiAtlas.Core.Area;
using FreiAtlas.Expedition;
using FreiAtlas.Host;
using FreiAtlas.Plugin.ExpeditionPanel;

namespace FreiAtlas.Host.Tests;

public sealed class ExpeditionRecipeValueAdapterTests
{
    [Fact]
    public void TryGetValue_ReturnsSingleRecipeTotalAndFormattedText()
    {
        var adapter = new ExpeditionRecipeValueAdapter(
            () => new PriceBook(("reward", 0.25m)));
        var recipe = Recipe(new AreaExpeditionReward(
            "reward",
            "Reward",
            2,
            true));

        var found = adapter.TryGetValue(recipe, out var value);

        Assert.True(found);
        Assert.Equal(0.5m, value.TotalDivine);
        Assert.Equal(400m, value.ExaltedPerDivine);
        Assert.Equal("200E", value.ValueText);
        Assert.Equal(
            ExpeditionPanelValueBand.UnderOneDivine,
            value.ValueBand);
    }

    [Fact]
    public void TryGetValue_UnpricedRewardLeavesValueUnavailable()
    {
        var adapter = new ExpeditionRecipeValueAdapter(() => new PriceBook());

        Assert.False(adapter.TryGetValue(
            Recipe(new AreaExpeditionReward("missing", "Missing", 1, true)),
            out _));
    }

    private static AreaExpeditionRecipe Recipe(AreaExpeditionReward reward)
        => new("recipe", 1, 1, [new AreaExpeditionRune(0, "Rune")], [reward]);

    private sealed class PriceBook(
        params (string ItemId, decimal Value)[] values) : IExpeditionPriceBook
    {
        private readonly IReadOnlyDictionary<string, decimal> _values =
            values.ToDictionary(value => value.ItemId, value => value.Value);

        public decimal ExaltedPerDivine => 400m;

        public bool TryGet(
            string itemId,
            string displayName,
            out decimal divineValue)
            => _values.TryGetValue(itemId, out divineValue);
    }
}
