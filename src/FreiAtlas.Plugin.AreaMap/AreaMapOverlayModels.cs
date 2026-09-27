using System.Collections.Immutable;
using System.Numerics;
using FreiAtlas.Core.Area;

namespace FreiAtlas.Plugin.AreaMap;

public enum AreaMapOverlayVisualState
{
    BossInactive,
    BossAvailable,
    BossCompleted,
    ExpeditionAvailable,
    ExpeditionSelected,
    ExpeditionCompleted,
    AbyssAvailable,
    AbyssCompleted,
    RitualAvailable,
    RitualCompleted,
    BreachAvailable,
    BreachCompleted,
    EssenceAvailable,
    EssenceCompleted,
    IncursionAvailable,
    StrongboxAvailable,
    OmenAltarAvailable,
    RareMonster,
    RareChest,
    UniqueChest,
    PollenWild,
    PollenSoul,
    PollenPrimal,
    PollenSacred,
    PollenAvailable
}

public enum AreaMapOverlayMarkerKind
{
    Boss,
    Expedition,
    Abyss,
    Ritual,
    Breach,
    Essence,
    Incursion,
    Strongbox,
    OmenAltar,
    RareMonster,
    RareChest,
    UniqueChest,
    Pollen
}

public sealed record AreaMapExpeditionMarker
{
    public AreaMapExpeditionMarker(int? holeCount, string? valueText = null)
    {
        HoleCount = holeCount;
        ValueText = valueText;
    }

    public int? HoleCount { get; }

    public string? ValueText { get; }
}

public sealed record AreaMapOverlayPlacement
{
    public AreaMapOverlayPlacement(
        string instanceId,
        AreaContentKind kind,
        AreaContentPhase phase,
        Vector2 center,
        AreaMapOverlayVisualState visualState,
        AreaMapExpeditionMarker? expeditionMarker = null,
        AreaMapOverlayMarkerKind? markerKind = null,
        uint? sourceEntityId = null)
    {
        InstanceId = instanceId;
        Kind = kind;
        Phase = phase;
        Center = center;
        VisualState = visualState;
        ExpeditionMarker = expeditionMarker;
        MarkerKind = markerKind ?? ResolveMarkerKind(kind);
        SourceEntityId = sourceEntityId;
    }

    public string InstanceId { get; }

    public AreaContentKind Kind { get; }

    public AreaContentPhase Phase { get; }

    public Vector2 Center { get; }

    public AreaMapOverlayVisualState VisualState { get; }

    public AreaMapExpeditionMarker? ExpeditionMarker { get; }

    public AreaMapOverlayMarkerKind MarkerKind { get; }

    public uint? SourceEntityId { get; }

    private static AreaMapOverlayMarkerKind ResolveMarkerKind(AreaContentKind kind)
        => kind switch
        {
            AreaContentKind.BossCandidate or AreaContentKind.Boss => AreaMapOverlayMarkerKind.Boss,
            AreaContentKind.Expedition => AreaMapOverlayMarkerKind.Expedition,
            AreaContentKind.Abyss => AreaMapOverlayMarkerKind.Abyss,
            AreaContentKind.Ritual => AreaMapOverlayMarkerKind.Ritual,
            AreaContentKind.Essence => AreaMapOverlayMarkerKind.Essence,
            AreaContentKind.Incursion => AreaMapOverlayMarkerKind.Incursion,
            AreaContentKind.Strongbox => AreaMapOverlayMarkerKind.Strongbox,
            AreaContentKind.OmenAltar => AreaMapOverlayMarkerKind.OmenAltar,
            AreaContentKind.Pollen => AreaMapOverlayMarkerKind.Pollen,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
}

public sealed record AreaMapOverlayScene
{
    public AreaMapOverlayScene(
        AreaMapViewKind viewKind,
        AreaUiRect viewport,
        ImmutableArray<AreaMapOverlayPlacement> placements)
    {
        ViewKind = viewKind;
        Viewport = viewport;
        Placements = placements;
    }

    public AreaMapViewKind ViewKind { get; }

    public AreaUiRect Viewport { get; }

    public ImmutableArray<AreaMapOverlayPlacement> Placements { get; }
}
