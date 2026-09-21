using System.Reflection;
using System.Text.Json;
using FreiAtlas.Core.Area;

namespace FreiAtlas.Game.Content;

internal sealed class AreaContentCatalog
{
    private readonly HashSet<string> _expeditionExactMetadata;
    private readonly HashSet<string> _bossExactMetadata;
    private readonly string[] _bossMetadataPrefixes;
    private readonly HashSet<string> _bossDisplayNames;
    private readonly IReadOnlyDictionary<string, string[]> _bossTilePatternsByArea;
    private readonly IReadOnlyList<MechanicRule> _mechanicRules;

    private AreaContentCatalog(CatalogDocument document, bool isAvailable)
    {
        _expeditionExactMetadata = ToSet(document.Expedition.ExactMetadata);
        _bossExactMetadata = ToSet(document.Bosses.ExactMetadata);
        _bossMetadataPrefixes = document.Bosses.MetadataPrefixes
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        _bossDisplayNames = ToSet(document.Bosses.DisplayNames);
        _bossTilePatternsByArea = document.Bosses.BossTilePatternsByArea;
        _mechanicRules =
        [
            CreateMechanicRule(AreaContentKind.Abyss, document.Mechanics.Abyss),
            CreateMechanicRule(AreaContentKind.Ritual, document.Mechanics.Ritual),
            CreateMechanicRule(AreaContentKind.Breach, document.Mechanics.Breach),
            CreateMechanicRule(AreaContentKind.Essence, document.Mechanics.Essence),
            CreateMechanicRule(AreaContentKind.Strongbox, document.Mechanics.Strongbox)
        ];
        IsAvailable = isAvailable;
    }

    public bool IsAvailable { get; }

    public static AreaContentCatalog LoadEmbedded()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith(
                    ".Data.area-content-catalog.json",
                    StringComparison.OrdinalIgnoreCase));
            if (resourceName is null)
            {
                return Unavailable();
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return Unavailable();
            }

            var document = JsonSerializer.Deserialize<CatalogDocument>(
                stream,
                JsonOptions);
            return document is null
                ? Unavailable()
                : new AreaContentCatalog(document, true);
        }
        catch (JsonException)
        {
            return Unavailable();
        }
        catch (IOException)
        {
            return Unavailable();
        }
    }

    internal static AreaContentCatalog FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var document = JsonSerializer.Deserialize<CatalogDocument>(
                           json,
                           JsonOptions)
                       ?? throw new JsonException("The area content catalog was empty.");
        return new AreaContentCatalog(document, true);
    }

    public bool IsExpedition(AreaEntitySnapshot entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return _expeditionExactMetadata.Contains(entity.MetadataPath);
    }

    public bool TryMatchMechanicEntity(
        AreaEntitySnapshot entity,
        out AreaContentKind kind,
        out AreaContentEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(entity);
        foreach (var rule in _mechanicRules)
        {
            if (!IsEligibleMechanicEntity(entity, rule.Kind)
                || rule.ExcludedMetadataFragments.Any(fragment =>
                    entity.MetadataPath.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var fragment = rule.MetadataFragments.FirstOrDefault(value =>
                entity.MetadataPath.Contains(value, StringComparison.OrdinalIgnoreCase));
            if (fragment is null)
            {
                continue;
            }

            kind = rule.Kind;
            evidence = new AreaContentEvidence(
                "Catalog",
                "MetadataFragment",
                fragment,
                0.95f);
            return true;
        }

        kind = AreaContentKind.Unknown;
        evidence = default!;
        return false;
    }

    public bool TryMatchBossEntity(
        AreaEntitySnapshot entity,
        out AreaContentEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (_bossExactMetadata.Contains(entity.MetadataPath))
        {
            evidence = new AreaContentEvidence(
                "Catalog",
                "ExactMetadata",
                entity.MetadataPath,
                1f);
            return true;
        }

        var prefix = _bossMetadataPrefixes.FirstOrDefault(value =>
            entity.MetadataPath.StartsWith(value, StringComparison.OrdinalIgnoreCase));
        if (prefix is not null)
        {
            evidence = new AreaContentEvidence(
                "Catalog",
                "MetadataPrefix",
                prefix,
                0.95f);
            return true;
        }

        if (_bossDisplayNames.Contains(entity.DisplayName))
        {
            evidence = new AreaContentEvidence(
                "Catalog",
                "DisplayName",
                entity.DisplayName,
                0.9f);
            return true;
        }

        evidence = default!;
        return false;
    }

    public bool IsBossLandmark(
        AreaIdentity area,
        AreaLandmarkSnapshot landmark)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(landmark);
        if (landmark.Kind == AreaLandmarkKind.BossHint)
        {
            return false;
        }

        if (landmark.Kind == AreaLandmarkKind.BossArena)
        {
            return true;
        }

        foreach (var entry in _bossTilePatternsByArea)
        {
            if (!string.Equals(entry.Key, area.AreaCode, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entry.Key, "*", StringComparison.Ordinal))
            {
                continue;
            }

            if (entry.Value.Any(pattern =>
                    !string.IsNullOrWhiteSpace(pattern)
                    && landmark.TilePath.Contains(
                        pattern,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static AreaContentCatalog Unavailable()
        => new(new CatalogDocument(), false);

    private static HashSet<string> ToSet(IEnumerable<string> values)
        => new(
            values.Where(value => !string.IsNullOrWhiteSpace(value)),
            StringComparer.OrdinalIgnoreCase);

    private static MechanicRule CreateMechanicRule(
        AreaContentKind kind,
        MechanicRuleCatalog catalog)
        => new(
            kind,
            catalog.MetadataFragments
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray(),
            catalog.ExcludedMetadataFragments
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray());

    private static bool IsEligibleMechanicEntity(
        AreaEntitySnapshot entity,
        AreaContentKind kind)
        => kind == AreaContentKind.Strongbox
            ? entity.Category == AreaEntityCategory.Chest
              && entity.ChestState == AreaChestState.Closed
            : entity.Category is AreaEntityCategory.Object or AreaEntityCategory.Other;

    private static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class CatalogDocument
    {
        public ExpeditionCatalog Expedition { get; init; } = new();
        public MechanicsCatalog Mechanics { get; init; } = new();
        public BossCatalog Bosses { get; init; } = new();
    }

    private sealed class ExpeditionCatalog
    {
        public string[] ExactMetadata { get; init; } = [];
    }

    private sealed class MechanicsCatalog
    {
        public MechanicRuleCatalog Abyss { get; init; } = new();
        public MechanicRuleCatalog Ritual { get; init; } = new();
        public MechanicRuleCatalog Breach { get; init; } = new();
        public MechanicRuleCatalog Essence { get; init; } = new();
        public MechanicRuleCatalog Strongbox { get; init; } = new();
    }

    private sealed class MechanicRuleCatalog
    {
        public string[] MetadataFragments { get; init; } = [];
        public string[] ExcludedMetadataFragments { get; init; } = [];
    }

    private sealed class BossCatalog
    {
        public string[] ExactMetadata { get; init; } = [];
        public string[] MetadataPrefixes { get; init; } = [];
        public string[] DisplayNames { get; init; } = [];
        public Dictionary<string, string[]> BossTilePatternsByArea { get; init; } = [];
    }

    private sealed record MechanicRule(
        AreaContentKind Kind,
        string[] MetadataFragments,
        string[] ExcludedMetadataFragments);
}
