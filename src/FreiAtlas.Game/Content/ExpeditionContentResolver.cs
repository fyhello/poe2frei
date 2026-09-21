using FreiAtlas.Core.Area;

namespace FreiAtlas.Game.Content;

internal sealed class ExpeditionContentResolver : IAreaContentStateResolver
{
    private readonly AreaContentCatalog _catalog;

    public ExpeditionContentResolver(AreaContentCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public bool TryResolve(
        AreaContentContext context,
        out AreaContentSnapshot content)
    {
        ArgumentNullException.ThrowIfNull(context);
        var entity = context.Entity;
        if (!_catalog.IsExpedition(entity))
        {
            content = default!;
            return false;
        }

        var evidence = new List<AreaContentEvidence>(context.Evidence)
        {
            new("Catalog", "ExactMetadata", entity.MetadataPath, 1f)
        };
        AreaContentPhase phase;
        float confidence;
        if (entity.HasMinimapIcon)
        {
            phase = entity.IsMinimapIconComplete
                ? AreaContentPhase.Completed
                : HasSelectedRecipe(context.Evidence)
                    ? AreaContentPhase.Selected
                    : AreaContentPhase.Available;
            confidence = 0.95f;
            evidence.Add(new AreaContentEvidence(
                "MinimapIcon",
                "Completed",
                entity.IsMinimapIconComplete ? "true" : "false",
                0.95f));
        }
        else
        {
            phase = AreaContentPhase.Unknown;
            confidence = 0.7f;
            evidence.Add(new AreaContentEvidence(
                "MinimapIcon",
                "Present",
                "false",
                1f));
        }

        content = new AreaContentSnapshot(
            $"expedition:{context.Area.SessionSequence}:{entity.EntityId}",
            "expedition",
            entity.DisplayName,
            AreaContentKind.Expedition,
            phase,
            entity.WorldPosition,
            entity.GridPosition,
            confidence,
            entity.EntityId,
            evidence);
        return true;
    }

    private static bool HasSelectedRecipe(
        IReadOnlyList<AreaContentEvidence> evidence)
        => evidence.Any(item =>
            string.Equals(
                item.Key,
                ExpeditionEvidenceKeys.SelectedRecipePresence,
                StringComparison.Ordinal)
            && string.Equals(item.Value, "true", StringComparison.Ordinal));
}
