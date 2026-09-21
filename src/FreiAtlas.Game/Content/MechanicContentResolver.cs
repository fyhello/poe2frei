using System.Globalization;
using System.Numerics;
using FreiAtlas.Core.Area;

namespace FreiAtlas.Game.Content;

internal sealed class MechanicContentResolver : IAreaContentStateResolver
{
    internal const float EssenceCompletionDistance = 120f;
    internal const int EssenceMissingSamplesToComplete = 3;

    private readonly AreaContentCatalog _catalog;
    private readonly Dictionary<uint, AreaContentSnapshot> _retainedMechanics = [];
    private readonly Dictionary<uint, int> _missingSampleCounts = [];
    private bool _hasArea;
    private long _sessionSequence;
    private uint _areaHash;
    private string _areaCode = string.Empty;

    public MechanicContentResolver(AreaContentCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public bool TryResolve(
        AreaContentContext context,
        out AreaContentSnapshot content)
    {
        ArgumentNullException.ThrowIfNull(context);
        EnsureArea(context.Area);
        var entity = context.Entity;
        if (!_catalog.TryMatchMechanicEntity(
                entity,
                out var kind,
                out var catalogEvidence))
        {
            content = default!;
            return false;
        }

        _missingSampleCounts.Remove(entity.EntityId);
        var evidence = new List<AreaContentEvidence>(capacity: 1 + context.Evidence.Count)
        {
            catalogEvidence
        };
        evidence.AddRange(context.Evidence);
        var phase = ResolveObservedPhase(kind, evidence);
        if (_retainedMechanics.TryGetValue(entity.EntityId, out var previous)
            && previous.Kind == kind
            && previous.Phase == AreaContentPhase.Completed)
        {
            phase = AreaContentPhase.Completed;
            var retainedLifecycleEvidence = previous.Evidence
                .Where(item => item.Source == "EntityLifecycle")
                .ToArray();
            if (retainedLifecycleEvidence.Length > 0)
            {
                evidence.RemoveAll(item =>
                    item.Source == "EntityLifecycle"
                    && retainedLifecycleEvidence.Any(previousItem =>
                        previousItem.Key == item.Key));
                evidence.AddRange(retainedLifecycleEvidence);
            }
        }

        var contentId = kind.ToString().ToLowerInvariant();
        content = new AreaContentSnapshot(
            $"{contentId}:{context.Area.SessionSequence}:{entity.EntityId}",
            contentId,
            string.IsNullOrWhiteSpace(entity.DisplayName)
                ? kind.ToString()
                : entity.DisplayName,
            kind,
            phase,
            entity.WorldPosition,
            entity.GridPosition,
            evidence.Count == 0
                ? 0f
                : Math.Clamp(evidence.Max(item => item.Confidence), 0f, 1f),
            entity.EntityId,
            evidence);
        if (ShouldRetainWhenUnloaded(kind))
        {
            _retainedMechanics[entity.EntityId] = content;
        }
        else
        {
            _retainedMechanics.Remove(entity.EntityId);
        }

        return true;
    }

    internal IReadOnlyList<AreaContentSnapshot> ResolveMissing(
        AreaIdentity area,
        IReadOnlySet<uint> observedEntityIds,
        IReadOnlySet<uint> snapshotEntityIds,
        IReadOnlySet<uint> matchedMechanicEntityIds,
        AreaPlayerSnapshot? player = null)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(observedEntityIds);
        ArgumentNullException.ThrowIfNull(snapshotEntityIds);
        ArgumentNullException.ThrowIfNull(matchedMechanicEntityIds);
        EnsureArea(area);
        var retained = new List<AreaContentSnapshot>();
        foreach (var entry in _retainedMechanics.OrderBy(entry => entry.Key).ToArray())
        {
            if (observedEntityIds.Contains(entry.Key))
            {
                if (!snapshotEntityIds.Contains(entry.Key))
                {
                    _missingSampleCounts.Remove(entry.Key);
                    retained.Add(entry.Value);
                }
                else if (!matchedMechanicEntityIds.Contains(entry.Key))
                {
                    _retainedMechanics.Remove(entry.Key);
                    _missingSampleCounts.Remove(entry.Key);
                }
                else
                {
                    _missingSampleCounts.Remove(entry.Key);
                }

                continue;
            }

            var current = entry.Value;
            var phase = current.Phase;
            if (current.Kind == AreaContentKind.Essence
                && phase != AreaContentPhase.Completed
                && player is not null
                && IsFinite(player.GridPosition))
            {
                var distance = Vector2.Distance(
                    player.GridPosition,
                    current.GridPosition);
                if (float.IsFinite(distance)
                    && distance <= EssenceCompletionDistance)
                {
                    var missingCount = _missingSampleCounts.TryGetValue(
                        entry.Key,
                        out var previousCount)
                        ? previousCount + 1
                        : 1;
                    _missingSampleCounts[entry.Key] = missingCount;
                    if (missingCount >= EssenceMissingSamplesToComplete)
                    {
                        phase = AreaContentPhase.Completed;
                        current = current with
                        {
                            Evidence = AppendCompletionEvidence(
                                current.Evidence,
                                EssenceMissingSamplesToComplete,
                                distance)
                        };
                    }
                }
            }

            var evidence = current.Evidence;
            if (!evidence.Any(item =>
                    item.Source == "EntityLifecycle"
                    && item.Key == "MissingFromSample"))
            {
                evidence =
                [
                    .. evidence,
                    new AreaContentEvidence(
                        "EntityLifecycle",
                        "MissingFromSample",
                        "true",
                        0.9f)
                ];
            }

            var missing = current with
            {
                Phase = phase,
                Evidence = evidence
            };
            _retainedMechanics[entry.Key] = missing;
            retained.Add(missing);
        }

        return retained;
    }

    private static bool ShouldRetainWhenUnloaded(AreaContentKind kind)
        => kind is AreaContentKind.Abyss
            or AreaContentKind.Ritual
            or AreaContentKind.Breach
            or AreaContentKind.Essence;

    private static AreaContentPhase ResolveObservedPhase(
        AreaContentKind kind,
        IReadOnlyList<AreaContentEvidence> evidence)
    {
        if (kind is AreaContentKind.Abyss or AreaContentKind.Essence)
        {
            return evidence.Any(item =>
                       string.Equals(
                           item.Source,
                           MechanicStateEvidenceReader.MinimapIconSource,
                           StringComparison.Ordinal)
                       && string.Equals(
                           item.Key,
                           MechanicStateEvidenceReader.CompleteKey,
                           StringComparison.Ordinal)
                       && string.Equals(item.Value, "true", StringComparison.Ordinal))
                ? AreaContentPhase.Completed
                : AreaContentPhase.Available;
        }

        if (kind == AreaContentKind.Ritual)
        {
            return evidence.Any(item =>
                       string.Equals(item.Source, "RitualInteractable", StringComparison.Ordinal)
                       && string.Equals(item.Key, "MinimapIcon.IsComplete", StringComparison.Ordinal)
                       && string.Equals(item.Value, "true", StringComparison.OrdinalIgnoreCase))
                ? AreaContentPhase.Completed
                : AreaContentPhase.Available;
        }

        if (kind == AreaContentKind.Breach)
        {
            return evidence.Any(item =>
                       string.Equals(
                           item.Source,
                           MechanicStateEvidenceReader.StateMachineSource,
                           StringComparison.Ordinal)
                       && string.Equals(
                           item.Key,
                           MechanicStateEvidenceReader.StateKey,
                           StringComparison.Ordinal)
                       && string.Equals(item.Value, "0x00000001", StringComparison.Ordinal))
                ? AreaContentPhase.Completed
                : AreaContentPhase.Available;
        }

        return AreaContentPhase.Available;
    }

    private static IReadOnlyList<AreaContentEvidence> AppendCompletionEvidence(
        IReadOnlyList<AreaContentEvidence> existing,
        int missingCount,
        float distance)
    {
        var evidence = existing
            .Where(item => item.Source != "EntityLifecycle"
                           || item.Key is not ("ConfirmedMissingNearPlayer"
                               or "MissingSampleCount"
                               or "PlayerDistance"))
            .ToList();
        evidence.Add(new AreaContentEvidence(
            "EntityLifecycle",
            "ConfirmedMissingNearPlayer",
            "true",
            0.9f));
        evidence.Add(new AreaContentEvidence(
            "EntityLifecycle",
            "MissingSampleCount",
            missingCount.ToString(CultureInfo.InvariantCulture),
            0.9f));
        evidence.Add(new AreaContentEvidence(
            "EntityLifecycle",
            "PlayerDistance",
            distance.ToString("0.###", CultureInfo.InvariantCulture),
            0.9f));
        return evidence;
    }

    private static bool IsFinite(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private void EnsureArea(AreaIdentity area)
    {
        var changed = !_hasArea
                      || _sessionSequence != area.SessionSequence
                      || _areaHash != area.AreaHash
                      || !string.Equals(
                          _areaCode,
                          area.AreaCode,
                          StringComparison.OrdinalIgnoreCase);
        if (!changed)
        {
            return;
        }

        _retainedMechanics.Clear();
        _missingSampleCounts.Clear();
        _hasArea = true;
        _sessionSequence = area.SessionSequence;
        _areaHash = area.AreaHash;
        _areaCode = area.AreaCode;
    }
}
