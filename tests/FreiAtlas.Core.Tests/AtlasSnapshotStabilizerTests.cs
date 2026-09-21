using FreiAtlas.Core.Atlas;

namespace FreiAtlas.Core.Tests;

public sealed class AtlasSnapshotStabilizerTests
{
    [Fact]
    public void PublishStableSnapshot_AfterThreeEqualSignatures()
    {
        var stabilizer = new AtlasSnapshotStabilizer(requiredStableSamples: 3);
        var snapshot = TestSnapshots.Create("signature-a");

        Assert.Equal(AtlasSnapshotStatus.Loading, stabilizer.Push(snapshot));
        Assert.Equal(AtlasSnapshotStatus.Loading, stabilizer.Push(snapshot));
        Assert.Equal(AtlasSnapshotStatus.Stable, stabilizer.Push(snapshot));
        Assert.NotNull(stabilizer.Current);
        Assert.Equal(AtlasSnapshotStatus.Stable, stabilizer.Current.Status);
        Assert.Equal(snapshot.Signature, stabilizer.Current.Signature);
    }

    [Fact]
    public void SignatureChange_EntersRebuildingAndKeepsLastStableSnapshot()
    {
        var stabilizer = new AtlasSnapshotStabilizer(requiredStableSamples: 2);
        var stable = TestSnapshots.Create("stable");
        var changed = TestSnapshots.Create("changed");

        stabilizer.Push(stable);
        stabilizer.Push(stable);
        Assert.NotNull(stabilizer.Current);
        Assert.Equal(AtlasSnapshotStatus.Stable, stabilizer.Current.Status);
        Assert.Equal(stable.Signature, stabilizer.Current.Signature);

        Assert.Equal(AtlasSnapshotStatus.Rebuilding, stabilizer.Push(changed));
        Assert.NotNull(stabilizer.Current);
        Assert.Equal(AtlasSnapshotStatus.Stable, stabilizer.Current.Status);
        Assert.Equal(stable.Signature, stabilizer.Current.Signature);
    }
}

internal static class TestSnapshots
{
    public static AtlasSnapshot Create(string signature)
    {
        return new AtlasSnapshot(
            DateTimeOffset.Parse("2026-07-29T00:00:00+08:00"),
            AtlasSnapshotStatus.Loading,
            0,
            0,
            [],
            [],
            null,
            AtlasProjection.Identity,
            signature);
    }
}
