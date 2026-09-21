using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FreiAtlas.Core.Area;

namespace FreiAtlas.Game.Content;

internal sealed class ExpeditionRecipeCatalog
{
    private const string ResourceSuffix = "expedition2_recipes.json";
    private readonly ImmutableArray<CatalogRecipe> _recipes;
    private readonly IReadOnlyDictionary<int, string> _runeNames;
    private readonly IReadOnlyDictionary<long, int> _partialMinimumLevels;

    private ExpeditionRecipeCatalog(
        ImmutableArray<CatalogRecipe> recipes,
        IReadOnlyDictionary<int, string> runeNames,
        IReadOnlyDictionary<long, int> partialMinimumLevels)
    {
        _recipes = recipes;
        _runeNames = runeNames;
        _partialMinimumLevels = partialMinimumLevels;
    }

    public static ExpeditionRecipeCatalog LoadEmbedded()
    {
        var assembly = typeof(ExpeditionRecipeCatalog).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        if (resourceName is null)
        {
            return Empty();
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        return stream is null ? Empty() : Load(stream);
    }

    internal static ExpeditionRecipeCatalog Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            var document = JsonSerializer.Deserialize<CatalogDocument>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (document?.Recipes is null)
            {
                return Empty();
            }

            var runeNames = new Dictionary<int, string>();
            if (document.Runes is not null)
            {
                foreach (var (key, name) in document.Runes)
                {
                    if (int.TryParse(key, out var index) && !string.IsNullOrWhiteSpace(name))
                    {
                        runeNames[index] = name;
                    }
                }
            }

            var partialMinimumLevels = new Dictionary<long, int>();
            if (document.RuneWeights is not null)
            {
                foreach (var weight in document.RuneWeights)
                {
                    var key = PartialKey(weight.Rune, weight.Position, weight.Size);
                    if (!partialMinimumLevels.TryGetValue(key, out var current)
                        || weight.MinimumLevel < current)
                    {
                        partialMinimumLevels[key] = weight.MinimumLevel;
                    }
                }
            }

            return new ExpeditionRecipeCatalog(
                document.Recipes
                    .Where(IsValidRecipe)
                    .OrderBy(recipe => recipe.Row)
                    .ToImmutableArray(),
                runeNames,
                partialMinimumLevels);
        }
        catch (JsonException)
        {
            return Empty();
        }
    }

    public ImmutableArray<AreaExpeditionRecipe> Resolve(
        int anchorRuneIndex,
        int anchorPosition,
        int holeCount,
        bool isUnique,
        int areaLevel)
    {
        if (holeCount <= 0 || (!isUnique && anchorRuneIndex < 0))
        {
            return [];
        }

        var result = ImmutableArray.CreateBuilder<AreaExpeditionRecipe>();
        foreach (var recipe in _recipes)
        {
            if (recipe.Size > holeCount || !IsInLevelBand(recipe, areaLevel))
            {
                continue;
            }

            if (!isUnique)
            {
                if (anchorPosition < 0
                    || recipe.RuneIndexes is null
                    || anchorPosition >= recipe.RuneIndexes.Count
                    || recipe.RuneIndexes[anchorPosition] != anchorRuneIndex)
                {
                    continue;
                }

                if (recipe.Size != holeCount
                    && !IsPartialAllowed(
                        anchorRuneIndex,
                        anchorPosition,
                        recipe.Size,
                        areaLevel))
                {
                    continue;
                }
            }

            result.Add(ToSnapshot(recipe));
        }

        return result.ToImmutable();
    }

    private AreaExpeditionRecipe ToSnapshot(CatalogRecipe recipe)
    {
        var runes = ImmutableArray.CreateBuilder<AreaExpeditionRune>();
        if (recipe.RuneIndexes is not null)
        {
            for (var index = 0; index < recipe.RuneIndexes.Count; index++)
            {
                var runeIndex = recipe.RuneIndexes[index];
                var displayName = recipe.RuneNames is { Count: > 0 }
                                  && index < recipe.RuneNames.Count
                    ? recipe.RuneNames[index]
                    : _runeNames.GetValueOrDefault(runeIndex, $"#{runeIndex}");
                runes.Add(new AreaExpeditionRune(runeIndex, displayName));
            }
        }

        ImmutableArray<AreaExpeditionReward> rewards;
        if (recipe.Reward is { } exactReward
            && !string.IsNullOrWhiteSpace(exactReward.Name))
        {
            rewards =
            [
                new AreaExpeditionReward(
                    exactReward.Id ?? string.Empty,
                    exactReward.Name,
                    Math.Max(1, recipe.RewardCount),
                    true)
            ];
        }
        else if (!string.IsNullOrWhiteSpace(recipe.Description))
        {
            rewards =
            [
                new AreaExpeditionReward(
                    string.Empty,
                    recipe.Description,
                    Math.Max(1, recipe.RewardCount),
                    false)
            ];
        }
        else
        {
            rewards = [];
        }

        return new AreaExpeditionRecipe(
            recipe.Id,
            recipe.Row,
            recipe.Size,
            runes.ToImmutable(),
            rewards);
    }

    private bool IsPartialAllowed(
        int anchorRuneIndex,
        int anchorPosition,
        int size,
        int areaLevel)
    {
        if (!_partialMinimumLevels.TryGetValue(
                PartialKey(anchorRuneIndex, anchorPosition + 1, size),
                out var minimumLevel))
        {
            return false;
        }

        return areaLevel <= 0 || areaLevel >= minimumLevel;
    }

    private static bool IsInLevelBand(CatalogRecipe recipe, int areaLevel)
        => areaLevel <= 0
           || (areaLevel >= recipe.MinimumLevel
               && (recipe.MaximumLevel <= 0 || areaLevel <= recipe.MaximumLevel));

    private static bool IsValidRecipe(CatalogRecipe recipe)
        => !string.IsNullOrWhiteSpace(recipe.Id)
           && recipe.Row >= 0
           && recipe.Size > 0;

    private static long PartialKey(int runeIndex, int oneBasedPosition, int size)
        => ((long)runeIndex << 32)
           | ((long)(uint)oneBasedPosition << 16)
           | (uint)size;

    private static ExpeditionRecipeCatalog Empty()
        => new([], new Dictionary<int, string>(), new Dictionary<long, int>());

    private sealed class CatalogDocument
    {
        public Dictionary<string, string>? Runes { get; set; }

        public List<CatalogRecipe>? Recipes { get; set; }

        public List<RuneWeight>? RuneWeights { get; set; }
    }

    private sealed class CatalogRecipe
    {
        public int Row { get; set; }

        public string Id { get; set; } = string.Empty;

        public int Size { get; set; }

        [JsonPropertyName("runeIdx")]
        public List<int>? RuneIndexes { get; set; }

        [JsonPropertyName("runes")]
        public List<string>? RuneNames { get; set; }

        public CatalogReward? Reward { get; set; }

        public int RewardCount { get; set; }

        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("minLevel")]
        public int MinimumLevel { get; set; }

        [JsonPropertyName("maxLevel")]
        public int MaximumLevel { get; set; }
    }

    private sealed class CatalogReward
    {
        public string? Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class RuneWeight
    {
        public int Rune { get; set; }

        [JsonPropertyName("pos")]
        public int Position { get; set; }

        public int Size { get; set; }

        [JsonPropertyName("minLevel")]
        public int MinimumLevel { get; set; }
    }
}
