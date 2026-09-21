using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Game.Content;

namespace FreiAtlas.Game.Tests.Content;

public sealed class ExpeditionContentResolverTests
{
    [Theory]
    [InlineData(false, AreaContentPhase.Available)]
    [InlineData(true, AreaContentPhase.Completed)]
    public void TryResolve_UsesTheMainDevicesMinimapCompletionState(
        bool completed,
        AreaContentPhase expected)
    {
        var resolver = CreateResolver();

        Assert.True(resolver.TryResolve(
            CreateContext(CreateDevice(completed: completed)),
            out var content));

        Assert.Equal("expedition:9:73", content.InstanceId);
        Assert.Equal("expedition", content.ContentId);
        Assert.Equal(AreaContentKind.Expedition, content.Kind);
        Assert.Equal(expected, content.Phase);
    }

    [Fact]
    public void TryResolve_PreservesUnverifiedStateMachineAndRuneStationValuesAsEvidenceOnly()
    {
        var resolver = CreateResolver();
        AreaContentEvidence[] rawEvidence =
        [
            new("StateMachine", "ListenerVec+0x20[0]", "0x00000003", 0.4f),
            new("RuneStation", "Chain+0x18", "0x00000007", 0.4f)
        ];

        Assert.True(resolver.TryResolve(
            CreateContext(CreateDevice(completed: false), rawEvidence),
            out var content));

        Assert.Equal(AreaContentPhase.Available, content.Phase);
        Assert.DoesNotContain(
            content.Phase,
            new[] { AreaContentPhase.Selected, AreaContentPhase.Active });
        Assert.Contains(rawEvidence[0], content.Evidence);
        Assert.Contains(rawEvidence[1], content.Evidence);
    }

    [Fact]
    public void TryResolve_UsesVerifiedSelectedRecipePresenceWhenIncomplete()
    {
        var resolver = CreateResolver();
        AreaContentEvidence selected = new(
            "RuneStation",
            "RuneStation.SelectedRecipe.Present",
            "true",
            0.95f);

        Assert.True(resolver.TryResolve(
            CreateContext(CreateDevice(completed: false), [selected]),
            out var content));

        Assert.Equal(AreaContentPhase.Selected, content.Phase);
        Assert.Equal(0.95f, content.Confidence);
        Assert.Contains(selected, content.Evidence);
    }

    [Fact]
    public void TryResolve_CompletionTakesPriorityOverSelectedRecipePresence()
    {
        var resolver = CreateResolver();
        AreaContentEvidence selected = new(
            "RuneStation",
            "RuneStation.SelectedRecipe.Present",
            "true",
            0.95f);

        Assert.True(resolver.TryResolve(
            CreateContext(CreateDevice(completed: true), [selected]),
            out var content));

        Assert.Equal(AreaContentPhase.Completed, content.Phase);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("unresolved")]
    public void TryResolve_DoesNotSelectWithoutVerifiedRecipePresence(string value)
    {
        var resolver = CreateResolver();
        AreaContentEvidence evidence = new(
            "RuneStation",
            "RuneStation.SelectedRecipe.Present",
            value,
            0.95f);

        Assert.True(resolver.TryResolve(
            CreateContext(CreateDevice(completed: false), [evidence]),
            out var content));

        Assert.Equal(AreaContentPhase.Available, content.Phase);
    }

    [Fact]
    public void TryResolve_ResearchEvidenceDoesNotDriveSelectedState()
    {
        var resolver = CreateResolver();
        AreaContentEvidence research = new(
            "RuneStation",
            "Research.RuneStation.SelectedRecipe.Present",
            "true",
            0.1f);

        Assert.True(resolver.TryResolve(
            CreateContext(CreateDevice(completed: false), [research]),
            out var content));

        Assert.Equal(AreaContentPhase.Available, content.Phase);
    }

    [Fact]
    public void TryResolve_LeavesPhaseUnknownWhenTheMinimapComponentIsAbsent()
    {
        var resolver = CreateResolver();

        Assert.True(resolver.TryResolve(
            CreateContext(CreateDevice(hasMinimapIcon: false)),
            out var content));

        Assert.Equal(AreaContentPhase.Unknown, content.Phase);
    }

    [Theory]
    [InlineData("Metadata/Monsters/LeagueExpedition/ExpeditionMonster")]
    [InlineData("Metadata/Effects/Expedition/Expedition2EncounterCrack")]
    [InlineData("Metadata/MiscellaneousObjects/Expedition2/Expedition2EncounterCrack")]
    public void TryResolve_DoesNotMatchExpeditionDecoys(string metadata)
    {
        var resolver = CreateResolver();

        Assert.False(resolver.TryResolve(
            CreateContext(CreateDevice(metadata: metadata)),
            out _));
    }

    private static ExpeditionContentResolver CreateResolver()
        => new(AreaContentCatalog.LoadEmbedded());

    private static AreaContentContext CreateContext(
        AreaEntitySnapshot entity,
        IReadOnlyList<AreaContentEvidence>? evidence = null)
        => new(
            new AreaIdentity(9, "TestArea", 80, 9),
            entity,
            [],
            evidence ?? []);

    private static AreaEntitySnapshot CreateDevice(
        string metadata = "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter",
        bool hasMinimapIcon = true,
        bool completed = false)
        => new(
            73,
            metadata,
            "Expedition Encounter",
            AreaEntityCategory.Object,
            Vector3.One,
            new Vector2(2, 3),
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster,
            0,
            0,
            hasMinimapIcon,
            completed,
            AreaChestState.NotApplicable,
            []);
}
