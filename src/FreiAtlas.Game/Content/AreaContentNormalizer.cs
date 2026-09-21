using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Memory;

namespace FreiAtlas.Game.Content;

internal sealed class AreaContentNormalizer
{
    private const string RitualObjectMetadata =
        "Metadata/Terrain/Leagues/Ritual/RitualRuneObject";
    private const string RitualInteractableMetadata =
        "Metadata/Terrain/Leagues/Ritual/RitualRuneInteractable";
    private const float RitualPairingDistance = 2f;

    private readonly AreaContentCatalog _catalog;
    private readonly ExpeditionContentResolver _expedition;
    private readonly MechanicContentResolver _mechanic;
    private readonly BossContentResolver _boss;
    private readonly float _worldToGridRatio;

    public AreaContentNormalizer(
        AreaContentCatalog catalog,
        Poe2MemoryProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _expedition = new ExpeditionContentResolver(catalog);
        _mechanic = new MechanicContentResolver(catalog);
        _boss = new BossContentResolver(catalog);
        _worldToGridRatio = (profile ?? Poe2MemoryProfile.Current).WorldToGridRatio;
    }

    public IReadOnlyList<AreaContentSnapshot> Normalize(
        AreaIdentity area,
        IReadOnlyList<AreaEntitySnapshot> entities,
        IReadOnlyList<AreaLandmarkSnapshot> landmarks,
        IReadOnlyDictionary<uint, IReadOnlyList<AreaContentEvidence>>? evidenceByEntity = null,
        IReadOnlyDictionary<uint, AreaExpeditionDetails>? expeditionDetailsByEntity = null,
        AreaPlayerSnapshot? player = null,
        IReadOnlySet<uint>? rawObservedEntityIds = null)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(landmarks);
        var contents = new List<AreaContentSnapshot>();
        var snapshotEntityIds = entities
            .Select(entity => entity.EntityId)
            .ToHashSet();
        var observedEntityIds = rawObservedEntityIds ?? snapshotEntityIds;
        var handledEntityIds = new HashSet<uint>();
        var matchedMechanicEntityIds = new HashSet<uint>();
        var ritualInteractableEvidence = BuildRitualInteractableEvidence(
            entities,
            evidenceByEntity,
            out var pairedRitualInteractableIds);
        handledEntityIds.UnionWith(pairedRitualInteractableIds);

        foreach (var entity in entities)
        {
            var context = CreateContext(
                area,
                entity,
                landmarks,
                evidenceByEntity,
                ritualInteractableEvidence);
            if (_expedition.TryResolve(context, out var content))
            {
                if (expeditionDetailsByEntity is not null
                    && expeditionDetailsByEntity.TryGetValue(
                        entity.EntityId,
                        out var expeditionDetails))
                {
                    content = content with { ExpeditionDetails = expeditionDetails };
                }

                contents.Add(content);
                handledEntityIds.Add(entity.EntityId);
            }
        }

        foreach (var entity in entities)
        {
            if (handledEntityIds.Contains(entity.EntityId))
            {
                continue;
            }

            var context = CreateContext(
                area,
                entity,
                landmarks,
                evidenceByEntity,
                ritualInteractableEvidence);
            if (_mechanic.TryResolve(context, out var content))
            {
                contents.Add(content);
                handledEntityIds.Add(entity.EntityId);
                matchedMechanicEntityIds.Add(entity.EntityId);
            }
        }

        contents.AddRange(_mechanic.ResolveMissing(
            area,
            observedEntityIds,
            snapshotEntityIds,
            matchedMechanicEntityIds,
            player));

        foreach (var entity in entities)
        {
            if (handledEntityIds.Contains(entity.EntityId))
            {
                continue;
            }

            var context = CreateContext(
                area,
                entity,
                landmarks,
                evidenceByEntity);
            if (_boss.TryResolve(context, out var content))
            {
                contents.Add(content);
                handledEntityIds.Add(entity.EntityId);
            }
        }

        contents.AddRange(_boss.ResolveMissing(
            area,
            observedEntityIds));
        var bosses = contents
            .Where(content => content.Kind == AreaContentKind.Boss)
            .ToArray();
        foreach (var landmark in landmarks.Where(landmark =>
                     _catalog.IsBossLandmark(area, landmark)))
        {
            if (!IsRepresentedByBoss(landmark, bosses))
            {
                contents.Add(CreateBossLandmarkCandidate(area, landmark));
            }
        }

        foreach (var landmark in landmarks.Where(landmark =>
                     landmark.Kind == AreaLandmarkKind.Incursion))
        {
            contents.Add(CreateIncursionLandmarkContent(area, landmark));
        }

        foreach (var entity in entities)
        {
            if (handledEntityIds.Contains(entity.EntityId))
            {
                continue;
            }

            var rawEvidence = GetEvidence(entity.EntityId, evidenceByEntity);
            if (!entity.HasMinimapIcon && rawEvidence.Count == 0)
            {
                continue;
            }

            contents.Add(CreateUnknown(area, entity, rawEvidence));
        }

        return contents;
    }

    private static bool IsRepresentedByBoss(
        AreaLandmarkSnapshot landmark,
        IReadOnlyList<AreaContentSnapshot> bosses)
    {
        foreach (var boss in bosses)
        {
            var association = boss.Evidence.FirstOrDefault(evidence =>
                evidence.Source == BossContentResolver.LandmarkEvidenceSource
                && evidence.Key == BossContentResolver.LandmarkIdEvidenceKey);
            if (association is not null)
            {
                if (association.Value.Equals(
                        landmark.LandmarkId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            if (Vector2.Distance(boss.GridPosition, landmark.GridPosition)
                <= BossContentResolver.BossLandmarkDistance)
            {
                return true;
            }
        }

        return false;
    }

    private AreaContentSnapshot CreateBossLandmarkCandidate(
        AreaIdentity area,
        AreaLandmarkSnapshot landmark)
        => new(
            $"boss-landmark:{area.SessionSequence}:{landmark.LandmarkId}",
            "boss-candidate",
            landmark.DisplayName,
            AreaContentKind.BossCandidate,
            AreaContentPhase.Unknown,
            new Vector3(
                landmark.GridPosition.X * _worldToGridRatio,
                landmark.GridPosition.Y * _worldToGridRatio,
                0f),
            landmark.GridPosition,
            0.65f,
            null,
            [
                new AreaContentEvidence(
                    "Landmark",
                    "TilePath",
                    landmark.TilePath,
                    0.9f),
                new AreaContentEvidence(
                    "Landmark",
                    "Kind",
                    landmark.Kind.ToString(),
                    0.8f)
            ]);

    private AreaContentSnapshot CreateIncursionLandmarkContent(
        AreaIdentity area,
        AreaLandmarkSnapshot landmark)
        => new(
            $"incursion:{area.SessionSequence}:{landmark.LandmarkId}",
            "incursion",
            landmark.DisplayName,
            AreaContentKind.Incursion,
            AreaContentPhase.Available,
            new Vector3(
                landmark.GridPosition.X * _worldToGridRatio,
                landmark.GridPosition.Y * _worldToGridRatio,
                0f),
            landmark.GridPosition,
            0.95f,
            null,
            [
                new AreaContentEvidence(
                    "Landmark",
                    "TilePath",
                    landmark.TilePath,
                    0.95f)
            ]);

    private static AreaContentContext CreateContext(
        AreaIdentity area,
        AreaEntitySnapshot entity,
        IReadOnlyList<AreaLandmarkSnapshot> landmarks,
        IReadOnlyDictionary<uint, IReadOnlyList<AreaContentEvidence>>? evidenceByEntity,
        IReadOnlyDictionary<uint, IReadOnlyList<AreaContentEvidence>>? additionalEvidenceByEntity = null)
        => new(
            area,
            entity,
            landmarks,
            MergeEvidence(
                GetEvidence(entity.EntityId, evidenceByEntity),
                GetEvidence(entity.EntityId, additionalEvidenceByEntity)));

    private static IReadOnlyDictionary<uint, IReadOnlyList<AreaContentEvidence>>
        BuildRitualInteractableEvidence(
            IReadOnlyList<AreaEntitySnapshot> entities,
            IReadOnlyDictionary<uint, IReadOnlyList<AreaContentEvidence>>? evidenceByEntity,
            out IReadOnlySet<uint> pairedInteractableIds)
    {
        var objects = entities
            .Where(entity => IsMetadata(entity.MetadataPath, RitualObjectMetadata))
            .OrderBy(entity => entity.EntityId)
            .ToArray();
        var interactables = entities
            .Where(entity => IsMetadata(entity.MetadataPath, RitualInteractableMetadata))
            .OrderBy(entity => entity.EntityId)
            .ToArray();
        var usedInteractables = new HashSet<uint>();
        var result = new Dictionary<uint, IReadOnlyList<AreaContentEvidence>>();

        foreach (var ritualObject in objects)
        {
            var match = interactables
                .Where(interactable => !usedInteractables.Contains(interactable.EntityId))
                .Select(interactable =>
                    (Entity: interactable,
                     Distance: Vector2.Distance(ritualObject.GridPosition, interactable.GridPosition)))
                .Where(candidate => float.IsFinite(candidate.Distance)
                                    && candidate.Distance <= RitualPairingDistance)
                .OrderBy(candidate => candidate.Distance)
                .ThenBy(candidate => candidate.Entity.EntityId)
                .FirstOrDefault();
            if (match.Entity is null)
            {
                continue;
            }

            usedInteractables.Add(match.Entity.EntityId);
            var isComplete = match.Entity.IsMinimapIconComplete
                             || GetEvidence(match.Entity.EntityId, evidenceByEntity)
                                 .Any(item =>
                                     string.Equals(item.Source, "MinimapIcon", StringComparison.OrdinalIgnoreCase)
                                     && (string.Equals(item.Key, "IsComplete", StringComparison.OrdinalIgnoreCase)
                                         || string.Equals(item.Key, "Completed", StringComparison.OrdinalIgnoreCase))
                                     && string.Equals(item.Value, "true", StringComparison.OrdinalIgnoreCase));
            result[ritualObject.EntityId] =
            [
                new AreaContentEvidence(
                    "RitualInteractable",
                    "MinimapIcon.IsComplete",
                    isComplete ? "true" : "false",
                    0.95f)
            ];
        }

        pairedInteractableIds = usedInteractables;
        return result;
    }

    private static IReadOnlyList<AreaContentEvidence> MergeEvidence(
        IReadOnlyList<AreaContentEvidence> primary,
        IReadOnlyList<AreaContentEvidence> additional)
    {
        if (additional.Count == 0)
        {
            return primary;
        }

        return [.. primary, .. additional];
    }

    private static bool IsMetadata(string candidate, string expected)
        => string.Equals(candidate, expected, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<AreaContentEvidence> GetEvidence(
        uint entityId,
        IReadOnlyDictionary<uint, IReadOnlyList<AreaContentEvidence>>? evidenceByEntity)
        => evidenceByEntity is not null
           && evidenceByEntity.TryGetValue(entityId, out var evidence)
            ? evidence
            : [];

    private static AreaContentSnapshot CreateUnknown(
        AreaIdentity area,
        AreaEntitySnapshot entity,
        IReadOnlyList<AreaContentEvidence> rawEvidence)
    {
        var evidence = new List<AreaContentEvidence>(rawEvidence);
        if (entity.HasMinimapIcon)
        {
            evidence.Add(new AreaContentEvidence(
                "MinimapIcon",
                "Present",
                "true",
                1f));
            evidence.Add(new AreaContentEvidence(
                "MinimapIcon",
                "Completed",
                entity.IsMinimapIconComplete ? "true" : "false",
                0.5f));
        }

        var confidence = evidence.Count == 0
            ? 0f
            : Math.Clamp(evidence.Max(item => item.Confidence), 0f, 1f);
        return new AreaContentSnapshot(
            $"unknown:{area.SessionSequence}:{entity.EntityId}",
            "unknown",
            entity.DisplayName,
            AreaContentKind.Unknown,
            AreaContentPhase.Unknown,
            entity.WorldPosition,
            entity.GridPosition,
            confidence,
            entity.EntityId,
            evidence);
    }
}
