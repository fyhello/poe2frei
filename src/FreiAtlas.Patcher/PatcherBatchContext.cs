using System.Collections.Immutable;
using FreiAtlas.Licensing;

namespace FreiAtlas.Patcher;

public sealed class PatcherBatchContext : IDisposable
{
    private readonly ClientPatchBatchLease _lease;
    private readonly Action<PatcherBatchContext> _release;
    private bool _disposed;

    internal PatcherBatchContext(
        ClientPatchBatchLease lease,
        Guid ownerInstanceId,
        ImmutableHashSet<ClientPatchModule> allowedModules,
        Action<PatcherBatchContext> release)
    {
        _lease = lease;
        _release = release;
        BatchId = lease.BatchId;
        OwnerInstanceId = ownerInstanceId;
        AllowedModules = allowedModules;
    }

    public Guid BatchId { get; }

    internal Guid OwnerInstanceId { get; }

    internal IReadOnlySet<ClientPatchModule> AllowedModules { get; }

    public bool IsDisposed => _disposed;

    internal bool IsValidFor(
        PatcherFacade owner,
        PatcherBatchContext? active,
        ClientPatchModule module)
    {
        return !_disposed
            && OwnerInstanceId == owner.PatcherInstanceId
            && active is not null
            && active.BatchId == BatchId
            && AllowedModules.Contains(module);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lease.Release();
        _release(this);
    }
}
