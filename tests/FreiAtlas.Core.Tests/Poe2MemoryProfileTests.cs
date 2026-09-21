using FreiAtlas.Core.Memory;

namespace FreiAtlas.Core.Tests;

public sealed class Poe2MemoryProfileTests
{
    [Fact]
    public void Current_ContainsVerifiedRadarLayout()
    {
        var profile = Poe2MemoryProfile.Current;

        Assert.Equal("poe2-2026.09.10-frei-live-r41", profile.ProfileId);
        Assert.Equal(new DateOnly(2026, 9, 10), profile.VerifiedOn);
        Assert.Contains("POE2Radar", profile.Source, StringComparison.Ordinal);
        Assert.Contains("GameHelper2", profile.Source, StringComparison.Ordinal);
        Assert.Contains("live-verified", profile.Source, StringComparison.Ordinal);
        Assert.Equal(
            [0x48, 0x39, 0x2D, null, null, null, null, 0x0F, 0x85, 0x20, 0x01, 0x00, 0x00],
            profile.GameStateReference.Pattern);
        Assert.Equal(3, profile.GameStateReference.DisplacementOffset);
        Assert.Equal(7, profile.GameStateReference.InstructionLength);

        Assert.Equal(0x10, profile.GameState.CurrentStateVectorOffset);
        Assert.Equal(0x50, profile.GameState.StateSlotsOffset);
        Assert.Equal(0x10, profile.GameState.StateSlotStride);
        Assert.Equal(12, profile.GameState.StateSlotCount);
        Assert.Equal(0x290, profile.InGameState.AreaInstanceOffset);
        Assert.Equal(0x2F0, profile.InGameState.UiRootOffset);

        Assert.Equal(0x098, profile.AreaInstance.AreaInfoOffset);
        Assert.Equal(0x5D0, profile.AreaInstance.LocalPlayerOffset);
        Assert.Equal(0x6F0, profile.AreaInstance.AwakeEntitiesOffset);
        Assert.Equal(0x700, profile.AreaInstance.SleepingEntitiesOffset);
        Assert.Equal(0x8D0, profile.AreaInstance.TerrainOffset);
        Assert.Equal(0x0BC, profile.AreaInstance.AreaLevelOffset);
        Assert.Equal(0x114, profile.AreaInstance.AreaHashOffset);

        Assert.Equal(0x00, profile.StdMapNode.LeftOffset);
        Assert.Equal(0x08, profile.StdMapNode.ParentOffset);
        Assert.Equal(0x10, profile.StdMapNode.RightOffset);
        Assert.Equal(0x19, profile.StdMapNode.IsNilOffset);
        Assert.Equal(0x20, profile.StdMapNode.KeyIdOffset);
        Assert.Equal(0x28, profile.StdMapNode.EntityOffset);
        Assert.Equal(0x30, profile.StdMapNode.Size);
        Assert.Equal(0x40000000u, profile.StdMapNode.VisualIdThreshold);

        Assert.Equal(0x08, profile.Entity.DetailsOffset);
        Assert.Equal(0x10, profile.Entity.ComponentsOffset);
        Assert.Equal(0x08, profile.EntityDetails.NameOffset);
        Assert.Equal(0x28, profile.EntityDetails.ComponentLookupOffset);
        Assert.Equal(0x28, profile.ComponentLookup.BucketOffset);
        Assert.Equal(0x10, profile.ComponentLookup.EntryStride);

        Assert.Equal(0x138, profile.Render.WorldPositionOffset);
        Assert.Equal(0x1B0, profile.Player.NameOffset);
        Assert.Equal(0x204, profile.Player.LevelOffset);
        Assert.Equal(0x1B0, profile.Life.HealthOffset);
        Assert.Equal(0x2C, profile.Vital.MaximumOffset);
        Assert.Equal(0x30, profile.Vital.CurrentOffset);
        Assert.Equal(0x1E0, profile.Positioned.ReactionOffset);
        Assert.Equal(0x144, profile.ObjectMagicProperties.RarityOffset);
        Assert.Equal(0x168, profile.ObjectMagicProperties.ModsOffset);
        Assert.Equal(0x20, profile.ObjectMagicProperties.ModElementStride);
        Assert.Equal(0x08, profile.ObjectMagicProperties.ModRecordOffset);
        Assert.Equal(0x00, profile.ObjectMagicProperties.ModIdPointerOffset);
        Assert.Equal(0x10, profile.MinimapIcon.CompletedOffset);
        Assert.Equal(0x168, profile.Chest.OpenStateOffset);
        Assert.Equal(0x20, profile.StateMachine.ListenersOffset);
        Assert.Equal(0x10, profile.StateMachine.StateOffset);
        Assert.Equal(0x10, profile.RuneStation.OwnerOffset);
        Assert.Equal(0x28, profile.RuneStation.AnchorReferenceOffset);
        Assert.Equal(0x30, profile.RuneStation.AnchorHolderOffset);
        Assert.Equal(0x38, profile.RuneStation.HoleCountOffset);
        Assert.Equal(0x3C, profile.RuneStation.AnchorPositionOffset);
        Assert.Equal(0x60, profile.RuneStation.SelectedRecipeOffset);
        Assert.Equal(0xA0, profile.RuneStation.ListenerSubOffset);
        Assert.Equal(0x28, profile.RuneStation.RuneTablePointerOffset);
        Assert.Equal(0x68, profile.RuneStation.RuneStride);
        Assert.Equal(34, profile.RuneStation.RuneCount);

        Assert.Equal(0x18, profile.Terrain.TotalTilesOffset);
        Assert.Equal(0x28, profile.Terrain.TilesOffset);
        Assert.Equal(0xD0, profile.Terrain.WalkableOffset);
        Assert.Equal(0x130, profile.Terrain.BytesPerRowOffset);
        Assert.Equal(23, profile.Terrain.TileGridCells);
        Assert.Equal(0x38, profile.Tile.StructureSize);
        Assert.Equal(0x08, profile.Tile.TgtFileOffset);
        Assert.Equal(0x08, profile.TgtFile.PathOffset);
        Assert.Equal(250f / 23f, profile.WorldToGridRatio);

        Assert.Equal(0x08, profile.UiElement.SelfOffset);
        Assert.Equal(0x10, profile.UiElement.ChildrenOffset);
        Assert.Equal(0x18, profile.UiElement.ChildrenEndOffset);
        Assert.Equal(0xB8, profile.UiElement.ParentOffset);
        Assert.Equal(0x100, profile.UiElement.RelativePositionOffset);
        Assert.Equal(0x118, profile.UiElement.ScaleOffset);
        Assert.Equal(0x168, profile.UiElement.FlagsOffset);
        Assert.Equal(0x0B, profile.UiElement.VisibleBit);
        Assert.Equal(0x270, profile.UiElement.WidthOffset);
        Assert.Equal(0x274, profile.UiElement.HeightOffset);
        Assert.Equal(0x172, profile.UiElement.ScaleIndexOffset);
        Assert.Equal(0x108, profile.UiElement.PositionModifierOffset);
        Assert.Equal(0x0A, profile.UiElement.ModifyPositionBit);
        Assert.Equal(2560f, profile.UiElement.BaseResolutionWidth);
        Assert.Equal(1600f, profile.UiElement.BaseResolutionHeight);
        Assert.Equal(0x350, profile.MapUi.ShiftOffset);
        Assert.Equal(0x358, profile.MapUi.DefaultShiftOffset);
        Assert.Equal(0x390, profile.MapUi.ZoomOffset);
        Assert.Equal(0x7B0, profile.ImportantUi.MapParentOffset);
        Assert.Equal(0x28, profile.MapParent.LargeMapOffset);
        Assert.Equal(0x30, profile.MapParent.MiniMapOffset);
    }

    [Fact]
    public void Current_ContainsLiveVerifiedRuneforgeUiLayout()
    {
        var layout = Poe2MemoryProfile.Current.RuneforgeUi;

        Assert.Equal(
            [0x00462EF1u, 0x00502EF3u, 0x00502EF7u, 0x00542EF1u, 0x00502EF1u],
            layout.PanelFlagFingerprints);
        Assert.Equal(0, layout.GateStep);
        Assert.Equal(2, layout.ViewportStep);
        Assert.Equal(0x108, layout.ScrollOffset);
        Assert.Equal(0x360, layout.RewardTextOffset);
        Assert.Equal(512, layout.MaximumRows);
        Assert.Equal(32, layout.MaximumChildrenPerRow);
        Assert.Equal(128, layout.MaximumRewardTextLength);
    }
}
