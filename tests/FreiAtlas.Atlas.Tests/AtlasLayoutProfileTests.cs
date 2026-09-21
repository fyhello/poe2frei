using FreiAtlas.Atlas.Memory;
using FreiAtlas.Core.Memory;

namespace FreiAtlas.Atlas.Tests;

public sealed class AtlasLayoutProfileTests
{
    [Fact]
    public void Default_ReferencesSharedGameProfileWithoutDuplicatingPublicOffsets()
    {
        var sharedOffsetProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "GameStateReferencePattern",
            "GameStatePatternDisplacementOffset",
            "GameStatePatternInstructionLength",
            "GameStateCurrentVectorOffset",
            "GameStateSlotsOffset",
            "GameStateSlotStride",
            "GameStateSlotCount",
            "InGameStateAreaInstanceOffset",
            "InGameStateUiRootOffset",
            "AreaInstanceLocalPlayerOffset",
            "EntityDetailsOffset",
            "EntityComponentListOffset",
            "EntityDetailsComponentLookupOffset",
            "ComponentLookupBucketOffset",
            "ComponentLookupEntryStride",
            "PlayerComponentNameOffset",
            "UiSelfOffset",
            "UiChildrenOffset",
            "UiChildrenEndOffset",
            "UiParentOffset",
            "UiRelativePositionOffset",
            "UiScaleOffset",
            "UiFlagsOffset",
            "UiVisibleBit",
            "UiWidthOffset",
            "UiHeightOffset"
        };

        Assert.Same(Poe2MemoryProfile.Current, AtlasLayoutProfile.Default.Game);
        Assert.DoesNotContain(
            typeof(AtlasLayoutProfile).GetProperties(),
            property => sharedOffsetProperties.Contains(property.Name));
    }
}
