using System.Collections.Immutable;

namespace FreiAtlas.Licensing;

public enum MembershipState
{
    Unverified,
    Active,
    Stale,
    Grace,
    Expired,
    Blocked,
    NetworkUnavailable,
    ConfigurationError,
    ActivationRequired,
}

public enum MembershipFeature
{
    AtlasOverlay,
    ClientPatchTranslation,
    ClientPatchMtx,
    ClientPatchColorMod,
    ClientPatchBatch,
    Recovery,
    ReadOnly,
}

public enum ClientPatchModule
{
    Translation,
    Mtx,
    ColorMod,
}

public sealed record MembershipDecision(
    bool Allowed,
    MembershipState State,
    string Message,
    MembershipFeature Feature);

public sealed record MembershipSnapshot(
    MembershipState State,
    bool FreshnessBlocked,
    bool CanStartPaid,
    bool CanContinueOverlay,
    string? Reason,
    string? LastValidationFailure,
    long LicenseExpiresAt,
    long TrialEndsAt,
    long TokenExpiresAt,
    long EffectiveExpiresAt,
    long GraceEndsAt,
    long LastSuccessAt,
    string ProductId)
{
    public static MembershipSnapshot Unverified(string productId) => new(
        MembershipState.Unverified,
        false,
        false,
        false,
        null,
        null,
        0,
        0,
        0,
        0,
        0,
        0,
        productId);
}

public sealed record MembershipGrant(
    Guid GrantId,
    Guid PatcherInstanceId,
    IReadOnlySet<ClientPatchModule> AllowedModules,
    long IssuedAtMonotonic,
    long ExpiresAtMonotonic);

public sealed class ClientPatchBatchLease
{
    private const int Issued = 0;
    private const int Claimed = 1;
    private const int Released = 2;

    private int _state = Issued;

    internal ClientPatchBatchLease(
        Guid batchId,
        Guid patcherInstanceId,
        IReadOnlySet<ClientPatchModule> allowedModules,
        long expiresAtMonotonic)
    {
        BatchId = batchId;
        PatcherInstanceId = patcherInstanceId;
        AllowedModules = allowedModules;
        ExpiresAtMonotonic = expiresAtMonotonic;
    }

    public Guid BatchId { get; }

    public Guid PatcherInstanceId { get; }

    public IReadOnlySet<ClientPatchModule> AllowedModules { get; }

    internal long ExpiresAtMonotonic { get; }

    internal bool TryClaim(Guid patcherInstanceId, long nowMonotonic)
    {
        if (patcherInstanceId != PatcherInstanceId)
        {
            return false;
        }

        if (nowMonotonic > ExpiresAtMonotonic)
        {
            return false;
        }

        return Interlocked.CompareExchange(ref _state, Claimed, Issued) == Issued;
    }

    internal void Release()
    {
        Interlocked.CompareExchange(ref _state, Released, Claimed);
    }

    internal bool IsReleased => Volatile.Read(ref _state) == Released;
}

public interface IMembershipGate
{
    MembershipSnapshot Current { get; }

    event EventHandler<MembershipSnapshot>? Changed;

    MembershipDecision Check(MembershipFeature feature);

    Task<MembershipDecision> EnsureFreshAsync(
        MembershipFeature feature,
        CancellationToken cancellationToken = default);

    Task<MembershipSnapshot> ActivateAsync(
        string licenseKey,
        CancellationToken cancellationToken = default);

    Task<MembershipSnapshot> RefreshAsync(
        bool forceOnline,
        CancellationToken cancellationToken = default);

    Task<ClientPatchBatchLease> CreateClientPatchBatchLeaseAsync(
        Guid patcherInstanceId,
        ImmutableHashSet<ClientPatchModule> requestedModules,
        CancellationToken cancellationToken = default);
}
