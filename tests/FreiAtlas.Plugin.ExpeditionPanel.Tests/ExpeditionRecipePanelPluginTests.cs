using System.Collections.Immutable;
using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Contracts;
using FreiAtlas.Plugin.ExpeditionPanel;

namespace FreiAtlas.Plugin.ExpeditionPanel.Tests;

public sealed class ExpeditionRecipePanelPluginTests
{
    [Fact]
    public void Build_SortsRecipesBySizeDescendingThenCatalogRowDescending()
    {
        var recipes = ImmutableArray.Create(
            Recipe("size4-row10", size: 4, row: 10),
            Recipe("size6-row1", size: 6, row: 1),
            Recipe("size6-row8", size: 6, row: 8),
            Recipe("size4-row20", size: 4, row: 20));
        var api = new TestAreaMapApi(Snapshot(Recipes: recipes));
        var plugin = new ExpeditionRecipePanelPlugin(api);

        var encounter = Assert.Single(plugin.Build()!.Encounters);

        Assert.Equal(
            ["size6-row8", "size6-row1", "size4-row20", "size4-row10"],
            encounter.AllRecipes.Select(recipe => recipe.RecipeId));
    }

    [Fact]
    public void Build_WithoutValueSourceUsesSortedFirstRecipeAsRecommended()
    {
        var recipes = ImmutableArray.Create(
            Recipe("row1", size: 4, row: 1),
            Recipe("row2", size: 5, row: 2));
        var plugin = new ExpeditionRecipePanelPlugin(
            new TestAreaMapApi(Snapshot(Recipes: recipes)));

        var encounter = Assert.Single(plugin.Build()!.Encounters);

        Assert.Equal("row2", encounter.RecommendedRecipe!.RecipeId);
        Assert.Equal(["row2", "row1"], encounter.AllRecipes.Select(item => item.RecipeId));
        Assert.Equal(ExpeditionPanelValueBand.Unknown, encounter.ValueBand);
        Assert.False(encounter.IsExpanded);
    }

    [Fact]
    public void Build_WithValuesChoosesHighestTotalAndTiesUseSortedFirst()
    {
        var recipes = ImmutableArray.Create(
            Recipe("same-high-later", size: 6, row: 2),
            Recipe("same-high-first", size: 6, row: 8),
            Recipe("lower", size: 4, row: 99));
        var values = new TestValueSource(
            ("same-high-later", 12m, "12E"),
            ("same-high-first", 12m, "12E"),
            ("lower", 2m, "2E"));
        var plugin = new ExpeditionRecipePanelPlugin(
            new TestAreaMapApi(Snapshot(Recipes: recipes)),
            values);

        var encounter = Assert.Single(plugin.Build()!.Encounters);

        Assert.Equal("same-high-first", encounter.RecommendedRecipe!.RecipeId);
        Assert.Equal("12E", encounter.RecommendedRecipe.ValueText);
    }

    [Fact]
    public void Build_ExpandedEncounterShowsEveryRecipeAndPreservesExpansionOnRefresh()
    {
        var api = new TestAreaMapApi(Snapshot(
            sequence: 11,
            Recipes: [Recipe("first", 4, 1), Recipe("second", 4, 2)]));
        var plugin = new ExpeditionRecipePanelPlugin(api);

        plugin.Build();
        plugin.ToggleEncounter("expedition:11:42");
        var expanded = Assert.Single(plugin.Build()!.Encounters);
        Assert.True(expanded.IsExpanded);
        Assert.Equal("second", expanded.RecommendedRecipe!.RecipeId);
        Assert.Equal(2, expanded.AllRecipes.Length);

        api.Set(Snapshot(sequence: 11, Recipes: [Recipe("second", 4, 2), Recipe("first", 4, 1)]));
        var refreshed = Assert.Single(plugin.Build()!.Encounters);
        Assert.True(refreshed.IsExpanded);
        Assert.Equal("second", refreshed.RecommendedRecipe!.RecipeId);
        Assert.Equal(["second", "first"], refreshed.AllRecipes.Select(item => item.RecipeId));

        api.Set(Snapshot(sequence: 12, Recipes: [Recipe("new", 4, 1)]));
        var newMap = Assert.Single(plugin.Build()!.Encounters);
        Assert.False(newMap.IsExpanded);
    }

    [Fact]
    public void Build_IsIndependentOfMapViewVisibilityAndIgnoresNonExpeditionContent()
    {
        var expedition = Content(
            "expedition:11:42",
            AreaContentKind.Expedition,
            new AreaExpeditionDetails(6, [Recipe("recipe", 6, 1)]));
        var boss = Content(
            "boss:11:99",
            AreaContentKind.Boss,
            null);
        var snapshot = Snapshot(
            contents: [boss, expedition],
            mapViews: AreaMapViewsSnapshot.Unavailable);
        var plugin = new ExpeditionRecipePanelPlugin(new TestAreaMapApi(snapshot));

        var scene = plugin.Build();

        var encounter = Assert.Single(scene!.Encounters);
        Assert.Equal("expedition:11:42", encounter.InstanceId);
        Assert.Equal(6, encounter.HoleCount);
    }

    [Fact]
    public void TogglePanelKeepsRecommendedRecipeAndEncounterFacts()
    {
        var plugin = new ExpeditionRecipePanelPlugin(
            new TestAreaMapApi(Snapshot(Recipes: [Recipe("recipe", 6, 1)])));

        plugin.Build();
        plugin.ToggleEncounter("expedition:11:42");
        plugin.TogglePanel();
        var collapsed = plugin.Build()!;
        Assert.False(collapsed.IsExpanded);
        var encounter = Assert.Single(collapsed.Encounters);
        Assert.Equal("recipe", encounter.RecommendedRecipe!.RecipeId);
        Assert.Single(encounter.AllRecipes);

        plugin.TogglePanel();
        Assert.True(Assert.Single(plugin.Build()!.Encounters).IsExpanded);
    }

    [Fact]
    public void Build_ReadsEachRecipeValueOnceAndKeepsNumericValuesOnRows()
    {
        var values = new TestValueSource(
            ("first", 0.25m, 400m, "100E"),
            ("second", 1.5m, 400m, "1.5D"));
        var plugin = new ExpeditionRecipePanelPlugin(
            new TestAreaMapApi(Snapshot(Recipes: [
                Recipe("first", 4, 1),
                Recipe("second", 4, 2)
            ])),
            values);

        var encounter = Assert.Single(plugin.Build()!.Encounters);

        Assert.Equal(2, values.CallCount);
        Assert.Equal("second", encounter.RecommendedRecipe!.RecipeId);
        Assert.Equal(1.5m, encounter.RecommendedRecipe.TotalDivine);
        Assert.Equal(400m, encounter.RecommendedRecipe.ExaltedPerDivine);
        Assert.Equal(ExpeditionPanelValueBand.OneToFiveDivine, encounter.ValueBand);
    }

    [Fact]
    public void Build_EncounterConsumesRecommendedRecipeBandVerbatim()
    {
        var plugin = new ExpeditionRecipePanelPlugin(
            new TestAreaMapApi(Snapshot(Recipes: [Recipe("recipe", 4, 1)])),
            new FixedValueSource(new ExpeditionRecipeValue(
                1.5m,
                400m,
                "1.5D",
                ExpeditionPanelValueBand.TenToFiftyDivine)));

        var encounter = Assert.Single(plugin.Build()!.Encounters);

        Assert.Equal(
            ExpeditionPanelValueBand.TenToFiftyDivine,
            encounter.ValueBand);
    }

    [Theory]
    [InlineData(0.05, 400, ExpeditionPanelValueBand.UpToTwentyExalted)]
    [InlineData(0.050025, 400, ExpeditionPanelValueBand.UnderOneDivine)]
    [InlineData(1, 400, ExpeditionPanelValueBand.OneToFiveDivine)]
    [InlineData(5, 400, ExpeditionPanelValueBand.FiveToTenDivine)]
    [InlineData(10, 400, ExpeditionPanelValueBand.TenToFiftyDivine)]
    [InlineData(50, 400, ExpeditionPanelValueBand.FiftyPlusDivine)]
    public void ClassifyValue_UsesConfirmedBoundaries(
        double totalDivine,
        double exaltedPerDivine,
        ExpeditionPanelValueBand expected)
        => Assert.Equal(
            expected,
                ExpeditionValuePresentation.Classify(
                (decimal)totalDivine,
                (decimal)exaltedPerDivine));

    [Theory]
    [InlineData(null, 400d)]
    [InlineData(1d, null)]
    [InlineData(1d, 0d)]
    [InlineData(1d, -1d)]
    public void ClassifyValue_ReturnsUnknownWhenValueOrRateIsUnavailable(
        double? totalDivine,
        double? exaltedPerDivine)
        => Assert.Equal(
            ExpeditionPanelValueBand.Unknown,
            ExpeditionValuePresentation.Classify(
                totalDivine is { } total ? (decimal)total : null,
                exaltedPerDivine is { } rate ? (decimal)rate : null));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_FirstMapUsesConfiguredExpansion(bool expanded)
    {
        var plugin = new ExpeditionRecipePanelPlugin(new TestAreaMapApi(Snapshot()));

        var scene = plugin.Build(expandOnAreaEntry: expanded)!;

        Assert.Equal(expanded, scene.IsExpanded);
        Assert.False(Assert.Single(scene.Encounters).IsExpanded);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_SameMapPreservesManualChoiceWhenDefaultChanges(bool expanded)
    {
        var api = new TestAreaMapApi(Snapshot());
        var plugin = new ExpeditionRecipePanelPlugin(api);
        plugin.Build(expandOnAreaEntry: expanded);
        plugin.TogglePanel();
        plugin.ToggleEncounter("expedition:11:42");
        api.Set(Snapshot(Recipes: [Recipe("updated", 5, 2)]));

        var refreshed = plugin.Build(expandOnAreaEntry: !expanded)!;

        Assert.Equal(!expanded, refreshed.IsExpanded);
        Assert.True(Assert.Single(refreshed.Encounters).IsExpanded);
        Assert.Equal("updated", refreshed.Encounters[0].RecommendedRecipe!.RecipeId);
        Assert.Equal(!expanded, plugin.Build(expandOnAreaEntry: expanded)!.IsExpanded);
    }

    [Theory]
    [InlineData(0x4321u, 11L)]
    [InlineData(0x1234u, 12L)]
    public void Build_NewAreaOrSessionAppliesLatestDefault(uint areaHash, long sequence)
    {
        var snapshot = Snapshot();
        var api = new TestAreaMapApi(snapshot);
        var plugin = new ExpeditionRecipePanelPlugin(api);
        Assert.True(plugin.Build()!.IsExpanded);
        plugin.ToggleEncounter("expedition:11:42");
        api.Set(snapshot with { Area = snapshot.Area with { AreaHash = areaHash, SessionSequence = sequence } });

        var scene = plugin.Build(expandOnAreaEntry: false)!;

        Assert.False(scene.IsExpanded);
        Assert.False(Assert.Single(scene.Encounters).IsExpanded);
    }

    [Fact]
    public void Build_VisitAreaWithoutExpeditionThenReturnResetsPanel()
    {
        var original = Snapshot();
        var api = new TestAreaMapApi(original);
        var plugin = new ExpeditionRecipePanelPlugin(api);
        plugin.Build();
        plugin.TogglePanel();
        api.Set(Snapshot(sequence: 12, contents: []));
        Assert.Null(plugin.Build());
        api.Set(original);

        Assert.True(plugin.Build()!.IsExpanded);
    }

    [Theory]
    [InlineData(AreaMapSnapshotStatus.Loading)]
    [InlineData(AreaMapSnapshotStatus.Detached)]
    [InlineData(AreaMapSnapshotStatus.Stable)]
    [InlineData(AreaMapSnapshotStatus.Degraded)]
    public void Build_TemporaryMissingDataPreservesManualState(AreaMapSnapshotStatus status)
    {
        var original = Snapshot();
        var api = new TestAreaMapApi(original);
        var plugin = new ExpeditionRecipePanelPlugin(api);
        plugin.Build();
        plugin.TogglePanel();
        plugin.ToggleEncounter("expedition:11:42");
        api.Set(original with { Status = status, Contents = [] });
        Assert.Null(plugin.Build());
        api.Set(original);

        var restored = plugin.Build()!;
        Assert.False(restored.IsExpanded);
        Assert.True(Assert.Single(restored.Encounters).IsExpanded);
    }

    [Fact]
    public void Build_TracksAreaWhileHiddenWithoutReadingPrices()
    {
        var api = new TestAreaMapApi(Snapshot());
        var prices = new TestValueSource(("default", 1m, "1D"));
        var plugin = new ExpeditionRecipePanelPlugin(api, prices);
        plugin.Build(expandOnAreaEntry: false);
        var calls = prices.CallCount;
        api.Set(Snapshot(sequence: 12));

        Assert.Null(plugin.Build(expandOnAreaEntry: true, showPanel: false));
        Assert.Equal(calls, prices.CallCount);
        Assert.True(plugin.Build(expandOnAreaEntry: false)!.IsExpanded);
    }

    private static AreaExpeditionRecipe Recipe(string id, int size, int row)
        => new(id, row, size, [new AreaExpeditionRune(1, "Rune")], [
            new AreaExpeditionReward("reward", "Reward", 1, true)
        ]);

    private static AreaMapSnapshot Snapshot(
        long sequence = 11,
        ImmutableArray<AreaExpeditionRecipe> Recipes = default,
        IReadOnlyList<AreaContentSnapshot>? contents = null,
        AreaMapViewsSnapshot? mapViews = null)
    {
        var recipeArray = Recipes.IsDefault ? [Recipe("default", 4, 1)] : Recipes;
        var expedition = Content(
            "expedition:11:42",
            AreaContentKind.Expedition,
            new AreaExpeditionDetails(6, recipeArray));
        return new AreaMapSnapshot(
            DateTimeOffset.UtcNow,
            29368,
            "test",
            AreaMapSnapshotStatus.Stable,
            new AreaIdentity(0x1234, "MapRugosa", 80, sequence),
            null,
            [],
            contents ?? [expedition],
            [],
            null,
            mapViews ?? AreaMapViewsSnapshot.Unavailable,
            []);
    }

    private static AreaContentSnapshot Content(
        string instanceId,
        AreaContentKind kind,
        AreaExpeditionDetails? details)
        => new(
            instanceId,
            instanceId,
            "Expedition",
            kind,
            AreaContentPhase.Available,
            Vector3.Zero,
            Vector2.Zero,
            1f,
            42,
            [],
            details);

    private sealed class TestAreaMapApi(AreaMapSnapshot snapshot) : IAreaMapApi
    {
        public AreaMapSnapshot Current { get; private set; } = snapshot;

        public event Action<AreaMapSnapshot>? SnapshotChanged;

        public void Set(AreaMapSnapshot next)
        {
            Current = next;
            SnapshotChanged?.Invoke(next);
        }
    }

    private sealed class TestValueSource(
        params (string RecipeId, decimal Total, decimal ExaltedPerDivine, string Text)[] values)
        : IExpeditionRecipeValueSource
    {
        public TestValueSource(
            params (string RecipeId, decimal Total, string Text)[] values)
            : this(values.Select(value => (
                value.RecipeId,
                value.Total,
                400m,
                value.Text)).ToArray())
        {
        }

        private readonly IReadOnlyDictionary<string, ExpeditionRecipeValue> _values =
            values.ToDictionary(
                value => value.RecipeId,
                value => new ExpeditionRecipeValue(
                    value.Total,
                    value.ExaltedPerDivine,
                    value.Text,
                    ExpeditionValuePresentation.Classify(
                        value.Total,
                        value.ExaltedPerDivine)),
                StringComparer.Ordinal);

        public int CallCount { get; private set; }

        public bool TryGetValue(
            AreaExpeditionRecipe recipe,
            out ExpeditionRecipeValue value)
        {
            CallCount++;
            return _values.TryGetValue(recipe.RecipeId, out value);
        }
    }

    private sealed class FixedValueSource(ExpeditionRecipeValue value)
        : IExpeditionRecipeValueSource
    {
        public bool TryGetValue(
            AreaExpeditionRecipe recipe,
            out ExpeditionRecipeValue result)
        {
            result = value;
            return true;
        }
    }
}
