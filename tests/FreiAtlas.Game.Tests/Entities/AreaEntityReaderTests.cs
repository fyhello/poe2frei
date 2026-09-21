using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Game.Entities;
using FreiAtlas.Game.Tests.Memory;

namespace FreiAtlas.Game.Tests.Entities;

public sealed class AreaEntityReaderTests
{
    [Fact]
    public void Read_ProjectsRawMonsterFactsWithoutGuessingBossState()
    {
        var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var render = builder.WriteRender(new Vector3(250, 500, 17));
        var positioned = builder.WritePositioned(0x80);
        var life = builder.WriteLife(current: 321, maximum: 900);
        var magic = builder.WriteMagicProperties(3, "MonsterFast", "MonsterFireDamage");
        var icon = builder.WriteMinimapIcon(completedState: 4);
        builder.WriteEntity(
            0x400000,
            "Metadata/Monsters/Test/TestTyrant",
            ("Render", render),
            ("Positioned", positioned),
            ("Life", life),
            ("ObjectMagicProperties", magic),
            ("MinimapIcon", icon));
        var catalog = EntityNameCatalog.FromEntries(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Metadata/Monsters/Test/TestTyrant"] = "Test Tyrant"
            });
        var reader = new AreaEntityReader(
            memory,
            new EntityComponentResolver(memory),
            catalog);

        var result = reader.Read(5, [new RawEntityRef(42, 0x400000)]);

        var entity = Assert.Single(result.Entities);
        Assert.Equal("Test Tyrant", entity.DisplayName);
        Assert.Equal(AreaEntityCategory.Monster, entity.Category);
        Assert.Equal(AreaEntityDisposition.Hostile, entity.Disposition);
        Assert.Equal(AreaEntityRarity.Unique, entity.Rarity);
        Assert.Equal(321, entity.CurrentLife);
        Assert.Equal(900, entity.MaximumLife);
        Assert.Equal(new Vector3(250, 500, 17), entity.WorldPosition);
        Assert.Equal(new Vector2(23, 46), entity.GridPosition);
        Assert.True(entity.HasMinimapIcon);
        Assert.True(entity.IsMinimapIconComplete);
        Assert.Equal(["MonsterFast", "MonsterFireDamage"], entity.ModIds);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Read_ClassifiesKnownKindsAndPreservesUnknownMetadata()
    {
        var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var refs = new List<RawEntityRef>();

        refs.Add(WriteMinimal(builder, 1, 0x401000, "Metadata/Characters/Int/IntPlayer"));
        refs.Add(WriteMinimal(builder, 2, 0x402000, "Metadata/Monsters/NPC/TestVendor"));

        var closedChest = builder.WriteChest(0);
        var rareChestMagic = builder.WriteMagicProperties(2);
        refs.Add(WriteMinimal(
            builder,
            3,
            0x403000,
            "Metadata/Chests/TestChest",
            ("Chest", closedChest),
            ("ObjectMagicProperties", rareChestMagic)));

        refs.Add(WriteMinimal(builder, 4, 0x404000, "Metadata/MiscellaneousObjects/AreaTransition"));
        refs.Add(WriteMinimal(builder, 5, 0x405000, "Metadata/Terrain/Test/Object"));
        refs.Add(WriteMinimal(builder, 6, 0x406000, "Metadata/Unknown/UncataloguedThing"));

        var reader = new AreaEntityReader(
            memory,
            new EntityComponentResolver(memory),
            EntityNameCatalog.FromEntries([]));

        var result = reader.Read(1, refs);

        Assert.Equal(
            [
                AreaEntityCategory.Player,
                AreaEntityCategory.Npc,
                AreaEntityCategory.Chest,
                AreaEntityCategory.Transition,
                AreaEntityCategory.Object,
                AreaEntityCategory.Other
            ],
            result.Entities.Select(entity => entity.Category));
        Assert.Equal(AreaChestState.Closed, result.Entities[2].ChestState);
        Assert.Equal(AreaEntityRarity.Rare, result.Entities[2].Rarity);
        Assert.Equal("UncataloguedThing", result.Entities[5].DisplayName);
    }

    [Fact]
    public void Read_ClassifiesNonCombatCarriersAndBreakablePropsAsOther()
    {
        var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var summoned = WriteMinimal(
            builder,
            11,
            0x407000,
            "Metadata/Monsters/Summoned/TestMinion");
        var breakable = WriteMinimal(
            builder,
            12,
            0x408000,
            "Metadata/Chests/Environment/TestPot");
        var reader = new AreaEntityReader(
            memory,
            new EntityComponentResolver(memory),
            EntityNameCatalog.FromEntries([]));

        var result = reader.Read(1, [summoned, breakable]);

        Assert.All(
            result.Entities,
            entity => Assert.Equal(AreaEntityCategory.Other, entity.Category));
    }

    [Fact]
    public void Read_RecordsFailedEntityAndContinuesWithOtherEntities()
    {
        var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var badRender = builder.WriteRender(new Vector3(1, 2, 3));
        builder.WriteEntity(
            0x410000,
            "Metadata/Unknown/Broken",
            ("Render", badRender));
        memory.FailRange(badRender + 0x138, 12);
        var good = WriteMinimal(builder, 8, 0x420000, "Metadata/Unknown/Good");
        var reader = new AreaEntityReader(
            memory,
            new EntityComponentResolver(memory),
            EntityNameCatalog.FromEntries([]));

        var result = reader.Read(
            1,
            [new RawEntityRef(7, 0x410000), good]);

        Assert.Equal(8u, Assert.Single(result.Entities).EntityId);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("entity-read-failed", diagnostic.Code);
        Assert.Equal(7u, diagnostic.EntityId);
    }

    [Fact]
    public void Read_RetainsEntityWhenOptionalLifeFieldCannotBeRead()
    {
        var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var render = builder.WriteRender(new Vector3(10, 20, 0));
        var life = builder.WriteLife(current: 12, maximum: 34);
        builder.WriteEntity(
            0x425000,
            "Metadata/Monsters/Test/TransientLife",
            ("Render", render),
            ("Life", life));
        memory.FailRange(life + 0x1B0 + 0x2C, sizeof(int));
        var reader = new AreaEntityReader(
            memory,
            new EntityComponentResolver(memory),
            EntityNameCatalog.FromEntries([]));

        var result = reader.Read(1, [new RawEntityRef(81, 0x425000)]);

        var entity = Assert.Single(result.Entities);
        Assert.Equal(0, entity.CurrentLife);
        Assert.Equal(0, entity.MaximumLife);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Read_ReportsUnpositionedControlEntityAsInformationalSkip()
    {
        var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var positioned = builder.WritePositioned(0);
        builder.WriteEntity(
            0x426000,
            "Metadata/MiscellaneousObjects/BossLeaguePrecursorBeacon",
            ("Positioned", positioned));
        var reader = new AreaEntityReader(
            memory,
            new EntityComponentResolver(memory),
            EntityNameCatalog.FromEntries([]));

        var result = reader.Read(1, [new RawEntityRef(85, 0x426000)]);

        Assert.Empty(result.Entities);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("entity-unpositioned", diagnostic.Code);
        Assert.Equal(AreaDiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Equal(85u, diagnostic.EntityId);
    }

    [Fact]
    public void Read_CollapsesSharedCoreComponentWrappersIntoOneLogicalEntity()
    {
        var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var render = builder.WriteRender(new Vector3(120, 240, 0));
        var life = builder.WriteLife(current: 500, maximum: 500);
        var magic = builder.WriteMagicProperties(3, "MonsterBoss");
        var firstPositioned = builder.WritePositioned(0);
        var duplicatePositioned = builder.WritePositioned(0);
        const string metadata = "Metadata/Monsters/Rugosa/Syvora";
        builder.WriteEntity(
            0x426100,
            metadata,
            ("Render", render),
            ("Positioned", firstPositioned),
            ("Life", life),
            ("ObjectMagicProperties", magic));
        builder.WriteEntity(
            0x426200,
            metadata,
            ("Render", render),
            ("Positioned", duplicatePositioned),
            ("Life", life),
            ("ObjectMagicProperties", magic));
        var reader = new AreaEntityReader(
            memory,
            new EntityComponentResolver(memory),
            EntityNameCatalog.FromEntries([]));

        var result = reader.Read(
            1,
            [new RawEntityRef(826, 0x426100), new RawEntityRef(827, 0x426200)]);

        Assert.Multiple(
            () => Assert.Equal(826u, Assert.Single(result.Entities).EntityId),
            () =>
            {
                var diagnostic = Assert.Single(result.Diagnostics);
                Assert.Equal("entity-logical-duplicate", diagnostic.Code);
                Assert.Equal(AreaDiagnosticSeverity.Info, diagnostic.Severity);
                Assert.Equal(827u, diagnostic.EntityId);
            });
    }

    [Fact]
    public void Read_PreservesDistinctEntitiesWhenOnlySomeIdentityEvidenceMatches()
    {
        var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var sharedRender = builder.WriteRender(new Vector3(120, 240, 0));
        var firstLife = builder.WriteLife(current: 500, maximum: 500);
        var secondLife = builder.WriteLife(current: 500, maximum: 500);
        var firstMagic = builder.WriteMagicProperties(3, "MonsterBoss");
        var secondMagic = builder.WriteMagicProperties(3, "MonsterBoss");
        const string metadata = "Metadata/Monsters/Rugosa/Syvora";
        builder.WriteEntity(
            0x426300,
            metadata,
            ("Render", sharedRender),
            ("Life", firstLife),
            ("ObjectMagicProperties", firstMagic));
        builder.WriteEntity(
            0x426400,
            metadata,
            ("Render", sharedRender),
            ("Life", secondLife),
            ("ObjectMagicProperties", secondMagic));
        var reader = new AreaEntityReader(
            memory,
            new EntityComponentResolver(memory),
            EntityNameCatalog.FromEntries([]));

        var result = reader.Read(
            1,
            [new RawEntityRef(828, 0x426300), new RawEntityRef(829, 0x426400)]);

        Assert.Equal([828u, 829u], result.Entities.Select(entity => entity.EntityId));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Read_LimitsModifierVectorTo128Entries()
    {
        var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var render = builder.WriteRender(Vector3.One);
        var magic = builder.WriteMagicProperties(
            2,
            Enumerable.Range(0, 129).Select(index => $"MonsterMod{index}").ToArray());
        builder.WriteEntity(
            0x430000,
            "Metadata/Monsters/Test/TooManyMods",
            ("Render", render),
            ("ObjectMagicProperties", magic));
        var reader = new AreaEntityReader(
            memory,
            new EntityComponentResolver(memory),
            EntityNameCatalog.FromEntries([]));

        var result = reader.Read(1, [new RawEntityRef(9, 0x430000)]);

        var entity = Assert.Single(result.Entities);
        Assert.Empty(entity.ModIds);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void EmbeddedCatalog_ContainsRadarEntityNames()
    {
        var catalog = EntityNameCatalog.LoadEmbedded();

        Assert.True(catalog.IsAvailable);
        Assert.True(catalog.Count > 3000);
        Assert.Equal(
            "Lightning Wraith",
            catalog.ResolveOrShorten("Metadata/Monsters/Wraith/WraithSpookyLightning"));
    }

    [Fact]
    public void Read_ReportsUnavailableNameCatalogButUsesMetadataShortName()
    {
        var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var entity = WriteMinimal(builder, 10, 0x440000, "Metadata/Unknown/FallbackName");
        var reader = new AreaEntityReader(
            memory,
            new EntityComponentResolver(memory),
            EntityNameCatalog.Unavailable());

        var result = reader.Read(1, [entity]);

        Assert.Equal("FallbackName", Assert.Single(result.Entities).DisplayName);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "entity-name-catalog-unavailable");
    }

    private static RawEntityRef WriteMinimal(
        EntityMemoryBuilder builder,
        uint id,
        nint address,
        string metadata,
        params (string Name, nint Address)[] extraComponents)
    {
        var render = builder.WriteRender(new Vector3(id * 10, id * 20, 0));
        builder.WriteEntity(
            address,
            metadata,
            [("Render", render), .. extraComponents]);
        return new RawEntityRef(id, address);
    }
}
