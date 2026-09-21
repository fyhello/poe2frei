using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using FreiAtlas.Expedition;

namespace FreiAtlas.Expedition;

public sealed record ExpeditionPriceEntry(
    string ItemId,
    string DisplayName,
    decimal DivineValue,
    ImmutableArray<string> Aliases = default);

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

    private ExpeditionPriceSnapshot(
        DateTimeOffset fetchedAtUtc,
        decimal exaltedPerDivine,
        ImmutableDictionary<string, decimal> values,
        string leagueId = "")
    {
        FetchedAtUtc = fetchedAtUtc;
        ExaltedPerDivine = exaltedPerDivine > 0m ? exaltedPerDivine : 400m;
        _values = values;
        LeagueId = leagueId;
    }

    public DateTimeOffset FetchedAtUtc { get; }

    public decimal ExaltedPerDivine { get; }

    public int ItemCount => _values.Count;

    public string LeagueId { get; }

    public static ExpeditionPriceSnapshot Empty { get; } =
        new(DateTimeOffset.MinValue, 400m, ImmutableDictionary<string, decimal>.Empty);

    public static ExpeditionPriceSnapshot Merge(
        DateTimeOffset fetchedAtUtc,
        IEnumerable<ExpeditionPriceSourceResult> sources,
        string leagueId = "")
    {
        ArgumentNullException.ThrowIfNull(sources);
        var values = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
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

                foreach (var key in EnumerateKeys(entry))
                {
                    if (!values.ContainsKey(key))
                    {
                        values.Add(key, entry.DivineValue);
                    }
                }
            }
        }

        return new ExpeditionPriceSnapshot(
            fetchedAtUtc,
            exaltedPerDivine,
            values.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
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
            Values = _values.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase)
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

            snapshot = new ExpeditionPriceSnapshot(
                document.FetchedAtUtc,
                document.ExaltedPerDivine,
                document.Values.ToImmutableDictionary(
                    item => Normalize(item.Key),
                    item => item.Value,
                    StringComparer.OrdinalIgnoreCase),
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

    private sealed class CacheDocument
    {
        public string? LeagueId { get; set; }

        public DateTimeOffset FetchedAtUtc { get; set; }

        public decimal ExaltedPerDivine { get; set; }

        public Dictionary<string, decimal>? Values { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}
