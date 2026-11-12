using System.Collections.Immutable;
using FreiAtlas.Licensing;
using FreiAtlas.Patcher;

namespace FreiAtlas.Patcher.Tests;

public sealed class LicenseGateTests
{
    [Fact]
    public void TranslationGateRunsBeforeAnyGameOrFileOperation()
    {
        var facade = new PatcherFacade(null, new DeniedMembershipGate());
        Assert.Throws<MembershipDeniedException>(() => facade.ApplyTranslation());
    }

    [Fact]
    public void MtxGateRunsBeforeAnyGameOrFileOperation()
    {
        var facade = new PatcherFacade(null, new DeniedMembershipGate());
        Assert.Throws<MembershipDeniedException>(() => facade.ApplyMtxPatch(
            null,
            new FreiAtlas.Patcher.MtxPatch.MtxPatchOptions(),
            null,
            out _));
    }

    [Fact]
    public void ColorModGateRunsBeforeAnyGameOrFileOperation()
    {
        var facade = new PatcherFacade(null, new DeniedMembershipGate());
        Assert.Throws<MembershipDeniedException>(() => facade.ApplyColorMods());
    }

    [Fact]
    public void ClaimedBatchContextDoesNotExpireWithLeaseTtl()
    {
        var facade = new PatcherFacade();
        var modules = ImmutableHashSet.Create(ClientPatchModule.Translation);
        var lease = new ClientPatchBatchLease(
            Guid.NewGuid(),
            facade.PatcherInstanceId,
            modules,
            expiresAtMonotonic: 0);
        var context = new PatcherBatchContext(
            lease,
            facade.PatcherInstanceId,
            modules,
            _ => { });

        Assert.True(context.IsValidFor(facade, context, ClientPatchModule.Translation));
    }

    private sealed class DeniedMembershipGate : IMembershipGate
    {
        public MembershipSnapshot Current { get; } = MembershipSnapshot.Unverified("frei");

        public event EventHandler<MembershipSnapshot>? Changed
        {
            add { }
            remove { }
        }

        public MembershipDecision Check(MembershipFeature feature)
            => new(false, MembershipState.ActivationRequired, "需要输入会员卡密。", feature);

        public Task<MembershipDecision> EnsureFreshAsync(
            MembershipFeature feature,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Check(feature));

        public Task<MembershipSnapshot> ActivateAsync(
            string licenseKey,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Current);

        public Task<MembershipSnapshot> RefreshAsync(
            bool forceOnline,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Current);

        public Task<ClientPatchBatchLease> CreateClientPatchBatchLeaseAsync(
            Guid patcherInstanceId,
            ImmutableHashSet<ClientPatchModule> requestedModules,
            CancellationToken cancellationToken = default)
            => throw new MembershipDeniedException("需要输入会员卡密。");
    }
}
