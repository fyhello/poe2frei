using System.Globalization;
using System.Numerics;
using FreiAtlas.Core.Area;

namespace FreiAtlas.Game.Content;

internal sealed class BossContentResolver : IAreaContentStateResolver
{
    internal const float BossLandmarkDistance = 50f;
    internal const string LandmarkEvidenceSource = "Landmark";
    internal const string LandmarkIdEvidenceKey = "Id";

    private readonly AreaContentCatalog _catalog;
    private readonly Dictionary<uint, AreaContentSnapshot> _confirmedBosses = [];
    private bool _hasArea;
    private long _sessionSequence;
    private uint _areaHash;
    private string _areaCode = string.Empty;

    public BossContentResolver(AreaContentCatalog catalog)
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
        if (entity.Disposition != AreaEntityDisposition.Hostile
            || entity.Rarity != AreaEntityRarity.Unique)
        {
            content = default!;
            return false;
        }

        var evidence = new List<AreaContentEvidence>
        {
            new("Entity", "Disposition", "Hostile", 1f),
            new("Entity", "Rarity", "Unique", 1f)
        };
        evidence.AddRange(context.Evidence);

        _confirmedBosses.TryGetValue(entity.EntityId, out var previous);
        var confirmed = _catalog.TryMatchBossEntity(entity, out var catalogEvidence);
        if (confirmed)
        {
            evidence.Add(catalogEvidence);
        }

        var previousLandmarkId = previous?.Evidence.FirstOrDefault(item =>
            item.Source == LandmarkEvidenceSource
            && item.Key == LandmarkIdEvidenceKey);
        if (previousLandmarkId is not null)
        {
            confirmed = true;
            evidence.Add(previousLandmarkId);
        }
        else if (TryFindNearbyBossLandmark(context, out var distance, out var landmark))
        {
            confirmed = true;
            evidence.Add(new AreaContentEvidence(
                LandmarkEvidenceSource,
                "BossDistance",
                distance.ToString("0.###", CultureInfo.InvariantCulture),
                0.9f));
            evidence.Add(new AreaContentEvidence(
                LandmarkEvidenceSource,
                "TilePath",
                landmark.TilePath,
                0.9f));
            evidence.Add(new AreaContentEvidence(
                LandmarkEvidenceSource,
                LandmarkIdEvidenceKey,
                landmark.LandmarkId,
                1f));
        }

        if (!confirmed)
        {
            content = default!;
            return false;
        }

        evidence.Add(new AreaContentEvidence(
            "Life",
            "Current",
            entity.CurrentLife.ToString(CultureInfo.InvariantCulture),
            1f));
        var phase = entity.CurrentLife == 0
            ? AreaContentPhase.Completed
            : AreaContentPhase.Available;
        content = new AreaContentSnapshot(
            $"boss:{context.Area.SessionSequence}:{entity.EntityId}",
            "boss",
            entity.DisplayName,
            AreaContentKind.Boss,
            phase,
            entity.WorldPosition,
            entity.GridPosition,
            0.95f,
            entity.EntityId,
            evidence);
        _confirmedBosses[entity.EntityId] = content;

        return true;
    }

    internal IReadOnlyList<AreaContentSnapshot> ResolveMissing(
        AreaIdentity area,
        IReadOnlySet<uint> observedEntityIds)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(observedEntityIds);
        EnsureArea(area);
        var missing = new List<AreaContentSnapshot>();
        foreach (var entry in _confirmedBosses.OrderBy(entry => entry.Key).ToArray())
        {
            if (observedEntityIds.Contains(entry.Key))
            {
                continue;
            }

            var evidence = entry.Value.Evidence;
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

            var completed = entry.Value with
            {
                Phase = AreaContentPhase.Completed,
                Evidence = evidence
            };
            _confirmedBosses[entry.Key] = completed;
            missing.Add(completed);
        }

        return missing;
    }

    private bool TryFindNearbyBossLandmark(
        AreaContentContext context,
        out float distance,
        out AreaLandmarkSnapshot landmark)
    {
        distance = float.MaxValue;
        landmark = default!;
        foreach (var candidate in context.Landmarks)
        {
            if (!_catalog.IsBossLandmark(context.Area, candidate))
            {
                continue;
            }

            var candidateDistance = Vector2.Distance(
                context.Entity.GridPosition,
                candidate.GridPosition);
            if (candidateDistance > BossLandmarkDistance
                || candidateDistance >= distance)
            {
                continue;
            }

            distance = candidateDistance;
            landmark = candidate;
        }

        return landmark is not null;
    }

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

        _confirmedBosses.Clear();
        _hasArea = true;
        _sessionSequence = area.SessionSequence;
        _areaHash = area.AreaHash;
        _areaCode = area.AreaCode;
    }
}
