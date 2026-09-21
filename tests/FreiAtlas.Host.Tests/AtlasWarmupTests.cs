using FreiAtlas.Atlas;
using FreiAtlas.Atlas.Memory;
using FreiAtlas.Core.Atlas;

namespace FreiAtlas.Host.Tests;

public sealed class AtlasWarmupTests
{
    [Fact]
    public void WaitUntilStable_ContinuesPastInitialRebuildSamples()
    {
        var source = new SequenceAtlasUiSource(
            [
                CreateTree("canvas-a"),
                CreateTree("canvas-b"),
                CreateTree("canvas-b"),
                CreateTree("canvas-b")
            ]);
        var provider = new AtlasUiTreeProvider(source, AtlasLayoutProfile.Default);
        var service = new AtlasProviderService(provider, requiredStableSamples: 2);

        var stable = AtlasWarmup.WaitUntilStable(
            service,
            maxSamples: 4,
            sampleInterval: TimeSpan.Zero);

        Assert.NotNull(stable);
        Assert.Equal(AtlasSnapshotStatus.Stable, stable!.Status);
        Assert.Equal("canvas-b", stable.Signature.Split('|')[0]);
    }

    private static AtlasUiTreeSnapshot CreateTree(string canvasToken)
        => new(
            HasUiRoot: true,
            HasAtlasCanvas: true,
            CanvasToken: canvasToken,
            Scale: 1f,
            Nodes: [],
            Edges: [],
            CurrentGrid: null);

    private sealed class SequenceAtlasUiSource(
        IReadOnlyList<AtlasUiTreeSnapshot> snapshots)
        : IAtlasUiSource
    {
        private int _index;

        public AtlasUiTreeSnapshot Read()
        {
            var index = Math.Min(_index++, snapshots.Count - 1);
            return snapshots[index];
        }
    }
}
