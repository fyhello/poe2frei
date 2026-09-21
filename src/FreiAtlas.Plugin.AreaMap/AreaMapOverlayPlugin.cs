using System.Collections.Immutable;
using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Contracts;
using FreiAtlas.Core.Settings;

namespace FreiAtlas.Plugin.AreaMap;

public sealed class AreaMapOverlayPlugin
{
    private readonly IAreaMapApi _areaMapApi;
    private readonly AreaMapProjectionProfile _profile;
    private readonly IAreaExpeditionValueProvider? _expeditionValueProvider;

    public AreaMapOverlayPlugin(
        IAreaMapApi areaMapApi,
        AreaMapProjectionProfile profile,
        IAreaExpeditionValueProvider? expeditionValueProvider = null)
    {
        _areaMapApi = areaMapApi ?? throw new ArgumentNullException(nameof(areaMapApi));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _expeditionValueProvider = expeditionValueProvider;
    }

    public AreaMapOverlayScene? Build(AreaMapDisplaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var snapshot = _areaMapApi.Current;
        if (!IsUsableSnapshot(snapshot)
            || !TrySelectView(snapshot.MapViews, out var view, out var parameters)
            || view.Viewport is not { } viewport)
        {
            return null;
        }

        var placements = ImmutableArray.CreateBuilder<AreaMapOverlayPlacement>();
        var strongboxEntityIds = snapshot.Contents
            .Where(content => content.Kind == AreaContentKind.Strongbox)
            .Select(content => content.SourceEntityId)
            .OfType<uint>()
            .ToHashSet();
        foreach (var content in snapshot.Contents)
        {
            if (!ShouldShow(content.Kind, settings))
            {
                continue;
            }

            var visualState = MapVisualState(content.Kind, content.Phase);
            if (visualState is null
                || !AreaMapProjection.TryProject(
                    snapshot.Player!.GridPosition,
                    content.GridPosition,
                    view,
                    parameters,
                    out var center)
                || !Contains(viewport, center))
            {
                continue;
            }

            if (view.Kind == AreaMapViewKind.MiniMap)
            {
                center = ClampMiniMapCenter(viewport, center);
            }

            var expeditionDetails = content.ExpeditionDetails;
            var expeditionMarker = content.Kind == AreaContentKind.Expedition
                ? new AreaMapExpeditionMarker(
                    expeditionDetails?.HoleCount,
                    _expeditionValueProvider?.GetValueText(expeditionDetails))
                : null;

            placements.Add(new AreaMapOverlayPlacement(
                content.InstanceId,
                content.Kind,
                content.Phase,
                center,
                visualState.Value,
                expeditionMarker,
                markerKind: MapMarkerKind(content.Kind),
                sourceEntityId: content.SourceEntityId));
        }

        foreach (var entity in snapshot.Entities)
        {
            var markerKind = MapDynamicMarkerKind(entity, strongboxEntityIds);
            if (markerKind is null
                || !ShouldShow(markerKind.Value, settings)
                || !AreaMapProjection.TryProject(
                    snapshot.Player!.GridPosition,
                    entity.GridPosition,
                    view,
                    parameters,
                    out var center)
                || !Contains(viewport, center))
            {
                continue;
            }

            if (view.Kind == AreaMapViewKind.MiniMap)
            {
                center = ClampMiniMapCenter(viewport, center);
            }

            placements.Add(new AreaMapOverlayPlacement(
                $"entity:{snapshot.Area.SessionSequence}:{entity.EntityId}:{markerKind.Value.ToString().ToLowerInvariant()}",
                AreaContentKind.Unknown,
                AreaContentPhase.Available,
                center,
                MapVisualState(markerKind.Value),
                markerKind: markerKind.Value,
                sourceEntityId: entity.EntityId));
        }

        return new AreaMapOverlayScene(
            view.Kind,
            viewport,
            placements.ToImmutable());
    }

    private bool IsUsableSnapshot(AreaMapSnapshot snapshot)
        => string.Equals(snapshot.ProfileId, _profile.ProfileId, StringComparison.Ordinal)
           && snapshot.Status is AreaMapSnapshotStatus.Stable or AreaMapSnapshotStatus.Degraded
           && snapshot.Area.AreaHash != 0
           && snapshot.Player is { } player
           && IsFinite(player.WorldPosition)
           && IsFinite(player.GridPosition);

    private bool TrySelectView(
        AreaMapViewsSnapshot views,
        out AreaMapViewSnapshot view,
        out AreaMapProjectionParameters parameters)
    {
        var hasLargeMap = IsVerifiedVisible(
            views.LargeMap,
            AreaMapViewKind.LargeMap);
        var hasMiniMap = IsVerifiedVisible(
            views.MiniMap,
            AreaMapViewKind.MiniMap);
        if (hasLargeMap == hasMiniMap)
        {
            view = default!;
            parameters = default!;
            return false;
        }

        view = hasLargeMap ? views.LargeMap : views.MiniMap;
        parameters = hasLargeMap ? _profile.LargeMap : _profile.MiniMap;
        return IsValidActiveView(view)
               && parameters.Kind == view.Kind
               && (view.Kind != AreaMapViewKind.MiniMap
                   || IsValidMiniMapInsetRatio(_profile.MiniMapVisibleCenterSafeInsetRatio));
    }

    private static bool IsVerifiedVisible(
        AreaMapViewSnapshot view,
        AreaMapViewKind expectedKind)
        => view.Kind == expectedKind
           && view.Availability == AreaMapViewAvailability.Verified
           && view.IsVisible;

    private static bool IsValidActiveView(AreaMapViewSnapshot view)
        => float.IsFinite(view.Zoom)
           && view.Zoom > 0f
           && IsFinite(view.Shift)
           && view.RotationRadians == 0f
           && !view.RotatesWithPlayer
           && view.Viewport is { } viewport
           && IsValidViewport(viewport);

    private static bool IsValidViewport(AreaUiRect viewport)
        => float.IsFinite(viewport.X)
           && float.IsFinite(viewport.Y)
           && float.IsFinite(viewport.Width)
           && float.IsFinite(viewport.Height)
           && viewport.Width > 0f
           && viewport.Height > 0f
           && float.IsFinite(viewport.X + viewport.Width)
           && float.IsFinite(viewport.Y + viewport.Height);

    private static bool IsValidMiniMapInsetRatio(float ratio)
        => float.IsFinite(ratio) && ratio >= 0f;

    private static AreaMapOverlayVisualState? MapVisualState(
        AreaContentKind kind,
        AreaContentPhase phase)
        => (kind, phase) switch
        {
            (AreaContentKind.BossCandidate, _) => AreaMapOverlayVisualState.BossInactive,
            (AreaContentKind.Boss, AreaContentPhase.Available
                or AreaContentPhase.Active
                or AreaContentPhase.Selected) => AreaMapOverlayVisualState.BossAvailable,
            (AreaContentKind.Boss, AreaContentPhase.Completed) =>
                AreaMapOverlayVisualState.BossCompleted,
            (AreaContentKind.Expedition, AreaContentPhase.Available) =>
                AreaMapOverlayVisualState.ExpeditionAvailable,
            (AreaContentKind.Expedition, AreaContentPhase.Selected
                or AreaContentPhase.Active) => AreaMapOverlayVisualState.ExpeditionSelected,
            (AreaContentKind.Expedition, AreaContentPhase.Completed) =>
                AreaMapOverlayVisualState.ExpeditionCompleted,
            (AreaContentKind.Abyss, AreaContentPhase.Available) =>
                AreaMapOverlayVisualState.AbyssAvailable,
            (AreaContentKind.Abyss, AreaContentPhase.Completed) =>
                AreaMapOverlayVisualState.AbyssCompleted,
            (AreaContentKind.Ritual, AreaContentPhase.Available) =>
                AreaMapOverlayVisualState.RitualAvailable,
            (AreaContentKind.Ritual, AreaContentPhase.Completed) =>
                AreaMapOverlayVisualState.RitualCompleted,
            (AreaContentKind.Breach, AreaContentPhase.Available) =>
                AreaMapOverlayVisualState.BreachAvailable,
            (AreaContentKind.Breach, AreaContentPhase.Completed) =>
                AreaMapOverlayVisualState.BreachCompleted,
            (AreaContentKind.Essence, AreaContentPhase.Available) =>
                AreaMapOverlayVisualState.EssenceAvailable,
            (AreaContentKind.Essence, AreaContentPhase.Completed) =>
                AreaMapOverlayVisualState.EssenceCompleted,
            (AreaContentKind.Incursion, AreaContentPhase.Available) =>
                AreaMapOverlayVisualState.IncursionAvailable,
            (AreaContentKind.Strongbox, AreaContentPhase.Available) =>
                AreaMapOverlayVisualState.StrongboxAvailable,
            _ => null
        };

    private static AreaMapOverlayVisualState MapVisualState(
        AreaMapOverlayMarkerKind kind)
        => kind switch
        {
            AreaMapOverlayMarkerKind.RareMonster => AreaMapOverlayVisualState.RareMonster,
            AreaMapOverlayMarkerKind.RareChest => AreaMapOverlayVisualState.RareChest,
            AreaMapOverlayMarkerKind.UniqueChest => AreaMapOverlayVisualState.UniqueChest,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private static AreaMapOverlayMarkerKind MapMarkerKind(AreaContentKind kind)
        => kind switch
        {
            AreaContentKind.BossCandidate or AreaContentKind.Boss => AreaMapOverlayMarkerKind.Boss,
            AreaContentKind.Expedition => AreaMapOverlayMarkerKind.Expedition,
            AreaContentKind.Abyss => AreaMapOverlayMarkerKind.Abyss,
            AreaContentKind.Ritual => AreaMapOverlayMarkerKind.Ritual,
            AreaContentKind.Breach => AreaMapOverlayMarkerKind.Breach,
            AreaContentKind.Essence => AreaMapOverlayMarkerKind.Essence,
            AreaContentKind.Incursion => AreaMapOverlayMarkerKind.Incursion,
            AreaContentKind.Strongbox => AreaMapOverlayMarkerKind.Strongbox,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private static AreaMapOverlayMarkerKind? MapDynamicMarkerKind(
        AreaEntitySnapshot entity,
        IReadOnlySet<uint> strongboxEntityIds)
    {
        if (entity.Category == AreaEntityCategory.Monster
            && entity.Disposition == AreaEntityDisposition.Hostile
            && entity.Rarity == AreaEntityRarity.Rare
            && entity.CurrentLife > 0)
        {
            return AreaMapOverlayMarkerKind.RareMonster;
        }

        if (entity.Category != AreaEntityCategory.Chest
            || entity.ChestState != AreaChestState.Closed
            || strongboxEntityIds.Contains(entity.EntityId))
        {
            return null;
        }

        return entity.Rarity switch
        {
            AreaEntityRarity.Rare => AreaMapOverlayMarkerKind.RareChest,
            AreaEntityRarity.Unique => AreaMapOverlayMarkerKind.UniqueChest,
            _ => null
        };
    }

    private static bool ShouldShow(
        AreaContentKind kind,
        AreaMapDisplaySettings settings)
        => kind switch
        {
            AreaContentKind.BossCandidate or AreaContentKind.Boss => settings.ShowBoss,
            AreaContentKind.Expedition => settings.ShowExpedition,
            AreaContentKind.Abyss => settings.ShowAbyss,
            AreaContentKind.Ritual => settings.ShowRitual,
            AreaContentKind.Breach => settings.ShowBreach,
            AreaContentKind.Essence => settings.ShowEssence,
            AreaContentKind.Incursion => settings.ShowIncursion,
            AreaContentKind.Strongbox => settings.ShowStrongbox,
            _ => false
        };

    private static bool ShouldShow(
        AreaMapOverlayMarkerKind kind,
        AreaMapDisplaySettings settings)
        => kind switch
        {
            AreaMapOverlayMarkerKind.RareMonster => settings.ShowRareMonster,
            AreaMapOverlayMarkerKind.RareChest or AreaMapOverlayMarkerKind.UniqueChest =>
                settings.ShowRareChests,
            _ => false
        };

    private Vector2 ClampMiniMapCenter(AreaUiRect viewport, Vector2 center)
    {
        var requestedInset = viewport.Height * _profile.MiniMapVisibleCenterSafeInsetRatio;
        var inset = MathF.Min(
            requestedInset,
            MathF.Min(viewport.Width, viewport.Height) / 2f);
        return new Vector2(
            Math.Clamp(center.X, viewport.X + inset, viewport.X + viewport.Width - inset),
            Math.Clamp(center.Y, viewport.Y + inset, viewport.Y + viewport.Height - inset));
    }

    private static bool Contains(AreaUiRect viewport, Vector2 point)
        => point.X >= viewport.X
           && point.X <= viewport.X + viewport.Width
           && point.Y >= viewport.Y
           && point.Y <= viewport.Y + viewport.Height;

    private static bool IsFinite(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X)
           && float.IsFinite(value.Y)
           && float.IsFinite(value.Z);
}
