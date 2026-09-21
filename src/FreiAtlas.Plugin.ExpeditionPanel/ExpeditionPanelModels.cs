using System.Collections.Immutable;
using FreiAtlas.Core.Area;

namespace FreiAtlas.Plugin.ExpeditionPanel;

public readonly record struct ExpeditionRecipeValue(
    decimal TotalDivine,
    decimal ExaltedPerDivine,
    string ValueText,
    ExpeditionPanelValueBand ValueBand);

public enum ExpeditionPanelValueBand
{
    Unknown,
    UpToTwentyExalted,
    UnderOneDivine,
    OneToFiveDivine,
    FiveToTenDivine,
    TenToFiftyDivine,
    FiftyPlusDivine
}

public interface IExpeditionRecipeValueSource
{
    bool TryGetValue(
        AreaExpeditionRecipe recipe,
        out ExpeditionRecipeValue value);
}

public sealed record ExpeditionPanelRecipeRow(
    string RecipeId,
    int CatalogRow,
    int Size,
    ImmutableArray<int> RuneIndices,
    ImmutableArray<AreaExpeditionReward> Rewards,
    decimal? TotalDivine,
    decimal? ExaltedPerDivine,
    string? ValueText,
    ExpeditionPanelValueBand ValueBand);

public sealed record ExpeditionPanelEncounter(
    string InstanceId,
    int? HoleCount,
    AreaContentPhase Phase,
    bool IsExpanded,
    ExpeditionPanelRecipeRow? RecommendedRecipe,
    ExpeditionPanelValueBand ValueBand,
    ImmutableArray<ExpeditionPanelRecipeRow> AllRecipes)
{
    public int RecipeCount => AllRecipes.IsDefault ? 0 : AllRecipes.Length;
}

public sealed record ExpeditionPanelScene(
    uint AreaHash,
    long SessionSequence,
    bool IsExpanded,
    ImmutableArray<ExpeditionPanelEncounter> Encounters);
