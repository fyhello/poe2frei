using System.Numerics;

namespace FreiAtlas.Core.Atlas;

public readonly record struct AtlasGridPos(int X, int Y);

public enum AtlasSnapshotStatus
{
    Loading,
    Rebuilding,
    Stable
}

public enum AtlasEdgeState
{
    Unknown,
    Known,
    Locked,
    Reachable
}

public enum AtlasEdgeColor
{
    Gray,
    Red,
    Green
}

public sealed record RawContentTag(
    string RawCode,
    nint SourceAddress,
    string SourceField,
    float Confidence);

public sealed record NormalizedMapContent(
    string ContentId,
    string DisplayName,
    string IconId,
    string DefaultColor,
    int Priority,
    IReadOnlyDictionary<string, string> Attributes)
{
    public string Description { get; init; } = string.Empty;
    public string ReferenceIconId { get; init; } = string.Empty;
}

public sealed record AtlasNodeSnapshot(
    AtlasGridPos Grid,
    string? MapId,
    string? DisplayName,
    float RelativeX,
    float RelativeY,
    bool IsVisible,
    bool IsAccessible,
    bool IsCompleted,
    bool IsCurrent,
    IReadOnlyList<RawContentTag> RawContent,
    IReadOnlyList<NormalizedMapContent> Contents,
    bool? IsDiscovered = null)
{
    public string Kind { get; init; } = "Normal";
    public string MapType { get; init; } = string.Empty;
    public string MapGroup { get; init; } = string.Empty;
    public IReadOnlyList<string> MapDataTags { get; init; } = Array.Empty<string>();
    public uint RawContentValue { get; init; }
    public IReadOnlyList<uint> ContentVectorValues { get; init; } = Array.Empty<uint>();
    public int IconType { get; init; }
    public IReadOnlyList<string> ContentBadges { get; init; } = Array.Empty<string>();
    public uint RegionKey { get; init; }
    public byte RawState { get; init; }
    public byte Biome { get; init; }
    public byte RawFlags { get; init; }
    public byte Completion { get; init; }
}

public sealed record AtlasEdgeSnapshot(
    AtlasGridPos From,
    AtlasGridPos To,
    AtlasEdgeState State,
    AtlasEdgeColor RenderColor);

public sealed record AtlasSnapshot(
    DateTimeOffset CapturedAt,
    AtlasSnapshotStatus Status,
    int NodeCount,
    int EdgeCount,
    IReadOnlyList<AtlasNodeSnapshot> Nodes,
    IReadOnlyList<AtlasEdgeSnapshot> Edges,
    AtlasGridPos? CurrentGrid,
    AtlasProjection Projection,
    string Signature,
    bool IsAtlasOpen = true);
