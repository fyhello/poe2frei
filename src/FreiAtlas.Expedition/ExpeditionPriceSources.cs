using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FreiAtlas.Expedition;

public interface IExpeditionPriceSource
{
    string Name { get; }

    Task<ExpeditionPriceSourceResult> FetchAsync(CancellationToken cancellationToken);
}

public sealed class PoeNinjaPriceSource : IExpeditionPriceSource
{
    private const string Endpoint =
        "https://poe.ninja/poe2/api/economy/exchange/current/overview";
    private static readonly string[] DefaultCategories =
        ["Currency", "Expedition", "Runes", "Verisium", "UncutGems"];
    private readonly HttpClient _httpClient;
    private readonly IReadOnlyList<string> _categories;

    public PoeNinjaPriceSource(
        HttpClient httpClient,
        string league,
        IEnumerable<string>? categories = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        League = league?.Trim() ?? string.Empty;
        _categories = (categories ?? DefaultCategories).ToArray();
    }

    public string Name => "poe.ninja";

    public string League { get; }

    public async Task<ExpeditionPriceSourceResult> FetchAsync(
        CancellationToken cancellationToken)
    {
        var league = League;
        if (string.IsNullOrWhiteSpace(league))
        {
            league = await Poe2LeagueResolver.ResolveCurrentLeagueAsync(
                    _httpClient,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(league))
        {
            return new ExpeditionPriceSourceResult(Name, 0m, []);
        }

        var entries = new List<ExpeditionPriceEntry>();
        var diagnostics = new List<string>();
        var exaltedPerDivine = 0m;
        foreach (var category in _categories)
        {
            try
            {
                var uri = $"{Endpoint}?league={Uri.EscapeDataString(league)}&type={Uri.EscapeDataString(category)}";
                var json = await _httpClient.GetStringAsync(uri, cancellationToken)
                    .ConfigureAwait(false);
                var result = ParseOverview(json, category);
                if (exaltedPerDivine <= 0m)
                {
                    exaltedPerDivine = result.ExaltedPerDivine;
                }

                entries.AddRange(result.Entries);
            }
            catch (HttpRequestException exception)
            {
                diagnostics.Add($"{category} 请求失败：{exception.StatusCode?.ToString() ?? "网络错误"}");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                diagnostics.Add($"{category} 请求超时");
            }
            catch (JsonException)
            {
                diagnostics.Add($"{category} 响应格式无效");
            }
        }

        return new ExpeditionPriceSourceResult(
            Name,
            exaltedPerDivine,
            entries.ToImmutableArray()) { LeagueId = league, Diagnostics = diagnostics.ToImmutableArray() };
    }

    public static ExpeditionPriceSourceResult ParseOverview(
        string json,
        string category = "poe.ninja")
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new ExpeditionPriceSourceResult(category, 0m, []);
        }

        var core = root.TryGetProperty("core", out var coreElement)
                   && coreElement.ValueKind == JsonValueKind.Object
            ? coreElement
            : default;
        var rate = ReadDecimal(core, "rates", "exalted");
        var items = new Dictionary<string, (string Name, string? Image)>(StringComparer.Ordinal);
        AddItems(core, items);
        AddItems(root, items);

        var entries = ImmutableArray.CreateBuilder<ExpeditionPriceEntry>();
        if (!root.TryGetProperty("lines", out var lines)
            || lines.ValueKind != JsonValueKind.Array)
        {
            return new ExpeditionPriceSourceResult(category, rate, entries.ToImmutable());
        }

        foreach (var line in lines.EnumerateArray())
        {
            if (line.ValueKind != JsonValueKind.Object
                || !line.TryGetProperty("id", out var idElement))
            {
                continue;
            }

            var id = ReadString(idElement);
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var name = line.TryGetProperty("name", out var lineName)
                       ? ReadString(lineName)
                       : null;
            string? image = line.TryGetProperty("icon", out var icon)
                ? ReadString(icon)
                : null;
            if (items.TryGetValue(id, out var metadata))
            {
                name ??= metadata.Name;
                image ??= metadata.Image;
            }

            var divineValue = ReadDecimal(line, "primaryValue");
            if (string.IsNullOrWhiteSpace(name) || divineValue <= 0m)
            {
                continue;
            }

            entries.Add(new ExpeditionPriceEntry(id, name, divineValue));
        }

        return new ExpeditionPriceSourceResult(category, rate, entries.ToImmutable());
    }

    private static void AddItems(
        JsonElement parent,
        IDictionary<string, (string Name, string? Image)> items)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty("items", out var itemArray)
            || itemArray.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in itemArray.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("id", out var idElement)
                || !item.TryGetProperty("name", out var nameElement))
            {
                continue;
            }

            var id = ReadString(idElement);
            var name = ReadString(nameElement);
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var image = item.TryGetProperty("image", out var imageElement)
                ? ReadString(imageElement)
                : null;
            items[id] = (name, image);
        }
    }

    private static decimal ReadDecimal(JsonElement element, params string[] path)
    {
        foreach (var segment in path)
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty(segment, out element))
            {
                return 0m;
            }
        }

        if (element.ValueKind == JsonValueKind.Number
            && element.TryGetDecimal(out var number))
        {
            return number;
        }

        return element.ValueKind == JsonValueKind.String
               && decimal.TryParse(
                   element.GetString(),
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out number)
            ? number
            : 0m;
    }

    private static string? ReadString(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null
        };
}

public static class Poe2LeagueResolver
{
    public const string DefaultEndpoint = "https://api.poe2scout.com/poe2/Leagues";

    public static async Task<string> ResolveCurrentLeagueAsync(
        HttpClient httpClient,
        CancellationToken cancellationToken,
        string? endpoint = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        try
        {
            var json = await httpClient.GetStringAsync(
                    string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint,
                    cancellationToken)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            string? firstCurrent = null;
            foreach (var league in document.RootElement.EnumerateArray())
            {
                if (league.ValueKind != JsonValueKind.Object
                    || !league.TryGetProperty("Value", out var valueElement)
                    || valueElement.ValueKind != JsonValueKind.String
                    || !league.TryGetProperty("IsCurrent", out var currentElement)
                    || currentElement.ValueKind is not JsonValueKind.True)
                {
                    continue;
                }

                var value = valueElement.GetString();
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                firstCurrent ??= value;
                if (!value.StartsWith("HC ", StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }

            return firstCurrent ?? string.Empty;
        }
        catch (HttpRequestException)
        {
            return string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    public static async Task<string> ResolveCurrentLeagueShortNameAsync(
        HttpClient httpClient,
        CancellationToken cancellationToken,
        string? endpoint = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        try
        {
            var json = await httpClient.GetStringAsync(
                    string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint,
                    cancellationToken)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            string? firstCurrent = null;
            foreach (var league in document.RootElement.EnumerateArray())
            {
                if (league.ValueKind != JsonValueKind.Object
                    || !league.TryGetProperty("ShortName", out var shortNameElement)
                    || shortNameElement.ValueKind != JsonValueKind.String
                    || !league.TryGetProperty("Value", out var valueElement)
                    || valueElement.ValueKind != JsonValueKind.String
                    || !league.TryGetProperty("IsCurrent", out var currentElement)
                    || currentElement.ValueKind is not JsonValueKind.True)
                {
                    continue;
                }

                var shortName = shortNameElement.GetString();
                var value = valueElement.GetString();
                if (string.IsNullOrWhiteSpace(shortName))
                {
                    continue;
                }

                firstCurrent ??= shortName;
                if (value is not null
                    && !value.StartsWith("HC ", StringComparison.OrdinalIgnoreCase))
                {
                    return shortName;
                }
            }

            return firstCurrent ?? string.Empty;
        }
        catch (HttpRequestException)
        {
            return string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}

public sealed class Poe2DbPriceSource : IExpeditionPriceSource
{
    public const string DefaultEndpoint = "https://poe2db.tw/cn/Economy_Currency";
    private readonly HttpClient _httpClient;

    public Poe2DbPriceSource(HttpClient httpClient, string? endpoint = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Endpoint = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint;
    }

    public string Name => "POE2DB";

    public string Endpoint { get; }

    public async Task<ExpeditionPriceSourceResult> FetchAsync(
        CancellationToken cancellationToken)
    {
        var html = await _httpClient.GetStringAsync(Endpoint, cancellationToken)
            .ConfigureAwait(false);
        return ParseHtml(html);
    }

    public static ExpeditionPriceSourceResult ParseHtml(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var rows = new List<ParsedRow>();
        foreach (Match rowMatch in Regex.Matches(
                     html,
                     "<tr\\b[^>]*>(?<row>.*?)</tr>",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var cells = Regex.Matches(
                    rowMatch.Groups["row"].Value,
                    "<td\\b[^>]*>(?<cell>.*?)</td>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline)
                .Select(match => match.Groups["cell"].Value)
                .ToArray();
            if (cells.Length < 2)
            {
                continue;
            }

            var item = Regex.Match(
                cells[0],
                "<a\\s+href=[\\\"']Economy_(?<slug>[^\\\"']+)[\\\"'][^>]*>(?<body>.*?)</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var ratio = Regex.Match(
                cells[1],
                "(?<left>[0-9][0-9,]*(?:\\.[0-9]+)?)\\s*<a\\s+href=[\\\"']Economy_(?<leftSlug>[^\\\"']+)[\\\"'][^>]*>.*?</a>.*?<i\\b[^>]*>.*?</i>\\s*(?<right>[0-9][0-9,]*(?:\\.[0-9]+)?)\\s*<a\\s+href=[\\\"']Economy_(?<rightSlug>[^\\\"']+)[\\\"']",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!item.Success
                || !ratio.Success
                || !TryParseNumber(ratio.Groups["left"].Value, out var left)
                || !TryParseNumber(ratio.Groups["right"].Value, out var right)
                || right <= 0m)
            {
                continue;
            }

            var slug = item.Groups["slug"].Value.Trim();
            var name = CleanText(item.Groups["body"].Value);
            if (string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            rows.Add(new ParsedRow(
                slug,
                name,
                ratio.Groups["leftSlug"].Value.Trim(),
                left,
                ratio.Groups["rightSlug"].Value.Trim(),
                right));
        }

        var chaosPerDivine = rows
            .Where(row => string.Equals(row.ItemId, "divine", StringComparison.OrdinalIgnoreCase))
            .Select(row => row.LeftCurrency.Equals("chaos", StringComparison.OrdinalIgnoreCase)
                && row.RightCurrency.Equals("divine", StringComparison.OrdinalIgnoreCase)
                ? row.LeftAmount / row.RightAmount
                : 0m)
            .FirstOrDefault(value => value > 0m);
        if (chaosPerDivine <= 0m)
        {
            chaosPerDivine = 8m;
        }

        var entries = ImmutableArray.CreateBuilder<ExpeditionPriceEntry>();
        foreach (var row in rows)
        {
            var divineValue = row.ItemId.Equals("divine", StringComparison.OrdinalIgnoreCase)
                ? 1m
                : ToDivineValue(row, chaosPerDivine);
            if (divineValue <= 0m)
            {
                continue;
            }

            var aliases = EnglishAlias(row.ItemId);
            entries.Add(new ExpeditionPriceEntry(
                row.ItemId,
                row.DisplayName,
                divineValue,
                aliases is null ? [] : [aliases]));
        }

        var exalted = entries.FirstOrDefault(item =>
            item.ItemId.Equals("exalted", StringComparison.OrdinalIgnoreCase));
        var exaltedPerDivine = exalted is not null && exalted.DivineValue > 0m
            ? decimal.Round(1m / exalted.DivineValue, 4)
            : 400m;
        return new ExpeditionPriceSourceResult("POE2DB", exaltedPerDivine, entries.ToImmutable());
    }

    private static decimal ToDivineValue(ParsedRow row, decimal chaosPerDivine)
    {
        if (row.LeftCurrency.Equals("divine", StringComparison.OrdinalIgnoreCase)
            && row.RightCurrency.Equals(row.ItemId, StringComparison.OrdinalIgnoreCase))
        {
            return row.LeftAmount / row.RightAmount;
        }

        if (row.RightCurrency.Equals("divine", StringComparison.OrdinalIgnoreCase)
            && row.LeftCurrency.Equals(row.ItemId, StringComparison.OrdinalIgnoreCase))
        {
            return row.RightAmount / row.LeftAmount;
        }

        if (row.LeftCurrency.Equals("chaos", StringComparison.OrdinalIgnoreCase)
            && row.RightCurrency.Equals(row.ItemId, StringComparison.OrdinalIgnoreCase))
        {
            return chaosPerDivine * row.LeftAmount / row.RightAmount;
        }

        if (row.RightCurrency.Equals("chaos", StringComparison.OrdinalIgnoreCase)
            && row.LeftCurrency.Equals(row.ItemId, StringComparison.OrdinalIgnoreCase))
        {
            return chaosPerDivine * row.RightAmount / row.LeftAmount;
        }

        return 0m;
    }

    private static string? EnglishAlias(string slug)
    {
        var knownAlias = slug.ToLowerInvariant() switch
        {
            "gcp" => "Gemcutter's Prism",
            "annul" => "Orb of Annulment",
            "alch" => "Orb of Alchemy",
            "scrap" => "Armourer's Scrap",
            "chaos" => "Chaos Orb",
            "chance" => "Orb of Chance",
            "divine" => "Divine Orb",
            "exalted" => "Exalted Orb",
            "vaal" => "Vaal Orb",
            "aug" => "Orb of Augmentation",
            _ => null
        };
        if (knownAlias is not null)
        {
            return knownAlias;
        }

        var words = slug.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]);
        var alias = string.Join(' ', words);
        return alias.Length == 0 ? null : alias;
    }

    private static string CleanText(string html)
        => WebUtility.HtmlDecode(
                Regex.Replace(html, "<[^>]+>", string.Empty))
            .Replace("\u00A0", " ", StringComparison.Ordinal)
            .Trim();

    private static bool TryParseNumber(string value, out decimal number)
        => decimal.TryParse(
            value.Replace(",", string.Empty, StringComparison.Ordinal),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out number);

    private sealed record ParsedRow(
        string ItemId,
        string DisplayName,
        string LeftCurrency,
        decimal LeftAmount,
        string RightCurrency,
        decimal RightAmount);
}

public sealed class Poe2ScoutPriceSource : IExpeditionPriceSource
{
    public const string DefaultApiBase = "https://api.poe2scout.com";
    private static readonly string[] DefaultCategories =
        ["currency", "expedition", "runes", "verisium", "uncutgems"];
    private readonly HttpClient _httpClient;
    private readonly IReadOnlyList<string> _categories;

    public Poe2ScoutPriceSource(
        HttpClient httpClient,
        string? apiBase = null,
        string? leagueShortName = null,
        IEnumerable<string>? categories = null,
        string leagueId = "")
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ApiBase = string.IsNullOrWhiteSpace(apiBase)
            ? DefaultApiBase
            : apiBase.TrimEnd('/');
        LeagueShortName = leagueShortName?.Trim() ?? string.Empty;
        LeagueId = leagueId;
        _categories = (categories ?? DefaultCategories)
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Select(category => category.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string Name => "poe2scout";

    public string ApiBase { get; }

    public string LeagueShortName { get; }

    public string LeagueId { get; }

    public async Task<ExpeditionPriceSourceResult> FetchAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var league = LeagueShortName;
            if (string.IsNullOrWhiteSpace(league))
            {
                league = await Poe2LeagueResolver.ResolveCurrentLeagueShortNameAsync(
                        _httpClient,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(league))
            {
                return new ExpeditionPriceSourceResult(Name, 0m, []);
            }

            var prefix = $"{ApiBase}/poe2/Leagues/{Uri.EscapeDataString(league)}";
            var referenceJson = await _httpClient.GetStringAsync(
                    $"{prefix}/ReferenceCurrencies",
                    cancellationToken)
                .ConfigureAwait(false);
            var exaltedPerDivine = ParseExaltedPerDivine(referenceJson);
            var entries = ImmutableArray.CreateBuilder<ExpeditionPriceEntry>();
            var diagnostics = ImmutableArray.CreateBuilder<string>();
            foreach (var category in _categories)
            {
                try
                {
                    var uri = $"{prefix}/Currencies/ByCategory"
                              + $"?category={Uri.EscapeDataString(category)}"
                              + "&page=1&perPage=250&referenceCurrency=divine";
                    var json = await _httpClient.GetStringAsync(uri, cancellationToken)
                        .ConfigureAwait(false);
                    entries.AddRange(ParseByCategory(json, exaltedPerDivine).Entries);
                }
                catch (HttpRequestException exception)
                {
                    diagnostics.Add($"{category} 请求失败：{exception.StatusCode?.ToString() ?? "网络错误"}");
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    diagnostics.Add($"{category} 请求超时");
                }
                catch (JsonException)
                {
                    diagnostics.Add($"{category} 响应格式无效");
                }
            }

            return new ExpeditionPriceSourceResult(
                Name,
                exaltedPerDivine,
                entries.ToImmutable()) { LeagueId = LeagueId, Diagnostics = diagnostics.ToImmutable() };
        }
        catch (HttpRequestException)
        {
            return new ExpeditionPriceSourceResult(Name, 0m, [])
                { LeagueId = LeagueId, Diagnostics = ["请求失败，未取得价格。"] };
        }
        catch (JsonException)
        {
            return new ExpeditionPriceSourceResult(Name, 0m, [])
                { LeagueId = LeagueId, Diagnostics = ["响应格式无效，未取得价格。"] };
        }
    }

    public static ExpeditionPriceSourceResult ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var items = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToArray()
            : FindArray(root, "items", "prices", "data");
        var entries = ImmutableArray.CreateBuilder<ExpeditionPriceEntry>();
        var rate = ReadDecimal(root, "exaltedPerDivine", "ExaltedPerDivine");
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = ReadString(item, "name", "Name", "itemName", "ItemName", "text", "Text");
            var id = ReadString(item, "id", "Id", "key", "Key") ?? name;
            var value = ReadDecimal(item, "divineValue", "DivineValue", "priceInDivine", "PriceInDivine", "price", "Price");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id) || value <= 0m)
            {
                continue;
            }

            entries.Add(new ExpeditionPriceEntry(id, name, value));
        }

        return new ExpeditionPriceSourceResult("poe2scout", rate, entries.ToImmutable());
    }

    public static ExpeditionPriceSourceResult ParseByCategory(
        string json,
        decimal exaltedPerDivine)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var items = FindArray(root, "Items", "items");
        var entries = ImmutableArray.CreateBuilder<ExpeditionPriceEntry>();
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var apiId = ReadString(item, "ApiId", "apiId");
            var itemId = ReadString(item, "BaseItemTypeId", "baseItemTypeId") ?? apiId;
            var displayName = ReadString(item, "Text", "text");
            var divineValue = ReadDecimal(item, "CurrentPrice", "currentPrice");
            if (string.IsNullOrWhiteSpace(itemId)
                || string.IsNullOrWhiteSpace(displayName)
                || divineValue <= 0m)
            {
                continue;
            }

            entries.Add(new ExpeditionPriceEntry(
                itemId,
                displayName,
                divineValue,
                string.IsNullOrWhiteSpace(apiId) ? [] : [apiId]));
        }

        return new ExpeditionPriceSourceResult(
            "poe2scout",
            exaltedPerDivine,
            entries.ToImmutable());
    }

    private static decimal ParseExaltedPerDivine(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return 0m;
        }

        foreach (var reference in document.RootElement.EnumerateArray())
        {
            if (string.Equals(
                    ReadString(reference, "ApiId", "apiId"),
                    "divine",
                    StringComparison.OrdinalIgnoreCase))
            {
                return ReadDecimal(reference, "RelativePrice", "relativePrice");
            }
        }

        return 0m;
    }

    private static JsonElement[] FindArray(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Array)
            {
                return value.EnumerateArray().ToArray();
            }
        }

        return [];
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property)
                && property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }
        }

        return null;
    }

    private static decimal ReadDecimal(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Number
                && property.TryGetDecimal(out var number))
            {
                return number;
            }

            if (property.ValueKind == JsonValueKind.String
                && decimal.TryParse(
                    property.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out number))
            {
                return number;
            }
        }

        return 0m;
    }

}
