using FreiAtlas.Core.Memory;

namespace FreiAtlas.Atlas.Memory;

public sealed record AtlasLayoutProfile(
    string GameBuild,
    string RelativePositionField,
    string ScaleField,
    string FlagsField,
    string GridPositionField,
    string StateField,
    string ContentField,
    string CanvasEdgesField,
    int RequiredStableSamples,
    int ExpectedNodeSize,
    int MaxNodeCount,
    int MaxEdgeCount)
{
    public Poe2MemoryProfile Game { get; init; } = Poe2MemoryProfile.Current;

    public int AtlasMapRowOffset { get; init; } = 0x2F0;
    public int AtlasCurrentMarkerNodeOffset { get; init; } = 0x2F0;
    public int AtlasContentRowOffset { get; init; } = 0x300;
    public int AtlasGridOffset { get; init; } = 0x320;
    public int AtlasRegionKeyOffset { get; init; } = 0x328;
    public int AtlasStateOffset { get; init; } = 0x32C;
    public int AtlasBiomeOffset { get; init; } = 0x32E;
    public int AtlasFlagsOffset { get; init; } = 0x32F;
    public int AtlasCompletionOffset { get; init; } = 0x339;
    public int AtlasContentVectorBeginOffset { get; init; } = 0x340;
    public int AtlasContentVectorEndOffset { get; init; } = 0x348;
    public int AtlasContentVectorMaxBytes { get; init; } = 4096;
    public int AtlasDataStorageOffset { get; init; } = 0x10;
    public int AtlasDataModelOffset { get; init; } = 0x20;
    public int AtlasDataStatusOffset { get; init; } = 0x2CF;
    public int AtlasMapWorldAreaOffset { get; init; } = 0x00;
    public int AtlasMapCodeOffset { get; init; } = 0x00;
    public int AtlasMapNameOffset { get; init; } = 0x08;
    public int AtlasContentHeadlineOffset { get; init; } = 0x38;
    public int AtlasContentNameOffset { get; init; } = 0x30;
    public int AtlasContentStatsOffset { get; init; } = 0x50;
    public int AtlasContentStatScanBytes { get; init; } = 0x10;
    public int AtlasContentBadgeStringOffset { get; init; } = 0x2E8;
    public int AtlasConnectionsOffset { get; init; } = 0x590;
    public int AtlasEdgeStride { get; init; } = 20;
    public int AtlasEdgeSourceOffset { get; init; } = 0x04;
    public int AtlasEdgeTargetOffset { get; init; } = 0x0C;
    public int AtlasNodeIconOffset { get; init; } = 0x028;
    public int AtlasNodeIconPositionOffset { get; init; } = 0x100;
    public int MinimumNodeClassCount { get; init; } = 50;
    public int MinimumBiomeKinds { get; init; } = 3;
    public int MinimumDistinctGrids { get; init; } = 20;
    public int UiTreeNodeLimit { get; init; } = 200_000;

    public static AtlasLayoutProfile Default => new(
        "observed-2026-11-10",
        "UiElement.RelativePos",
        "UiElement.LocalScaleMul",
        "UiElement.Flags",
        "AtlasNode.GridPos",
        "AtlasNode.State",
        "AtlasNode.Content",
        "AtlasCanvas.Edges",
        3,
        40,
        20_000,
        4000);
}
