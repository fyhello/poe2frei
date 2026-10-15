using System.Numerics;

namespace FreiAtlas.Core.Pickup;

/// <summary>Settings for pickup decisions. This feature never chooses a movement destination.</summary>
public sealed record PickupSettings
{
    public bool Enabled { get; init; }

    private readonly IReadOnlyList<string> _whitelist = [];

    /// <summary>Exact item display names or stable art names allowed for pickup.</summary>
    public IReadOnlyList<string> Whitelist
    {
        get => _whitelist;
        init => _whitelist = value is null ? [] : PickupTargetSelector.ParseWhitelist(value);
    }

    /// <summary>Maximum player-to-item distance in grid units for pickup eligibility (near-body range, e.g. 6-10).</summary>
    public float PickupRangeGrid { get; init; } = 8f;

    /// <summary>Timeout in milliseconds to verify item disappearance before next action.</summary>
    public int DisappearTimeoutMilliseconds { get; init; } = 800;

    /// <summary>Maximum retry attempts on a single item before temporary cooldown.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Whether a modifier key is required to be held while moving to trigger pickup.</summary>
    public bool ModifierKeyEnabled { get; init; }

    /// <summary>Modifier key required if ModifierKeyEnabled is true.</summary>
    public string ModifierKey { get; init; } = "Shift";

    public void Validate()
    {
        if (!float.IsFinite(PickupRangeGrid) || PickupRangeGrid is <= 0f or > 100f)
            throw new ArgumentOutOfRangeException(nameof(PickupRangeGrid), "拾取范围必须大于 0 且不超过 100 格。");

        if (DisappearTimeoutMilliseconds is < 100 or > 10000)
            throw new ArgumentOutOfRangeException(nameof(DisappearTimeoutMilliseconds), "消失确认超时必须在 100 到 10000 毫秒之间。");

        if (MaxAttempts is < 1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), "最大重试次数必须在 1 到 10 之间。");

        if (Whitelist is null || Whitelist.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("拾取白名单不能为 null 或包含空项目。", nameof(Whitelist));
    }

    public bool IsWhitelisted(string? itemName, string? artName = null)
        => PickupTargetSelector.IsWhitelisted(Whitelist, itemName, artName);

    public bool Equals(PickupSettings? other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;
        return Enabled == other.Enabled
               && Math.Abs(PickupRangeGrid - other.PickupRangeGrid) < 0.001f
               && DisappearTimeoutMilliseconds == other.DisappearTimeoutMilliseconds
               && MaxAttempts == other.MaxAttempts
               && ModifierKeyEnabled == other.ModifierKeyEnabled
               && string.Equals(ModifierKey, other.ModifierKey, StringComparison.OrdinalIgnoreCase)
               && Whitelist.SequenceEqual(other.Whitelist, StringComparer.Ordinal);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Enabled);
        hash.Add(PickupRangeGrid);
        hash.Add(DisappearTimeoutMilliseconds);
        hash.Add(MaxAttempts);
        hash.Add(ModifierKeyEnabled);
        hash.Add(ModifierKey, StringComparer.OrdinalIgnoreCase);
        foreach (var item in Whitelist)
        {
            hash.Add(item, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }
}

/// <summary>Read-only data required to decide whether a ground item is eligible.</summary>
public sealed record PickupItemCandidate(
    uint EntityId,
    string ItemName,
    string ArtName,
    Vector2 GridPosition,
    float DistanceToPlayer,
    Vector3? WorldPosition = null,
    nint EntityAddress = 0);
public enum PickupDecisionReason
{
    Matched,
    Disabled,
    EmptyWhitelist,
    MissingPlayerPosition,
    InvalidRange,
    NoEligibleItem
}

public sealed record PickupDecision(
    PickupDecisionReason Reason,
    PickupItemCandidate? Target)
{
    public bool ShouldPickup => Reason == PickupDecisionReason.Matched && Target is not null;
}

/// <summary>Pure selector: returns an eligible nearby item and never plans or sends movement/input.</summary>
public static class PickupTargetSelector
{
    public static PickupDecision Select(
        PickupSettings settings,
        Vector2? playerGridPosition,
        IEnumerable<PickupItemCandidate> candidates,
        uint? preferredEntityId = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(candidates);

        if (!settings.Enabled)
            return new(PickupDecisionReason.Disabled, null);
        if (settings.Whitelist is null || settings.Whitelist.Count == 0)
            return new(PickupDecisionReason.EmptyWhitelist, null);
        if (playerGridPosition is not { } player || !IsFinite(player))
            return new(PickupDecisionReason.MissingPlayerPosition, null);
        if (!float.IsFinite(settings.PickupRangeGrid) || settings.PickupRangeGrid <= 0f)
            return new(PickupDecisionReason.InvalidRange, null);

        var eligible = candidates
            .Where(candidate => candidate is not null
                                && candidate.EntityId != 0
                                && IsFinite(candidate.GridPosition)
                                && settings.IsWhitelisted(candidate.ItemName, candidate.ArtName)
                                && Vector2.Distance(player, candidate.GridPosition) <= settings.PickupRangeGrid)
            .OrderBy(candidate => Vector2.Distance(player, candidate.GridPosition))
            .ThenBy(candidate => candidate.EntityId)
            .ToArray();

        var target = preferredEntityId is { } preferred
            ? eligible.FirstOrDefault(candidate => candidate.EntityId == preferred) ?? eligible.FirstOrDefault()
            : eligible.FirstOrDefault();

        return target is null
            ? new(PickupDecisionReason.NoEligibleItem, null)
            : new(PickupDecisionReason.Matched, target);
    }

    public static bool IsWhitelisted(IReadOnlyList<string> whitelist, PickupItemCandidate candidate)
        => IsWhitelisted(whitelist, candidate.ItemName, candidate.ArtName);

    public static bool IsWhitelisted(IReadOnlyList<string> whitelist, string? itemName, string? artName = null)
    {
        if (whitelist is null || whitelist.Count == 0) return false;
        if (string.IsNullOrWhiteSpace(itemName) && string.IsNullOrWhiteSpace(artName)) return false;

        var cleanItem = !string.IsNullOrWhiteSpace(itemName) ? Compact(Normalize(itemName)) : null;
        var cleanArt = !string.IsNullOrWhiteSpace(artName) ? Compact(Normalize(artName)) : null;

        foreach (var entry in whitelist)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var cleanEntry = Compact(Normalize(entry));
            if (string.IsNullOrWhiteSpace(cleanEntry)) continue;

            if (cleanItem != null && cleanItem.Contains(cleanEntry, StringComparison.OrdinalIgnoreCase))
                return true;

            if (cleanArt != null && cleanArt.Contains(cleanEntry, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static string Compact(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
    }

    public static IReadOnlyList<string> ParseWhitelist(IEnumerable<string>? entries)
    {
        if (entries is null) return [];
        var result = new List<string>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var parts = entry.Split([',', '，', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                if (!string.IsNullOrWhiteSpace(part))
                    result.Add(part);
            }
        }
        return result;
    }
    public static string Normalize(string value)
    {
        var normalized = new string(value.Trim().Where(character => character is not ('\u200B' or '\u200C' or '\u200D' or '\uFEFF')).ToArray());
        var prefixEnd = 0;
        while (prefixEnd < normalized.Length && char.IsDigit(normalized[prefixEnd])) prefixEnd++;
        if (prefixEnd > 0 && prefixEnd < normalized.Length && normalized[prefixEnd] is 'x' or 'X')
        {
            prefixEnd++;
            while (prefixEnd < normalized.Length && char.IsWhiteSpace(normalized[prefixEnd])) prefixEnd++;
            normalized = normalized[prefixEnd..];
        }

        var annotationStart = normalized.LastIndexOf('[');
        if (annotationStart >= 0 && normalized.EndsWith(']'))
            normalized = normalized[..annotationStart].TrimEnd();

        return normalized;
    }

    private static bool IsFinite(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y);
}
