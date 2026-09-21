using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Game.Content;
using FreiAtlas.Game.Entities;
using FreiAtlas.Game.Tests.Entities;
using FreiAtlas.Game.Tests.Memory;

namespace FreiAtlas.Game.Tests.Content;

public sealed class AreaContentNormalizerTests
{
    [Theory]
    [InlineData(AreaContentKind.Abyss, "Metadata/MiscellaneousObjects/Abyss/AbyssFinalNodeBase", AreaEntityCategory.Object, AreaChestState.NotApplicable)]
    [InlineData(AreaContentKind.Ritual, "Metadata/Terrain/Leagues/Ritual/RitualRuneObject", AreaEntityCategory.Other, AreaChestState.NotApplicable)]
    [InlineData(AreaContentKind.Breach, "Metadata/MiscellaneousObjects/Brequel/BrequelInitiator", AreaEntityCategory.Object, AreaChestState.NotApplicable)]
    [InlineData(AreaContentKind.Essence, "Metadata/MiscellaneousObjects/Monolith", AreaEntityCategory.Other, AreaChestState.NotApplicable)]
    [InlineData(AreaContentKind.Strongbox, "Metadata/Chests/StrongBoxes/Strongbox1", AreaEntityCategory.Chest, AreaChestState.Closed)]
    public void Normalize_EmitsAvailableTypedMechanicContent(
        AreaContentKind expectedKind,
        string metadata,
        AreaEntityCategory category,
        AreaChestState chestState)
    {
        var entity = CreateEntity(
            501,
            metadata,
            expectedKind.ToString(),
            category,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster,
            chestState: chestState);

        var content = Assert.Single(CreateNormalizer().Normalize(
            CreateArea(11),
            [entity],
            []));

        Assert.Equal(expectedKind, content.Kind);
        Assert.Equal(AreaContentPhase.Available, content.Phase);
        Assert.Equal(entity.EntityId, content.SourceEntityId);
        Assert.Equal($"{expectedKind.ToString().ToLowerInvariant()}:11:501", content.InstanceId);
    }

    [Theory]
    [InlineData(AreaContentKind.Abyss, "Metadata/MiscellaneousObjects/Abyss/AbyssFinalNodeBase", AreaEntityCategory.Object, "MinimapIcon", "IsComplete", "true")]
    [InlineData(AreaContentKind.Breach, "Metadata/MiscellaneousObjects/Brequel/BrequelInitiator", AreaEntityCategory.Object, "StateMachine", "State", "0x00000001")]
    [InlineData(AreaContentKind.Essence, "Metadata/MiscellaneousObjects/Monolith", AreaEntityCategory.Other, "MinimapIcon", "IsComplete", "true")]
    public void Normalize_CompletesMechanicFromReliableStateEvidence(
        AreaContentKind expectedKind,
        string metadata,
        AreaEntityCategory category,
        string source,
        string key,
        string value)
    {
        var entity = CreateEntity(
            601,
            metadata,
            expectedKind.ToString(),
            category,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster);
        var evidence = new Dictionary<uint, IReadOnlyList<AreaContentEvidence>>
        {
            [entity.EntityId] = [new(source, key, value, 0.95f)]
        };

        var content = Assert.Single(CreateNormalizer().Normalize(
            CreateArea(11),
            [entity],
            [],
            evidence));

        Assert.Equal(expectedKind, content.Kind);
        Assert.Equal(AreaContentPhase.Completed, content.Phase);
        Assert.Equal(0.95f, content.Confidence);
        Assert.Contains(content.Evidence, item => item.Source == "Catalog");
        Assert.Contains(content.Evidence, item => item.Source == source && item.Key == key);
    }

    [Fact]
    public void Normalize_CompletesRitualObjectFromCompletedInteractableAtSamePosition()
    {
        var altar = CreateEntity(
            701,
            "Metadata/Terrain/Leagues/Ritual/RitualRuneObject",
            "RitualRuneObject",
            AreaEntityCategory.Object,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster,
            gridPosition: new Vector2(120, 80));
        var interactable = CreateEntity(
            702,
            "Metadata/Terrain/Leagues/Ritual/RitualRuneInteractable",
            "RitualRuneInteractable",
            AreaEntityCategory.Object,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster,
            hasMinimapIcon: true,
            completed: true,
            gridPosition: new Vector2(120, 80));
        var evidence = new Dictionary<uint, IReadOnlyList<AreaContentEvidence>>
        {
            [interactable.EntityId] =
            [
                new("MinimapIcon", "IsComplete", "true", 0.95f)
            ]
        };

        var content = Assert.Single(CreateNormalizer().Normalize(
            CreateArea(11),
            [altar, interactable],
            [],
            evidence));

        Assert.Equal(AreaContentKind.Ritual, content.Kind);
        Assert.Equal(AreaContentPhase.Completed, content.Phase);
        Assert.Equal(altar.EntityId, content.SourceEntityId);
        Assert.Contains(
            content.Evidence,
            item => item.Source == "RitualInteractable"
                    && item.Key == "MinimapIcon.IsComplete"
                    && item.Value == "true");
    }

    [Fact]
    public void Normalize_KeepsRitualObjectAvailableFromIncompleteInteractableAtSamePosition()
    {
        var altar = CreateEntity(
            703,
            "Metadata/Terrain/Leagues/Ritual/RitualRuneObject",
            "RitualRuneObject",
            AreaEntityCategory.Object,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster,
            gridPosition: new Vector2(120, 80));
        var interactable = CreateEntity(
            704,
            "Metadata/Terrain/Leagues/Ritual/RitualRuneInteractable",
            "RitualRuneInteractable",
            AreaEntityCategory.Object,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster,
            hasMinimapIcon: true,
            completed: false,
            gridPosition: new Vector2(120, 80));

        var content = Assert.Single(CreateNormalizer().Normalize(
            CreateArea(11),
            [altar, interactable],
            []));

        Assert.Equal(AreaContentKind.Ritual, content.Kind);
        Assert.Equal(AreaContentPhase.Available, content.Phase);
        Assert.Equal(altar.EntityId, content.SourceEntityId);
        Assert.Contains(
            content.Evidence,
            item => item.Source == "RitualInteractable"
                    && item.Key == "MinimapIcon.IsComplete"
                    && item.Value == "false");
    }

    [Theory]
    [InlineData(AreaContentKind.Abyss, "Metadata/MiscellaneousObjects/Abyss/AbyssFinalNodeBase", AreaEntityCategory.Object, "MinimapIcon", "IsComplete", "false")]
    [InlineData(AreaContentKind.Abyss, "Metadata/MiscellaneousObjects/Abyss/AbyssFinalNodeBase", AreaEntityCategory.Object, "MinimapIcon", "IsComplete", "unknown")]
    [InlineData(AreaContentKind.Ritual, "Metadata/Terrain/Leagues/Ritual/RitualRuneObject", AreaEntityCategory.Object, "StateMachine", "State", "0x00000000")]
    [InlineData(AreaContentKind.Breach, "Metadata/MiscellaneousObjects/Brequel/BrequelInitiator", AreaEntityCategory.Object, "StateMachine", "State", "0x00000000")]
    [InlineData(AreaContentKind.Breach, "Metadata/MiscellaneousObjects/Brequel/BrequelInitiator", AreaEntityCategory.Object, "StateMachine", "State", "0x00000002")]
    [InlineData(AreaContentKind.Breach, "Metadata/MiscellaneousObjects/Brequel/BrequelInitiator", AreaEntityCategory.Object, "StateMachine", "Resolved", "false")]
    [InlineData(AreaContentKind.Breach, "Metadata/MiscellaneousObjects/Brequel/BrequelInitiator", AreaEntityCategory.Object, "StateMachine", "StateRead", "false")]
    [InlineData(AreaContentKind.Essence, "Metadata/MiscellaneousObjects/Monolith", AreaEntityCategory.Other, "MinimapIcon", "IsComplete", "false")]
    public void Normalize_KeepsMechanicAvailableForUnreliableStateEvidence(
        AreaContentKind expectedKind,
        string metadata,
        AreaEntityCategory category,
        string source,
        string key,
        string value)
    {
        var entity = CreateEntity(
            602,
            metadata,
            expectedKind.ToString(),
            category,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster);
        var evidence = new Dictionary<uint, IReadOnlyList<AreaContentEvidence>>
        {
            [entity.EntityId] = [new(source, key, value, 0.95f)]
        };

        var content = Assert.Single(CreateNormalizer().Normalize(
            CreateArea(11),
            [entity],
            [],
            evidence));

        Assert.Equal(expectedKind, content.Kind);
        Assert.Equal(AreaContentPhase.Available, content.Phase);
    }

    [Theory]
    [InlineData(AreaContentKind.Abyss, "Metadata/MiscellaneousObjects/Abyss/AbyssFinalNodeBase", AreaEntityCategory.Object, "MinimapIcon", "IsComplete", "true", "false")]
    [InlineData(AreaContentKind.Breach, "Metadata/MiscellaneousObjects/Brequel/BrequelInitiator", AreaEntityCategory.Object, "StateMachine", "State", "0x00000001", "0x00000000")]
    [InlineData(AreaContentKind.Essence, "Metadata/MiscellaneousObjects/Monolith", AreaEntityCategory.Other, "MinimapIcon", "IsComplete", "true", "false")]
    public void Normalize_RetainsCompletedMechanicWhenLaterEvidenceIsFalse(
        AreaContentKind expectedKind,
        string metadata,
        AreaEntityCategory category,
        string source,
        string key,
        string completedValue,
        string availableValue)
    {
        var normalizer = CreateNormalizer();
        var area = CreateArea(11);
        var entity = CreateEntity(
            603,
            metadata,
            expectedKind.ToString(),
            category,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster);
        var completedEvidence = new Dictionary<uint, IReadOnlyList<AreaContentEvidence>>
        {
            [entity.EntityId] = [new(source, key, completedValue, 0.95f)]
        };
        var availableEvidence = new Dictionary<uint, IReadOnlyList<AreaContentEvidence>>
        {
            [entity.EntityId] = [new(source, key, availableValue, 0.95f)]
        };

        Assert.Equal(
            AreaContentPhase.Completed,
            Assert.Single(normalizer.Normalize(area, [entity], [], completedEvidence)).Phase);
        var retained = Assert.Single(normalizer.Normalize(
            area,
            [entity],
            [],
            availableEvidence));

        Assert.Equal(expectedKind, retained.Kind);
        Assert.Equal(AreaContentPhase.Completed, retained.Phase);
    }

    [Fact]
    public void Normalize_ClearsCompletedMechanicWhenSessionChanges()
    {
        var normalizer = CreateNormalizer();
        var entity = CreateEntity(
            604,
            "Metadata/MiscellaneousObjects/Abyss/AbyssFinalNodeBase",
            "Abyss",
            AreaEntityCategory.Object,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster);
        var evidence = new Dictionary<uint, IReadOnlyList<AreaContentEvidence>>
        {
            [entity.EntityId] = [new("MinimapIcon", "IsComplete", "true", 0.95f)]
        };

        Assert.Equal(
            AreaContentPhase.Completed,
            Assert.Single(normalizer.Normalize(CreateArea(11), [entity], [], evidence)).Phase);
        var nextSession = Assert.Single(normalizer.Normalize(
            CreateArea(12),
            [entity],
            [],
            new Dictionary<uint, IReadOnlyList<AreaContentEvidence>>
            {
                [entity.EntityId] = [new("MinimapIcon", "IsComplete", "false", 0.95f)]
            }));

        Assert.Equal(AreaContentPhase.Available, nextSession.Phase);
    }

    [Fact]
    public void Normalize_DoesNotCarryCompletedPhaseAcrossMechanicKindsForSameEntityId()
    {
        var normalizer = CreateNormalizer();
        var area = CreateArea(11);
        var abyss = CreateEntity(
            614,
            "Metadata/MiscellaneousObjects/Abyss/AbyssFinalNodeBase",
            "Abyss",
            AreaEntityCategory.Object,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster);
        var breach = CreateEntity(
            614,
            "Metadata/MiscellaneousObjects/Brequel/BrequelInitiator",
            "Breach",
            AreaEntityCategory.Object,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster);
        Assert.Equal(
            AreaContentPhase.Completed,
            Assert.Single(normalizer.Normalize(
                area,
                [abyss],
                [],
                new Dictionary<uint, IReadOnlyList<AreaContentEvidence>>
                {
                    [abyss.EntityId] = [new("MinimapIcon", "IsComplete", "true", 0.95f)]
                })).Phase);

        var changedKind = Assert.Single(normalizer.Normalize(
            area,
            [breach],
            [],
            new Dictionary<uint, IReadOnlyList<AreaContentEvidence>>
            {
                [breach.EntityId] = [new("StateMachine", "State", "0x00000000", 0.95f)]
            }));

        Assert.Equal(AreaContentKind.Breach, changedKind.Kind);
        Assert.Equal(AreaContentPhase.Available, changedKind.Phase);
    }

    [Fact]
    public void Normalize_CompletesEssenceAfterThreeNearPlayerMissingSamples()
    {
        var normalizer = CreateNormalizer();
        var area = CreateArea(11);
        var essence = CreateEntity(
            605,
            "Metadata/MiscellaneousObjects/Monolith",
            "Essence",
            AreaEntityCategory.Other,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster,
            gridPosition: new Vector2(100, 100));
        var availableEvidence = new Dictionary<uint, IReadOnlyList<AreaContentEvidence>>
        {
            [essence.EntityId] = [new("MinimapIcon", "IsComplete", "false", 0.95f)]
        };
        var player = CreatePlayer(new Vector2(100, 100));

        Assert.Equal(
            AreaContentPhase.Available,
            Assert.Single(normalizer.Normalize(area, [essence], [], availableEvidence, player: player)).Phase);

        var firstMissing = Assert.Single(normalizer.Normalize(area, [], [], player: player));
        var secondMissing = Assert.Single(normalizer.Normalize(area, [], [], player: player));
        var completed = Assert.Single(normalizer.Normalize(area, [], [], player: player));

        Assert.Equal(AreaContentPhase.Available, firstMissing.Phase);
        Assert.Equal(AreaContentPhase.Available, secondMissing.Phase);
        Assert.Equal(AreaContentPhase.Completed, completed.Phase);
        Assert.DoesNotContain(firstMissing.Evidence, IsEssenceCompletionEvidence);
        Assert.DoesNotContain(secondMissing.Evidence, IsEssenceCompletionEvidence);
        Assert.Single(completed.Evidence, item =>
            item.Source == "EntityLifecycle" && item.Key == "MissingFromSample");
        Assert.Contains(completed.Evidence, item =>
            item.Source == "EntityLifecycle"
            && item.Key == "ConfirmedMissingNearPlayer"
            && item.Value == "true");
        Assert.Contains(completed.Evidence, item =>
            item.Source == "EntityLifecycle"
            && item.Key == "MissingSampleCount"
            && item.Value == "3");
        Assert.Contains(completed.Evidence, item =>
            item.Source == "EntityLifecycle"
            && item.Key == "PlayerDistance"
            && item.Value == "0");
    }

    [Fact]
    public void Normalize_DoesNotCompleteEssenceAfterThreeMissingSamplesWithoutPlayer()
    {
        var normalizer = CreateNormalizer();
        var area = CreateArea(11);
        var essence = CreateEntity(
            609,
            "Metadata/MiscellaneousObjects/Monolith",
            "Essence",
            AreaEntityCategory.Other,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster,
            gridPosition: new Vector2(100, 100));
        var evidence = new Dictionary<uint, IReadOnlyList<AreaContentEvidence>>
        {
            [essence.EntityId] = [new("MinimapIcon", "IsComplete", "false", 0.95f)]
        };

        Assert.Equal(
            AreaContentPhase.Available,
            Assert.Single(normalizer.Normalize(area, [essence], [], evidence, player: null)).Phase);

        for (var i = 0; i < 3; i++)
        {
            var missing = Assert.Single(normalizer.Normalize(area, [], [], player: null));
            Assert.Equal(AreaContentPhase.Available, missing.Phase);
            Assert.DoesNotContain(missing.Evidence, IsEssenceCompletionEvidence);
        }
    }

    [Fact]
    public void Normalize_DoesNotAdvanceEssenceMissingWhileRawEntityIsUnpositioned()
    {
        var normalizer = CreateNormalizer();
        var area = CreateArea(11);
        var player = CreatePlayer(new Vector2(100, 100));
        var essence = CreateEntity(
            42,
            "Metadata/MiscellaneousObjects/Monolith",
            "Essence",
            AreaEntityCategory.Other,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster,
            gridPosition: new Vector2(100, 100));
        var evidence = new Dictionary<uint, IReadOnlyList<AreaContentEvidence>>
        {
            [essence.EntityId] = [new("MinimapIcon", "IsComplete", "false", 0.95f)]
        };
        Assert.Equal(
            AreaContentPhase.Available,
            Assert.Single(normalizer.Normalize(area, [essence], [], evidence, player: player)).Phase);

        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var entityAddress = builder.Allocate();
        builder.WriteEntity(entityAddress, "Metadata/MiscellaneousObjects/Monolith");
        var rawEntities = new[] { new RawEntityRef(42, entityAddress) };
        var reader = new AreaEntityReader(
            memory,
            new EntityComponentResolver(memory),
            EntityNameCatalog.FromEntries([]));
        var unpositioned = reader.Read(area.SessionSequence, rawEntities);
        Assert.Empty(unpositioned.Entities);
        Assert.Contains(unpositioned.Diagnostics, diagnostic =>
            diagnostic.Code == "entity-unpositioned"
            && diagnostic.EntityId == 42);
        var rawObservedEntityIds = rawEntities
            .Select(entity => entity.EntityId)
            .ToHashSet();

        for (var i = 0; i < 3; i++)
        {
            var retained = Assert.Single(normalizer.Normalize(
                area,
                unpositioned.Entities,
                [],
                player: player,
                rawObservedEntityIds: rawObservedEntityIds));
            Assert.Equal(AreaContentPhase.Available, retained.Phase);
            Assert.DoesNotContain(retained.Evidence, IsEssenceCompletionEvidence);
        }

        Assert.Equal(
            AreaContentPhase.Available,
            Assert.Single(normalizer.Normalize(
                area,
                [],
                [],
                player: player,
                rawObservedEntityIds: new HashSet<uint>())).Phase);
        Assert.Equal(
            AreaContentPhase.Available,
            Assert.Single(normalizer.Normalize(
                area,
                [],
                [],
                player: player,
                rawObservedEntityIds: new HashSet<uint>())).Phase);
        var completed = Assert.Single(normalizer.Normalize(
            area,
            [],
            [],
            player: player,
            rawObservedEntityIds: new HashSet<uint>()));
        Assert.Equal(AreaContentPhase.Completed, completed.Phase);
    }

    [Fact]
    public void Normalize_UnpositionedEssenceDoesNotBlockAnotherMissingEssenceCompletion()
    {
        var normalizer = CreateNormalizer();
        var area = CreateArea(11);
        var player = CreatePlayer(new Vector2(100, 100));
        var unpositionedEssence = CreateEntity(
            42,
            "Metadata/MiscellaneousObjects/Monolith",
            "Unpositioned Essence",
            AreaEntityCategory.Other,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster,
            gridPosition: new Vector2(100, 100));
        var missingEssence = CreateEntity(
            43,
            "Metadata/MiscellaneousObjects/Monolith",
            "Missing Essence",
            AreaEntityCategory.Other,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster,
            gridPosition: new Vector2(100, 100));
        var evidence = new Dictionary<uint, IReadOnlyList<AreaContentEvidence>>
        {
            [unpositionedEssence.EntityId] = [new("MinimapIcon", "IsComplete", "false", 0.95f)],
            [missingEssence.EntityId] = [new("MinimapIcon", "IsComplete", "false", 0.95f)]
        };
        Assert.Equal(
            2,
            normalizer.Normalize(
                area,
                [unpositionedEssence, missingEssence],
                [],
                evidence,
                player: player).Count);

        IReadOnlyList<AreaContentSnapshot> contents = [];
        for (var i = 0; i < 3; i++)
        {
            contents = normalizer.Normalize(
                area,
                [],
                [],
                player: player,
                rawObservedEntityIds: new HashSet<uint> { 42 });
            Assert.Equal(
                AreaContentPhase.Available,
                contents.Single(item => item.SourceEntityId == 42).Phase);
        }

        Assert.Equal(
            AreaContentPhase.Completed,
            contents.Single(item => item.SourceEntityId == 43).Phase);
    }

    [Fact]
    public void Normalize_DoesNotCompleteEssenceWhenPlayerIsFarOrInvalid()
    {
        var normalizer = CreateNormalizer();
        var area = CreateArea(11);
        var essence = CreateEntity(
            606,
            "Metadata/MiscellaneousObjects/Monolith",
            "Essence",
            AreaEntityCategory.Other,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster,
            gridPosition: new Vector2(100, 100));
        var evidence = new Dictionary<uint, IReadOnlyList<AreaContentEvidence>>
        {
            [essence.EntityId] = [new("MinimapIcon", "IsComplete", "false", 0.95f)]
        };
        Assert.Single(normalizer.Normalize(
            area,
            [essence],
            [],
            evidence,
            player: CreatePlayer(new Vector2(100, 100))));

        var far = CreatePlayer(new Vector2(221, 100));
        for (var i = 0; i < 4; i++)
        {
            var content = Assert.Single(normalizer.Normalize(area, [], [], player: far));
            Assert.Equal(AreaContentPhase.Available, content.Phase);
            Assert.DoesNotContain(content.Evidence, item =>
                item.Key == "ConfirmedMissingNearPlayer");
        }

        var invalid = CreatePlayer(new Vector2(float.NaN, 100));
        for (var i = 0; i < 4; i++)
        {
            var content = Assert.Single(normalizer.Normalize(area, [], [], player: invalid));
            Assert.Equal(AreaContentPhase.Available, content.Phase);
        }
    }

    [Fact]
    public void Normalize_ResetsEssenceMissingCountWhenEntityReappears()
    {
        var normalizer = CreateNormalizer();
        var area = CreateArea(11);
        var essence = CreateEntity(
            607,
            "Metadata/MiscellaneousObjects/Monolith",
            "Essence",
            AreaEntityCategory.Other,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster,
            gridPosition: new Vector2(100, 100));
        var availableEvidence = new Dictionary<uint, IReadOnlyList<AreaContentEvidence>>
        {
            [essence.EntityId] = [new("MinimapIcon", "IsComplete", "false", 0.95f)]
        };
        var player = CreatePlayer(new Vector2(100, 100));
        normalizer.Normalize(area, [essence], [], availableEvidence, player: player);
        Assert.Equal(AreaContentPhase.Available, Assert.Single(normalizer.Normalize(area, [], [], player: player)).Phase);
        Assert.Equal(AreaContentPhase.Available, Assert.Single(normalizer.Normalize(area, [], [], player: player)).Phase);

        var reappeared = Assert.Single(normalizer.Normalize(area, [essence], [], availableEvidence, player: player));
        Assert.Equal(AreaContentPhase.Available, reappeared.Phase);
        Assert.Equal(AreaContentPhase.Available, Assert.Single(normalizer.Normalize(area, [], [], player: player)).Phase);
        Assert.Equal(AreaContentPhase.Available, Assert.Single(normalizer.Normalize(area, [], [], player: player)).Phase);
    }

    [Fact]
    public void Normalize_KeepsCompletedEssenceCompletedAfterMissingAndReappearance()
    {
        var normalizer = CreateNormalizer();
        var area = CreateArea(11);
        var essence = CreateEntity(
            608,
            "Metadata/MiscellaneousObjects/Monolith",
            "Essence",
            AreaEntityCategory.Other,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster,
            gridPosition: new Vector2(100, 100));
        var availableEvidence = new Dictionary<uint, IReadOnlyList<AreaContentEvidence>>
        {
            [essence.EntityId] = [new("MinimapIcon", "IsComplete", "false", 0.95f)]
        };
        var completedEvidence = new Dictionary<uint, IReadOnlyList<AreaContentEvidence>>
        {
            [essence.EntityId] = [new("MinimapIcon", "IsComplete", "true", 0.95f)]
        };
        var player = CreatePlayer(new Vector2(100, 100));
        normalizer.Normalize(area, [essence], [], completedEvidence, player: player);
        var missing = Assert.Single(normalizer.Normalize(area, [], [], player: player));
        Assert.Equal(AreaContentPhase.Completed, missing.Phase);

        var reappeared = Assert.Single(normalizer.Normalize(area, [essence], [], availableEvidence, player: player));
        Assert.Equal(AreaContentPhase.Completed, reappeared.Phase);
        Assert.Equal(AreaContentPhase.Completed, Assert.Single(normalizer.Normalize(area, [], [], player: player)).Phase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Normalize_ClearsMechanicCompletionAndMissingCountWhenAreaIdentityChanges(
        bool changeAreaHash)
    {
        var normalizer = CreateNormalizer();
        var area = CreateArea(11);
        var changedArea = changeAreaHash
            ? area with { AreaHash = area.AreaHash + 1 }
            : area with { AreaCode = "ChangedArea" };
        var player = CreatePlayer(new Vector2(100, 100));
        var completedEssence = CreateEntity(
            610,
            "Metadata/MiscellaneousObjects/Monolith",
            "Completed Essence",
            AreaEntityCategory.Other,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster,
            gridPosition: new Vector2(100, 100));
        var completedEvidence = new Dictionary<uint, IReadOnlyList<AreaContentEvidence>>
        {
            [completedEssence.EntityId] = [new("MinimapIcon", "IsComplete", "true", 0.95f)]
        };
        Assert.Equal(
            AreaContentPhase.Completed,
            Assert.Single(normalizer.Normalize(area, [completedEssence], [], completedEvidence, player: player)).Phase);

        var countedEssence = CreateEntity(
            611,
            "Metadata/MiscellaneousObjects/Monolith",
            "Counted Essence",
            AreaEntityCategory.Other,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster,
            gridPosition: new Vector2(100, 100));
        var availableEvidence = new Dictionary<uint, IReadOnlyList<AreaContentEvidence>>
        {
            [countedEssence.EntityId] = [new("MinimapIcon", "IsComplete", "false", 0.95f)]
        };
        normalizer.Normalize(area, [countedEssence], [], availableEvidence, player: player);
        var oldMissing = normalizer.Normalize(area, [], [], player: player)
            .Single(item => item.SourceEntityId == countedEssence.EntityId);
        Assert.Equal(AreaContentPhase.Available, oldMissing.Phase);

        var resetCompletion = Assert.Single(normalizer.Normalize(
            changedArea,
            [completedEssence],
            [],
            new Dictionary<uint, IReadOnlyList<AreaContentEvidence>>
            {
                [completedEssence.EntityId] = [new("MinimapIcon", "IsComplete", "false", 0.95f)]
            },
            player: player));
        Assert.Equal(AreaContentPhase.Available, resetCompletion.Phase);

        Assert.Equal(
            AreaContentPhase.Available,
            normalizer.Normalize(changedArea, [countedEssence], [], availableEvidence, player: player)
                .Single(item => item.SourceEntityId == countedEssence.EntityId)
                .Phase);

        for (var i = 0; i < 2; i++)
        {
            var missing = normalizer.Normalize(changedArea, [], [], player: player)
                .Single(item => item.SourceEntityId == countedEssence.EntityId);
            Assert.Equal(AreaContentPhase.Available, missing.Phase);
            Assert.DoesNotContain(missing.Evidence, IsEssenceCompletionEvidence);
        }
    }

    [Fact]
    public void Normalize_DeduplicatesEssenceCompletionEvidenceAndUsesLatestValues()
    {
        var normalizer = CreateNormalizer();
        var area = CreateArea(11);
        var essence = CreateEntity(
            612,
            "Metadata/MiscellaneousObjects/Monolith",
            "Essence",
            AreaEntityCategory.Other,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster,
            gridPosition: new Vector2(100, 100));
        var evidence = new Dictionary<uint, IReadOnlyList<AreaContentEvidence>>
        {
            [essence.EntityId] =
            [
                new("MinimapIcon", "IsComplete", "false", 0.95f),
                new("EntityLifecycle", "ConfirmedMissingNearPlayer", "false", 0.1f),
                new("EntityLifecycle", "ConfirmedMissingNearPlayer", "true", 0.2f),
                new("EntityLifecycle", "MissingSampleCount", "1", 0.1f),
                new("EntityLifecycle", "PlayerDistance", "12.345", 0.1f)
            ]
        };
        normalizer.Normalize(area, [essence], [], evidence, player: CreatePlayer(new Vector2(100, 100)));

        var player = CreatePlayer(new Vector2(110, 100));
        normalizer.Normalize(area, [], [], player: player);
        normalizer.Normalize(area, [], [], player: player);
        var completed = Assert.Single(normalizer.Normalize(area, [], [], player: player));

        Assert.Equal(AreaContentPhase.Completed, completed.Phase);
        foreach (var key in new[]
                 {
                     "ConfirmedMissingNearPlayer",
                     "MissingSampleCount",
                     "PlayerDistance"
                 })
        {
            Assert.Single(completed.Evidence, item =>
                item.Source == "EntityLifecycle" && item.Key == key);
        }

        Assert.Equal(
            "3",
            Assert.Single(completed.Evidence, item =>
                item.Source == "EntityLifecycle" && item.Key == "MissingSampleCount").Value);
        Assert.Equal(
            "10",
            Assert.Single(completed.Evidence, item =>
                item.Source == "EntityLifecycle" && item.Key == "PlayerDistance").Value);
    }

    [Fact]
    public void Normalize_PreservesEssenceCompletionEvidenceWhenEntityReappears()
    {
        var normalizer = CreateNormalizer();
        var area = CreateArea(11);
        var essence = CreateEntity(
            613,
            "Metadata/MiscellaneousObjects/Monolith",
            "Essence",
            AreaEntityCategory.Other,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster,
            gridPosition: new Vector2(100, 100));
        var availableEvidence = new Dictionary<uint, IReadOnlyList<AreaContentEvidence>>
        {
            [essence.EntityId] = [new("MinimapIcon", "IsComplete", "false", 0.95f)]
        };
        var player = CreatePlayer(new Vector2(100, 100));
        normalizer.Normalize(area, [essence], [], availableEvidence, player: player);
        normalizer.Normalize(area, [], [], player: player);
        normalizer.Normalize(area, [], [], player: player);
        var completed = Assert.Single(normalizer.Normalize(area, [], [], player: player));
        Assert.Equal(AreaContentPhase.Completed, completed.Phase);

        var reappeared = Assert.Single(normalizer.Normalize(
            area,
            [essence],
            [],
            availableEvidence,
            player: player));
        Assert.Equal(AreaContentPhase.Completed, reappeared.Phase);
        AssertCompletionEvidence(reappeared, "0");

        var missingAgain = Assert.Single(normalizer.Normalize(area, [], [], player: player));
        Assert.Equal(AreaContentPhase.Completed, missingAgain.Phase);
        AssertCompletionEvidence(missingAgain, "0");
    }

    [Theory]
    [InlineData(AreaContentKind.Abyss, "Metadata/MiscellaneousObjects/Abyss/AbyssFinalNodeBase", AreaEntityCategory.Object)]
    [InlineData(AreaContentKind.Ritual, "Metadata/Terrain/Leagues/Ritual/RitualRuneObject", AreaEntityCategory.Other)]
    [InlineData(AreaContentKind.Breach, "Metadata/MiscellaneousObjects/Brequel/BrequelInitiator", AreaEntityCategory.Object)]
    [InlineData(AreaContentKind.Essence, "Metadata/MiscellaneousObjects/Monolith", AreaEntityCategory.Other)]
    public void Normalize_PreservesObservedStaticMechanicWhenEntityTemporarilyUnloads(
        AreaContentKind expectedKind,
        string metadata,
        AreaEntityCategory category)
    {
        var normalizer = CreateNormalizer();
        var area = CreateArea(11);
        var entity = CreateEntity(
            501,
            metadata,
            expectedKind.ToString(),
            category,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster,
            gridPosition: new Vector2(123, 456));
        var observed = Assert.Single(
            normalizer.Normalize(area, [entity], []),
            item => item.Kind == expectedKind);

        var retained = Assert.Single(
            normalizer.Normalize(area, [], []),
            item => item.Kind == expectedKind);

        Assert.Equal(observed.InstanceId, retained.InstanceId);
        Assert.Equal(observed.GridPosition, retained.GridPosition);
        Assert.Equal(AreaContentPhase.Available, retained.Phase);
        Assert.Contains(
            retained.Evidence,
            evidence => evidence.Source == "EntityLifecycle"
                        && evidence.Key == "MissingFromSample"
                        && evidence.Value == "true");
        Assert.DoesNotContain(
            normalizer.Normalize(CreateArea(12), [], []),
            item => item.Kind == expectedKind);
    }

    [Fact]
    public void Normalize_DoesNotPreserveStrongboxWhenEntityUnloads()
    {
        var normalizer = CreateNormalizer();
        var area = CreateArea(11);
        var strongbox = CreateEntity(
            501,
            "Metadata/Chests/StrongBoxes/Strongbox1",
            "Strongbox",
            AreaEntityCategory.Chest,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.Rare,
            chestState: AreaChestState.Closed);
        Assert.Contains(
            normalizer.Normalize(area, [strongbox], []),
            item => item.Kind == AreaContentKind.Strongbox);

        Assert.DoesNotContain(
            normalizer.Normalize(area, [], []),
            item => item.Kind == AreaContentKind.Strongbox);
    }

    [Fact]
    public void Normalize_DropsRetainedStaticMechanicWhenEntityRemainsButNoLongerMatches()
    {
        var normalizer = CreateNormalizer();
        var area = CreateArea(11);
        var abyss = CreateEntity(
            501,
            "Metadata/MiscellaneousObjects/Abyss/AbyssFinalNodeBase",
            "Abyss",
            AreaEntityCategory.Object,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster);
        var transformed = CreateEntity(
            501,
            "Metadata/MiscellaneousObjects/Abyss/CompletedNode",
            "Completed Abyss",
            AreaEntityCategory.Object,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster);
        Assert.Contains(
            normalizer.Normalize(area, [abyss], []),
            item => item.Kind == AreaContentKind.Abyss);

        Assert.DoesNotContain(
            normalizer.Normalize(area, [transformed], []),
            item => item.Kind == AreaContentKind.Abyss);
        Assert.DoesNotContain(
            normalizer.Normalize(area, [], []),
            item => item.Kind == AreaContentKind.Abyss);
    }

    [Fact]
    public void Normalize_EmbeddedCatalog_EmitsOnlyFinalNodeAsAbyssContent()
    {
        var finalNode = CreateEntity(
            501,
            "Metadata/MiscellaneousObjects/Abyss/AbyssFinalNodeBase",
            "Abyss",
            AreaEntityCategory.Object,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster,
            hasMinimapIcon: true);
        var cracks = Enumerable.Range(0, 4)
            .Select(index => CreateEntity(
                (uint)(502 + index),
                $"Metadata/MiscellaneousObjects/Abyss/AbyssCrack{index}",
                "Abyss Crack",
                AreaEntityCategory.Object,
                AreaEntityDisposition.Neutral,
                AreaEntityRarity.NonMonster,
                hasMinimapIcon: true))
            .ToArray();

        var contents = new AreaContentNormalizer(AreaContentCatalog.LoadEmbedded()).Normalize(
            CreateArea(11),
            [finalNode, .. cracks],
            []);

        var abyss = Assert.Single(contents, item => item.Kind == AreaContentKind.Abyss);
        Assert.Equal(finalNode.EntityId, abyss.SourceEntityId);
    }

    [Fact]
    public void Normalize_EmitsAvailableIncursionFromWaygateLandmark()
    {
        var landmark = new AreaLandmarkSnapshot(
            "incursion-waygate",
            "神庙",
            "Metadata/Terrain/Leagues/Incursion/Tiles/Features/Waygates/WaygateDevice_01.tdt",
            AreaLandmarkKind.Incursion,
            new Vector2(80, 120),
            3);

        var content = Assert.Single(CreateNormalizer().Normalize(
            CreateArea(11),
            [],
            [landmark]));

        Assert.Equal(AreaContentKind.Incursion, content.Kind);
        Assert.Equal(AreaContentPhase.Available, content.Phase);
        Assert.Equal(landmark.GridPosition, content.GridPosition);
        Assert.Null(content.SourceEntityId);
        Assert.Equal("incursion:11:incursion-waygate", content.InstanceId);
    }

    [Fact]
    public void Normalize_AttachesTypedDetailsOnlyToMatchingExpedition()
    {
        var expedition = CreateExpedition();
        var boss = CreateBoss();
        var details = new Dictionary<uint, AreaExpeditionDetails>
        {
            [expedition.EntityId] = new(6),
            [boss.EntityId] = new(4)
        };

        var contents = CreateNormalizer().Normalize(
            CreateArea(11),
            [boss, expedition],
            [],
            expeditionDetailsByEntity: details);

        Assert.Equal(
            6,
            Assert.Single(contents, item => item.Kind == AreaContentKind.Expedition)
                .ExpeditionDetails!.HoleCount);
        Assert.Null(
            Assert.Single(contents, item => item.Kind == AreaContentKind.Boss)
                .ExpeditionDetails);
    }

    [Fact]
    public void Normalize_PreservesMatchedExpeditionWithUnknownHoleCount()
    {
        var expedition = CreateExpedition();
        var details = new Dictionary<uint, AreaExpeditionDetails>
        {
            [expedition.EntityId] = new(null)
        };

        var content = Assert.Single(CreateNormalizer().Normalize(
            CreateArea(11),
            [expedition],
            [],
            expeditionDetailsByEntity: details));

        Assert.NotNull(content.ExpeditionDetails);
        Assert.Null(content.ExpeditionDetails!.HoleCount);
    }

    [Fact]
    public void Normalize_AppliesExpeditionBeforeBossRegardlessOfEntityOrder()
    {
        var normalizer = CreateNormalizer();
        var entities = new[] { CreateBoss(), CreateExpedition() };

        var contents = normalizer.Normalize(CreateArea(11), entities, []);

        Assert.Collection(
            contents,
            content => Assert.Equal(AreaContentKind.Expedition, content.Kind),
            content => Assert.Equal(AreaContentKind.Boss, content.Kind));
        Assert.Equal(2, entities.Length);
    }

    [Fact]
    public void Normalize_EmitsUnknownContentWhenOnlyUnclassifiedGameplayEvidenceExists()
    {
        var normalizer = CreateNormalizer();
        var entity = CreateUnknown(hasMinimapIcon: false);
        var raw = new AreaContentEvidence(
            "StateMachine",
            "ListenerVec+0x20[0]",
            "0x00000005",
            0.35f);
        var evidence = new Dictionary<uint, IReadOnlyList<AreaContentEvidence>>
        {
            [entity.EntityId] = [raw]
        };

        var content = Assert.Single(normalizer.Normalize(
            CreateArea(11),
            [entity],
            [],
            evidence));

        Assert.Equal(AreaContentKind.Unknown, content.Kind);
        Assert.Equal(AreaContentPhase.Unknown, content.Phase);
        Assert.Equal("unknown:11:99", content.InstanceId);
        Assert.Contains(raw, content.Evidence);
    }

    [Fact]
    public void Normalize_DoesNotApplyMinimapCompletionAsAGenericMechanicRule()
    {
        var normalizer = CreateNormalizer();

        var content = Assert.Single(normalizer.Normalize(
            CreateArea(11),
            [CreateUnknown(hasMinimapIcon: true, completed: true)],
            []));

        Assert.Equal(AreaContentKind.Unknown, content.Kind);
        Assert.Equal(AreaContentPhase.Unknown, content.Phase);
        Assert.Contains(
            content.Evidence,
            evidence => evidence.Source == "MinimapIcon"
                        && evidence.Key == "Completed"
                        && evidence.Value == "true");
    }

    [Fact]
    public void Normalize_EmitsUnknownForUncataloguedHostileUniqueWithMinimapIcon()
    {
        var normalizer = CreateNormalizer();
        var entity = CreateEntity(
            1165,
            "Metadata/Monsters/RogueExiles/Dex/ExileRanger2",
            "Rogue Exile",
            AreaEntityCategory.Monster,
            AreaEntityDisposition.Hostile,
            AreaEntityRarity.Unique,
            currentLife: 0,
            maximumLife: 100,
            hasMinimapIcon: true);

        var content = Assert.Single(normalizer.Normalize(
            CreateArea(11),
            [entity],
            []));

        Assert.Equal(AreaContentKind.Unknown, content.Kind);
        Assert.Equal(entity.EntityId, content.SourceEntityId);
        Assert.Contains(
            content.Evidence,
            evidence => evidence.Source == "MinimapIcon"
                        && evidence.Key == "Present"
                        && evidence.Value == "true");
    }

    [Fact]
    public void Normalize_PreservesAConfirmedBossAsCompletedWhenItDisappears()
    {
        var normalizer = CreateNormalizer();
        var area = CreateArea(11);
        Assert.Equal(
            AreaContentPhase.Available,
            Assert.Single(normalizer.Normalize(area, [CreateBoss()], [])).Phase);

        var missing = Assert.Single(normalizer.Normalize(area, [], []));

        Assert.Equal(AreaContentKind.Boss, missing.Kind);
        Assert.Equal(AreaContentPhase.Completed, missing.Phase);
        Assert.Empty(normalizer.Normalize(CreateArea(12), [], []));
    }

    [Fact]
    public void Normalize_EmitsBossCandidateFromStaticLandmarkWhenEntityIsNotLoaded()
    {
        var normalizer = CreateNormalizer();
        var landmark = new AreaLandmarkSnapshot(
            "boss-arena",
            "Map Boss",
            "Metadata/Terrain/Test/BossArena.tdt",
            AreaLandmarkKind.BossArena,
            new Vector2(100, 200),
            1);

        var content = Assert.Single(normalizer.Normalize(
            CreateArea(11),
            [],
            [landmark]));

        Assert.Equal(AreaContentKind.BossCandidate, content.Kind);
        Assert.Equal(AreaContentPhase.Unknown, content.Phase);
        Assert.Equal(new Vector2(100, 200), content.GridPosition);
        Assert.Equal(new Vector3(100 * (250f / 23f), 200 * (250f / 23f), 0), content.WorldPosition);
        Assert.Null(content.SourceEntityId);
        Assert.Contains(
            content.Evidence,
            evidence => evidence.Source == "Landmark"
                        && evidence.Key == "TilePath"
                        && evidence.Value == landmark.TilePath);
    }

    [Fact]
    public void Normalize_DoesNotEmitBossCandidateFromUnscopedBossHint()
    {
        var hint = CreateLandmark(
            "boss-hint",
            AreaLandmarkKind.BossHint,
            new Vector2(50, 60));

        Assert.Empty(CreateNormalizer().Normalize(CreateArea(11), [], [hint]));
    }

    [Fact]
    public void Normalize_EmitsOnlyTrustedCandidateWhenArenaAndHintCoexist()
    {
        var arena = CreateLandmark(
            "boss-arena",
            AreaLandmarkKind.BossArena,
            new Vector2(100, 200));
        var hint = CreateLandmark(
            "boss-hint",
            AreaLandmarkKind.BossHint,
            new Vector2(50, 60));

        var content = Assert.Single(CreateNormalizer().Normalize(
            CreateArea(11),
            [],
            [arena, hint]));

        Assert.Equal(AreaContentKind.BossCandidate, content.Kind);
        Assert.Equal(arena.GridPosition, content.GridPosition);
    }

    [Fact]
    public void Normalize_EmitsBossAndCandidateForDifferentTrustedArenas()
    {
        var normalizer = CreateNormalizer();
        var first = CreateLandmark(
            "boss-arena-left",
            AreaLandmarkKind.BossArena,
            new Vector2(10, 10));
        var second = CreateLandmark(
            "boss-arena-right",
            AreaLandmarkKind.BossArena,
            new Vector2(200, 200));

        var contents = normalizer.Normalize(
            CreateArea(11),
            [CreateBoss(new Vector2(10, 10))],
            [first, second]);

        var boss = Assert.Single(contents, item => item.Kind == AreaContentKind.Boss);
        var candidate = Assert.Single(
            contents,
            item => item.Kind == AreaContentKind.BossCandidate);
        Assert.Equal(second.GridPosition, candidate.GridPosition);
        Assert.Contains(
            boss.Evidence,
            evidence => evidence.Source == "Landmark"
                        && evidence.Key == "Id"
                        && evidence.Value == first.LandmarkId);
    }

    [Fact]
    public void Normalize_DoesNotDuplicateCandidateForAssociatedBossArena()
    {
        var arena = CreateLandmark(
            "boss-arena",
            AreaLandmarkKind.BossArena,
            new Vector2(10, 10));

        var content = Assert.Single(CreateNormalizer().Normalize(
            CreateArea(11),
            [CreateBoss(new Vector2(10, 10))],
            [arena]));

        Assert.Equal(AreaContentKind.Boss, content.Kind);
    }

    [Fact]
    public void Normalize_PreservesCompletedBossAndOtherArenaCandidate()
    {
        var normalizer = CreateNormalizer();
        var area = CreateArea(11);
        var first = CreateLandmark(
            "boss-arena-left",
            AreaLandmarkKind.BossArena,
            new Vector2(10, 10));
        var second = CreateLandmark(
            "boss-arena-right",
            AreaLandmarkKind.BossArena,
            new Vector2(200, 200));
        Assert.Contains(
            normalizer.Normalize(
                area,
                [CreateBoss(new Vector2(10, 10))],
                [first, second]),
            item => item.Kind == AreaContentKind.Boss);

        var contents = normalizer.Normalize(area, [], [first, second]);

        var completed = Assert.Single(
            contents,
            item => item.Kind == AreaContentKind.Boss);
        Assert.Equal(AreaContentPhase.Completed, completed.Phase);
        Assert.Contains(
            completed.Evidence,
            evidence => evidence.Source == "Landmark"
                        && evidence.Key == "Id"
                        && evidence.Value == first.LandmarkId);
        var candidate = Assert.Single(
            contents,
            item => item.Kind == AreaContentKind.BossCandidate);
        Assert.Equal(second.GridPosition, candidate.GridPosition);
    }

    private static AreaContentNormalizer CreateNormalizer()
        => new(AreaContentCatalog.FromJson("""
            {
              "expedition": {
                "exactMetadata": [
                  "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter"
                ]
              },
              "mechanics": {
                "abyss": {
                  "metadataFragments": ["Metadata/MiscellaneousObjects/Abyss/AbyssFinalNodeBase"]
                },
                "ritual": {
                  "metadataFragments": ["Metadata/Terrain/Leagues/Ritual/RitualRuneObject"]
                },
                "breach": {
                  "metadataFragments": ["Metadata/MiscellaneousObjects/Brequel/BrequelInitiator"]
                },
                "essence": {
                  "metadataFragments": ["Metadata/MiscellaneousObjects/Monolith"]
                },
                "strongbox": { "metadataFragments": ["/StrongBoxes/"] }
              },
              "bosses": {
                "exactMetadata": ["Metadata/Monsters/Test/ConfirmedBoss"],
                "metadataPrefixes": [],
                "displayNames": [],
                "bossTilePatternsByArea": {}
              }
            }
            """));

    private static AreaIdentity CreateArea(long sequence)
        => new((uint)sequence, "TestArea", 80, sequence);

    private static AreaEntitySnapshot CreateBoss(Vector2? gridPosition = null)
        => CreateEntity(
            42,
            "Metadata/Monsters/Test/ConfirmedBoss",
            "Test Tyrant",
            AreaEntityCategory.Monster,
            AreaEntityDisposition.Hostile,
            AreaEntityRarity.Unique,
            currentLife: 100,
            maximumLife: 100,
            hasMinimapIcon: true,
            gridPosition: gridPosition);

    private static AreaLandmarkSnapshot CreateLandmark(
        string landmarkId,
        AreaLandmarkKind kind,
        Vector2 gridPosition)
        => new(
            landmarkId,
            "Map Boss",
            $"Metadata/Terrain/Test/{landmarkId}.tdt",
            kind,
            gridPosition,
            1);

    private static AreaEntitySnapshot CreateExpedition()
        => CreateEntity(
            73,
            "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter",
            "Expedition Encounter",
            AreaEntityCategory.Object,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster,
            hasMinimapIcon: true);

    private static AreaEntitySnapshot CreateUnknown(
        bool hasMinimapIcon,
        bool completed = false)
        => CreateEntity(
            99,
            "Metadata/MiscellaneousObjects/UnknownLeague/Device",
            "Unknown Device",
            AreaEntityCategory.Object,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster,
            hasMinimapIcon: hasMinimapIcon,
            completed: completed);

    private static AreaPlayerSnapshot CreatePlayer(Vector2 gridPosition)
        => new(
            "TestPlayer",
            1,
            new Vector3(gridPosition.X, gridPosition.Y, 0f),
            gridPosition);

    private static bool IsEssenceCompletionEvidence(AreaContentEvidence evidence)
        => evidence.Source == "EntityLifecycle"
           && evidence.Key is "ConfirmedMissingNearPlayer"
               or "MissingSampleCount"
               or "PlayerDistance";

    private static void AssertCompletionEvidence(
        AreaContentSnapshot content,
        string expectedDistance)
    {
        foreach (var key in new[]
                 {
                     "ConfirmedMissingNearPlayer",
                     "MissingSampleCount",
                     "PlayerDistance"
                 })
        {
            Assert.Single(content.Evidence, item =>
                item.Source == "EntityLifecycle" && item.Key == key);
        }

        Assert.Equal(
            "3",
            Assert.Single(content.Evidence, item =>
                item.Source == "EntityLifecycle" && item.Key == "MissingSampleCount").Value);
        Assert.Equal(
            expectedDistance,
            Assert.Single(content.Evidence, item =>
                item.Source == "EntityLifecycle" && item.Key == "PlayerDistance").Value);
    }

    private static AreaEntitySnapshot CreateEntity(
        uint entityId,
        string metadata,
        string displayName,
        AreaEntityCategory category,
        AreaEntityDisposition disposition,
        AreaEntityRarity rarity,
        int currentLife = 0,
        int maximumLife = 0,
        bool hasMinimapIcon = false,
        bool completed = false,
        Vector2? gridPosition = null,
        AreaChestState chestState = AreaChestState.NotApplicable)
        => new(
            entityId,
            metadata,
            displayName,
            category,
            Vector3.Zero,
            gridPosition ?? Vector2.Zero,
            disposition,
            rarity,
            currentLife,
            maximumLife,
            hasMinimapIcon,
            completed,
            chestState,
            []);
}
