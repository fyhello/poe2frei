namespace FreiAtlas.Core.Memory;

// Shared offsets verified against the current PoE2 client and the cited references.
public sealed record Poe2MemoryProfile
{
    public static Poe2MemoryProfile Current { get; } = new();

    public string ProfileId { get; init; } = "poe2-2026.09.10-frei-live-r41";

    public DateOnly VerifiedOn { get; init; } = new(2026, 9, 10);

    public string Source { get; init; } =
        "POE2Radar v0.17.1 commit 2615bec65caf62589c4e80dca9b6f75425c9c014; "
        + "GameHelper2 commit 53b46e05ddf31e38a9b5b805a4be39328a95937a; "
        + "direct map pointer chain live-verified 2026-08-01; "
        + "runeforge UI tree live-verified 2026-08-02; "
        + "6K-Minimal-Follow root layout reference; "
        + "root, area identity and UI layout live-verified 2026-09-10; "
        + "UI parent scroll layout verified from client code and cached runeforge rewards 2026-09-10; "
        + "client poe2:6a9e477a:04c9c000:none:057fec473010c6370d67b764f9eed2764b41b32af6f2b274c1daf8a7654504aa";

    public AobReferenceLayout GameStateReference { get; init; } = new(
        Array.AsReadOnly<byte?>(
        [
            0x48, 0x39, 0x2D, null, null, null, null,
            0x0F, 0x85, 0x20, 0x01, 0x00, 0x00
        ]),
        3,
        7);

    public GameStateLayout GameState { get; init; } = new(0x10, 0x50, 0x10, 12);

    public InGameStateLayout InGameState { get; init; } = new(0x290, 0x2F0);

    public AreaInstanceLayout AreaInstance { get; init; } = new(
        0x098,
        0x5D0,
        0x6F0,
        0x700,
        0x8D0,
        0x0BC,
        0x114);

    public StdMapNodeLayout StdMapNode { get; init; } = new(
        0x00,
        0x08,
        0x10,
        0x19,
        0x20,
        0x28,
        0x30,
        0x40000000);

    public EntityLayout Entity { get; init; } = new(0x08, 0x10);

    public EntityDetailsLayout EntityDetails { get; init; } = new(0x08, 0x28);

    public ComponentLookupLayout ComponentLookup { get; init; } = new(0x28, 0x10);

    public RenderLayout Render { get; init; } = new(0x138);

    public PlayerLayout Player { get; init; } = new(0x1B0, 0x204, 64);

    public LifeLayout Life { get; init; } = new(0x1B0);

    public VitalLayout Vital { get; init; } = new(0x2C, 0x30);

    public PositionedLayout Positioned { get; init; } = new(0x1E0);

    public ObjectMagicPropertiesLayout ObjectMagicProperties { get; init; } = new(
        0x144,
        0x168,
        0x20,
        0x08,
        0x00);

    public MinimapIconLayout MinimapIcon { get; init; } = new(0x10);

    public ChestLayout Chest { get; init; } = new(0x168);

    public StateMachineLayout StateMachine { get; init; } = new(0x20);

    public RuneStationLayout RuneStation { get; init; } = new(
        0x10,
        0x28,
        0x30,
        0x38,
        0x3C,
        0x60,
        0xA0,
        0x28,
        0x68,
        34);

    public TerrainLayout Terrain { get; init; } = new(0x18, 0x28, 0xD0, 0x130, 23);

    public TileLayout Tile { get; init; } = new(0x38, 0x08);

    public TgtFileLayout TgtFile { get; init; } = new(0x08);

    public UiElementLayout UiElement { get; init; } = new(
        0x08,
        0x10,
        0x18,
        0xB8,
        0x100,
        0x118,
        0x168,
        0x0B,
        0x270,
        0x274,
        0x172,
        0x108,
        0x0A,
        2560f,
        1600f);

    public MapUiLayout MapUi { get; init; } = new(0x350, 0x358, 0x390);

    public RuneforgeUiLayout RuneforgeUi { get; init; } = new(
        Array.AsReadOnly<uint>(
        [
            0x00462EF1,
            0x00502EF3,
            0x00502EF7,
            0x00542EF1,
            0x00502EF1
        ]),
        GateStep: 0,
        ViewportStep: 2,
        ScrollOffset: 0x108,
        RewardTextOffset: 0x360,
        MaximumRows: 512,
        MaximumChildrenPerRow: 32,
        MaximumRewardTextLength: 128);

    public ImportantUiLayout ImportantUi { get; init; } = new(0x7B0);

    public MapParentLayout MapParent { get; init; } = new(0x28, 0x30);

    public StdWStringLayout StdWString { get; init; } = new(0x10, 8);

    public float WorldToGridRatio { get; init; } = 250f / 23f;
}

public sealed record AobReferenceLayout(
    IReadOnlyList<byte?> Pattern,
    int DisplacementOffset,
    int InstructionLength);

public sealed record GameStateLayout(
    int CurrentStateVectorOffset,
    int StateSlotsOffset,
    int StateSlotStride,
    int StateSlotCount);

public sealed record InGameStateLayout(
    int AreaInstanceOffset,
    int UiRootOffset);

public sealed record AreaInstanceLayout(
    int AreaInfoOffset,
    int LocalPlayerOffset,
    int AwakeEntitiesOffset,
    int SleepingEntitiesOffset,
    int TerrainOffset,
    int AreaLevelOffset,
    int AreaHashOffset);

public sealed record StdMapNodeLayout(
    int LeftOffset,
    int ParentOffset,
    int RightOffset,
    int IsNilOffset,
    int KeyIdOffset,
    int EntityOffset,
    int Size,
    uint VisualIdThreshold);

public sealed record EntityLayout(int DetailsOffset, int ComponentsOffset);

public sealed record EntityDetailsLayout(int NameOffset, int ComponentLookupOffset);

public sealed record ComponentLookupLayout(int BucketOffset, int EntryStride);

public sealed record RenderLayout(int WorldPositionOffset);

public sealed record PlayerLayout(int NameOffset, int LevelOffset, int MaximumNameLength);

public sealed record LifeLayout(
    int HealthOffset,
    int ManaOffset = 0x208,
    int EnergyShieldOffset = 0x248,
    int OwnerOffset = 0x08);

public sealed record VitalLayout(
    int MaximumOffset,
    int CurrentOffset,
    int ReservedFlatOffset = 0x10,
    int ReservedFractionOffset = 0x14);

public sealed record PositionedLayout(int ReactionOffset);

public sealed record ObjectMagicPropertiesLayout(
    int RarityOffset,
    int ModsOffset,
    int ModElementStride,
    int ModRecordOffset,
    int ModIdPointerOffset);

public sealed record MinimapIconLayout(int CompletedOffset);

public sealed record ChestLayout(int OpenStateOffset);

public sealed record StateMachineLayout(
    int ListenersOffset,
    int StateOffset = 0x10);

public sealed record RuneStationLayout(
    int OwnerOffset,
    int AnchorReferenceOffset,
    int AnchorHolderOffset,
    int HoleCountOffset,
    int AnchorPositionOffset,
    int SelectedRecipeOffset,
    int ListenerSubOffset,
    int RuneTablePointerOffset,
    int RuneStride,
    int RuneCount);

public sealed record TerrainLayout(
    int TotalTilesOffset,
    int TilesOffset,
    int WalkableOffset,
    int BytesPerRowOffset,
    int TileGridCells);

public sealed record TileLayout(int StructureSize, int TgtFileOffset);

public sealed record TgtFileLayout(int PathOffset);

public sealed record UiElementLayout(
    int SelfOffset,
    int ChildrenOffset,
    int ChildrenEndOffset,
    int ParentOffset,
    int RelativePositionOffset,
    int ScaleOffset,
    int FlagsOffset,
    int VisibleBit,
    int WidthOffset,
    int HeightOffset,
    int ScaleIndexOffset = 0x172,
    int PositionModifierOffset = 0x108,
    int ModifyPositionBit = 0x0A,
    float BaseResolutionWidth = 2560f,
    float BaseResolutionHeight = 1600f);

public sealed record MapUiLayout(int ShiftOffset, int DefaultShiftOffset, int ZoomOffset);

public sealed record RuneforgeUiLayout(
    IReadOnlyList<uint> PanelFlagFingerprints,
    int GateStep,
    int ViewportStep,
    int ScrollOffset,
    int RewardTextOffset,
    int MaximumRows,
    int MaximumChildrenPerRow,
    int MaximumRewardTextLength);

public sealed record ImportantUiLayout(int MapParentOffset);

public sealed record MapParentLayout(int LargeMapOffset, int MiniMapOffset);

public sealed record StdWStringLayout(int LengthOffset, int InlineCapacity);
