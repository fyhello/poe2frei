using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Memory;
using FreiAtlas.Game.Content;
using FreiAtlas.Game.Entities;
using FreiAtlas.Game.Tests.Entities;
using FreiAtlas.Game.Tests.Memory;

namespace FreiAtlas.Game.Tests.Content;

public sealed class ExpeditionStateEvidenceReaderTests
{
    [Fact]
    public void Read_ResolvesRuneStationChainAndPreservesRawEvidence()
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var device = builder.Allocate();
        var stateMachine = builder.Allocate(0x100);
        var station = builder.Allocate(0x180);
        var listener = builder.Allocate(0x20);
        var vector = builder.Allocate(8);
        var holder = builder.Allocate(0x40);
        var tablePointer = builder.Allocate(8);
        var tableBase = (nint)0x900000;
        var anchor = tableBase + (14 * 0x68);

        memory.WritePointer(stateMachine + 0x20, vector);
        memory.WritePointer(stateMachine + 0x28, vector + 8);
        memory.WriteInt32(stateMachine + 0x10, 3);
        memory.WritePointer(vector, listener);
        memory.WritePointer(listener, station + 0xA0);
        memory.WritePointer(station + 0x10, device);
        memory.WriteInt32(station + 0x38, 4);
        memory.WriteInt32(station + 0x3C, 2);
        memory.WritePointer(station + 0x28, anchor);
        memory.WritePointer(station + 0x30, holder);
        memory.WritePointer(holder + 0x28, tablePointer);
        memory.WritePointer(tablePointer, tableBase);
        builder.WriteEntity(
            device,
            "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter",
            ("StateMachine", stateMachine));

        var entity = new AreaEntitySnapshot(
            42,
            "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter",
            "Expedition2Encounter",
            AreaEntityCategory.Object,
            Vector3.Zero,
            Vector2.Zero,
            AreaEntityDisposition.Neutral,
            AreaEntityRarity.NonMonster,
            100,
            100,
            true,
            false,
            AreaChestState.NotApplicable,
            []);
        var reader = new ExpeditionStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory));

        var result = reader.Read(
            new RawEntityRef(42, device),
            entity);

        Assert.True(result.Matched);
        Assert.True(result.Resolved);
        Assert.Equal(4, result.HoleCount);
        Assert.Contains(result.Evidence, item => item.Key == "StateMachine.State" && item.Value == "0x00000003");
        Assert.Contains(result.Evidence, item => item.Key == "RuneStation.HoleCount" && item.Value == "0x00000004");
        Assert.Contains(result.Evidence, item => item.Key == "RuneStation.AnchorPos" && item.Value == "0x00000002");
        Assert.Contains(result.Evidence, item => item.Key == "RuneStation.AnchorIndex" && item.Value == "14");
        Assert.Contains(
            result.Evidence,
            item => item.Key == "RuneStation.SelectedRecipe.Present"
                    && item.Value == "false"
                    && item.Confidence == 0.95f);
        Assert.DoesNotContain(result.Evidence, item => item.Key.StartsWith("Research.", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_ResearchModeScansStateMachineBoundariesAndFiltersValues()
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var device = builder.Allocate();
        var stateMachine = builder.Allocate(0x104);
        var pointerTarget = builder.Allocate();

        memory.WriteInt32(stateMachine, -1);
        memory.WriteInt32(stateMachine + 0x04, -2);
        memory.WriteInt32(stateMachine + 0x08, 4097);
        memory.WriteInt32(stateMachine + 0x0C, 0x12345678);
        memory.WritePointer(stateMachine + 0x40, pointerTarget);
        memory.WriteInt32(stateMachine + 0x100, 4096);
        builder.WriteEntity(
            device,
            "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter",
            ("StateMachine", stateMachine));

        var reader = new ExpeditionStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory),
            includeResearchEvidence: true);

        var result = reader.Read(
            new RawEntityRef(42, device),
            CreateEntity(
                42,
                "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter"));

        Assert.Contains(
            result.Evidence,
            item => item.Source == "StateMachine"
                    && item.Key == "Research.StateMachine.+0x00"
                    && item.Value == "-1"
                    && item.Confidence == 0.1f);
        Assert.Contains(
            result.Evidence,
            item => item.Source == "StateMachine"
                    && item.Key == "Research.StateMachine.+0x100"
                    && item.Value == "4096"
                    && item.Confidence == 0.1f);
        Assert.DoesNotContain(result.Evidence, item => item.Key == "Research.StateMachine.+0x04");
        Assert.DoesNotContain(result.Evidence, item => item.Key == "Research.StateMachine.+0x08");
        Assert.DoesNotContain(result.Evidence, item => item.Key == "Research.StateMachine.+0x0C");
        Assert.DoesNotContain(result.Evidence, item => item.Key == "Research.StateMachine.+0x40");
        Assert.DoesNotContain(result.Evidence, item => item.Key == "Research.StateMachine.+0x44");
    }

    [Fact]
    public void Read_ResearchModePublishesSelectedRecipePresenceWithoutItsPointer()
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var device = builder.Allocate();
        var stateMachine = builder.Allocate(0x104);
        var station = builder.Allocate(0x184);
        var listener = builder.Allocate(0x20);
        var vector = builder.Allocate(8);
        var selectedRecipe = builder.Allocate(0x20);
        var selectedRecipeId = builder.Allocate(128 * sizeof(char));

        memory.WritePointer(stateMachine + 0x20, vector);
        memory.WritePointer(stateMachine + 0x28, vector + 8);
        memory.WritePointer(vector, listener);
        memory.WritePointer(listener, station + 0xA0);
        memory.WritePointer(station + 0x10, device);
        memory.WriteInt32(station + 0x38, 6);
        memory.WritePointer(station + 0x60, selectedRecipe);
        memory.WritePointer(selectedRecipe, selectedRecipeId);
        memory.WriteUtf16Buffer(selectedRecipeId, "6SlotResearchRecipe", 128);
        builder.WriteEntity(
            device,
            "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter",
            ("StateMachine", stateMachine));

        var reader = new ExpeditionStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory),
            includeResearchEvidence: true);

        var result = reader.Read(
            new RawEntityRef(42, device),
            CreateEntity(
                42,
                "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter"));

        Assert.Contains(
            result.Evidence,
            item => item.Source == "RuneStation"
                    && item.Key == "Research.RuneStation.SelectedRecipe.Present"
                    && item.Value == "true"
                    && item.Confidence == 0.1f);
        Assert.Contains(
            result.Evidence,
            item => item.Source == "RuneStation"
                    && item.Key == "RuneStation.SelectedRecipe.Present"
                    && item.Value == "true"
                    && item.Confidence == 0.95f);
        Assert.DoesNotContain(
            result.Evidence,
            item => item.Value.Contains(
                selectedRecipe.ToInt64().ToString("X"),
                StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            result.Evidence,
            item => item.Value.Contains(
                "6SlotResearchRecipe",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Read_DefaultModePublishesValidatedSelectedRecipePresence()
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var device = builder.Allocate();
        var stateMachine = builder.Allocate(0x104);
        var station = builder.Allocate(0x184);
        var listener = builder.Allocate(0x20);
        var vector = builder.Allocate(8);
        var selectedRecipe = builder.Allocate(0x20);
        var selectedRecipeId = builder.Allocate(128 * sizeof(char));

        WriteResolvedRuneStation(
            memory,
            builder,
            device,
            stateMachine,
            station,
            listener,
            vector);
        memory.WritePointer(station + 0x60, selectedRecipe);
        memory.WritePointer(selectedRecipe, selectedRecipeId);
        memory.WriteUtf16Buffer(selectedRecipeId, "5SlotRecipe", 128);
        var reader = new ExpeditionStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory));

        var result = reader.Read(
            new RawEntityRef(42, device),
            CreateEntity(
                42,
                "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter"));

        Assert.Contains(
            result.Evidence,
            item => item.Key == "RuneStation.SelectedRecipe.Present"
                    && item.Value == "true"
                    && item.Confidence == 0.95f);
        Assert.DoesNotContain(
            result.Evidence,
            item => item.Value.Contains("5SlotRecipe", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Evidence,
            item => item.Key.StartsWith("Research.", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_DefaultModePublishesUnresolvedWhenSelectedRecipeCannotBeRead()
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var device = builder.Allocate();
        var stateMachine = builder.Allocate(0x104);
        var station = builder.Allocate(0x184);
        var listener = builder.Allocate(0x20);
        var vector = builder.Allocate(8);

        WriteResolvedRuneStation(
            memory,
            builder,
            device,
            stateMachine,
            station,
            listener,
            vector);
        memory.FailRange(station + 0x60, IntPtr.Size);
        var reader = new ExpeditionStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory));

        var result = reader.Read(
            new RawEntityRef(42, device),
            CreateEntity(
                42,
                "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter"));

        Assert.Contains(
            result.Evidence,
            item => item.Key == "RuneStation.SelectedRecipe.Present"
                    && item.Value == "unresolved"
                    && item.Confidence == 0.2f);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Recipe\u0001Id")]
    public void Read_DefaultModeRejectsInvalidSelectedRecipeId(string recipeId)
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var device = builder.Allocate();
        var stateMachine = builder.Allocate(0x104);
        var station = builder.Allocate(0x184);
        var listener = builder.Allocate(0x20);
        var vector = builder.Allocate(8);
        var selectedRecipe = builder.Allocate(0x20);
        var selectedRecipeId = builder.Allocate(128 * sizeof(char));

        WriteResolvedRuneStation(
            memory,
            builder,
            device,
            stateMachine,
            station,
            listener,
            vector);
        memory.WritePointer(station + 0x60, selectedRecipe);
        memory.WritePointer(selectedRecipe, selectedRecipeId);
        memory.WriteUtf16Buffer(selectedRecipeId, recipeId, 128);
        var reader = new ExpeditionStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory));

        var result = reader.Read(
            new RawEntityRef(42, device),
            CreateEntity(
                42,
                "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter"));

        Assert.Contains(
            result.Evidence,
            item => item.Key == "RuneStation.SelectedRecipe.Present"
                    && item.Value == "unresolved"
                    && item.Confidence == 0.2f);
    }

    [Fact]
    public void Read_ExpeditionEarlyReturnStillPublishesFormalUnresolvedPresence()
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var device = builder.Allocate();
        builder.WriteEntity(
            device,
            "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter");
        var reader = new ExpeditionStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory));

        var result = reader.Read(
            new RawEntityRef(42, device),
            CreateEntity(
                42,
                "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter"));

        Assert.False(result.Resolved);
        Assert.Contains(
            result.Evidence,
            item => item.Key == "RuneStation.SelectedRecipe.Present"
                    && item.Value == "unresolved"
                    && item.Confidence == 0.2f);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(17)]
    public void Read_InvalidHoleCountKeepsMatchedResultWithoutPublishingValue(int holeCount)
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var device = builder.Allocate();
        var stateMachine = builder.Allocate(0x104);
        var station = builder.Allocate(0x184);
        var listener = builder.Allocate(0x20);
        var vector = builder.Allocate(8);
        WriteResolvedRuneStation(
            memory,
            builder,
            device,
            stateMachine,
            station,
            listener,
            vector,
            holeCount);
        var reader = new ExpeditionStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory));

        var result = reader.Read(
            new RawEntityRef(42, device),
            CreateEntity(
                42,
                "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter"));

        Assert.True(result.Matched);
        Assert.False(result.Resolved);
        Assert.Null(result.HoleCount);
    }

    [Fact]
    public void Read_UnreadableHoleCountKeepsMatchedResultWithoutPublishingValue()
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var device = builder.Allocate();
        var stateMachine = builder.Allocate(0x104);
        var station = builder.Allocate(0x184);
        var listener = builder.Allocate(0x20);
        var vector = builder.Allocate(8);
        WriteResolvedRuneStation(
            memory,
            builder,
            device,
            stateMachine,
            station,
            listener,
            vector);
        memory.FailRange(station + 0x38, sizeof(int));
        var reader = new ExpeditionStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory));

        var result = reader.Read(
            new RawEntityRef(42, device),
            CreateEntity(
                42,
                "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter"));

        Assert.True(result.Matched);
        Assert.False(result.Resolved);
        Assert.Null(result.HoleCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    public void Read_ValidBoundaryHoleCountPublishesTypedValue(int holeCount)
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var device = builder.Allocate();
        var stateMachine = builder.Allocate(0x104);
        var station = builder.Allocate(0x184);
        var listener = builder.Allocate(0x20);
        var vector = builder.Allocate(8);
        WriteResolvedRuneStation(
            memory,
            builder,
            device,
            stateMachine,
            station,
            listener,
            vector,
            holeCount);
        var reader = new ExpeditionStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory));

        var result = reader.Read(
            new RawEntityRef(42, device),
            CreateEntity(
                42,
                "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter"));

        Assert.True(result.Matched);
        Assert.True(result.Resolved);
        Assert.Equal(holeCount, result.HoleCount);
    }

    [Fact]
    public void Read_ResolvedUniqueStationPublishesOrderedRecipeRewards()
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var device = builder.Allocate();
        var stateMachine = builder.Allocate(0x104);
        var station = builder.Allocate(0x184);
        var listener = builder.Allocate(0x20);
        var vector = builder.Allocate(8);
        WriteResolvedRuneStation(
            memory,
            builder,
            device,
            stateMachine,
            station,
            listener,
            vector,
            holeCount: 5);

        var reader = new ExpeditionStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory));

        var result = reader.Read(
            new RawEntityRef(42, device),
            CreateEntity(
                42,
                "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter"));

        Assert.True(result.Resolved);
        Assert.NotEmpty(result.Recipes);
        Assert.All(result.Recipes, recipe =>
        {
            Assert.InRange(recipe.Size, 1, 5);
            Assert.NotEmpty(recipe.Runes);
            Assert.NotEmpty(recipe.Rewards);
        });
        Assert.True(
            result.Recipes.Zip(
                    result.Recipes.Skip(1),
                    (first, second) => first.CatalogRow <= second.CatalogRow)
                .All(item => item));
    }

    [Fact]
    public void Read_ResearchModeNeverPublishesSelectedRecipePointerLanes()
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var device = builder.Allocate();
        var stateMachine = builder.Allocate(0x104);
        var station = builder.Allocate(0x184);
        var listener = builder.Allocate(0x20);
        var vector = builder.Allocate(8);

        WriteResolvedRuneStation(
            memory,
            builder,
            device,
            stateMachine,
            station,
            listener,
            vector);
        memory.WritePointer(
            station + 0x60,
            new IntPtr(0x0000000100000100L));
        var reader = new ExpeditionStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory),
            includeResearchEvidence: true);

        var result = reader.Read(
            new RawEntityRef(42, device),
            CreateEntity(
                42,
                "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter"));

        Assert.DoesNotContain(
            result.Evidence,
            item => item.Key is "Research.RuneStation.+0x60"
                or "Research.RuneStation.+0x64");
        Assert.Contains(
            result.Evidence,
            item => item.Key == "RuneStation.SelectedRecipe.Present"
                    && item.Value == "unresolved");
    }

    [Fact]
    public void Read_ResearchModeDoesNotPublishReadableAlignedPointerLanes()
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var readablePointer = new IntPtr(0x0000000100000100L);
        var device = readablePointer;
        var stateMachine = builder.Allocate(0x104);
        var station = builder.Allocate(0x184);
        var listener = builder.Allocate(0x20);
        var vector = builder.Allocate(8);

        memory.WritePointer(stateMachine + 0x20, vector);
        memory.WritePointer(stateMachine + 0x28, vector + 8);
        memory.WritePointer(stateMachine + 0x40, readablePointer);
        memory.WritePointer(vector, listener);
        memory.WritePointer(listener, station + 0xA0);
        memory.WritePointer(station + 0x10, device);
        memory.WriteInt32(station + 0x38, 4);
        builder.WriteEntity(
            device,
            "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter",
            ("StateMachine", stateMachine));

        var reader = new ExpeditionStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory),
            includeResearchEvidence: true);

        var result = reader.Read(
            new RawEntityRef(42, device),
            CreateEntity(
                42,
                "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter"));

        Assert.DoesNotContain(result.Evidence, item => item.Key == "Research.StateMachine.+0x40");
        Assert.DoesNotContain(result.Evidence, item => item.Key == "Research.StateMachine.+0x44");
        Assert.DoesNotContain(result.Evidence, item => item.Key == "Research.RuneStation.+0x10");
        Assert.DoesNotContain(result.Evidence, item => item.Key == "Research.RuneStation.+0x14");
    }

    [Fact]
    public void Read_ResearchModePublishesUnalignedAdjacentScalarsEvenWhenTheyFormReadablePointer()
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var device = builder.Allocate();
        var stateMachine = builder.Allocate(0x104);
        var readablePointer = new IntPtr(0x0000000100000100L);

        memory.WriteInt32(stateMachine + 0x04, 256);
        memory.WriteInt32(stateMachine + 0x08, 1);
        memory.WriteByte(readablePointer, 0xCD);
        builder.WriteEntity(
            device,
            "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter",
            ("StateMachine", stateMachine));

        var reader = new ExpeditionStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory),
            includeResearchEvidence: true);

        var result = reader.Read(
            new RawEntityRef(42, device),
            CreateEntity(
                42,
                "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter"));

        Assert.Contains(
            result.Evidence,
            item => item.Key == "Research.StateMachine.+0x04" && item.Value == "256");
        Assert.Contains(
            result.Evidence,
            item => item.Key == "Research.StateMachine.+0x08" && item.Value == "1");
    }

    [Fact]
    public void Read_DefaultModeDoesNotReadResearchOnlyOffsets()
    {
        using var innerMemory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(innerMemory);
        var device = builder.Allocate();
        var stateMachine = builder.Allocate(0x104);

        innerMemory.WriteInt32(stateMachine + 0x10, 3);
        innerMemory.WriteInt32(stateMachine + 0x80, 7);
        builder.WriteEntity(
            device,
            "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter",
            ("StateMachine", stateMachine));
        var memory = new RecordingProcessMemory(innerMemory);
        var reader = new ExpeditionStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory));

        reader.Read(
            new RawEntityRef(42, device),
            CreateEntity(
                42,
                "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter"));

        Assert.DoesNotContain(stateMachine + 0x80, memory.Int32ReadAddresses);
    }

    [Fact]
    public void Read_ResearchModeContinuesAfterUnreadableScalar()
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var device = builder.Allocate();
        var stateMachine = builder.Allocate(0x104);

        memory.FailRange(stateMachine + 0x80, sizeof(int));
        memory.WriteInt32(stateMachine + 0x84, 7);
        builder.WriteEntity(
            device,
            "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter",
            ("StateMachine", stateMachine));

        var reader = new ExpeditionStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory),
            includeResearchEvidence: true);

        var result = reader.Read(
            new RawEntityRef(42, device),
            CreateEntity(
                42,
                "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter"));

        Assert.DoesNotContain(result.Evidence, item => item.Key == "Research.StateMachine.+0x80");
        Assert.Contains(
            result.Evidence,
            item => item.Key == "Research.StateMachine.+0x84" && item.Value == "7");
    }

    [Fact]
    public void Read_ResearchModeScansOwnerValidatedRuneStationBeforeHoleCountValidation()
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var device = builder.Allocate();
        var stateMachine = builder.Allocate(0x104);
        var station = builder.Allocate(0x184);
        var listener = builder.Allocate(0x20);
        var vector = builder.Allocate(8);

        memory.WritePointer(stateMachine + 0x20, vector);
        memory.WritePointer(stateMachine + 0x28, vector + 8);
        memory.WritePointer(vector, listener);
        memory.WritePointer(listener, station + 0xA0);
        memory.WriteInt32(station, -1);
        memory.WritePointer(station + 0x10, device);
        memory.WriteInt32(station + 0x38, -2);
        memory.WriteInt32(station + 0x180, 4096);
        builder.WriteEntity(
            device,
            "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter",
            ("StateMachine", stateMachine));

        var reader = new ExpeditionStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory),
            includeResearchEvidence: true);

        var result = reader.Read(
            new RawEntityRef(42, device),
            CreateEntity(
                42,
                "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter"));

        Assert.False(result.Resolved);
        Assert.Contains(
            result.Evidence,
            item => item.Source == "RuneStation"
                    && item.Key == "Research.RuneStation.+0x00"
                    && item.Value == "-1"
                    && item.Confidence == 0.1f);
        Assert.Contains(
            result.Evidence,
            item => item.Source == "RuneStation"
                    && item.Key == "Research.RuneStation.+0x180"
                    && item.Value == "4096"
                    && item.Confidence == 0.1f);
        Assert.DoesNotContain(result.Evidence, item => item.Key == "Research.RuneStation.+0x38");
        Assert.Contains(
            result.Evidence,
            item => item.Key == "Research.RuneStation.SelectedRecipe.Present"
                    && item.Value == "false");
    }

    [Fact]
    public void Read_ResearchModeDoesNotScanRuneStationWhenOwnerDoesNotMatch()
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var device = builder.Allocate();
        var stateMachine = builder.Allocate(0x104);
        var station = builder.Allocate(0x184);
        var listener = builder.Allocate(0x20);
        var vector = builder.Allocate(8);
        var otherOwner = builder.Allocate();

        memory.WritePointer(stateMachine + 0x20, vector);
        memory.WritePointer(stateMachine + 0x28, vector + 8);
        memory.WritePointer(vector, listener);
        memory.WritePointer(listener, station + 0xA0);
        memory.WritePointer(station + 0x10, otherOwner);
        memory.WriteInt32(station + 0x40, 7);
        builder.WriteEntity(
            device,
            "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter",
            ("StateMachine", stateMachine));

        var reader = new ExpeditionStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory),
            includeResearchEvidence: true);

        var result = reader.Read(
            new RawEntityRef(42, device),
            CreateEntity(
                42,
                "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter"));

        Assert.DoesNotContain(
            result.Evidence,
            item => item.Key.StartsWith("Research.RuneStation.", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_ResearchModeDoesNotScanCaseMismatchedExpeditionMetadata()
    {
        using var memory = new SyntheticProcessMemory();
        var builder = new EntityMemoryBuilder(memory);
        var device = builder.Allocate();
        var stateMachine = builder.Allocate(0x104);

        memory.WriteInt32(stateMachine, -1);
        memory.WriteInt32(stateMachine + 0x10, 3);
        builder.WriteEntity(
            device,
            "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter",
            ("StateMachine", stateMachine));

        var reader = new ExpeditionStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory),
            includeResearchEvidence: true);

        var result = reader.Read(
            new RawEntityRef(7, device),
            CreateEntity(
                7,
                "Metadata/MiscellaneousObjects/Expedition2/expedition2Encounter"));

        Assert.Contains(
            result.Evidence,
            item => item.Key == "StateMachine.State" && item.Value == "0x00000003");
        Assert.DoesNotContain(
            result.Evidence,
            item => item.Key.StartsWith("Research.", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_ResearchModeDoesNotReadNonExpeditionEntity()
    {
        using var memory = new SyntheticProcessMemory();
        var reader = new ExpeditionStateEvidenceReader(
            memory,
            new EntityComponentResolver(memory),
            includeResearchEvidence: true);
        var entity = CreateEntity(
            7,
            "Metadata/MiscellaneousObjects/Other/Device",
            "Other");

        var result = reader.Read(new RawEntityRef(7, (nint)0x500000), entity);

        Assert.False(result.Matched);
        Assert.False(result.Resolved);
        Assert.Null(result.HoleCount);
        Assert.Empty(result.Evidence);
    }

    private static AreaEntitySnapshot CreateEntity(
        uint entityId,
        string metadataPath,
        string displayName = "Expedition2Encounter")
        => new(
            entityId,
            metadataPath,
            displayName,
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

    private static void WriteResolvedRuneStation(
        SyntheticProcessMemory memory,
        EntityMemoryBuilder builder,
        nint device,
        nint stateMachine,
        nint station,
        nint listener,
        nint vector,
        int holeCount = 5)
    {
        memory.WritePointer(stateMachine + 0x20, vector);
        memory.WritePointer(stateMachine + 0x28, vector + 8);
        memory.WritePointer(vector, listener);
        memory.WritePointer(listener, station + 0xA0);
        memory.WritePointer(station + 0x10, device);
        memory.WriteInt32(station + 0x38, holeCount);
        builder.WriteEntity(
            device,
            "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter",
            ("StateMachine", stateMachine));
    }

    private sealed class RecordingProcessMemory(IProcessMemory inner) : IProcessMemory
    {
        public int ProcessId => inner.ProcessId;

        public List<nint> Int32ReadAddresses { get; } = [];

        public bool TryRead(nint address, Span<byte> destination)
            => inner.TryRead(address, destination);

        public bool TryReadInt32(nint address, out int value)
        {
            Int32ReadAddresses.Add(address);
            return inner.TryReadInt32(address, out value);
        }

        public bool TryReadInt64(nint address, out long value)
            => inner.TryReadInt64(address, out value);

        public bool TryReadFloat(nint address, out float value)
            => inner.TryReadFloat(address, out value);

        public bool TryReadPointer(nint address, out nint value)
            => inner.TryReadPointer(address, out value);

        public bool TryReadUtf16(nint address, int maxChars, out string? value)
            => inner.TryReadUtf16(address, maxChars, out value);

        public void Dispose()
        {
        }
    }
}
