using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Game.Content;

namespace FreiAtlas.Game.Tests.Content;

public sealed class AreaContentCatalogTests
{
    [Theory]
    [InlineData("Metadata/MiscellaneousObjects/Abyss/AbyssFinalNodeBase", AreaEntityCategory.Object, AreaChestState.NotApplicable, AreaContentKind.Abyss)]
    [InlineData("Metadata/Terrain/Leagues/Ritual/RitualRuneObject", AreaEntityCategory.Other, AreaChestState.NotApplicable, AreaContentKind.Ritual)]
    [InlineData("Metadata/MiscellaneousObjects/Brequel/BrequelInitiator", AreaEntityCategory.Object, AreaChestState.NotApplicable, AreaContentKind.Breach)]
    [InlineData("Metadata/MiscellaneousObjects/Monolith", AreaEntityCategory.Other, AreaChestState.NotApplicable, AreaContentKind.Essence)]
    [InlineData("Metadata/Chests/StrongBoxes/Strongbox1", AreaEntityCategory.Chest, AreaChestState.Closed, AreaContentKind.Strongbox)]
    public void MechanicCatalog_MatchesApprovedEntityRules(
        string metadata,
        AreaEntityCategory category,
        AreaChestState chestState,
        AreaContentKind expected)
    {
        var catalog = AreaContentCatalog.FromJson(CatalogJson);

        var matched = catalog.TryMatchMechanicEntity(
            CreateEntity(metadata, category: category, chestState: chestState),
            out var kind,
            out var evidence);

        Assert.True(matched);
        Assert.Equal(expected, kind);
        Assert.Equal("MetadataFragment", evidence.Key);
    }

    [Theory]
    [InlineData("Metadata/Terrain/Leagues/Ritual/RitualRuneInteractable", AreaEntityCategory.Other, AreaChestState.NotApplicable)]
    [InlineData("Metadata/Terrain/Leagues/Ritual/RitualRuneLight", AreaEntityCategory.Other, AreaChestState.NotApplicable)]
    [InlineData("Metadata/Monsters/LeagueRitual/RitualMonster", AreaEntityCategory.Monster, AreaChestState.NotApplicable)]
    [InlineData("Metadata/MiscellaneousObjects/Abyss/AbyssCrack", AreaEntityCategory.Object, AreaChestState.NotApplicable)]
    [InlineData("Metadata/Effects/Breach/BreachEffect", AreaEntityCategory.Other, AreaChestState.NotApplicable)]
    [InlineData("Metadata/Monsters/Daemon/EssenceModDaemons/EssenceDaemon", AreaEntityCategory.Other, AreaChestState.NotApplicable)]
    [InlineData("Metadata/Chests/StrongBoxes/Strongbox1", AreaEntityCategory.Chest, AreaChestState.Opened)]
    [InlineData("Metadata/Chests/StrongBoxes/Strongbox1", AreaEntityCategory.Chest, AreaChestState.Unknown)]
    public void MechanicCatalog_RejectsExcludedOrInactiveEntities(
        string metadata,
        AreaEntityCategory category,
        AreaChestState chestState)
    {
        var catalog = AreaContentCatalog.FromJson(CatalogJson);

        Assert.False(catalog.TryMatchMechanicEntity(
            CreateEntity(metadata, category: category, chestState: chestState),
            out _,
            out _));
    }

    [Theory]
    [InlineData("Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter", true)]
    [InlineData("metadata/miscellaneousobjects/expedition2/expedition2encounter", true)]
    [InlineData("Metadata/Monsters/LeagueExpedition/ExpeditionMonster", false)]
    [InlineData("Metadata/Effects/Expedition/Expedition2EncounterCrack", false)]
    [InlineData("Metadata/MiscellaneousObjects/Expedition2/Expedition2EncounterCrack", false)]
    public void EmbeddedCatalog_MatchesOnlyTheExpeditionMainDevice(
        string metadata,
        bool expected)
    {
        var catalog = AreaContentCatalog.LoadEmbedded();

        Assert.True(catalog.IsAvailable);
        Assert.Equal(expected, catalog.IsExpedition(CreateEntity(metadata: metadata)));
    }

    [Theory]
    [InlineData("Metadata/Terrain/Leagues/Ritual/RitualRuneObject", AreaEntityCategory.Other, true)]
    [InlineData("Metadata/Terrain/Leagues/Ritual/RitualRuneInteractable", AreaEntityCategory.Other, false)]
    [InlineData("Metadata/Terrain/Leagues/Ritual/RitualRuneLight", AreaEntityCategory.Other, false)]
    [InlineData("Metadata/Monsters/LeagueRitual/RitualMonster", AreaEntityCategory.Monster, false)]
    public void EmbeddedCatalog_MatchesOnlyTheRitualAltarObject(
        string metadata,
        AreaEntityCategory category,
        bool expected)
    {
        var catalog = AreaContentCatalog.LoadEmbedded();

        Assert.True(catalog.IsAvailable);
        Assert.Equal(expected, catalog.TryMatchMechanicEntity(
            CreateEntity(metadata: metadata, category: category),
            out _,
            out _));
    }

    [Theory]
    [InlineData("Metadata/MiscellaneousObjects/Abyss/AbyssFinalNodeBase", AreaEntityCategory.Object, AreaContentKind.Abyss)]
    [InlineData("Metadata/MiscellaneousObjects/Brequel/BrequelInitiator", AreaEntityCategory.Object, AreaContentKind.Breach)]
    [InlineData("Metadata/MiscellaneousObjects/Monolith", AreaEntityCategory.Other, AreaContentKind.Essence)]
    public void EmbeddedCatalog_MatchesObservedLiveMechanicEntities(
        string metadata,
        AreaEntityCategory category,
        AreaContentKind expected)
    {
        var catalog = AreaContentCatalog.LoadEmbedded();

        Assert.True(catalog.IsAvailable);
        Assert.True(catalog.TryMatchMechanicEntity(
            CreateEntity(metadata: metadata, category: category),
            out var kind,
            out _));
        Assert.Equal(expected, kind);
    }

    [Fact]
    public void EmbeddedCatalog_DoesNotMatchAbyssCrackSegments()
    {
        var catalog = AreaContentCatalog.LoadEmbedded();

        Assert.True(catalog.IsAvailable);
        Assert.False(catalog.TryMatchMechanicEntity(
            CreateEntity(
                metadata: "Metadata/MiscellaneousObjects/Abyss/AbyssCrack",
                category: AreaEntityCategory.Object),
            out _,
            out _));
    }

    [Theory]
    [InlineData("metadata/monsters/test/exactboss", "Other", true)]
    [InlineData("Metadata/Monsters/Test/Prefix/BossVariant", "Other", true)]
    [InlineData("Metadata/Monsters/Test/Other", "the named tyrant", true)]
    [InlineData("Metadata/Monsters/Test/Other", "Other", false)]
    public void BossCatalog_MatchesConfiguredEntityRulesIgnoringCase(
        string metadata,
        string displayName,
        bool expected)
    {
        var catalog = AreaContentCatalog.FromJson(CatalogJson);

        var matched = catalog.TryMatchBossEntity(
            CreateEntity(metadata: metadata, displayName: displayName),
            out _);

        Assert.Equal(expected, matched);
    }

    [Fact]
    public void BossCatalog_MatchesAreaTilePatternsIgnoringCase()
    {
        var catalog = AreaContentCatalog.FromJson(CatalogJson);
        var area = new AreaIdentity(12, "test_area", 80, 3);
        var landmark = new AreaLandmarkSnapshot(
            "landmark",
            "Arena",
            "METADATA/TERRAIN/TEST/BOSSROOM_01.TDTX",
            AreaLandmarkKind.Unknown,
            Vector2.Zero,
            1);

        Assert.True(catalog.IsBossLandmark(area, landmark));
        Assert.False(catalog.IsBossLandmark(
            area with { AreaCode = "other_area" },
            landmark));
    }

    [Fact]
    public void BossCatalog_DoesNotPromoteBossHintThroughTilePattern()
    {
        var catalog = AreaContentCatalog.FromJson(CatalogJson);
        var area = new AreaIdentity(12, "test_area", 80, 3);
        var hint = new AreaLandmarkSnapshot(
            "boss-hint",
            "Arena",
            "Metadata/Terrain/Test/BossRoom_01.tdt",
            AreaLandmarkKind.BossHint,
            Vector2.Zero,
            1);

        Assert.False(catalog.IsBossLandmark(area, hint));
    }

    [Fact]
    public void EmbeddedCatalog_MatchesObservedMapPlantationBoss()
    {
        var catalog = AreaContentCatalog.LoadEmbedded();
        var entity = CreateEntity(
            metadata: "Metadata/Monsters/HusbandMonster/BloodKnightBossMAP2_@78",
            displayName: "Varloch, the Ashen Lord");

        Assert.True(catalog.TryMatchBossEntity(entity, out var evidence));
        Assert.Equal("MetadataPrefix", evidence.Key);
    }

    [Fact]
    public void EmbeddedCatalog_MatchesObservedMapRugosaBoss()
    {
        var catalog = AreaContentCatalog.LoadEmbedded();
        var entity = CreateEntity(
            metadata: "Metadata/Monsters/SirenMonster/SirenBossMAP_@79",
            displayName: "Syvora, Daughter of the Deep");

        Assert.True(catalog.TryMatchBossEntity(entity, out var evidence));
        Assert.Equal("MetadataPrefix", evidence.Key);
    }

    private const string CatalogJson = """
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
            "strongbox": {
              "metadataFragments": ["/StrongBoxes/"]
            }
          },
          "bosses": {
            "exactMetadata": ["Metadata/Monsters/Test/ExactBoss"],
            "metadataPrefixes": ["Metadata/Monsters/Test/Prefix/"],
            "displayNames": ["The Named Tyrant"],
            "bossTilePatternsByArea": {
              "Test_Area": ["Terrain/Test/BossRoom"]
            }
          }
        }
        """;

    private static AreaEntitySnapshot CreateEntity(
        string metadata,
        string displayName = "Test",
        AreaEntityCategory category = AreaEntityCategory.Other,
        AreaChestState chestState = AreaChestState.NotApplicable)
        => new(
            10,
            metadata,
            displayName,
            category,
            Vector3.Zero,
            Vector2.Zero,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster,
            0,
            0,
            false,
            false,
            chestState,
            []);
}
