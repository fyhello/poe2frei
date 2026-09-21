using FreiAtlas.Core.Area;
using FreiAtlas.Expedition;
using FreiAtlas.Plugin.ExpeditionPanel;

namespace FreiAtlas.Host;

internal sealed class ExpeditionRecipeValueAdapter : IExpeditionRecipeValueSource
{
    private readonly Func<IExpeditionPriceBook> _priceBookAccessor;

    public ExpeditionRecipeValueAdapter(
        Func<IExpeditionPriceBook> priceBookAccessor)
    {
        _priceBookAccessor = priceBookAccessor
            ?? throw new ArgumentNullException(nameof(priceBookAccessor));
    }

    public bool TryGetValue(
        AreaExpeditionRecipe recipe,
        out ExpeditionRecipeValue value)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var priceBook = _priceBookAccessor();
        var result = ExpeditionValueCalculator.Calculate([recipe], priceBook);
        if (result.BestTotalDivine is not { } total
            || priceBook.ExaltedPerDivine <= 0m)
        {
            value = default;
            return false;
        }

        value = new ExpeditionRecipeValue(
            total,
            priceBook.ExaltedPerDivine,
            ExpeditionValueTextFormatter.Format(
                total,
                priceBook.ExaltedPerDivine,
                incomplete: result.HasUnpricedReward),
            ExpeditionValuePresentation.Classify(total, priceBook.ExaltedPerDivine));
        return true;
    }
}
