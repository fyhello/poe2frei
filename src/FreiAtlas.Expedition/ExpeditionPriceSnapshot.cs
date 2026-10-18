using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using FreiAtlas.Expedition;

namespace FreiAtlas.Expedition;

public sealed record ExpeditionPriceEntry(
    string ItemId,
    string DisplayName,
    decimal DivineValue,
    ImmutableArray<string> Aliases = default)
{
    /// <summary>价格源返回的分类（如 Currency/Expedition/Runes）。</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>价格来源名称（如 poe.ninja/poe2scout）。</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>客户端 BaseItemTypes 的 metadata 路径（价格源能提供时用于跨语言精确匹配）。</summary>
    public string MetadataPath { get; init; } = string.Empty;
}

public sealed record ExpeditionPriceSourceResult(
    string SourceName,
    decimal ExaltedPerDivine,
    ImmutableArray<ExpeditionPriceEntry> Entries = default)
{
    public string LeagueId { get; init; } = string.Empty;
    public ImmutableArray<string> Diagnostics { get; init; } = [];
}

public sealed class ExpeditionPriceSnapshot : IExpeditionPriceBook
{
    private readonly ImmutableDictionary<string, decimal> _values;
    private readonly ImmutableArray<ExpeditionPriceEntry> _entries;

    private ExpeditionPriceSnapshot(
        DateTimeOffset fetchedAtUtc,
        decimal exaltedPerDivine,
        ImmutableDictionary<string, decimal> values,
        ImmutableArray<ExpeditionPriceEntry> entries,
        string leagueId = "")
    {
        FetchedAtUtc = fetchedAtUtc;
        ExaltedPerDivine = exaltedPerDivine > 0m ? exaltedPerDivine : 400m;
        _values = values;
        _entries = entries.IsDefault ? [] : entries;
        LeagueId = leagueId;
    }

    public DateTimeOffset FetchedAtUtc { get; }

    public decimal ExaltedPerDivine { get; }

    /// <summary>唯一条目数（不再把 ItemId/别名重复计入）。</summary>
    public int ItemCount => _entries.Length;

    public ImmutableDictionary<string, decimal> AllValues => _values;

    /// <summary>价格表中用于展示的唯一条目。</summary>
    public ImmutableArray<ExpeditionPriceEntry> Entries => _entries;

    public string LeagueId { get; }

    public static ExpeditionPriceSnapshot Empty { get; } =
        new(DateTimeOffset.MinValue, 400m, ImmutableDictionary<string, decimal>.Empty, []);

    public static ExpeditionPriceSnapshot Merge(
        DateTimeOffset fetchedAtUtc,
        IEnumerable<ExpeditionPriceSourceResult> sources,
        string leagueId = "")
    {
        ArgumentNullException.ThrowIfNull(sources);
        var values = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var entries = ImmutableArray.CreateBuilder<ExpeditionPriceEntry>();
        var entryIndexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var exaltedPerDivine = 0m;
        foreach (var source in sources)
        {
            if (leagueId.Length > 0 && source.LeagueId != leagueId)
                throw new ArgumentException("不能合并不同赛季或未标明赛季的价格。");
            if (exaltedPerDivine <= 0m && source.ExaltedPerDivine > 0m)
            {
                exaltedPerDivine = source.ExaltedPerDivine;
            }

            if (source.Entries.IsDefaultOrEmpty)
            {
                continue;
            }

            foreach (var entry in source.Entries)
            {
                if (entry.DivineValue < 0m)
                {
                    continue;
                }

                var displayName = Normalize(entry.DisplayName);
                var key = displayName.Length > 0
                    ? "name:" + displayName + "|" + Normalize(entry.Category).ToLowerInvariant()
                    : "id:" + Normalize(entry.ItemId);
                if (entryIndexByKey.TryGetValue(key, out int index))
                {
                    // 同名条目来自不同来源时，优先补齐 poe2scout 提供的 metadata 路径。
                    var existing = entries[index];
                    if (string.IsNullOrWhiteSpace(existing.MetadataPath)
                        && !string.IsNullOrWhiteSpace(entry.MetadataPath))
                    {
                        entries[index] = existing with { MetadataPath = entry.MetadataPath };
                    }
                }
                else
                {
                    entryIndexByKey[key] = entries.Count;
                    entries.Add(NormalizeEntry(entry.Source.Length == 0
                        ? entry with { Source = source.SourceName }
                        : entry));
                }

                foreach (var alias in EnumerateKeys(entry))
                {
                    if (!values.ContainsKey(alias))
                    {
                        values.Add(alias, entry.DivineValue);
                    }
                }
            }
        }

        return new ExpeditionPriceSnapshot(
            fetchedAtUtc,
            exaltedPerDivine,
            values.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            entries.ToImmutable(),
            leagueId);
    }

    public bool TryGet(
        string itemId,
        string displayName,
        out decimal divineValue)
    {
        if (!string.IsNullOrWhiteSpace(itemId)
            && _values.TryGetValue(Normalize(itemId), out divineValue))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(displayName)
            && _values.TryGetValue(Normalize(displayName), out divineValue))
        {
            return true;
        }

        divineValue = default;
        return false;
    }

    public bool IsExpired(
        DateTimeOffset now,
        TimeSpan? lifetime = null)
        => FetchedAtUtc == DateTimeOffset.MinValue
           || now - FetchedAtUtc >= (lifetime ?? TimeSpan.FromHours(24));

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = path + ".tmp";
        var document = new CacheDocument
        {
            LeagueId = LeagueId,
            FetchedAtUtc = FetchedAtUtc,
            ExaltedPerDivine = ExaltedPerDivine,
            Values = _values.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase),
            Items = _entries.ToList()
        };
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }

    public static bool TryLoad(
        string path,
        DateTimeOffset now,
        TimeSpan lifetime,
        out ExpeditionPriceSnapshot snapshot,
        string expectedLeagueId = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            snapshot = Empty;
            return false;
        }

        try
        {
            var document = JsonSerializer.Deserialize<CacheDocument>(
                File.ReadAllText(path),
                JsonOptions);
            if (document is null
                || (expectedLeagueId.Length > 0 && document.LeagueId != expectedLeagueId)
                || document.ExaltedPerDivine <= 0m
                || document.Values is null)
            {
                snapshot = Empty;
                return false;
            }

            var entries = document.Items is { Count: > 0 }
                ? document.Items.Select(NormalizeEntry).ToImmutableArray()
                : document.Values
                    .Select(item => new ExpeditionPriceEntry(item.Key, item.Key, item.Value, []))
                    .ToImmutableArray();
            snapshot = new ExpeditionPriceSnapshot(
                document.FetchedAtUtc,
                document.ExaltedPerDivine,
                document.Values.ToImmutableDictionary(
                    item => Normalize(item.Key),
                    item => item.Value,
                    StringComparer.OrdinalIgnoreCase),
                entries,
                document.LeagueId ?? string.Empty);
            if (snapshot.IsExpired(now, lifetime))
            {
                snapshot = Empty;
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            snapshot = Empty;
            return false;
        }
        catch (IOException)
        {
            snapshot = Empty;
            return false;
        }
    }

    private static IEnumerable<string> EnumerateKeys(ExpeditionPriceEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.ItemId))
        {
            yield return Normalize(entry.ItemId);
        }

        if (!string.IsNullOrWhiteSpace(entry.DisplayName))
        {
            yield return Normalize(entry.DisplayName);
        }

        if (!entry.Aliases.IsDefaultOrEmpty)
        {
            foreach (var alias in entry.Aliases)
            {
                if (!string.IsNullOrWhiteSpace(alias))
                {
                    yield return Normalize(alias);
                }
            }
        }
    }

    private static string Normalize(string value)
        => value.Trim();

    private static ExpeditionPriceEntry NormalizeEntry(ExpeditionPriceEntry entry)
        => entry.Aliases.IsDefault ? entry with { Aliases = [] } : entry;

    private sealed class CacheDocument
    {
        public string? LeagueId { get; set; }

        public DateTimeOffset FetchedAtUtc { get; set; }

        public decimal ExaltedPerDivine { get; set; }

        public Dictionary<string, decimal>? Values { get; set; }

        public List<ExpeditionPriceEntry>? Items { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}
