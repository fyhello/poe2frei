using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Game.Entities;
using FreiAtlas.Game.Memory;
using FreiAtlas.Game.Tests.Entities;
using FreiAtlas.Game.Tests.Memory;
using FreiAtlas.Game.World;

namespace FreiAtlas.Game.Tests.World;

public sealed class AreaWorldReaderTests
{
    [Fact]
    public void Read_CombinesAreaPlayerAndEntityFacts()
    {
        var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var areaInstance = builder.Allocate(0xA00);
        var areaInfo = builder.Allocate();
        var areaCode = builder.Allocate(128);
        var localPlayer = (nint)0x400000;
        var render = builder.WriteRender(new Vector3(250, 500, 17));
        var player = builder.Allocate(0x240);
        memory.WritePointer(areaInstance + 0x098, areaInfo);
        memory.WritePointer(areaInfo, areaCode);
        memory.WriteUtf16Buffer(areaCode, "MapPlantation", 64);
        memory.WriteInt32(areaInstance + 0x0BC, 78);
        memory.WriteUInt32(areaInstance + 0x114, 0xAABBCCDD);
        memory.WriteStdWString(player + 0x1B0, "FreiTest");
        memory.WriteByte(player + 0x204, 91);
        builder.WriteEntity(
            localPlayer,
            "Metadata/Characters/Int/IntPlayer",
            ("Render", render),
            ("Player", player));
        var references = new[] { new RawEntityRef(7, localPlayer) };
        var components = new EntityComponentResolver(memory);
        var entities = new AreaEntityReader(
            memory,
            components,
            EntityNameCatalog.FromEntries([]));
        var reader = new AreaWorldReader(memory, components, entities);

        var result = reader.Read(
            new GameRootState(0, 0, 0, areaInstance, localPlayer),
            sessionSequence: 12,
            references);

        Assert.Equal(AreaMapSnapshotStatus.Stable, result.Status);
        var area = Assert.IsType<AreaIdentity>(result.Area);
        Assert.Equal("MapPlantation", area.AreaCode);
        Assert.Equal(78, area.AreaLevel);
        Assert.Equal(0xAABBCCDDu, area.AreaHash);
        Assert.Equal(12, area.SessionSequence);
        var snapshot = Assert.IsType<AreaPlayerSnapshot>(result.Player);
        Assert.Equal("FreiTest", snapshot.CharacterName);
        Assert.Equal(91, snapshot.Level);
        Assert.Equal(new Vector3(250, 500, 17), snapshot.WorldPosition);
        Assert.Equal(new Vector2(23, 46), snapshot.GridPosition);
        Assert.Equal(7u, Assert.Single(result.Entities).EntityId);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Read_SkipsUnpositionedControlEntityWithoutDegradingSnapshot()
    {
        var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var areaInstance = builder.Allocate(0xA00);
        var areaInfo = builder.Allocate();
        var areaCode = builder.Allocate(128);
        var localPlayer = (nint)0x415000;
        var playerRender = builder.WriteRender(new Vector3(10, 20, 0));
        var player = builder.Allocate(0x240);
        memory.WritePointer(areaInstance + 0x098, areaInfo);
        memory.WritePointer(areaInfo, areaCode);
        memory.WriteUtf16Buffer(areaCode, "MapTest", 64);
        memory.WriteInt32(areaInstance + 0x0BC, 80);
        memory.WriteUInt32(areaInstance + 0x114, 123);
        memory.WriteStdWString(player + 0x1B0, "Reader");
        memory.WriteByte(player + 0x204, 90);
        builder.WriteEntity(
            localPlayer,
            "Metadata/Characters/Int/IntPlayer",
            ("Render", playerRender),
            ("Player", player));

        var controlAddress = (nint)0x427000;
        builder.WriteEntity(
            controlAddress,
            "Metadata/MiscellaneousObjects/BossLeaguePrecursorBeacon",
            ("Positioned", builder.WritePositioned(0)));

        var references = new[]
        {
            new RawEntityRef(7, localPlayer),
            new RawEntityRef(85, controlAddress)
        };
        var components = new EntityComponentResolver(memory);
        var entities = new AreaEntityReader(
            memory,
            components,
            EntityNameCatalog.FromEntries([]));
        var reader = new AreaWorldReader(memory, components, entities);

        var result = reader.Read(
            new GameRootState(0, 0, 0, areaInstance, localPlayer),
            sessionSequence: 4,
            references);

        Assert.Equal(AreaMapSnapshotStatus.Stable, result.Status);
        Assert.Equal(7u, Assert.Single(result.Entities).EntityId);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("entity-unpositioned", diagnostic.Code);
        Assert.Equal(AreaDiagnosticSeverity.Info, diagnostic.Severity);
    }

    [Fact]
    public void Read_OmitsFailedEntityAndDegradesWithoutInterrupting()
    {
        var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var areaInstance = builder.Allocate(0xA00);
        var areaInfo = builder.Allocate();
        var areaCode = builder.Allocate(128);
        var localPlayer = (nint)0x410000;
        var playerRender = builder.WriteRender(new Vector3(10, 20, 0));
        var player = builder.Allocate(0x240);
        memory.WritePointer(areaInstance + 0x098, areaInfo);
        memory.WritePointer(areaInfo, areaCode);
        memory.WriteUtf16Buffer(areaCode, "MapTest", 64);
        memory.WriteInt32(areaInstance + 0x0BC, 80);
        memory.WriteUInt32(areaInstance + 0x114, 123);
        memory.WriteStdWString(player + 0x1B0, "Reader");
        memory.WriteByte(player + 0x204, 90);
        builder.WriteEntity(
            localPlayer,
            "Metadata/Characters/Int/IntPlayer",
            ("Render", playerRender),
            ("Player", player));

        var failedAddress = (nint)0x420000;
        var failedRender = builder.WriteRender(Vector3.One);
        builder.WriteEntity(
            failedAddress,
            "Metadata/Monsters/Test/Broken",
            ("Render", failedRender));
        memory.FailRange(failedRender + 0x138, 12);

        var references = new[]
        {
            new RawEntityRef(7, localPlayer),
            new RawEntityRef(8, failedAddress)
        };
        var components = new EntityComponentResolver(memory);
        var entities = new AreaEntityReader(
            memory,
            components,
            EntityNameCatalog.FromEntries([]));
        var reader = new AreaWorldReader(memory, components, entities);

        var result = reader.Read(
            new GameRootState(0, 0, 0, areaInstance, localPlayer),
            sessionSequence: 4,
            references);

        Assert.Equal(AreaMapSnapshotStatus.Degraded, result.Status);
        Assert.Equal(7u, Assert.Single(result.Entities).EntityId);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("entity-read-failed", diagnostic.Code);
        Assert.Equal(8u, diagnostic.EntityId);
    }
}
