using System.Numerics;
using FreiAtlas.Atlas.Memory;
using FreiAtlas.Core.Atlas;

namespace FreiAtlas.Atlas.Tests;

public sealed class AtlasProviderServiceTests
{
    [Fact]
    public void Sample_PublishesStableSnapshotAfterRequiredSamples()
    {
        var source = new MutableAtlasUiSource(CreateTree("canvas-a"));
        var provider = new AtlasUiTreeProvider(source, AtlasLayoutProfile.Default);
        var service = new AtlasProviderService(provider, requiredStableSamples: 3);

        Assert.Equal(AtlasSnapshotStatus.Loading, service.Sample().Status);
        Assert.Equal(AtlasSnapshotStatus.Loading, service.Sample().Status);
        Assert.Equal(AtlasSnapshotStatus.Stable, service.Sample().Status);
        Assert.NotNull(service.Current);
        Assert.Equal(AtlasSnapshotStatus.Stable, service.Current!.Status);
        Assert.True(service.IsAtlasAvailable);
    }

    [Fact]
    public void SignatureChange_RebuildsWhileKeepingLastStableSnapshot()
    {
        var source = new MutableAtlasUiSource(CreateTree("canvas-a"));
        var provider = new AtlasUiTreeProvider(source, AtlasLayoutProfile.Default);
        var service = new AtlasProviderService(provider, requiredStableSamples: 2);

        service.Sample();
        var stable = service.Sample();
        Assert.Equal(AtlasSnapshotStatus.Stable, stable.Status);

        source.Set(CreateTree("canvas-b"));

        var rebuilding = service.Sample();

        Assert.Equal(AtlasSnapshotStatus.Rebuilding, rebuilding.Status);
        Assert.Same(stable, service.Current);
    }

    [Fact]
    public void NodePositionChange_PublishesLatestGeometryWithoutRebuilding()
    {
        var source = new MutableAtlasUiSource(CreateTree("canvas-a"));
        var provider = new AtlasUiTreeProvider(source, AtlasLayoutProfile.Default);
        var service = new AtlasProviderService(provider, requiredStableSamples: 2);

        service.Sample();
        var stable = service.Sample();
        Assert.Equal(AtlasSnapshotStatus.Stable, stable.Status);

        source.Set(CreateTree("canvas-a", relativeX: 40));

        var updated = service.Sample();

        Assert.Equal(AtlasSnapshotStatus.Stable, updated.Status);
        Assert.NotSame(stable, service.Current);
        Assert.Equal(40f, service.Current!.Nodes[0].RelativeX);
    }

    [Fact]
    public void MissingCanvas_KeepsPublishedSnapshotDuringGracePeriod()
    {
        var source = new MutableAtlasUiSource(CreateTree("canvas-a"));
        var provider = new AtlasUiTreeProvider(source, AtlasLayoutProfile.Default);
        var clock = new FakeTimeProvider();
        var service = new AtlasProviderService(
            provider,
            requiredStableSamples: 2,
            timeProvider: clock);

        service.Sample();
        service.Sample();
        var stable = service.Current;
        Assert.NotNull(stable);

        source.Set(new AtlasUiTreeSnapshot(
            HasUiRoot: true,
            HasAtlasCanvas: false,
            CanvasToken: string.Empty,
            Scale: 1f,
            Nodes: [],
            Edges: [],
            CurrentGrid: null));

        var loading = service.Sample();

        Assert.Same(stable, loading);
        Assert.Same(stable, service.Current);
        Assert.True(service.IsAtlasAvailable);
        Assert.True(service.IsAtlasOpen);

        clock.Advance(TimeSpan.FromMilliseconds(401));
        loading = service.Sample();

        Assert.Equal(AtlasSnapshotStatus.Loading, loading.Status);
        Assert.Null(service.Current);
        Assert.False(service.IsAtlasAvailable);
        Assert.False(service.IsAtlasOpen);
    }

    [Fact]
    public void ClosedAtlas_ClearsPublishedSnapshotImmediately()
    {
        var source = new MutableAtlasUiSource(CreateTree("canvas-a"));
        var provider = new AtlasUiTreeProvider(source, AtlasLayoutProfile.Default);
        var clock = new FakeTimeProvider();
        var service = new AtlasProviderService(
            provider,
            requiredStableSamples: 2,
            timeProvider: clock);

        service.Sample();
        service.Sample();
        Assert.NotNull(service.Current);
        Assert.True(service.IsAtlasAvailable);

        source.Set(CreateTree("canvas-a") with { IsAtlasOpen = false });

        var closed = service.Sample();

        Assert.Equal(AtlasSnapshotStatus.Loading, closed.Status);
        Assert.Null(service.Current);
        Assert.False(service.IsAtlasAvailable);
        Assert.False(service.IsAtlasOpen);
    }

    [Fact]
    public void TransientEmptyRead_KeepsLastStableSnapshotDuringGracePeriod()
    {
        var source = new MutableAtlasUiSource(CreateTree("canvas-a"));
        var provider = new AtlasUiTreeProvider(source, AtlasLayoutProfile.Default);
        var clock = new FakeTimeProvider();
        var service = new AtlasProviderService(
            provider,
            requiredStableSamples: 2,
            timeProvider: clock);

        service.Sample();
        var stable = service.Sample();
        Assert.Equal(AtlasSnapshotStatus.Stable, stable.Status);

        source.Set(new AtlasUiTreeSnapshot(
            HasUiRoot: false,
            HasAtlasCanvas: false,
            CanvasToken: string.Empty,
            Scale: 1f,
            Nodes: [],
            Edges: [],
            CurrentGrid: null,
            IsAtlasOpen: false));

        var transient = service.Sample();

        Assert.Same(stable, service.Current);
        Assert.Same(stable, transient);
        Assert.True(service.IsAtlasAvailable);
        Assert.True(service.IsAtlasOpen);

        clock.Advance(TimeSpan.FromMilliseconds(401));
        service.Sample();

        Assert.Null(service.Current);
        Assert.False(service.IsAtlasAvailable);
        Assert.False(service.IsAtlasOpen);
    }

    [Fact]
    public void RefreshProjection_UpdatesCanvasTransformWithoutFullSample()
    {
        var source = new MutableAtlasUiSource(CreateTree("canvas-a"));
        var provider = new AtlasUiTreeProvider(
            source,
            AtlasLayoutProfile.Default,
            () => new AtlasViewport(1600, 1600));
        var service = new AtlasProviderService(provider, requiredStableSamples: 2);

        service.Sample();
        service.Sample();
        var previous = service.Current;
        source.SetTransform(new AtlasUiTransform(new(120, 60), 2f));

        Assert.True(service.RefreshProjection());
        Assert.NotSame(previous, service.Current);
        Assert.Equal(2f, service.Current!.Projection.ScaleX);
        Assert.Equal(2f, service.Current.Projection.ScaleY);
        Assert.Equal(0f, service.Current.Projection.OffsetX);
        Assert.Equal(0f, service.Current.Projection.OffsetY);
    }

    [Fact]
    public void RefreshProjection_UpdatesLiveNodePositionsWithoutRebuildingSnapshot()
    {
        var source = new MutableAtlasUiSource(CreateTree("canvas-a"));
        var provider = new AtlasUiTreeProvider(
            source,
            AtlasLayoutProfile.Default,
            () => new AtlasViewport(1600, 1600));
        var service = new AtlasProviderService(provider, requiredStableSamples: 2);

        service.Sample();
        service.Sample();
        var previous = service.Current!;
        var previousMapName = previous.Nodes[0].DisplayName;
        var previousEdges = previous.Edges;

        source.SetLiveGeometry(
            new AtlasUiLiveGeometry(
                new Dictionary<AtlasGridPos, Vector2>
                {
                    [new AtlasGridPos(0, 0)] = new(320, 240)
                },
                new AtlasUiTransform(new(12, 8), 1.5f)));

        Assert.True(service.RefreshProjection());

        var updated = service.Current!;
        Assert.NotSame(previous, updated);
        Assert.Equal(320f, updated.Nodes[0].RelativeX);
        Assert.Equal(240f, updated.Nodes[0].RelativeY);
        Assert.Equal(previousMapName, updated.Nodes[0].DisplayName);
        Assert.Same(previousEdges, updated.Edges);
        Assert.Equal(1.5f, updated.Projection.ScaleX);
        Assert.Equal(0f, updated.Projection.OffsetX);
        Assert.Equal(0f, updated.Projection.OffsetY);
    }

    [Fact]
    public void Sample_DoesNotOverwriteLiveProjectionWithBackgroundSample()
    {
        var source = new MutableAtlasUiSource(CreateTree("canvas-a"));
        var provider = new AtlasUiTreeProvider(
            source,
            AtlasLayoutProfile.Default,
            () => new AtlasViewport(1600, 1600));
        var service = new AtlasProviderService(provider, requiredStableSamples: 2);

        service.Sample();
        service.Sample();
        source.SetLiveGeometry(
            new AtlasUiLiveGeometry(
                new Dictionary<AtlasGridPos, Vector2>
                {
                    [new AtlasGridPos(0, 0)] = new(320, 240)
                },
                new AtlasUiTransform(Vector2.Zero, 1f)));

        Assert.True(service.RefreshProjection());
        Assert.Equal(320f, service.Current!.Nodes[0].RelativeX);
        Assert.Equal(240f, service.Current.Nodes[0].RelativeY);

        service.Sample();

        Assert.Equal(320f, service.Current!.Nodes[0].RelativeX);
        Assert.Equal(240f, service.Current.Nodes[0].RelativeY);
    }

    [Fact]
    public void RefreshProjection_UsesLiveAtlasZoomInsteadOfStaleSnapshotZoom()
    {
        var source = new MutableAtlasUiSource(CreateTree("canvas-a"));
        var provider = new AtlasUiTreeProvider(
            source,
            AtlasLayoutProfile.Default,
            () => new AtlasViewport(1600, 1600));
        var service = new AtlasProviderService(provider, requiredStableSamples: 2);

        service.Sample();
        service.Sample();
        Assert.Equal(1f, service.Current!.Projection.AtlasZoom);

        source.SetLiveGeometry(
            new AtlasUiLiveGeometry(
                new Dictionary<AtlasGridPos, Vector2>
                {
                    [new AtlasGridPos(0, 0)] = new(100, 100)
                },
                new AtlasUiTransform(Vector2.Zero, 1f),
                AtlasZoom: 0.5f));

        Assert.True(service.RefreshProjection());

        Assert.Equal(0.5f, service.Current!.Projection.AtlasZoom);
        Assert.Equal(0.5f, service.Current.Projection.ScaleX);
    }

    [Fact]
    public void RefreshProjection_UsesValidGeometryDuringTransientInstability()
    {
        var source = new MutableAtlasUiSource(CreateTree("canvas-a"));
        var provider = new AtlasUiTreeProvider(
            source,
            AtlasLayoutProfile.Default,
            () => new AtlasViewport(1600, 1600));
        var service = new AtlasProviderService(provider, requiredStableSamples: 2);

        service.Sample();
        service.Sample();
        var previous = service.Current!;

        source.SetLiveGeometry(
            new AtlasUiLiveGeometry(
                new Dictionary<AtlasGridPos, Vector2>
                {
                    [new AtlasGridPos(0, 0)] = new(100, 100)
                },
                new AtlasUiTransform(Vector2.Zero, 1f),
                AtlasZoom: 0.5f,
                IsStable: false));

        Assert.True(service.RefreshProjection());
        Assert.NotSame(previous, service.Current);
        Assert.Equal(100f, service.Current!.Nodes[0].RelativeX);
        Assert.Equal(100f, service.Current.Nodes[0].RelativeY);
        Assert.Equal(0.5f, service.Current.Projection.ScaleX);
    }

    [Fact]
    public async Task RefreshProjection_RemainsAvailableWhileBackgroundSampleRebuilds()
    {
        var source = new BlockingAtlasUiSource(CreateTree("canvas-a"));
        var provider = new AtlasUiTreeProvider(
            source,
            AtlasLayoutProfile.Default,
            () => new AtlasViewport(1600, 1600));
        var service = new AtlasProviderService(provider, requiredStableSamples: 2);

        service.Sample();
        service.Sample();
        source.SetLiveGeometry(
            new AtlasUiLiveGeometry(
                new Dictionary<AtlasGridPos, Vector2>
                {
                    [new AtlasGridPos(0, 0)] = new(320, 240)
                },
                new AtlasUiTransform(new(12, 8), 1.5f)));
        source.BlockNextRead();

        var sampling = Task.Run(() => service.Sample());
        Assert.True(
            await Task.Run(
                () => source.ReadEntered.Wait(TimeSpan.FromSeconds(1))));

        Assert.True(service.RefreshProjection());
        Assert.Equal(320f, service.Current!.Nodes[0].RelativeX);
        Assert.Equal(240f, service.Current.Nodes[0].RelativeY);

        source.ReleaseRead();
        await sampling.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Sample_DoesNotPublishOlderSnapshotAfterProjectionRefreshWinsRace()
    {
        var source = new BlockingAtlasUiSource(CreateTree("canvas-a"));
        var provider = new AtlasUiTreeProvider(
            source,
            AtlasLayoutProfile.Default,
            () => new AtlasViewport(1600, 1600));
        var service = new AtlasProviderService(provider, requiredStableSamples: 2);

        service.Sample();
        service.Sample();
        source.SetLiveGeometry(
            new AtlasUiLiveGeometry(
                new Dictionary<AtlasGridPos, Vector2>
                {
                    [new AtlasGridPos(0, 0)] = new(320, 240)
                },
                new AtlasUiTransform(Vector2.Zero, 1f)));
        source.BlockNextRead();

        var sampling = Task.Run(() => service.Sample());
        Assert.True(
            await Task.Run(
                () => source.ReadEntered.Wait(TimeSpan.FromSeconds(1))));

        Assert.True(service.RefreshProjection());
        Assert.Equal(320f, service.Current!.Nodes[0].RelativeX);
        Assert.Equal(240f, service.Current.Nodes[0].RelativeY);

        source.ReleaseRead();
        await sampling.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(320f, service.Current!.Nodes[0].RelativeX);
        Assert.Equal(240f, service.Current.Nodes[0].RelativeY);
    }

    private static AtlasUiTreeSnapshot CreateTree(
        string canvasToken,
        float relativeX = 0)
        => new(
            HasUiRoot: true,
            HasAtlasCanvas: true,
            CanvasToken: canvasToken,
            Scale: 1f,
            Nodes:
            [
                new AtlasUiNodeData(
                    new AtlasGridPos(0, 0),
                    "map-a",
                    "地图 A",
                    relativeX,
                    0,
                    40,
                    40,
                    true,
                    true,
                    false,
                    true,
                    [])
            ],
            Edges: [],
            CurrentGrid: new AtlasGridPos(0, 0),
            IsAtlasOpen: true);

    private sealed class MutableAtlasUiSource(AtlasUiTreeSnapshot initial)
        : IAtlasUiSource, IAtlasUiTransformSource, IAtlasUiLiveGeometrySource
    {
        private AtlasUiTreeSnapshot _current = initial;
        private AtlasUiTransform _transform = new(Vector2.Zero, 1f);
        private AtlasUiLiveGeometry? _liveGeometry;

        public AtlasUiTreeSnapshot Read() => _current;

        public void Set(AtlasUiTreeSnapshot snapshot) => _current = snapshot;

        public void SetTransform(AtlasUiTransform transform)
            => _transform = transform;

        public void SetLiveGeometry(AtlasUiLiveGeometry geometry)
            => _liveGeometry = geometry;

        public bool TryReadTransform(out AtlasUiTransform transform)
        {
            transform = _transform;
            return true;
        }

        public bool TryReadLiveGeometry(out AtlasUiLiveGeometry geometry)
        {
            if (_liveGeometry is { } liveGeometry)
            {
                geometry = liveGeometry;
                return true;
            }

            geometry = default;
            return false;
        }
    }

    private sealed class BlockingAtlasUiSource(AtlasUiTreeSnapshot initial)
        : IAtlasUiSource, IAtlasUiTransformSource, IAtlasUiLiveGeometrySource
    {
        private readonly ManualResetEventSlim _releaseRead = new();
        private AtlasUiTreeSnapshot _current = initial;
        private AtlasUiTransform _transform = new(Vector2.Zero, 1f);
        private AtlasUiLiveGeometry? _liveGeometry;
        private int _blockNextRead;

        public ManualResetEventSlim ReadEntered { get; } = new();

        public AtlasUiTreeSnapshot Read()
        {
            if (Interlocked.Exchange(ref _blockNextRead, 0) == 1)
            {
                ReadEntered.Set();
                _releaseRead.Wait();
            }

            return _current;
        }

        public void BlockNextRead()
        {
            ReadEntered.Reset();
            _releaseRead.Reset();
            Volatile.Write(ref _blockNextRead, 1);
        }

        public void ReleaseRead() => _releaseRead.Set();

        public void SetLiveGeometry(AtlasUiLiveGeometry geometry)
            => _liveGeometry = geometry;

        public bool TryReadTransform(out AtlasUiTransform transform)
        {
            transform = _transform;
            return true;
        }

        public bool TryReadLiveGeometry(out AtlasUiLiveGeometry geometry)
        {
            if (_liveGeometry is { } liveGeometry)
            {
                geometry = liveGeometry;
                return true;
            }

            geometry = default;
            return false;
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;
    }
}
