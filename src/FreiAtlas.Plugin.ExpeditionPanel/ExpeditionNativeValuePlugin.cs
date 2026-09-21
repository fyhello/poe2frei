using System.Collections.Immutable;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Contracts;

namespace FreiAtlas.Plugin.ExpeditionPanel;

public sealed class ExpeditionNativeValuePlugin
{
    private readonly IAreaMapApi _api;
    private readonly IExpeditionRecipeValueSource _valueSource;

    public ExpeditionNativeValuePlugin(
        IAreaMapApi api,
        IExpeditionRecipeValueSource valueSource)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _valueSource = valueSource ?? throw new ArgumentNullException(nameof(valueSource));
    }

    public ExpeditionNativeValueScene? Build()
    {
        var snapshot = _api.Current;
        if (snapshot.Status is not (AreaMapSnapshotStatus.Stable or AreaMapSnapshotStatus.Degraded)
            || snapshot.Area.AreaHash == 0)
        {
            return null;
        }

        var panel = snapshot.ExpeditionRecipePanel;
        if (panel.Availability != AreaExpeditionRecipePanelAvailability.Verified
            || !panel.IsOpen
            || panel.PanelBounds is not { } panelBounds
            || panel.ListClipBounds is not { } listClipBounds
            || string.IsNullOrWhiteSpace(panel.InstanceId)
            || panel.Rows.IsDefaultOrEmpty
            || !IsValidRect(panelBounds)
            || !IsValidRect(listClipBounds))
        {
            return null;
        }

        var matches = snapshot.Contents
            .Where(content => content.Kind == AreaContentKind.Expedition
                              && string.Equals(content.InstanceId, panel.InstanceId, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (matches.Length != 1)
        {
            return null;
        }

        var recipes = matches[0].ExpeditionDetails?.Recipes ?? [];
        var recipesByKey = new Dictionary<RecipeKey, AreaExpeditionRecipe>();
        foreach (var recipe in recipes)
        {
            if (recipe is null
                || string.IsNullOrWhiteSpace(recipe.RecipeId)
                || recipe.CatalogRow < 0
                || !recipesByKey.TryAdd(new RecipeKey(recipe.RecipeId, recipe.CatalogRow), recipe))
            {
                return null;
            }
        }

        var panelKeys = new HashSet<RecipeKey>();
        var rows = ImmutableArray.CreateBuilder<ExpeditionNativeValueRow>(panel.Rows.Length);
        foreach (var panelRow in panel.Rows)
        {
            if (panelRow is null
                || string.IsNullOrWhiteSpace(panelRow.RecipeId)
                || panelRow.CatalogRow < 0
                || !IsValidRect(panelRow.Bounds))
            {
                return null;
            }

            var key = new RecipeKey(panelRow.RecipeId, panelRow.CatalogRow);
            if (!panelKeys.Add(key) || !recipesByKey.TryGetValue(key, out var recipe))
            {
                return null;
            }

            if (!panelRow.IsVisible)
            {
                continue;
            }

            var hasValue = _valueSource.TryGetValue(recipe, out var value);
            rows.Add(new ExpeditionNativeValueRow(
                panelRow.RecipeId,
                panelRow.CatalogRow,
                panelRow.Bounds,
                hasValue ? value.ValueText : "?",
                hasValue ? value.ValueBand : ExpeditionPanelValueBand.Unknown));
        }

        return new ExpeditionNativeValueScene(
            snapshot.Area.AreaHash,
            snapshot.Area.SessionSequence,
            panel.InstanceId,
            panelBounds,
            listClipBounds,
            rows.ToImmutable());
    }

    private static bool IsValidRect(AreaUiRect rect)
        => float.IsFinite(rect.X)
           && float.IsFinite(rect.Y)
           && float.IsFinite(rect.Width)
           && float.IsFinite(rect.Height)
           && rect.Width > 0f
           && rect.Height > 0f;

    private readonly record struct RecipeKey(string RecipeId, int CatalogRow);
}
