using System.Numerics;
using FreiAtlas.Core.Contracts;
using FreiAtlas.Core.PortalSqueeze;

namespace FreiAtlas.QuickAssist.Tests;

public sealed class PortalSqueezeServiceTests
{
    [Fact]
    public async Task Disabled_DoesNotScan()
    {
        var source = new FakeSource();
        var interaction = new FakeInteraction();
        await using var service = new PortalSqueezeService(_ => source, interaction);

        await service.TriggerAsync(1234);

        Assert.Equal(0, source.ScanCount);
        Assert.Equal(PortalSqueezeState.Paused, service.Current.State);
    }

    [Fact]
    public async Task Trigger_SelectsNearestPortalAndCompletesAfterAreaChange()
    {
        var source = new FakeSource
        {
            Candidates =
            [
                Candidate(10, 8),
                Candidate(11, 3)
            ],
            ChangeAreaAfterScan = 2
        };
        var interaction = new FakeInteraction();
        await using var service = new PortalSqueezeService(_ => source, interaction);
        service.Configure(new PortalSqueezeSettings { Enabled = true, ConfirmTimeoutMilliseconds = 2_000 });

        await service.TriggerAsync(1234);

        Assert.Equal((uint)11, interaction.Target?.EntityId);
        Assert.Equal(PortalSqueezeState.Completed, service.Current.State);
        Assert.Equal(1, interaction.Calls);
    }

    [Fact]
    public async Task MissingCandidate_FailsWithoutInteraction()
    {
        var source = new FakeSource();
        var interaction = new FakeInteraction();
        await using var service = new PortalSqueezeService(_ => source, interaction);
        service.Configure(new PortalSqueezeSettings { Enabled = true });

        await service.TriggerAsync(1234);

        Assert.Equal(PortalSqueezeState.Failed, service.Current.State);
        Assert.Equal(0, interaction.Calls);
        Assert.Contains("没有", service.Current.Message);
    }

    private static PortalCandidate Candidate(uint id, float distance)
        => new(id, (nint)(0x10000 + id), "Metadata/MultiplexPortal", Vector2.Zero, distance);

    private sealed class FakeSource : IPortalCandidateSource
    {
        public IReadOnlyList<PortalCandidate> Candidates { get; init; } = [];
        public int ChangeAreaAfterScan { get; init; } = int.MaxValue;
        public int ScanCount { get; private set; }

        public PortalCandidateScan Scan(float maxDistanceGrid)
        {
            ScanCount++;
            return new(
                1234,
                1,
                (nint)0x20000,
                (nint)0x30000,
                ScanCount >= ChangeAreaAfterScan ? (nint)0x50000 : (nint)0x40000,
                1,
                Vector2.Zero,
                Candidates,
                null);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeInteraction : IPortalInteraction
    {
        public int Calls { get; private set; }
        public PortalCandidate? Target { get; private set; }

        public PortalInteractionResult Interact(
            PortalCandidateScan scan,
            PortalCandidate target,
            TimeSpan timeout)
        {
            Calls++;
            Target = target;
            return new(true, false, false, "交互线程已启动", scan.AreaInstance, 0);
        }
    }
}
