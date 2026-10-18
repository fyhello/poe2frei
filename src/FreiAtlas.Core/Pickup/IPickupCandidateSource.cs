using System.Numerics;

namespace FreiAtlas.Core.Pickup;

public sealed record PickupCandidateScan(
    bool IsValid,
    Vector2? PlayerGridPosition,
    IReadOnlyList<PickupItemCandidate> Candidates,
    string? Error,
    uint? HoveredGroundItemEntityId = null,
    float[]? CameraWorldToScreenMatrix = null,
    IReadOnlyList<GroundItemLabel>? Labels = null);

/// <summary>Read-only source for ground-item candidates.</summary>
public interface IPickupCandidateSource : IDisposable
{
    PickupCandidateScan Scan();
}
public interface IPickupHoverCandidateSource : IPickupCandidateSource
{
    uint? ScanHoveredGroundItemEntityId();
}
