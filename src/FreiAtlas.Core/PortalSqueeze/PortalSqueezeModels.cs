using System.Numerics;
using FreiAtlas.Core.Recovery;

namespace FreiAtlas.Core.PortalSqueeze;

public enum PortalSqueezeState
{
    Idle,
    Scanning,
    Interacting,
    WaitingAreaChange,
    Completed,
    Failed,
    Paused
}

public sealed record PortalSqueezeSettings
{
    public bool Enabled { get; init; }

    public string Hotkey { get; init; } = "Ctrl+J";

    public float MaxDistanceGrid { get; init; } = 80f;

    public int MaxAttempts { get; init; } = 3;

    public int ConfirmTimeoutMilliseconds { get; init; } = 8_000;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Hotkey)
            || !RecoveryKey.TryParse(Hotkey, out _)
            || !float.IsFinite(MaxDistanceGrid)
            || MaxDistanceGrid is < 1f or > 500f
            || MaxAttempts is < 1 or > 5
            || ConfirmTimeoutMilliseconds is < 1_000 or > 60_000)
        {
            throw new ArgumentException("挤门设置无效：请检查触发按键、距离和重试参数。");
        }
    }
}

public sealed record PortalCandidate(
    uint EntityId,
    nint EntityAddress,
    string MetadataPath,
    Vector2 GridPosition,
    float DistanceToPlayer);

public sealed record PortalCandidateScan(
    int ProcessId,
    long SessionSequence,
    nint GameStateSlot,
    nint InGameState,
    nint AreaInstance,
    uint AreaHash,
    Vector2 PlayerGridPosition,
    IReadOnlyList<PortalCandidate> Candidates,
    string? Error)
{
    public bool IsValid => Error is null && InGameState != 0 && AreaInstance != 0;
}

public sealed record PortalInteractionResult(
    bool Started,
    bool Completed,
    bool LayoutPending,
    string Message,
    nint AreaInstanceBefore,
    nint AreaInstanceAfter);

public sealed record PortalSqueezeSnapshot(
    PortalSqueezeState State,
    string Message,
    PortalCandidate? Target,
    int Attempts,
    DateTimeOffset UpdatedAt)
{
    public static PortalSqueezeSnapshot Idle { get; } = new(
        PortalSqueezeState.Idle,
        "未触发",
        null,
        0,
        DateTimeOffset.UtcNow);
}
