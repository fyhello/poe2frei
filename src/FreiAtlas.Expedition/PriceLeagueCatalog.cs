using System.Collections.Immutable;
using System.Text.Json;

namespace FreiAtlas.Expedition;

public sealed record PriceLeague(string Id, string DisplayName, string? ScoutShortName);

public static class PriceLeagueCatalog
{
    public static ImmutableArray<PriceLeague> Defaults { get; } =
    [
        new("Runes of Aldur", "奥杜尔符文", "runes"),
        new("Forbidden Rites", "禁忌仪式", "forbiddenrites"),
        new("Standard", "永久区", "standard")
    ];

    public static async Task<ImmutableArray<PriceLeague>> FetchAsync(
        HttpClient client, CancellationToken cancellationToken)
    {
        var json = await client.GetStringAsync(Poe2LeagueResolver.DefaultEndpoint, cancellationToken)
            .ConfigureAwait(false);
        return Parse(json);
    }

    public static ImmutableArray<PriceLeague> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("赛季列表格式无效。");
        var leagues = new List<PriceLeague>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("Value", out var value) || value.ValueKind != JsonValueKind.String)
                continue;
            var id = value.GetString();
            if (string.IsNullOrWhiteSpace(id) || id.StartsWith("HC ", StringComparison.OrdinalIgnoreCase)
                || id.Equals("Hardcore", StringComparison.OrdinalIgnoreCase)) continue;
            var current = item.TryGetProperty("IsCurrent", out var flag) && flag.ValueKind == JsonValueKind.True;
            if (!current && id != "Standard") continue;
            new FreiAtlas.Core.Settings.PriceSettings(id).Validate();
            var shortName = item.TryGetProperty("ShortName", out var shortValue)
                && shortValue.ValueKind == JsonValueKind.String ? shortValue.GetString() : null;
            leagues.Add(new PriceLeague(id, DisplayName(id), shortName));
        }
        if (leagues.Count == 0) throw new JsonException("来源没有返回可用的普通赛季。");
        if (leagues.All(league => league.Id != "Standard")) leagues.Add(Defaults[2]);
        return leagues.DistinctBy(league => league.Id, StringComparer.Ordinal)
            .OrderBy(league => league.Id == "Standard" ? 1 : 0).ToImmutableArray();
    }

    public static string DisplayName(string id) => Defaults.FirstOrDefault(league => league.Id == id)?.DisplayName ?? id;
}
