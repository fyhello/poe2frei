using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Memory;

namespace FreiAtlas.Game.Terrain;

internal sealed class AreaLandmarkReader
{
    private const int ClusterGap = 2;

    private readonly IReadOnlyDictionary<string, IReadOnlyList<LandmarkRule>> _rules;
    private readonly Poe2MemoryProfile _profile;

    private AreaLandmarkReader(
        IReadOnlyDictionary<string, IReadOnlyList<LandmarkRule>> rules,
        Poe2MemoryProfile? profile = null)
    {
        _rules = rules;
        _profile = profile ?? Poe2MemoryProfile.Current;
    }

    public static AreaLandmarkReader LoadEmbedded(
        Poe2MemoryProfile? profile = null)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith(
                    ".Data.area-landmarks.json",
                    StringComparison.OrdinalIgnoreCase));
            if (resourceName is null)
            {
                return Empty(profile);
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            return stream is null
                ? Empty(profile)
                : new AreaLandmarkReader(Parse(stream), profile);
        }
        catch (JsonException)
        {
            return Empty(profile);
        }
        catch (IOException)
        {
            return Empty(profile);
        }
    }

    internal static AreaLandmarkReader FromJson(
        string json,
        Poe2MemoryProfile? profile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        return new AreaLandmarkReader(Parse(stream), profile);
    }

    public IReadOnlyList<AreaLandmarkSnapshot> Read(
        string areaCode,
        IReadOnlyList<AreaTerrainTile> tiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(areaCode);
        ArgumentNullException.ThrowIfNull(tiles);

        var matchingTiles = new Dictionary<string, MatchedTileGroup>(
            StringComparer.Ordinal);
        foreach (var tile in tiles)
        {
            if (!TryMatch(areaCode, tile.TilePath, out var rule))
            {
                continue;
            }

            if (!matchingTiles.TryGetValue(tile.TilePath, out var group))
            {
                group = new MatchedTileGroup(rule);
                matchingTiles[tile.TilePath] = group;
            }

            group.Cells.Add((tile.TileX, tile.TileY));
        }

        var landmarks = new List<AreaLandmarkSnapshot>();
        foreach (var (tilePath, group) in matchingTiles.OrderBy(
                     entry => entry.Key,
                     StringComparer.Ordinal))
        {
            foreach (var cluster in Cluster(group.Cells))
            {
                var averageX = cluster.Average(cell => (double)cell.X);
                var averageY = cluster.Average(cell => (double)cell.Y);
                var gridPosition = new Vector2(
                    (float)(averageX * _profile.Terrain.TileGridCells),
                    (float)(averageY * _profile.Terrain.TileGridCells));
                landmarks.Add(new AreaLandmarkSnapshot(
                    CreateLandmarkId(areaCode, tilePath, gridPosition),
                    group.Rule.DisplayName,
                    tilePath,
                    ResolveLandmarkKind(areaCode, group.Rule),
                    gridPosition,
                    cluster.Count));
            }
        }

        return landmarks;
    }

    private bool TryMatch(
        string areaCode,
        string tilePath,
        out LandmarkRule rule)
    {
        if (TryMatchRules(areaCode, tilePath, out rule))
        {
            return true;
        }

        return TryMatchRules("*", tilePath, out rule);
    }

    private bool TryMatchRules(
        string areaCode,
        string tilePath,
        out LandmarkRule rule)
    {
        if (_rules.TryGetValue(areaCode, out var rules))
        {
            foreach (var candidate in rules)
            {
                if (Matches(candidate, tilePath))
                {
                    rule = candidate;
                    return true;
                }
            }
        }

        rule = default!;
        return false;
    }

    private static IReadOnlyList<List<(int X, int Y)>> Cluster(
        IReadOnlyCollection<(int X, int Y)> cells)
    {
        var remaining = cells.ToHashSet();
        var clusters = new List<List<(int X, int Y)>>();
        while (remaining.Count > 0)
        {
            var start = remaining.First();
            remaining.Remove(start);
            var cluster = new List<(int X, int Y)>();
            var queue = new Queue<(int X, int Y)>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                cluster.Add(current);
                for (var dx = -ClusterGap; dx <= ClusterGap; dx++)
                {
                    for (var dy = -ClusterGap; dy <= ClusterGap; dy++)
                    {
                        var neighbor = (current.X + dx, current.Y + dy);
                        if (remaining.Remove(neighbor))
                        {
                            queue.Enqueue(neighbor);
                        }
                    }
                }
            }

            clusters.Add(cluster);
        }

        return clusters;
    }

    private static string CreateLandmarkId(
        string areaCode,
        string tilePath,
        Vector2 gridPosition)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{areaCode}|{tilePath}|{MathF.Round(gridPosition.X)}|{MathF.Round(gridPosition.Y)}");

    private static IReadOnlyDictionary<string, IReadOnlyList<LandmarkRule>> Parse(
        Stream stream)
    {
        using var document = JsonDocument.Parse(stream);
        var result = new Dictionary<string, IReadOnlyList<LandmarkRule>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var area in document.RootElement.EnumerateObject())
        {
            var rules = new List<LandmarkRule>();
            var normalizedRules = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var tile in area.Value.EnumerateObject())
            {
                var pattern = NormalizePattern(tile.Name);
                if (string.IsNullOrWhiteSpace(pattern.Value))
                {
                    continue;
                }

                var ruleKey = $"{pattern.MatchKind}:{pattern.Value}";
                if (!normalizedRules.Add(ruleKey))
                {
                    continue;
                }

                var displayName = tile.Value.GetString();
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = DeriveName(pattern.Value);
                }

                rules.Add(new LandmarkRule(
                    area.Name,
                    pattern.Value,
                    displayName,
                    Classify(pattern.Value, displayName),
                    pattern.MatchKind,
                    pattern.MatchKind != LandmarkMatchKind.Contains
                    && !pattern.Value.Contains('/')));
            }

            result[area.Name] = rules;
        }

        return result;
    }

    private static NormalizedPattern NormalizePattern(string value)
    {
        var suffix = value.IndexOf(':');
        var path = suffix >= 0 ? value[..suffix] : value;
        if (path.Length > 2
            && path.StartsWith('*')
            && path.EndsWith('*'))
        {
            return new NormalizedPattern(
                path[1..^1],
                LandmarkMatchKind.Contains);
        }

        var wildcard = path.IndexOf('*');
        var matchKind = wildcard >= 0
            || (!path.EndsWith(".tdtx", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith(".tdt", StringComparison.OrdinalIgnoreCase))
            ? LandmarkMatchKind.Prefix
            : LandmarkMatchKind.Exact;
        if (wildcard >= 0)
        {
            path = path[..wildcard];
        }

        return new NormalizedPattern(
            path.Replace(".tdtx", ".tdt", StringComparison.OrdinalIgnoreCase),
            matchKind);
    }

    private static bool Matches(LandmarkRule rule, string tilePath)
    {
        var candidatePath = rule.MatchFileNameOnly
            ? GetFileName(tilePath)
            : tilePath;
        return rule.MatchKind switch
        {
            LandmarkMatchKind.Exact => candidatePath.Equals(
                rule.TilePattern,
                StringComparison.OrdinalIgnoreCase),
            LandmarkMatchKind.Prefix => candidatePath.StartsWith(
                rule.TilePattern,
                StringComparison.OrdinalIgnoreCase),
            LandmarkMatchKind.Contains => candidatePath.Contains(
                rule.TilePattern,
                StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static string GetFileName(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    private static AreaLandmarkKind ResolveLandmarkKind(
        string areaCode,
        LandmarkRule rule)
    {
        if (rule.Kind != AreaLandmarkKind.BossArena)
        {
            return rule.Kind;
        }

        if (rule.AreaPattern.Equals(areaCode, StringComparison.OrdinalIgnoreCase))
        {
            return AreaLandmarkKind.BossArena;
        }

        if (!rule.AreaPattern.Equals("*", StringComparison.OrdinalIgnoreCase))
        {
            return AreaLandmarkKind.BossHint;
        }

        var segments = rule.TilePattern.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Take(Math.Max(segments.Length - 1, 0)).Any(segment =>
                segment.Equals(areaCode, StringComparison.OrdinalIgnoreCase)))
        {
            return AreaLandmarkKind.BossArena;
        }

        var areaToken = areaCode.StartsWith("Map", StringComparison.OrdinalIgnoreCase)
            ? areaCode[3..].TrimEnd('_')
            : areaCode.TrimEnd('_');
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (!segments[index].Equals("Maps", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return segments[index + 1].Equals(
                areaToken,
                StringComparison.OrdinalIgnoreCase)
                ? AreaLandmarkKind.BossArena
                : AreaLandmarkKind.BossHint;
        }

        return AreaLandmarkKind.BossHint;
    }

    private static string DeriveName(string path)
    {
        var slash = path.LastIndexOf('/');
        var name = slash >= 0 ? path[(slash + 1)..] : path;
        return name.EndsWith(".tdt", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;
    }

    private static AreaLandmarkKind Classify(
        string tilePattern,
        string displayName)
    {
        var combined = $"{tilePattern} {displayName}";
        if (combined.Contains("WaygateDevice", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Incursion", StringComparison.OrdinalIgnoreCase)
            || displayName.Equals("神庙", StringComparison.Ordinal))
        {
            return AreaLandmarkKind.Incursion;
        }

        if (combined.Contains("waypoint", StringComparison.OrdinalIgnoreCase))
        {
            return AreaLandmarkKind.Waypoint;
        }

        if (combined.Contains("boss", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("arena", StringComparison.OrdinalIgnoreCase))
        {
            return AreaLandmarkKind.BossArena;
        }

        if (combined.Contains("transition", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("entrance", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("stairs", StringComparison.OrdinalIgnoreCase)
            || displayName.StartsWith("To ", StringComparison.OrdinalIgnoreCase))
        {
            return AreaLandmarkKind.Transition;
        }

        return AreaLandmarkKind.Mechanic;
    }

    private static AreaLandmarkReader Empty(Poe2MemoryProfile? profile)
        => new(
            new Dictionary<string, IReadOnlyList<LandmarkRule>>(
                StringComparer.OrdinalIgnoreCase),
            profile);

    private sealed record LandmarkRule(
        string AreaPattern,
        string TilePattern,
        string DisplayName,
        AreaLandmarkKind Kind,
        LandmarkMatchKind MatchKind,
        bool MatchFileNameOnly);

    private sealed record NormalizedPattern(
        string Value,
        LandmarkMatchKind MatchKind);

    private enum LandmarkMatchKind
    {
        Exact,
        Prefix,
        Contains
    }

    private sealed class MatchedTileGroup(LandmarkRule rule)
    {
        public LandmarkRule Rule { get; } = rule;
        public HashSet<(int X, int Y)> Cells { get; } = [];
    }
}
