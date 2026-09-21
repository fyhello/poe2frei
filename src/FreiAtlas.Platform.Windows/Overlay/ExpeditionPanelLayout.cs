using System.Collections.Immutable;
using System.Drawing;
using FreiAtlas.Plugin.ExpeditionPanel;

namespace FreiAtlas.Platform.Windows.Overlay;

internal sealed record ExpeditionPanelRecipeLayout(
    int DisplayIndex,
    ExpeditionPanelRecipeRow Recipe,
    Rectangle Bounds);

internal sealed record ExpeditionPanelEncounterLayout(
    ExpeditionPanelEncounter Encounter,
    Rectangle SummaryBounds,
    ImmutableArray<ExpeditionPanelRecipeLayout> Recipes);

internal sealed record ExpeditionPanelLayout(
    Size PanelSize,
    Rectangle TitleBounds,
    Rectangle PanelToggleBounds,
    Rectangle BodyBounds,
    int ContentHeight,
    int ScrollOffset,
    ExpeditionPanelMetrics Metrics,
    ImmutableArray<ExpeditionPanelEncounterLayout> Encounters);

internal static class ExpeditionPanelLayoutCalculator
{
    public static ExpeditionPanelLayout Create(
        ExpeditionPanelScene scene,
        Size availableSize,
        int scrollOffset,
        float panelScale = 1f)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var metrics = ExpeditionPanelMetrics.Create(panelScale);
        var maxRecipeSize = scene.Encounters
            .SelectMany(encounter => encounter.AllRecipes.IsDefault
                ? []
                : encounter.AllRecipes)
            .Select(recipe => recipe.Size)
            .DefaultIfEmpty(4)
            .Max();
        var baseWidth = Math.Clamp(140 + (maxRecipeSize * 23), 248, 370);
        var width = Math.Max(1, Math.Min(
            metrics.Px(baseWidth),
            availableSize.Width - metrics.HorizontalMargin));
        var titleBounds = new Rectangle(0, 0, width, metrics.TitleHeight);
        var toggleSize = metrics.Px(24);
        var toggleBounds = new Rectangle(
            metrics.Px(4),
            Math.Max(0, (metrics.TitleHeight - toggleSize) / 2),
            toggleSize,
            toggleSize);
        if (!scene.IsExpanded)
        {
            return new ExpeditionPanelLayout(
                new Size(width, metrics.TitleHeight),
                titleBounds,
                toggleBounds,
                Rectangle.Empty,
                0,
                0,
                metrics,
                []);
        }

        var contentHeight = scene.Encounters.Sum(encounter =>
            metrics.SummaryHeight
            + (encounter.IsExpanded
                ? encounter.RecipeCount * metrics.RecipeHeight
                : 0));
        var maximumPanelHeight = Math.Max(
            metrics.TitleHeight,
            Math.Min(
                metrics.MaximumHeight,
                availableSize.Height - metrics.VerticalMargin));
        var bodyHeight = Math.Min(
            contentHeight,
            Math.Max(0, maximumPanelHeight - metrics.TitleHeight));
        var bodyBounds = new Rectangle(
            0,
            metrics.TitleHeight,
            width,
            bodyHeight);
        var maximumScroll = Math.Max(0, contentHeight - bodyHeight);
        var clampedScroll = Math.Clamp(scrollOffset, 0, maximumScroll);

        var layouts = ImmutableArray.CreateBuilder<ExpeditionPanelEncounterLayout>(
            scene.Encounters.Length);
        var top = metrics.TitleHeight - clampedScroll;
        foreach (var encounter in scene.Encounters)
        {
            var summary = new Rectangle(0, top, width, metrics.SummaryHeight);
            top += metrics.SummaryHeight;
            var recipes = ImmutableArray.CreateBuilder<ExpeditionPanelRecipeLayout>(
                encounter.IsExpanded ? encounter.RecipeCount : 0);
            if (encounter.IsExpanded && !encounter.AllRecipes.IsDefaultOrEmpty)
            {
                for (var index = 0; index < encounter.AllRecipes.Length; index++)
                {
                    var bounds = new Rectangle(
                        metrics.GroupInset,
                        top,
                        Math.Max(1, width - metrics.GroupInset),
                        metrics.RecipeHeight);
                    recipes.Add(new ExpeditionPanelRecipeLayout(
                        index + 1,
                        encounter.AllRecipes[index],
                        bounds));
                    top += metrics.RecipeHeight;
                }
            }

            layouts.Add(new ExpeditionPanelEncounterLayout(
                encounter,
                summary,
                recipes.ToImmutable()));
        }

        return new ExpeditionPanelLayout(
            new Size(width, metrics.TitleHeight + bodyHeight),
            titleBounds,
            toggleBounds,
            bodyBounds,
            contentHeight,
            clampedScroll,
            metrics,
            layouts.ToImmutable());
    }

}
