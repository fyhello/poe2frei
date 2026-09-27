using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Game.Content;
using FreiAtlas.Game.Entities;
using FreiAtlas.Game.Tests.Entities;
using FreiAtlas.Game.Tests.Memory;

namespace FreiAtlas.Game.Tests.Content;

public sealed class PollenStateEvidenceReaderTests
{
    private const string PollenMetadata =
        "Metadata/MiscellaneousObjects/Azmeri/AzmeriResourceBase";
    private const string ModelRoot =
        "Metadata/Effects/Spells/monsters_effects/League_Azmeri/resources/wisp_doodads/";

    [Theory]
    [InlineData("wisp_warden_med.ao", AreaPollenKind.Wild)]
    [InlineData("wisp_vodoo_big.ao", AreaPollenKind.Soul)]
    [InlineData("wisp_voodoo_sml.ao", AreaPollenKind.Soul)]
    [InlineData("wisp_primal_med.ao", AreaPollenKind.Primal)]
    [InlineData("wisp_sacred_big.ao", AreaPollenKind.Sacred)]
    public void Read_MapsValidatedWispModelResourceToPollenKind(
        string modelFile,
        AreaPollenKind expected)
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var reader = new PollenStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory));
        var entity = WritePollenEntity(builder, memory, ModelRoot + modelFile);

        var result = reader.Read(new RawEntityRef(42, entity), CreateEntity(42));

        Assert.True(result.Matched);
        Assert.Equal(expected, result.Details.Kind);
        Assert.Contains(result.Evidence, item => item == new AreaContentEvidence(
            "Pollen",
            "ModelPath",
            ModelRoot + modelFile,
            1f));
        Assert.Contains(result.Evidence, item => item.Key == "ModelLayout"
            && item.Value == "animated+0x358/modelInfo+0x18/fileRecord+0x8"
            && item.Confidence == 0.9f);
        Assert.Contains(result.Evidence, item => item == new AreaContentEvidence(
            "Pollen",
            "ModelLayoutSource",
            "configured",
            0.9f));
    }

    [Fact]
    public void Read_UsesModelResourceRatherThanSharedLifespanSignature()
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var reader = new PollenStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory));
        var sharedLifespan = builder.Allocate(0x120);
        memory.WritePointer(sharedLifespan + 0x88, builder.Allocate());
        memory.WriteInt32(sharedLifespan + 0xA0, 0x101);
        var warden = WritePollenEntity(
            builder,
            memory,
            ModelRoot + "wisp_warden_med.ao",
            sharedLifespan);
        var primal = WritePollenEntity(
            builder,
            memory,
            ModelRoot + "wisp_primal_sml.ao",
            sharedLifespan);

        var wardenResult = reader.Read(
            new RawEntityRef(101, warden),
            CreateEntity(101));
        var primalResult = reader.Read(
            new RawEntityRef(102, primal),
            CreateEntity(102));

        Assert.Equal(AreaPollenKind.Wild, wardenResult.Details.Kind);
        Assert.Equal(AreaPollenKind.Primal, primalResult.Details.Kind);
        Assert.DoesNotContain(wardenResult.Evidence, item =>
            item.Key is "KindSignature" or "KindFlag");
        Assert.DoesNotContain(primalResult.Evidence, item =>
            item.Key is "KindSignature" or "KindFlag");
    }

    [Fact]
    public void Read_AutoDiscoversModelLayoutWhenConfiguredLayoutIsInvalid()
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var reader = new PollenStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory));
        var entity = WritePollenEntity(
            builder,
            memory,
            ModelRoot + "wisp_primal_med.ao",
            animatedModelInfoOffset: 0x368,
            modelInfoFileRecordOffset: 0x20,
            fileRecordNameOffset: 0x10);

        var result = reader.Read(new RawEntityRef(42, entity), CreateEntity(42));

        Assert.True(result.Matched);
        Assert.Equal(AreaPollenKind.Primal, result.Details.Kind);
        Assert.Contains(result.Evidence, item => item == new AreaContentEvidence(
            "Pollen",
            "ModelLayout",
            "animated+0x368/modelInfo+0x20/fileRecord+0x10",
            0.9f));
        Assert.Contains(result.Evidence, item => item == new AreaContentEvidence(
            "Pollen",
            "ModelLayoutSource",
            "auto-discovered",
            0.9f));
    }

    [Fact]
    public void Read_ReturnsUnknownForUnrecognisedAzmeriWispModel()
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var reader = new PollenStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory));
        var model = ModelRoot + "wisp_futuretype_med.ao";
        var entity = WritePollenEntity(builder, memory, model);

        var result = reader.Read(new RawEntityRef(42, entity), CreateEntity(42));

        Assert.True(result.Matched);
        Assert.Equal(AreaPollenKind.Unknown, result.Details.Kind);
        Assert.Contains(result.Evidence, item => item == new AreaContentEvidence(
            "Pollen",
            "ModelPath",
            model,
            1f));
        Assert.Contains(result.Evidence, item => item == new AreaContentEvidence(
            "Pollen",
            "ModelKey",
            "unknown",
            1f));
    }

    [Fact]
    public void Read_ReturnsUnknownWithoutThrowingWhenAnimatedComponentIsMissing()
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var reader = new PollenStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory));
        var entity = builder.Allocate();
        builder.WriteEntity(entity, PollenMetadata);

        var result = reader.Read(new RawEntityRef(42, entity), CreateEntity(42));

        Assert.True(result.Matched);
        Assert.Equal(AreaPollenKind.Unknown, result.Details.Kind);
        Assert.Empty(result.Evidence);
    }

    [Theory]
    [InlineData("Metadata/MiscellaneousObjects/Azmeri/AzmeriResourceCore")]
    [InlineData("Metadata/MiscellaneousObjects/Azmeri")]
    [InlineData("Metadata/MiscellaneousObjects/Monolith")]
    public void Read_DoesNotMatchOtherMetadata(string metadata)
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var reader = new PollenStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory));
        var entity = builder.Allocate();

        var result = reader.Read(new RawEntityRef(42, entity), CreateEntity(42, metadata));

        Assert.False(result.Matched);
        Assert.Equal(AreaPollenKind.Unknown, result.Details.Kind);
        Assert.Empty(result.Evidence);
    }

    [Fact]
    public void Read_MatchesCompleteMetadataPathIgnoringCase()
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var reader = new PollenStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory));
        var entity = WritePollenEntity(
            builder,
            memory,
            ModelRoot + "wisp_warden_med.ao");

        var result = reader.Read(
            new RawEntityRef(42, entity),
            CreateEntity(42, "metadata/miscellaneousobjects/azmeri/azmeriresourcebase"));

        Assert.True(result.Matched);
        Assert.Equal(AreaPollenKind.Wild, result.Details.Kind);
    }

    private static nint WritePollenEntity(
        EntityMemoryBuilder builder,
        SyntheticProcessMemory memory,
        string modelPath,
        nint? limitedLifespan = null,
        int animatedModelInfoOffset = 0x358,
        int modelInfoFileRecordOffset = 0x18,
        int fileRecordNameOffset = 0x08)
    {
        var entity = builder.Allocate();
        var animated = builder.Allocate(0x400);
        var modelInfo = builder.Allocate(0x80);
        var fileRecord = builder.Allocate(0x80);
        memory.WritePointer(animated + animatedModelInfoOffset, modelInfo);
        memory.WritePointer(modelInfo + modelInfoFileRecordOffset, fileRecord);
        memory.WriteStdWString(fileRecord + fileRecordNameOffset, modelPath);
        var lifespan = limitedLifespan ?? builder.Allocate(0x120);
        builder.WriteEntity(
            entity,
            PollenMetadata,
            ("Animated", animated),
            ("LimitedLifespan", lifespan));
        return entity;
    }

    private static AreaEntitySnapshot CreateEntity(
        uint entityId,
        string metadata = PollenMetadata)
        => new(
            entityId,
            metadata,
            "Wisp",
            AreaEntityCategory.Object,
            Vector3.Zero,
            Vector2.Zero,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster,
            0,
            0,
            false,
            false,
            AreaChestState.NotApplicable,
            []);
}
