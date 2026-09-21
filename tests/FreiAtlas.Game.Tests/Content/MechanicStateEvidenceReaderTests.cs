using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Game.Content;
using FreiAtlas.Game.Entities;
using FreiAtlas.Game.Tests.Entities;
using FreiAtlas.Game.Tests.Memory;

namespace FreiAtlas.Game.Tests.Content;

public sealed class MechanicStateEvidenceReaderTests
{
    private const string AbyssMetadata =
        "Metadata/MiscellaneousObjects/Abyss/AbyssFinalNodeBase";
    private const string BreachMetadata =
        "Metadata/MiscellaneousObjects/Brequel/BrequelInitiator";
    private const string EssenceMetadata =
        "Metadata/MiscellaneousObjects/Monolith";
    private const string RitualMetadata =
        "Metadata/Terrain/Leagues/Ritual/RitualRuneObject";

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Read_RitualPreservesStateMachineStateAsHighConfidenceEvidence(int state)
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var altar = builder.Allocate();
        var stateMachine = builder.Allocate(0x40);
        memory.WriteInt32(stateMachine + 0x10, state);
        builder.WriteEntity(
            altar,
            RitualMetadata,
            ("StateMachine", stateMachine));
        var reader = new MechanicStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory));

        var result = reader.Read(
            new RawEntityRef(42, altar),
            CreateEntity(42, RitualMetadata));

        Assert.True(result.Matched);
        Assert.Collection(
            result.Evidence,
            item => Assert.Equal(
                new AreaContentEvidence(
                    "StateMachine",
                    "State",
                    $"0x{state:X8}",
                    0.95f),
                item));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public void Read_BreachPreservesStateAsHighConfidenceEvidence(int state)
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var device = builder.Allocate();
        var stateMachine = builder.Allocate(0x40);
        memory.WriteInt32(stateMachine + 0x10, state);
        builder.WriteEntity(
            device,
            BreachMetadata,
            ("StateMachine", stateMachine));
        var reader = new MechanicStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory));

        var result = reader.Read(
            new RawEntityRef(42, device),
            CreateEntity(42, BreachMetadata));

        Assert.True(result.Matched);
        Assert.Collection(
            result.Evidence,
            item => Assert.Equal(
                new AreaContentEvidence(
                    "StateMachine",
                    "State",
                    $"0x{state:X8}",
                    0.95f),
                item));
    }

    [Theory]
    [InlineData(AbyssMetadata, true)]
    [InlineData(AbyssMetadata, false)]
    [InlineData(EssenceMetadata, true)]
    [InlineData(EssenceMetadata, false)]
    public void Read_AbyssAndEssencePreserveMinimapCompletion(
        string metadata,
        bool isComplete)
    {
        using var memory = new SyntheticProcessMemory();
        var reader = new MechanicStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory));

        var result = reader.Read(
            new RawEntityRef(42, (nint)0x500000),
            CreateEntity(42, metadata, isComplete));

        Assert.True(result.Matched);
        Assert.Collection(
            result.Evidence,
            item => Assert.Equal(
                new AreaContentEvidence(
                    "MinimapIcon",
                    "IsComplete",
                    isComplete ? "true" : "false",
                    0.95f),
                item));
    }

    [Fact]
    public void Read_BreachWithoutStateMachineReportsOnlyUnresolvedEvidence()
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var device = builder.Allocate();
        builder.WriteEntity(device, BreachMetadata);
        var reader = new MechanicStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory));

        var result = reader.Read(
            new RawEntityRef(42, device),
            CreateEntity(42, BreachMetadata));

        Assert.True(result.Matched);
        Assert.Collection(
            result.Evidence,
            item => Assert.Equal(
                new AreaContentEvidence(
                    "StateMachine",
                    "Resolved",
                    "false",
                    0.2f),
                item));
    }

    [Fact]
    public void Read_BreachStateReadFailureReportsOnlyStateReadEvidence()
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var device = builder.Allocate();
        var stateMachine = builder.Allocate(0x40);
        memory.FailRange(stateMachine + 0x10, sizeof(int));
        builder.WriteEntity(
            device,
            BreachMetadata,
            ("StateMachine", stateMachine));
        var reader = new MechanicStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory));

        var result = reader.Read(
            new RawEntityRef(42, device),
            CreateEntity(42, BreachMetadata));

        Assert.True(result.Matched);
        Assert.Collection(
            result.Evidence,
            item => Assert.Equal(
                new AreaContentEvidence(
                    "StateMachine",
                    "StateRead",
                    "false",
                    0.2f),
                item));
    }

    [Theory]
    [InlineData("metadata/miscellaneousobjects/abyss/abyssfinalnodebase")]
    [InlineData("METADATA/MISCELLANEOUSOBJECTS/BREQUEL/BREQUELINITIATOR")]
    [InlineData("metadata/miscellaneousobjects/monolith")]
    public void Read_MatchesCompleteMetadataPathIgnoringCase(string metadata)
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var device = builder.Allocate();
        var stateMachine = builder.Allocate(0x40);
        memory.WriteInt32(stateMachine + 0x10, 1);
        builder.WriteEntity(
            device,
            metadata,
            ("StateMachine", stateMachine));
        var reader = new MechanicStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory));

        var result = reader.Read(
            new RawEntityRef(42, device),
            CreateEntity(42, metadata));

        Assert.True(result.Matched);
    }

    [Theory]
    [InlineData("Metadata/MiscellaneousObjects/Abyss/AbyssCrack")]
    [InlineData("Metadata/MiscellaneousObjects/Ritual/RitualRuneInteractable")]
    [InlineData("Metadata/MiscellaneousObjects/Other/Unknown")]
    [InlineData("Metadata/MiscellaneousObjects/MonolithExtra")]
    public void Read_DoesNotMatchOtherMetadata(string metadata)
    {
        using var memory = new SyntheticProcessMemory();
        var reader = new MechanicStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory));

        var result = reader.Read(
            new RawEntityRef(42, (nint)0x500000),
            CreateEntity(42, metadata));

        Assert.False(result.Matched);
        Assert.Empty(result.Evidence);
    }

    private static AreaEntitySnapshot CreateEntity(
        uint entityId,
        string metadata,
        bool isMinimapIconComplete = false)
        => new(
            entityId,
            metadata,
            "Mechanic",
            AreaEntityCategory.Object,
            Vector3.Zero,
            Vector2.Zero,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster,
            100,
            100,
            true,
            isMinimapIconComplete,
            AreaChestState.NotApplicable,
            []);
}
