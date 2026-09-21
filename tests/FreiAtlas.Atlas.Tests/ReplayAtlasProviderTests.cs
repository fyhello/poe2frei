using FreiAtlas.Core.Atlas;
using FreiAtlas.Core.Contracts;
using FreiAtlas.Atlas.Replay;

namespace FreiAtlas.Atlas.Tests;

public sealed class ReplayAtlasProviderTests
{
    [Fact]
    public void StableReplay_ContainsExpectedAtlasCardinality()
    {
        var provider = ReplayAtlasProvider.Load(
            "data/atlas/replay/pid7928-stable.json");

        var snapshot = provider.Read();

        Assert.Equal(891, snapshot.NodeCount);
        Assert.Equal(1420, snapshot.EdgeCount);
        Assert.Equal(891, snapshot.Nodes.Count);
        Assert.Equal(1420, snapshot.Edges.Count);
    }

    [Fact]
    public void StableReplay_ContainsOnlyValidNodeAndEdgeReferences()
    {
        var provider = ReplayAtlasProvider.Load(
            "data/atlas/replay/pid7928-stable.json");

        var snapshot = provider.Read();
        var grids = snapshot.Nodes.Select(node => node.Grid).ToHashSet();

        Assert.Equal(snapshot.Nodes.Count, grids.Count);
        Assert.All(snapshot.Edges, edge =>
        {
            Assert.Contains(edge.From, grids);
            Assert.Contains(edge.To, grids);
            Assert.NotEqual(edge.From, edge.To);
        });
    }

    [Fact]
    public void LoadingReplay_DoesNotPublishAsStable()
    {
        var provider = ReplayAtlasProvider.Load(
            "data/atlas/replay/pid7928-loading.json");

        Assert.NotEqual(AtlasSnapshotStatus.Stable, provider.Read().Status);
    }

    [Fact]
    public void ReplayProviderImplementsAtlasApi()
    {
        IAtlasApi provider = ReplayAtlasProvider.Load(
            "data/atlas/replay/pid7928-stable.json");

        Assert.NotNull(provider.Current);
        Assert.Equal(AtlasSnapshotStatus.Stable, provider.Current!.Status);
    }
}
