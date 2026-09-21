using System.Numerics;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace FreiAtlas.Core.Area;

public enum AreaMapSnapshotStatus
{
    Detached,
    Loading,
    Stable,
    Degraded
}

public enum AreaEntityCategory
{
    Player,
    Monster,
    Npc,
    Chest,
    Transition,
    Object,
    Other
}

public enum AreaEntityDisposition
{
    Unknown,
    Friendly,
    Neutral,
    Hostile
}

public enum AreaEntityRarity
{
    NonMonster = -1,
    Normal,
    Magic,
    Rare,
    Unique
}

public enum AreaChestState
{
    NotApplicable,
    Unknown,
    Closed,
    Opened
}

public enum AreaContentKind
{
    Unknown,
    Boss,
    BossCandidate,
    Expedition,
    Ritual,
    Breach,
    Essence,
    Abyss,
    Strongbox,
    Shrine,
    Waypoint,
    Transition,
    Incursion
}

public enum AreaContentPhase
{
    Unknown,
    Available,
    Active,
    Selected,
    Completed
}

public enum AreaLandmarkKind
{
    Unknown,
    BossArena,
    Waypoint,
    Transition,
    Mechanic,
    BossHint,
    Incursion
}

public enum AreaMapViewKind
{
    LargeMap,
    MiniMap
}

public enum AreaMapViewAvailability
{
    Unavailable,
    Unverified,
    Verified
}

public enum AreaDiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed record AreaIdentity(
    uint AreaHash,
    string AreaCode,
    int AreaLevel,
    long SessionSequence);

public sealed record AreaPlayerSnapshot(
    string CharacterName,
    int Level,
    Vector3 WorldPosition,
    Vector2 GridPosition);

public sealed record AreaContentEvidence(
    string Source,
    string Key,
    string Value,
    float Confidence);

public sealed record AreaExpeditionRune(
    int Index,
    string DisplayName);

public sealed record AreaExpeditionReward(
    string ItemId,
    string DisplayName,
    int Quantity,
    bool IsExactItem);

public sealed record AreaExpeditionRecipe(
    string RecipeId,
    int CatalogRow,
    int Size,
    ImmutableArray<AreaExpeditionRune> Runes,
    ImmutableArray<AreaExpeditionReward> Rewards);

public sealed record AreaExpeditionDetails
{
    private ImmutableArray<AreaExpeditionRecipe> _recipes = [];

    [JsonConstructor]
    public AreaExpeditionDetails(
        int? holeCount,
        ImmutableArray<AreaExpeditionRecipe> recipes = default)
    {
        HoleCount = holeCount;
        Recipes = recipes;
    }

    public int? HoleCount { get; init; }

    public ImmutableArray<AreaExpeditionRecipe> Recipes
    {
        get => _recipes;
        init => _recipes = value.IsDefault ? [] : value;
    }
}

public sealed record AreaReadDiagnostic(
    string Code,
    string Message,
    AreaDiagnosticSeverity Severity,
    uint? EntityId = null);

public sealed record AreaEntitySnapshot(
    uint EntityId,
    string MetadataPath,
    string DisplayName,
    AreaEntityCategory Category,
    Vector3 WorldPosition,
    Vector2 GridPosition,
    AreaEntityDisposition Disposition,
    AreaEntityRarity Rarity,
    int CurrentLife,
    int MaximumLife,
    bool HasMinimapIcon,
    bool IsMinimapIconComplete,
    AreaChestState ChestState,
    IReadOnlyList<string> ModIds);

public sealed record AreaContentSnapshot(
    string InstanceId,
    string ContentId,
    string DisplayName,
    AreaContentKind Kind,
    AreaContentPhase Phase,
    Vector3 WorldPosition,
    Vector2 GridPosition,
    float Confidence,
    uint? SourceEntityId,
    IReadOnlyList<AreaContentEvidence> Evidence,
    AreaExpeditionDetails? ExpeditionDetails = null);

public sealed record AreaLandmarkSnapshot(
    string LandmarkId,
    string DisplayName,
    string TilePath,
    AreaLandmarkKind Kind,
    Vector2 GridPosition,
    int TileCount);

public sealed record AreaTerrainSnapshot(
    int Width,
    int Height,
    ReadOnlyMemory<byte> Walkable,
    IReadOnlyList<string> TilePaths);

public sealed record AreaUiRect(float X, float Y, float Width, float Height);

public enum AreaExpeditionRecipePanelAvailability
{
    Unavailable,
    Unverified,
    Verified
}

public sealed record AreaExpeditionRecipePanelRow(
    string RecipeId,
    int CatalogRow,
    AreaUiRect Bounds,
    bool IsVisible);

public sealed record AreaExpeditionRecipePanelSnapshot
{
    private ImmutableArray<AreaExpeditionRecipePanelRow> _rows = [];

    [JsonConstructor]
    public AreaExpeditionRecipePanelSnapshot(
        AreaExpeditionRecipePanelAvailability availability,
        bool isOpen,
        AreaUiRect? panelBounds,
        AreaUiRect? listClipBounds,
        string? instanceId,
        ImmutableArray<AreaExpeditionRecipePanelRow> rows = default)
    {
        var normalizedRows = rows.IsDefault ? [] : rows;
        if (availability == AreaExpeditionRecipePanelAvailability.Verified &&
            isOpen &&
            (panelBounds is null ||
             listClipBounds is null ||
             string.IsNullOrWhiteSpace(instanceId) ||
             normalizedRows.IsEmpty))
        {
            throw new ArgumentException(
                "A verified open recipe panel requires bounds, identity, and rows.");
        }

        Availability = availability;
        IsOpen = isOpen;
        PanelBounds = panelBounds;
        ListClipBounds = listClipBounds;
        InstanceId = instanceId;
        _rows = normalizedRows;
    }

    public AreaExpeditionRecipePanelAvailability Availability { get; }

    public bool IsOpen { get; }

    public AreaUiRect? PanelBounds { get; }

    public AreaUiRect? ListClipBounds { get; }

    public string? InstanceId { get; }

    public ImmutableArray<AreaExpeditionRecipePanelRow> Rows => _rows;

    public static AreaExpeditionRecipePanelSnapshot Unavailable { get; } =
        new(AreaExpeditionRecipePanelAvailability.Unavailable, false, null, null, null, []);

    public static AreaExpeditionRecipePanelSnapshot Closed { get; } =
        new(AreaExpeditionRecipePanelAvailability.Verified, false, null, null, null, []);

    public static AreaExpeditionRecipePanelSnapshot UnverifiedOpen { get; } =
        new(AreaExpeditionRecipePanelAvailability.Unverified, true, null, null, null, []);
}

public sealed record AreaMapViewSnapshot(
    AreaMapViewKind Kind,
    AreaMapViewAvailability Availability,
    bool IsVisible,
    Vector2 Shift,
    float Zoom,
    float RotationRadians,
    bool RotatesWithPlayer,
    AreaUiRect? Viewport,
    float Confidence);

public sealed record AreaMapViewsSnapshot(
    AreaMapViewSnapshot LargeMap,
    AreaMapViewSnapshot MiniMap)
{
    public static AreaMapViewsSnapshot Unavailable { get; } = new(
        CreateUnavailableView(AreaMapViewKind.LargeMap),
        CreateUnavailableView(AreaMapViewKind.MiniMap));

    private static AreaMapViewSnapshot CreateUnavailableView(AreaMapViewKind kind)
        => new(
            kind,
            AreaMapViewAvailability.Unavailable,
            false,
            Vector2.Zero,
            0f,
            0f,
            false,
            null,
            0f);
}

public sealed record AreaMapSnapshot(
    DateTimeOffset CapturedAt,
    int ProcessId,
    string ProfileId,
    AreaMapSnapshotStatus Status,
    AreaIdentity Area,
    AreaPlayerSnapshot? Player,
    IReadOnlyList<AreaEntitySnapshot> Entities,
    IReadOnlyList<AreaContentSnapshot> Contents,
    IReadOnlyList<AreaLandmarkSnapshot> Landmarks,
    AreaTerrainSnapshot? Terrain,
    AreaMapViewsSnapshot MapViews,
    IReadOnlyList<AreaReadDiagnostic> Diagnostics)
{
    public AreaExpeditionRecipePanelSnapshot ExpeditionRecipePanel { get; init; } =
        AreaExpeditionRecipePanelSnapshot.Unavailable;

    public static AreaMapSnapshot Loading(
        int processId,
        string profileId,
        long sequence)
        => new(
            DateTimeOffset.UtcNow,
            processId,
            profileId,
            AreaMapSnapshotStatus.Loading,
            new AreaIdentity(0, string.Empty, 0, sequence),
            null,
            [],
            [],
            [],
            null,
            AreaMapViewsSnapshot.Unavailable,
            []);
}
