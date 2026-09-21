using FreiAtlas.Core.Area;
using FreiAtlas.Core.Contracts;

namespace FreiAtlas.Expedition;

public interface IExpeditionPriceBook
{
    decimal ExaltedPerDivine { get; }

    bool TryGet(
        string itemId,
        string displayName,
        out decimal divineValue);
}

public sealed record ExpeditionValueResult(
    decimal? BestTotalDivine,
    bool HasPricedRecipe,
    bool HasUnpricedReward);

public static class ExpeditionValueCalculator
{
    public static ExpeditionValueResult Calculate(
        IEnumerable<AreaExpeditionRecipe> recipes,
        IExpeditionPriceBook priceBook)
    {
        ArgumentNullException.ThrowIfNull(recipes);
        ArgumentNullException.ThrowIfNull(priceBook);

        decimal? best = null;
        var hasPricedRecipe = false;
        var hasUnpricedReward = false;
        foreach (var recipe in recipes)
        {
            if (recipe.Rewards.IsDefaultOrEmpty)
            {
                hasUnpricedReward = true;
                continue;
            }

            var total = 0m;
            var recipePriced = true;
            foreach (var reward in recipe.Rewards)
            {
                if (!reward.IsExactItem
                    || reward.Quantity <= 0
                    || !priceBook.TryGet(
                        reward.ItemId,
                        reward.DisplayName,
                        out var unitValue)
                    || unitValue < 0m)
                {
                    recipePriced = false;
                    break;
                }

                total += unitValue * reward.Quantity;
            }

            if (!recipePriced)
            {
                hasUnpricedReward = true;
                continue;
            }

            hasPricedRecipe = true;
            if (best is null || total > best.Value)
            {
                best = total;
            }
        }

        return new ExpeditionValueResult(best, hasPricedRecipe, hasUnpricedReward);
    }
}

public static class ExpeditionValueTextFormatter
{
    public static string Format(
        decimal divineValue,
        decimal exaltedPerDivine,
        bool incomplete)
    {
        if (divineValue < 0m || exaltedPerDivine <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(divineValue));
        }

        string text;
        if (divineValue <= 0.5m)
        {
            var exalted = decimal.Round(
                divineValue * exaltedPerDivine,
                0,
                MidpointRounding.AwayFromZero);
            text = $"{exalted.ToString("0", System.Globalization.CultureInfo.InvariantCulture)}E";
        }
        else
        {
            var rounded = decimal.Round(divineValue, 1, MidpointRounding.AwayFromZero);
            text = $"{rounded.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}D";
        }

        return incomplete ? text + "+" : text;
    }
}

public sealed class ExpeditionValueProvider : IAreaExpeditionValueProvider
{
    private readonly Func<IExpeditionPriceBook> _priceBookAccessor;

    public ExpeditionValueProvider(Func<IExpeditionPriceBook> priceBookAccessor)
    {
        _priceBookAccessor = priceBookAccessor
            ?? throw new ArgumentNullException(nameof(priceBookAccessor));
    }

    public string? GetValueText(AreaExpeditionDetails? details)
    {
        if (details is null || details.Recipes.IsDefaultOrEmpty)
        {
            return null;
        }

        var priceBook = _priceBookAccessor();
        var result = ExpeditionValueCalculator.Calculate(details.Recipes, priceBook);
        return result.BestTotalDivine is { } value
            ? ExpeditionValueTextFormatter.Format(
                value,
                priceBook.ExaltedPerDivine,
                result.HasUnpricedReward)
            : null;
    }
}
