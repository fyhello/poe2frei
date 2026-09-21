using System.Collections.Immutable;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Contracts;

namespace FreiAtlas.Plugin.ExpeditionPanel;

public sealed class ExpeditionRecipePanelPlugin
{
    private readonly IAreaMapApi _api;
    private readonly IExpeditionRecipeValueSource? _valueSource;
    private readonly HashSet<string> _expandedEncounterIds =
        new(StringComparer.Ordinal);
    private (uint AreaHash, long SessionSequence)? _identity;
    private bool _panelExpanded = true;

    public ExpeditionRecipePanelPlugin(
        IAreaMapApi api,
        IExpeditionRecipeValueSource? valueSource = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _valueSource = valueSource;
    }

    public ExpeditionPanelScene? Build(
        bool expandOnAreaEntry = true,
        bool showPanel = true)
    {
        var snapshot = _api.Current;
        if (snapshot.Status is not (AreaMapSnapshotStatus.Stable
            or AreaMapSnapshotStatus.Degraded)
            || snapshot.Area.AreaHash == 0)
        {
            return null;
        }

        // 隐藏面板或区域没有秘藏时也跟踪身份；短暂不可用的快照不重置手动状态。
        var identity = (snapshot.Area.AreaHash, snapshot.Area.SessionSequence);
        if (_identity != identity)
        {
            _expandedEncounterIds.Clear();
            _panelExpanded = expandOnAreaEntry;
            _identity = identity;
        }

        if (!showPanel) return null;

        var expeditionContents = snapshot.Contents
            .Where(content => content.Kind == AreaContentKind.Expedition)
            .OrderBy(content => content.InstanceId, StringComparer.Ordinal)
            .ToArray();
        if (expeditionContents.Length == 0)
        {
            return null;
        }

        _expandedEncounterIds.RemoveWhere(
            id => expeditionContents.All(content => content.InstanceId != id));

        var encounters = expeditionContents
            .Select(BuildEncounter)
            .ToImmutableArray();
        return new ExpeditionPanelScene(
            snapshot.Area.AreaHash,
            snapshot.Area.SessionSequence,
            _panelExpanded,
            encounters);
    }

    public void TogglePanel()
        => _panelExpanded = !_panelExpanded;

    public void ToggleEncounter(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        if (!_expandedEncounterIds.Add(instanceId))
        {
            _expandedEncounterIds.Remove(instanceId);
        }
    }

    internal static ImmutableArray<AreaExpeditionRecipe> SortRecipes(
        IEnumerable<AreaExpeditionRecipe> recipes)
    {
        ArgumentNullException.ThrowIfNull(recipes);
        return recipes
            .OrderByDescending(recipe => recipe.Size)
            .ThenByDescending(recipe => recipe.CatalogRow)
            .ThenBy(recipe => recipe.RecipeId, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private ExpeditionPanelEncounter BuildEncounter(
        AreaContentSnapshot content)
    {
        var details = content.ExpeditionDetails;
        var recipes = details is { Recipes.IsDefault: false }
            ? details.Recipes
            : [];
        var sorted = SortRecipes(recipes);
        var allRows = sorted
            .Select(ToRow)
            .ToImmutableArray();
        var recommended = SelectRecommended(allRows);

        return new ExpeditionPanelEncounter(
            content.InstanceId,
            details?.HoleCount,
            content.Phase,
            _expandedEncounterIds.Contains(content.InstanceId),
            recommended,
            recommended?.ValueBand ?? ExpeditionPanelValueBand.Unknown,
            allRows);
    }


    private static ExpeditionPanelRecipeRow? SelectRecommended(
        ImmutableArray<ExpeditionPanelRecipeRow> rows)
        => rows.IsDefaultOrEmpty
            ? null
            : rows
                .Select((row, index) => (Row: row, Index: index))
                .Where(item => item.Row.TotalDivine.HasValue)
                .OrderByDescending(item => item.Row.TotalDivine)
                .ThenBy(item => item.Index)
                .Select(item => item.Row)
                .FirstOrDefault() ?? rows[0];

    private ExpeditionPanelRecipeRow ToRow(AreaExpeditionRecipe recipe)
    {
        var runeIndices = recipe.Runes.IsDefault
            ? []
            : recipe.Runes.Select(rune => rune.Index).ToImmutableArray();
        var rewards = recipe.Rewards.IsDefault
            ? []
            : recipe.Rewards;
        ExpeditionRecipeValue value = default;
        var hasValue = _valueSource is not null
            && _valueSource.TryGetValue(recipe, out value);
        return new ExpeditionPanelRecipeRow(
            recipe.RecipeId,
            recipe.CatalogRow,
            recipe.Size,
            runeIndices,
            rewards,
            hasValue ? value.TotalDivine : null,
            hasValue ? value.ExaltedPerDivine : null,
            hasValue ? value.ValueText : null,
            hasValue ? value.ValueBand : ExpeditionPanelValueBand.Unknown);
    }
}
