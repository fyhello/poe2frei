using System.Collections.Immutable;
using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Contracts;
using FreiAtlas.Plugin.ExpeditionPanel;

namespace FreiAtlas.Plugin.ExpeditionPanel.Tests;

public sealed class ExpeditionNativeValuePluginTests
{
    private static readonly AreaUiRect PanelBounds = new(20, 70, 360, 390);
    private static readonly AreaUiRect ListClipBounds = new(30, 100, 330, 200);

    [Fact]
    public void Build_PreservesPanelOrderAndCopiesValueTextAndBand()
    {
        var panel = VerifiedPanel(
            Row("recipe-b", 20, y: 100),
            Row("recipe-a", 10, y: 132));
        var api = new TestAreaMapApi(Snapshot(
            panel: panel,
            recipes:
            [
                Recipe("recipe-a", 10),
                Recipe("recipe-b", 20)
            ]));
        var values = new TestValueSource(
            ("recipe-a", 10, "80E", ExpeditionPanelValueBand.UnderOneDivine),
            ("recipe-b", 20, "6.5D", ExpeditionPanelValueBand.FiveToTenDivine));
        var plugin = new ExpeditionNativeValuePlugin(api, values);

        var scene = plugin.Build();

        Assert.NotNull(scene);
        Assert.Equal(0x1234u, scene.AreaHash);
        Assert.Equal(11, scene.SessionSequence);
        Assert.Equal("expedition:11:42", scene.InstanceId);
        Assert.Equal(PanelBounds, scene.PanelBounds);
        Assert.Equal(ListClipBounds, scene.ListClipBounds);
        Assert.Equal(["recipe-b", "recipe-a"], scene.Rows.Select(row => row.RecipeId));
        Assert.Equal("6.5D", scene.Rows[0].ValueText);
        Assert.Equal(ExpeditionPanelValueBand.FiveToTenDivine, scene.Rows[0].ValueBand);
        Assert.Equal("80E", scene.Rows[1].ValueText);
        Assert.Equal(ExpeditionPanelValueBand.UnderOneDivine, scene.Rows[1].ValueBand);
        Assert.Equal(panel.Rows.Select(row => row.Bounds), scene.Rows.Select(row => row.Bounds));
    }

    [Fact]
    public void Build_MissingValueUsesQuestionMarkWithoutChangingOtherRows()
    {
        var panel = VerifiedPanel(
            Row("missing", 1, y: 100),
            Row("priced", 2, y: 132));
        var plugin = new ExpeditionNativeValuePlugin(
            new TestAreaMapApi(Snapshot(
                panel: panel,
                recipes: [Recipe("missing", 1), Recipe("priced", 2)])),
            new TestValueSource(
                ("priced", 2, "1.5D", ExpeditionPanelValueBand.OneToFiveDivine)));

        var scene = plugin.Build();

        Assert.NotNull(scene);
        Assert.Collection(
            scene.Rows,
            row =>
            {
                Assert.Equal("?", row.ValueText);
                Assert.Equal(ExpeditionPanelValueBand.Unknown, row.ValueBand);
            },
            row =>
            {
                Assert.Equal("1.5D", row.ValueText);
                Assert.Equal(ExpeditionPanelValueBand.OneToFiveDivine, row.ValueBand);
            });
    }

    [Fact]
    public void Build_AcceptsDegradedSnapshot()
    {
        var plugin = Plugin(Snapshot(status: AreaMapSnapshotStatus.Degraded));

        Assert.NotNull(plugin.Build());
    }

    [Theory]
    [InlineData(AreaMapSnapshotStatus.Detached, 0x1234u)]
    [InlineData(AreaMapSnapshotStatus.Loading, 0x1234u)]
    [InlineData(AreaMapSnapshotStatus.Stable, 0u)]
    [InlineData(AreaMapSnapshotStatus.Degraded, 0u)]
    public void Build_RejectsInvalidSnapshotState(
        AreaMapSnapshotStatus status,
        uint areaHash)
    {
        var plugin = Plugin(Snapshot(status: status, areaHash: areaHash));

        Assert.Null(plugin.Build());
    }

    [Theory]
    [InlineData(AreaExpeditionRecipePanelAvailability.Unavailable, false)]
    [InlineData(AreaExpeditionRecipePanelAvailability.Unavailable, true)]
    [InlineData(AreaExpeditionRecipePanelAvailability.Unverified, true)]
    [InlineData(AreaExpeditionRecipePanelAvailability.Verified, false)]
    public void Build_RequiresVerifiedOpenPanel(
        AreaExpeditionRecipePanelAvailability availability,
        bool isOpen)
    {
        var panel = new AreaExpeditionRecipePanelSnapshot(
            availability,
            isOpen,
            PanelBounds,
            ListClipBounds,
            "expedition:11:42",
            [Row("recipe", 1, y: 100)]);
        var plugin = Plugin(Snapshot(panel: panel));

        Assert.Null(plugin.Build());
    }

    [Fact]
    public void Build_ReturnsNullWhenPanelInstanceDoesNotMatchExpedition()
    {
        var panel = VerifiedPanel("missing-instance", Row("recipe", 1, y: 100));
        var plugin = Plugin(Snapshot(panel: panel));

        Assert.Null(plugin.Build());
    }

    [Fact]
    public void Build_ReturnsNullWhenPanelInstanceMatchesMultipleExpeditions()
    {
        var content = Expedition("expedition:11:42", [Recipe("recipe", 1)]);
        var plugin = Plugin(Snapshot(contents: [content, content]));

        Assert.Null(plugin.Build());
    }

    [Theory]
    [InlineData("missing", 1)]
    [InlineData("recipe", 2)]
    public void Build_ReturnsNullWhenPanelRecipeKeyDoesNotMatch(
        string recipeId,
        int catalogRow)
    {
        var panel = VerifiedPanel(Row(recipeId, catalogRow, y: 100));
        var plugin = Plugin(Snapshot(panel: panel));

        Assert.Null(plugin.Build());
    }

    [Fact]
    public void Build_ReturnsNullWhenExpeditionContainsDuplicateRecipeKey()
    {
        var recipe = Recipe("recipe", 1);
        var plugin = Plugin(Snapshot(recipes: [recipe, recipe]));

        Assert.Null(plugin.Build());
    }

    [Fact]
    public void Build_ReturnsNullWhenPanelContainsDuplicateRecipeKey()
    {
        var panel = VerifiedPanel(
            Row("recipe", 1, y: 100),
            Row("recipe", 1, y: 132));
        var plugin = Plugin(Snapshot(panel: panel));

        Assert.Null(plugin.Build());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_ReturnsNullWhenPanelOrClipBoundsAreInvalid(bool invalidPanelBounds)
    {
        var invalid = new AreaUiRect(20, 70, 0, 390);
        var panel = new AreaExpeditionRecipePanelSnapshot(
            AreaExpeditionRecipePanelAvailability.Verified,
            true,
            invalidPanelBounds ? invalid : PanelBounds,
            invalidPanelBounds ? ListClipBounds : invalid,
            "expedition:11:42",
            [Row("recipe", 1, y: 100)]);
        var plugin = Plugin(Snapshot(panel: panel));

        Assert.Null(plugin.Build());
    }

    [Theory]
    [InlineData("", 1, 330f, 32f)]
    [InlineData("recipe", -1, 330f, 32f)]
    [InlineData("recipe", 1, 0f, 32f)]
    public void Build_ReturnsNullWhenPanelRowIsIncomplete(
        string recipeId,
        int catalogRow,
        float width,
        float height)
    {
        var row = new AreaExpeditionRecipePanelRow(
            recipeId,
            catalogRow,
            new AreaUiRect(30, 100, width, height),
            true);
        var plugin = Plugin(Snapshot(
            panel: VerifiedPanel(row),
            recipes: [Recipe(recipeId, catalogRow)]));

        Assert.Null(plugin.Build());
    }

    [Fact]
    public void Build_SkipsInvisibleRowsAndKeepsPartiallyVisibleRows()
    {
        var hidden = Row("hidden", 1, y: 70, isVisible: false);
        var partial = Row("partial", 2, y: 90, isVisible: true);
        var panel = VerifiedPanel(hidden, partial);
        var plugin = Plugin(Snapshot(
            panel: panel,
            recipes: [Recipe("hidden", 1), Recipe("partial", 2)]));

        var scene = plugin.Build();

        var row = Assert.Single(scene!.Rows);
        Assert.Equal("partial", row.RecipeId);
        Assert.Equal(partial.Bounds, row.Bounds);
        Assert.True(row.Bounds.Y < scene.ListClipBounds.Y);
        Assert.True(row.Bounds.Y + row.Bounds.Height > scene.ListClipBounds.Y);
    }

    private static ExpeditionNativeValuePlugin Plugin(AreaMapSnapshot snapshot)
        => new(new TestAreaMapApi(snapshot), new TestValueSource());

    private static AreaMapSnapshot Snapshot(
        AreaMapSnapshotStatus status = AreaMapSnapshotStatus.Stable,
        uint areaHash = 0x1234,
        AreaExpeditionRecipePanelSnapshot? panel = null,
        ImmutableArray<AreaExpeditionRecipe> recipes = default,
        IReadOnlyList<AreaContentSnapshot>? contents = null)
    {
        var normalizedRecipes = recipes.IsDefault
            ? [Recipe("recipe", 1)]
            : recipes;
        var normalizedPanel = panel ?? VerifiedPanel(Row("recipe", 1, y: 100));
        return new AreaMapSnapshot(
            DateTimeOffset.UtcNow,
            29368,
            "test",
            status,
            new AreaIdentity(areaHash, "MapRugosa", 80, 11),
            null,
            [],
            contents ?? [Expedition("expedition:11:42", normalizedRecipes)],
            [],
            null,
            AreaMapViewsSnapshot.Unavailable,
            [])
        {
            ExpeditionRecipePanel = normalizedPanel
        };
    }

    private static AreaContentSnapshot Expedition(
        string instanceId,
        ImmutableArray<AreaExpeditionRecipe> recipes)
        => new(
            instanceId,
            instanceId,
            "Expedition",
            AreaContentKind.Expedition,
            AreaContentPhase.Available,
            Vector3.Zero,
            Vector2.Zero,
            1f,
            42,
            [],
            new AreaExpeditionDetails(6, recipes));

    private static AreaExpeditionRecipe Recipe(string recipeId, int catalogRow)
        => new(
            recipeId,
            catalogRow,
            4,
            [new AreaExpeditionRune(1, "Rune")],
            [new AreaExpeditionReward("reward", "Reward", 1, true)]);

    private static AreaExpeditionRecipePanelSnapshot VerifiedPanel(
        params AreaExpeditionRecipePanelRow[] rows)
        => VerifiedPanel("expedition:11:42", rows);

    private static AreaExpeditionRecipePanelSnapshot VerifiedPanel(
        string instanceId,
        params AreaExpeditionRecipePanelRow[] rows)
        => new(
            AreaExpeditionRecipePanelAvailability.Verified,
            true,
            PanelBounds,
            ListClipBounds,
            instanceId,
            rows.ToImmutableArray());

    private static AreaExpeditionRecipePanelRow Row(
        string recipeId,
        int catalogRow,
        float y,
        bool isVisible = true)
        => new(recipeId, catalogRow, new AreaUiRect(30, y, 330, 32), isVisible);

    private sealed class TestAreaMapApi(AreaMapSnapshot snapshot) : IAreaMapApi
    {
        public AreaMapSnapshot Current { get; } = snapshot;

        public event Action<AreaMapSnapshot>? SnapshotChanged
        {
            add { }
            remove { }
        }
    }

    private sealed class TestValueSource(
        params (string RecipeId, int CatalogRow, string Text, ExpeditionPanelValueBand Band)[] values)
        : IExpeditionRecipeValueSource
    {
        private readonly IReadOnlyDictionary<(string RecipeId, int CatalogRow), ExpeditionRecipeValue> _values =
            values.ToDictionary(
                value => (value.RecipeId, value.CatalogRow),
                value => new ExpeditionRecipeValue(1m, 400m, value.Text, value.Band));

        public bool TryGetValue(
            AreaExpeditionRecipe recipe,
            out ExpeditionRecipeValue value)
            => _values.TryGetValue((recipe.RecipeId, recipe.CatalogRow), out value);
    }
}
