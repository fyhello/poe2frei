using System.Numerics;

namespace FreiAtlas.Core.Pickup;

public sealed record GroundItemLabel(
    nint Address,
    string RawText,
    string CleanText,
    Vector2 Size,
    Vector2 RelativePosition,
    Vector2 Center,
    IReadOnlyList<string>? Texts = null);

public sealed record GroundItemPickupTarget(
    uint EntityId,
    string ItemName,
    float DistanceToPlayer,
    Vector2 LabelCenter,
    nint LabelAddress,
    nint EntityAddress);

public static class PickupTargetCorrelator
{
    public static IReadOnlyList<GroundItemPickupTarget> Correlate(
        PickupSettings settings,
        Vector2? playerGrid,
        float[]? cameraMatrix,
        Vector2 viewport,
        IReadOnlyList<PickupItemCandidate> candidates,
        IReadOnlyList<GroundItemLabel> labels,
        IReadOnlySet<uint>? blacklistedEntityIds = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(labels);

        if (!settings.Enabled || settings.Whitelist is null || settings.Whitelist.Count == 0)
            return [];
        if (playerGrid is not { } player || !float.IsFinite(player.X) || !float.IsFinite(player.Y))
            return [];
        if (!float.IsFinite(settings.PickupRangeGrid) || settings.PickupRangeGrid <= 0f)
            return [];

        // 1. 过滤符合白名单、未在黑名单冷却中、且在近身范围内的实体
        var eligibleCandidates = candidates
            .Where(c => c is not null
                        && c.EntityId != 0
                        && (blacklistedEntityIds is null || !blacklistedEntityIds.Contains(c.EntityId))
                        && settings.IsWhitelisted(c.ItemName, c.ArtName)
                        && c.DistanceToPlayer <= settings.PickupRangeGrid)
            .OrderBy(c => c.DistanceToPlayer)
            .ToList();

        if (eligibleCandidates.Count == 0 || labels.Count == 0)
            return [];

        var remainingLabels = labels.ToList();
        var results = new List<GroundItemPickupTarget>();

        // 2. 逐一为每个近身候选寻找最匹配的屏幕标签
        foreach (var cand in eligibleCandidates)
        {
            var candNorm = PickupTargetSelector.Normalize(cand.ItemName);

            // 寻找文本名称匹配的可用标签
            var matchedLabels = remainingLabels
                .Where(l => MatchesLabel(l, candNorm))
                .ToList();

            if (matchedLabels.Count == 0) continue;

            GroundItemLabel chosenLabel;
            if (matchedLabels.Count == 1)
            {
                chosenLabel = matchedLabels[0];
            }
            else
            {
                // 同名标签有多项：使用粗略投影匹配方向与位置
                if (cameraMatrix is not null
                    && cand.WorldPosition.HasValue
                    && PickupScreenProjection.TryProject(cameraMatrix, cand.WorldPosition.Value, viewport, out var projPos))
                {
                    chosenLabel = matchedLabels
                        .OrderBy(l => Vector2.Distance(projPos, l.Center))
                        .First();
                }
                else
                {
                    // 若无相机矩阵，按距离屏幕中心（角色站立点）最近的就近选取
                    var screenCenter = viewport * 0.5f;
                    chosenLabel = matchedLabels
                        .OrderBy(l => Vector2.Distance(screenCenter, l.Center))
                        .First();
                }
            }

            remainingLabels.Remove(chosenLabel);
            results.Add(new GroundItemPickupTarget(
                cand.EntityId,
                cand.ItemName,
                cand.DistanceToPlayer,
                chosenLabel.Center,
                chosenLabel.Address,
                cand.EntityAddress));
        }

        return results;
    }

    private static bool MatchesLabel(GroundItemLabel label, string candNorm)
    {
        var cleanLabel = PickupTargetSelector.Compact(label.CleanText);
        var cleanRaw = PickupTargetSelector.Compact(label.RawText);
        var cleanCand = PickupTargetSelector.Compact(candNorm);

        if (string.Equals(cleanLabel, cleanCand, StringComparison.OrdinalIgnoreCase)
            || cleanRaw.Contains(cleanCand, StringComparison.OrdinalIgnoreCase)
            || cleanCand.Contains(cleanLabel, StringComparison.OrdinalIgnoreCase))
            return true;

        if (label.Texts != null && label.Texts.Count > 0)
        {
            return label.Texts.Any(t =>
            {
                var norm = PickupTargetSelector.Compact(PickupTargetSelector.Normalize(t));
                return string.Equals(norm, cleanCand, StringComparison.OrdinalIgnoreCase)
                       || norm.Contains(cleanCand, StringComparison.OrdinalIgnoreCase)
                       || cleanCand.Contains(norm, StringComparison.OrdinalIgnoreCase);
            });
        }

        return false;
    }
}
