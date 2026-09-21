using System.Collections.Immutable;
using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Game.Views;

namespace FreiAtlas.Game.Tests.Views;

public sealed class ExpeditionRecipePanelMatcherTests
{
    [Fact]
    public void Observe_RequiresTwoIdenticalSamplesBeforePublishingVerifiedRows()
    {
        var matcher = new ExpeditionRecipePanelMatcher();
        var probe = OpenProbe((0x1010, 4, 1), (0x2020, 3, 2));
        var contents = new[]
        {
            Expedition("expedition:a", Recipe("r1", 4, 20, 1), Recipe("r2", 3, 10, 2))
        };

        var first = matcher.Observe(7, probe, contents);
        var second = matcher.Observe(7, probe, contents);

        Assert.Equal(AreaExpeditionRecipePanelAvailability.Unverified, first.Availability);
        Assert.Equal(AreaExpeditionRecipePanelAvailability.Verified, second.Availability);
        Assert.Equal(["r1", "r2"], second.Rows.Select(row => row.RecipeId));
    }

    [Fact]
    public void Observe_DuplicateCompleteSignatureNeverChoosesNearestOrFirstEncounter()
    {
        var matcher = new ExpeditionRecipePanelMatcher();
        var probe = OpenProbe((0x1010, 4, 1));
        var contents = new[]
        {
            Expedition("expedition:a", Recipe("a", 4, 1, 1)),
            Expedition("expedition:b", Recipe("b", 4, 2, 1))
        };

        Assert.Equal(AreaExpeditionRecipePanelAvailability.Unverified, matcher.Observe(7, probe, contents).Availability);
        Assert.Equal(AreaExpeditionRecipePanelAvailability.Unverified, matcher.Observe(7, probe, contents).Availability);
    }

    [Fact]
    public void Observe_RowAddressChangeRestartsStabilityGate()
    {
        var matcher = new ExpeditionRecipePanelMatcher();
        var contents = new[] { Expedition("expedition:a", Recipe("r1", 4, 1, 1)) };

        matcher.Observe(7, OpenProbe((0x1010, 4, 1)), contents);
        var changed = matcher.Observe(7, OpenProbe((0x2020, 4, 1)), contents);

        Assert.Equal(AreaExpeditionRecipePanelAvailability.Unverified, changed.Availability);
    }

    [Fact]
    public void Observe_OrdersRecipesBySizeThenCatalogRowThenRecipeId()
    {
        var matcher = new ExpeditionRecipePanelMatcher();
        var contents = new[]
        {
            Expedition(
                "expedition:a",
                Recipe("z", 4, 10, 1),
                Recipe("a", 4, 10, 2),
                Recipe("b", 3, 10, 3))
        };
        var probe = OpenProbe((0x1010, 4, 2), (0x2020, 4, 1), (0x3030, 3, 3));

        matcher.Observe(7, probe, contents);
        var result = matcher.Observe(7, probe, contents);

        Assert.Equal(["a", "z", "b"], result.Rows.Select(row => row.RecipeId));
        Assert.Equal([10, 10, 10], result.Rows.Select(row => row.CatalogRow));
    }

    [Fact]
    public void Observe_RejectsMultipleRewardsEmptyRewardsAndRowCountMismatch()
    {
        var matcher = new ExpeditionRecipePanelMatcher();
        var multiReward = Recipe(
            "multi",
            4,
            1,
            1,
            new AreaExpeditionReward("a", "A", 1, true),
            new AreaExpeditionReward("b", "B", 1, true));
        var emptyReward = new AreaExpeditionRecipe("empty", 4, 1, [], []);

        Assert.Equal(AreaExpeditionRecipePanelAvailability.Unverified,
            matcher.Observe(1, OpenProbe((0x1010, 4, 1)), [Expedition("multi", multiReward)]).Availability);
        Assert.Equal(AreaExpeditionRecipePanelAvailability.Unverified,
            matcher.Observe(2, OpenProbe((0x1010, 4, 1)), [Expedition("empty", emptyReward)]).Availability);
        Assert.Equal(AreaExpeditionRecipePanelAvailability.Unverified,
            matcher.Observe(3, OpenProbe((0x1010, 4, 1)), [Expedition("count", Recipe("r1", 4, 1, 1), Recipe("r2", 3, 2, 2))]).Availability);
    }

    [Fact]
    public void Observe_ClosedStateImmediatelyReturnsClosedAndClearsCandidate()
    {
        var matcher = new ExpeditionRecipePanelMatcher();
        var contents = new[] { Expedition("expedition:a", Recipe("r1", 4, 1, 1)) };
        matcher.Observe(7, OpenProbe((0x1010, 4, 1)), contents);

        var closed = matcher.Observe(7, new ExpeditionRecipeUiProbeResult(
            ExpeditionRecipeUiProbeState.Closed, 0, 0, 0, null, null, [], []), contents);
        var reopened = matcher.Observe(7, OpenProbe((0x1010, 4, 1)), contents);

        Assert.Equal(AreaExpeditionRecipePanelAvailability.Verified, closed.Availability);
        Assert.Equal(AreaExpeditionRecipePanelAvailability.Unverified, reopened.Availability);
    }

    [Fact]
    public void Observe_UsesListClipIntersectionForVisibility()
    {
        var matcher = new ExpeditionRecipePanelMatcher();
        var probe = OpenProbe((0x1010, 4, 1)) with
        {
            ListClipBounds = new AreaUiRect(10, 10, 100, 20)
        } with
        {
            Rows = [new ExpeditionRecipeUiRowCandidate(0x1010, 4, 1, new AreaUiRect(20, 20, 20, 20))]
        };
        var contents = new[] { Expedition("expedition:a", Recipe("r1", 4, 1, 1)) };

        matcher.Observe(7, probe, contents);
        var result = matcher.Observe(7, probe, contents);

        Assert.True(Assert.Single(result.Rows).IsVisible);
    }

    [Fact]
    public void Observe_UnavailableAndUnverifiedOpenFailClosed()
    {
        var matcher = new ExpeditionRecipePanelMatcher();
        var contents = new[] { Expedition("expedition:a", Recipe("r1", 4, 1, 1)) };

        var unavailable = matcher.Observe(1, new ExpeditionRecipeUiProbeResult(
            ExpeditionRecipeUiProbeState.Unavailable, 0, 0, 0, null, null, [], []), contents);
        var unverified = matcher.Observe(1, new ExpeditionRecipeUiProbeResult(
            ExpeditionRecipeUiProbeState.OpenUnverified, 1, 2, 3, null, null, [], []), contents);

        Assert.Equal(AreaExpeditionRecipePanelAvailability.Unavailable, unavailable.Availability);
        Assert.Equal(AreaExpeditionRecipePanelAvailability.Unverified, unverified.Availability);
        Assert.Empty(unverified.Rows);
    }

    private static ExpeditionRecipeUiProbeResult OpenProbe(params (nint Address, int Runes, int Quantity)[] rows)
    {
        var candidates = rows
            .Select((row, index) => new ExpeditionRecipeUiRowCandidate(
                row.Address,
                row.Runes,
                row.Quantity,
                new AreaUiRect(10, 20 + (index * 30), 100, 20)))
            .ToImmutableArray();
        return new ExpeditionRecipeUiProbeResult(
            ExpeditionRecipeUiProbeState.OpenCandidate,
            0xAAAA,
            0xBBBB,
            0xCCCC,
            new AreaUiRect(0, 0, 300, 300),
            new AreaUiRect(0, 0, 300, 300),
            candidates,
            []);
    }

    private static AreaContentSnapshot Expedition(string instanceId, params AreaExpeditionRecipe[] recipes)
        => new(
            instanceId,
            "expedition",
            "Expedition",
            AreaContentKind.Expedition,
            AreaContentPhase.Available,
            Vector3.Zero,
            Vector2.Zero,
            1f,
            1,
            [],
            new AreaExpeditionDetails(6, recipes.ToImmutableArray()));

    private static AreaExpeditionRecipe Recipe(
        string id,
        int size,
        int catalogRow,
        int quantity,
        params AreaExpeditionReward[] rewards)
        => new(
            id,
            catalogRow,
            size,
            Enumerable.Range(0, size)
                .Select(index => new AreaExpeditionRune(index, $"Rune {index}"))
                .ToImmutableArray(),
            rewards.Length == 0
                ? ImmutableArray.Create(new AreaExpeditionReward("item", "Item", quantity, true))
                : rewards.ToImmutableArray());
}
