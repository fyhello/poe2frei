using FreiAtlas.Atlas.Memory;
using FreiAtlas.Core.Atlas;
using FreiAtlas.Core.Contracts;
using System.Numerics;

namespace FreiAtlas.Atlas;

public sealed class AtlasProviderService : IAtlasApi
    , IAtlasLiveGeometryApi
{
    private static readonly TimeSpan EmptyReadGracePeriod =
        TimeSpan.FromMilliseconds(400);
    private readonly AtlasUiTreeProvider _provider;
    private readonly AtlasSnapshotStabilizer _stabilizer;
    private readonly TimeProvider _timeProvider;
    private readonly object _sampleGate = new();
    private AtlasSnapshot? _latest;
    private AtlasSnapshot? _published;
    private int _projectionRefreshInProgress;
    private int _projectionVersion;
    private int _sampledProjectionVersion;
    private bool _atlasAvailable;
    private bool _atlasOpen;
    private DateTimeOffset _lastHealthyReadAt;

    public AtlasProviderService(
        AtlasUiTreeProvider provider,
        int requiredStableSamples = 3,
        TimeProvider? timeProvider = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _stabilizer = new AtlasSnapshotStabilizer(requiredStableSamples);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public AtlasSnapshot? Current => Volatile.Read(ref _published);
    public bool IsAtlasAvailable => Volatile.Read(ref _atlasAvailable);
    public bool IsAtlasOpen => Volatile.Read(ref _atlasOpen);
    public IReadOnlyList<string> Diagnostics => _provider.Diagnostics;

    public event Action<AtlasSnapshot>? SnapshotChanged;

    public bool TryReadLiveGeometry(
        IReadOnlySet<AtlasGridPos> grids,
        out AtlasLiveRenderGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(grids);

        if (_provider.TryReadLiveRenderGeometry(grids, out var liveGeometry)
            && _provider is { } provider
            && provider.TryGetViewport(out var viewport))
        {
            var projection = AtlasProjection.FromCanvas(
                viewport,
                liveGeometry.Transform.CanvasRelativePosition,
                liveGeometry.Transform.CanvasScale,
                liveGeometry.AtlasZoom,
                Vector2.One);
            geometry = new AtlasLiveRenderGeometry(
                liveGeometry.NodePositions,
                projection,
                liveGeometry.IsStable);
            return true;
        }

        geometry = default;
        return false;
    }

    public bool RefreshProjection()
    {
        if (Interlocked.Exchange(ref _projectionRefreshInProgress, 1) != 0)
        {
            return false;
        }

        try
        {
            if (Volatile.Read(ref _published) is not { } current
                || !_provider.TryRefreshProjection(current, out var updated))
            {
                return false;
            }

            if (!ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _published,
                        updated,
                        current),
                    current))
            {
                return false;
            }

            Volatile.Write(ref _latest, updated);
            Interlocked.Increment(ref _projectionVersion);
            SnapshotChanged?.Invoke(updated);
            return true;
        }
        finally
        {
            Volatile.Write(ref _projectionRefreshInProgress, 0);
        }
    }

    public AtlasSnapshot Sample()
    {
        AtlasSnapshot result;
        AtlasSnapshot? changed = null;
        var publishedBeforeSample = Volatile.Read(ref _published);
        var sampledProjectionVersionBeforeSample = Volatile.Read(
            ref _sampledProjectionVersion);
        lock (_sampleGate)
        {
            var raw = _provider.Read();
            _latest = raw;
            var now = _timeProvider.GetUtcNow();

            if (!_provider.IsAtlasAvailable || !_provider.IsAtlasOpen)
            {
                var isTransientRead =
                    !_provider.HasUiRoot || !_provider.HasAtlasCanvas;
                if (isTransientRead
                    && publishedBeforeSample is { IsAtlasOpen: true }
                    && now - _lastHealthyReadAt <= EmptyReadGracePeriod)
                {
                    Volatile.Write(ref _atlasAvailable, true);
                    Volatile.Write(ref _atlasOpen, true);
                    result = publishedBeforeSample;
                    return result;
                }

                _stabilizer.Reset();
                Volatile.Write(ref _published, null);
                Volatile.Write(ref _atlasAvailable, false);
                Volatile.Write(ref _atlasOpen, false);
                result = raw with
                {
                    Status = AtlasSnapshotStatus.Loading,
                    IsAtlasOpen = false
                };
            }
            else
            {
                Volatile.Write(ref _atlasAvailable, true);
                Volatile.Write(ref _atlasOpen, true);
                _lastHealthyReadAt = now;
                var status = _stabilizer.Push(raw);
                if (status == AtlasSnapshotStatus.Stable
                    && _stabilizer.Current is { } stable)
                {
                    // The low-frequency tree sample owns topology and metadata, while the
                    // projection refresh owns live pan/zoom geometry. Keep the newer geometry
                    // when both snapshots describe the same currently open atlas.
                    var published = PublishStableSnapshot(
                        stable,
                        sampledProjectionVersionBeforeSample);
                    changed = published;
                    result = published;
                }
                else
                {
                    result = raw with { Status = status };
                }
            }
        }

        if (changed is { } snapshot)
        {
            SnapshotChanged?.Invoke(snapshot);
        }

        return result;
    }

    private AtlasSnapshot PublishStableSnapshot(
        AtlasSnapshot sampled,
        int sampledProjectionVersionBeforeSample)
    {
        while (true)
        {
            var publishedBeforeCommit = Volatile.Read(ref _published);
            var projectionVersionAtCommit = Volatile.Read(
                ref _projectionVersion);
            var published = MergePublishedGeometry(
                sampled,
                publishedBeforeCommit,
                projectionVersionAtCommit > sampledProjectionVersionBeforeSample);
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _published,
                        published,
                        publishedBeforeCommit),
                    publishedBeforeCommit))
            {
                Volatile.Write(
                    ref _sampledProjectionVersion,
                    projectionVersionAtCommit);
                return published;
            }
        }
    }

    private static AtlasSnapshot MergePublishedGeometry(
        AtlasSnapshot sampled,
        AtlasSnapshot? published,
        bool preserveLiveGeometry)
    {
        if (!preserveLiveGeometry
            || published is null
            || !published.IsAtlasOpen
            || published.Nodes.Count == 0)
        {
            return sampled;
        }

        var livePositions = published.Nodes
            .ToDictionary(
                node => node.Grid,
                node => (node.RelativeX, node.RelativeY));
        var nodes = sampled.Nodes
            .Select(node =>
                livePositions.TryGetValue(node.Grid, out var position)
                    ? node with
                    {
                        RelativeX = position.RelativeX,
                        RelativeY = position.RelativeY
                    }
                    : node)
            .ToArray();

        return sampled with
        {
            Nodes = nodes,
            Projection = published.Projection
        };
    }

    public AtlasSnapshot? ReadLatest()
        => Current ?? Volatile.Read(ref _latest);
}
