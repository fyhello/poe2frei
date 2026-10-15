using FreiAtlas.Core.Memory;
using FreiAtlas.Core.Pickup;
using FreiAtlas.Game.Memory;

namespace FreiAtlas.Host;

internal sealed class PickupCandidateMemorySource : IPickupHoverCandidateSource
{
    private readonly IProcessMemory _memory;
    private readonly GroundItemResearchProbe _probe;
    private readonly Func<System.Numerics.Vector2?>? _getClientSize;
    private bool _disposed;

    public PickupCandidateMemorySource(IProcessMemory memory, Func<System.Numerics.Vector2?>? getClientSize = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _probe = new GroundItemResearchProbe(memory);
        _getClientSize = getClientSize;
    }

    public PickupCandidateScan Scan()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var clientSize = _getClientSize?.Invoke();
        var result = _probe.Capture(clientSize);
        if (result.Area is null || result.PlayerGridPosition is not { } player)
        {
            var reason = result.Diagnostics.FirstOrDefault()?.Message ?? "拾取扫描数据不可用";
            return new(false, null, [], reason);
        }

        var hover = _probe.CaptureHover();

        return new(
            true,
            player,
            result.Candidates
                .Where(candidate => candidate.ItemEntityReady)
                .Select(candidate => new PickupItemCandidate(
                    candidate.EntityId,
                    candidate.ItemName,
                    candidate.ArtName,
                    candidate.GridPosition,
                    candidate.DistanceToPlayer,
                    candidate.WorldPosition,
                    candidate.EntityAddress))
                .ToArray(),
            null,
            hover.MatchedGroundItemEntityId,
            result.CameraWorldToScreenMatrix,
            result.Labels);
    }

    public uint? ScanHoveredGroundItemEntityId()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _probe.CaptureHover().MatchedGroundItemEntityId;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _memory.Dispose();
    }
}
