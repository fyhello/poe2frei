using System.Globalization;
using FreiAtlas.Expedition;
using FreiAtlas.Patcher;
using FreiAtlas.Patcher.Modules.Price;

namespace FreiAtlas.Settings;

/// <summary>
/// 把价格快照条目转换为可注入的 PriceRow。保持为纯函数，便于单测价格门槛、单位与分类规则。
/// </summary>
public static class PriceAnnotationBuilder
{
    public static List<PriceRow> BuildRows(
        IEnumerable<ExpeditionPriceEntry> entries,
        decimal exaltedPerDivine,
        decimal minExalted,
        string unitMode,
        IReadOnlyList<string>? categories = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        decimal rate = exaltedPerDivine > 0m ? exaltedPerDivine : 400m;
        decimal minimum = minExalted > 0m ? minExalted : 1m;
        string mode = string.IsNullOrWhiteSpace(unitMode) ? "auto" : unitMode.Trim().ToLowerInvariant();
        var rows = new List<PriceRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (entry.DivineValue <= 0m || string.IsNullOrWhiteSpace(entry.DisplayName))
            {
                continue;
            }

            string cat = PricePatchService.InferCategory(entry.Category, entry.MetadataPath);
            if (categories is { Count: > 0 }
                && !categories.Contains(cat, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            decimal exaltedValue = entry.DivineValue * rate;
            if (exaltedValue < minimum)
            {
                continue;
            }

            string identity = string.IsNullOrWhiteSpace(entry.ItemId) ? entry.DisplayName : entry.ItemId;
            if (!seen.Add(identity))
            {
                continue;
            }

            string suffix = FormatPriceText(entry.DivineValue, exaltedValue, mode);
            if (suffix.Length == 0)
            {
                continue;
            }

            rows.Add(new PriceRow(entry.DisplayName, suffix, entry.MetadataPath, cat));
        }

        return rows;
    }

    public static string FormatPriceText(decimal divineValue, decimal exaltedValue, string unitMode)
    {
        static string Format(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        return unitMode switch
        {
            "divine" => $"[{Format(divineValue)}D]",
            "exalted" => $"[{Format(exaltedValue)}E]",
            _ => divineValue >= 0.5m
                ? $"[{Format(divineValue)}D]"
                : $"[{Format(exaltedValue)}E]"
        };
    }
}
