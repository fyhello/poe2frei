using FreiAtlas.Atlas.Content;
using FreiAtlas.Atlas.Graph;
using FreiAtlas.Core.Atlas;
using System.Globalization;
using System.Numerics;

namespace FreiAtlas.Atlas.Memory;

public sealed record AtlasUiNodeData(
    AtlasGridPos Grid,
    string? MapId,
    string? DisplayName,
    float RelativeX,
    float RelativeY,
    float Width,
    float Height,
    bool IsVisible,
    bool IsAccessible,
    bool IsCompleted,
    bool IsCurrent,
    IReadOnlyList<string> RawContentCodes,
    bool? IsDiscovered = null)
{
    public string Kind { get; init; } = "Normal";
    public string MapType { get; init; } = string.Empty;
    public string MapGroup { get; init; } = string.Empty;
    public IReadOnlyList<string> MapDataTags { get; init; } = Array.Empty<string>();
    public uint RawContentValue { get; init; }
    public IReadOnlyList<uint> ContentVectorValues { get; init; } = Array.Empty<uint>();
    public int IconType { get; init; }
    public IReadOnlyList<string> ContentBadges { get; init; } = Array.Empty<string>();
    public uint RegionKey { get; init; }
    public byte RawState { get; init; }
    public byte Biome { get; init; }
    public byte RawFlags { get; init; }
    public byte Completion { get; init; }
}

public sealed record AtlasUiEdgeData(
    AtlasGridPos From,
    AtlasGridPos To);

public sealed record AtlasUiTreeSnapshot(
    bool HasUiRoot,
    bool HasAtlasCanvas,
    string CanvasToken,
    float Scale,
    IReadOnlyList<AtlasUiNodeData> Nodes,
    IReadOnlyList<AtlasUiEdgeData> Edges,
    AtlasGridPos? CurrentGrid,
    Vector2 CanvasRelativePosition = default,
    float CanvasScale = 1f,
    Vector2 NodeSize = default,
    bool IsAtlasOpen = true);

public interface IAtlasUiSource
{
    AtlasUiTreeSnapshot Read();
    IReadOnlyList<string> Diagnostics => [];
}

public readonly record struct AtlasUiTransform(
    Vector2 CanvasRelativePosition,
    float CanvasScale);

public interface IAtlasUiTransformSource
{
    bool TryReadTransform(out AtlasUiTransform transform);
}

public readonly record struct AtlasUiLiveGeometry(
    IReadOnlyDictionary<AtlasGridPos, Vector2> NodePositions,
    AtlasUiTransform Transform,
    float AtlasZoom = 1f,
    bool IsStable = true);

public interface IAtlasUiLiveGeometrySource
{
    bool TryReadLiveGeometry(out AtlasUiLiveGeometry geometry);
}

public interface IAtlasUiSelectiveLiveGeometrySource
    : IAtlasUiLiveGeometrySource
{
    bool TryReadLiveGeometry(
        IReadOnlySet<AtlasGridPos> grids,
        out AtlasUiLiveGeometry geometry);
}

public interface IAtlasUiLiveRenderGeometrySource
{
    bool TryReadLiveRenderGeometry(
        IReadOnlySet<AtlasGridPos> grids,
        out AtlasUiLiveGeometry geometry);
}

public sealed class AtlasUiTreeProvider
{
    private const float LiveProjectionMargin = 320f;
    private readonly IAtlasUiSource _source;
    private readonly AtlasLayoutProfile _profile;
    private readonly AtlasGraphProvider _graphProvider = new();
    private readonly List<string> _diagnostics = [];
    private readonly Func<AtlasViewport?>? _viewportProvider;

    public AtlasUiTreeProvider(
        IAtlasUiSource source,
        AtlasLayoutProfile profile,
        Func<AtlasViewport?>? viewportProvider = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _viewportProvider = viewportProvider;
    }

    public IReadOnlyList<string> Diagnostics => _diagnostics;
    public bool HasUiRoot { get; private set; }
    public bool HasAtlasCanvas { get; private set; }
    public bool IsAtlasAvailable { get; private set; }
    public bool IsAtlasOpen { get; private set; }

    public bool TryGetViewport(out AtlasViewport viewport)
    {
        viewport = _viewportProvider?.Invoke() ?? default;
        return viewport.IsValid;
    }

    public bool TryReadLiveGeometry(
        IReadOnlySet<AtlasGridPos> grids,
        out AtlasUiLiveGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(grids);

        if (_source is IAtlasUiSelectiveLiveGeometrySource selectiveSource
            && selectiveSource.TryReadLiveGeometry(grids, out geometry))
        {
            return true;
        }

        if (_source is IAtlasUiLiveGeometrySource liveGeometrySource
            && liveGeometrySource.TryReadLiveGeometry(out geometry))
        {
            return true;
        }

        geometry = default;
        return false;
    }

    public bool TryReadLiveRenderGeometry(
        IReadOnlySet<AtlasGridPos> grids,
        out AtlasUiLiveGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(grids);

        if (_source is IAtlasUiLiveRenderGeometrySource renderSource
            && renderSource.TryReadLiveRenderGeometry(grids, out geometry))
        {
            return true;
        }

        return TryReadLiveGeometry(grids, out geometry);
    }

    public bool TryRefreshProjection(
        AtlasSnapshot snapshot,
        out AtlasSnapshot updated)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (_viewportProvider?.Invoke() is not { } viewport)
        {
            updated = snapshot;
            return false;
        }

        var requestedGrids = SelectLiveNodeGrids(snapshot);
        AtlasUiLiveGeometry liveGeometry;
        if (_source is IAtlasUiSelectiveLiveGeometrySource selectiveSource)
        {
            if (!selectiveSource.TryReadLiveGeometry(
                    requestedGrids,
                    out liveGeometry))
            {
                updated = snapshot;
                return false;
            }
        }
        else if (_source is IAtlasUiLiveGeometrySource liveGeometrySource)
        {
            if (!liveGeometrySource.TryReadLiveGeometry(out liveGeometry))
            {
                if (_source is not IAtlasUiTransformSource transformSource
                    || !transformSource.TryReadTransform(out var transform))
                {
                    updated = snapshot;
                    return false;
                }

                liveGeometry = new AtlasUiLiveGeometry(
                    new Dictionary<AtlasGridPos, Vector2>(),
                    transform);
            }
        }
        else if (_source is IAtlasUiTransformSource transformSource
                 && transformSource.TryReadTransform(out var transform))
        {
            liveGeometry = new AtlasUiLiveGeometry(
                new Dictionary<AtlasGridPos, Vector2>(),
                transform);
        }
        else
        {
            updated = snapshot;
            return false;
        }

        var projection = AtlasProjection.FromCanvas(
            viewport,
            liveGeometry.Transform.CanvasRelativePosition,
            liveGeometry.Transform.CanvasScale,
            liveGeometry.AtlasZoom,
            Vector2.One);

        var positionsChanged = false;
        var nodes = new AtlasNodeSnapshot[snapshot.Nodes.Count];
        for (var index = 0; index < snapshot.Nodes.Count; index++)
        {
            var node = snapshot.Nodes[index];
            if (liveGeometry.NodePositions.TryGetValue(node.Grid, out var position)
                && float.IsFinite(position.X)
                && float.IsFinite(position.Y)
                && (position.X != node.RelativeX
                    || position.Y != node.RelativeY))
            {
                node = node with
                {
                    RelativeX = position.X,
                    RelativeY = position.Y
                };
                positionsChanged = true;
            }

            nodes[index] = node;
        }

        // The game can expose stale/culled RelativePos values for fogged nodes. The
        // low-frequency read already normalizes those nodes from the grid geometry;
        // keep that same invariant after each live refresh so a raw outlier cannot
        // overwrite a valid position and make the overlay disappear or flicker.
        var normalizedNodes = AtlasGridPositionNormalizer.Normalize(nodes);
        if (!ReferenceEquals(normalizedNodes, nodes))
        {
            nodes = normalizedNodes.ToArray();
            positionsChanged = true;
        }

        if (!positionsChanged
            && projection == snapshot.Projection)
        {
            updated = snapshot;
            return false;
        }

        updated = snapshot with
        {
            CapturedAt = DateTimeOffset.UtcNow,
            Nodes = nodes,
            Projection = projection,
            Status = AtlasSnapshotStatus.Stable
        };
        return true;
    }

    private static IReadOnlySet<AtlasGridPos> SelectLiveNodeGrids(
        AtlasSnapshot snapshot)
        => snapshot.Nodes
            .Select(node => node.Grid)
            .ToHashSet();

    public AtlasSnapshot Read()
    {
        _diagnostics.Clear();
        var tree = _source.Read();
        _diagnostics.AddRange(_source.Diagnostics);
        HasUiRoot = tree.HasUiRoot;
        HasAtlasCanvas = tree.HasAtlasCanvas;
        IsAtlasOpen = tree.HasUiRoot && tree.HasAtlasCanvas && tree.IsAtlasOpen;
        IsAtlasAvailable = IsAtlasOpen;

        if (!tree.HasUiRoot)
        {
            _diagnostics.Add("UI root is unavailable.");
            return EmptySnapshot(tree, "ui-root-missing");
        }

        if (!tree.HasAtlasCanvas)
        {
            _diagnostics.Add("Atlas canvas is unavailable.");
            return EmptySnapshot(tree, "atlas-canvas-missing");
        }

        if (!tree.IsAtlasOpen)
        {
            _diagnostics.Add("Atlas canvas is present but not visible.");
            return EmptySnapshot(tree, "atlas-closed");
        }

        var nodes = new List<AtlasNodeSnapshot>();
        foreach (var rawNode in tree.Nodes)
        {
            if (!IsValidNode(rawNode))
            {
                _diagnostics.Add(
                    $"Invalid node size at {rawNode.Grid}: "
                    + $"{rawNode.Width:0.##}x{rawNode.Height:0.##}.");
                continue;
            }

            var decodedContents = rawNode.RawContentCodes
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Where(code => !ContentDictionary.IsNoiseCode(code))
                .Select(code => AtlasContentDecoder.Decode(code, "ui-tree"))
                .ToArray();
            var metadataContents = rawNode.MapDataTags
                .Select(tag => tag.Equals(
                    "boss",
                    StringComparison.OrdinalIgnoreCase)
                    ? "MapBoss"
                    : tag)
                .Where(tag => ContentDictionary.TryGetContentId(tag, out _))
                .Select(tag => AtlasContentDecoder.Decode(
                    tag,
                    "atlas-map-metadata"))
                .ToArray();
            var inferredContent = AtlasContentDecoder.InferFromNodeSignals(
                rawNode.IconType,
                rawNode.RawContentValue,
                rawNode.RawFlags);
            var supplementalContents = inferredContent is null
                ? metadataContents
                : metadataContents.Append(inferredContent);
            var contents = decodedContents
                .Concat(supplementalContents
                    .Where(candidate => !decodedContents.Any(content =>
                        content.ContentId.Equals(
                            candidate.ContentId,
                            StringComparison.OrdinalIgnoreCase)))
                    .DistinctBy(
                        content => content.ContentId,
                        StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(content => content.Priority)
                .ToArray();

            nodes.Add(new AtlasNodeSnapshot(
                rawNode.Grid,
                rawNode.MapId,
                rawNode.DisplayName,
                rawNode.RelativeX,
                rawNode.RelativeY,
                rawNode.IsVisible,
                rawNode.IsAccessible,
                rawNode.IsCompleted,
                rawNode.IsCurrent || tree.CurrentGrid == rawNode.Grid,
                rawNode.RawContentCodes
                    .Select(code => new RawContentTag(code, 0, "ui-tree", 1f))
                    .ToArray(),
                contents,
                rawNode.IsDiscovered)
            {
                Kind = rawNode.Kind,
                MapType = rawNode.MapType,
                MapGroup = rawNode.MapGroup,
                MapDataTags = rawNode.MapDataTags,
                RawContentValue = rawNode.RawContentValue,
                ContentVectorValues = rawNode.ContentVectorValues,
                IconType = rawNode.IconType,
                ContentBadges = rawNode.ContentBadges,
                RegionKey = rawNode.RegionKey,
                RawState = rawNode.RawState,
                Biome = rawNode.Biome,
                RawFlags = rawNode.RawFlags,
                Completion = rawNode.Completion
            });
        }

        var normalizedNodes = AtlasGridPositionNormalizer.Normalize(nodes);
        if (!ReferenceEquals(normalizedNodes, nodes))
        {
            _diagnostics.Add(
                $"Normalized fogged atlas positions from grid geometry; "
                + $"nodes={normalizedNodes.Count}.");
            nodes = normalizedNodes.ToList();
        }

        var graph = _graphProvider.Build(
            nodes,
            tree.Edges.Take(_profile.MaxEdgeCount));
        _diagnostics.AddRange(_graphProvider.Diagnostics);
        var edges = graph.Edges
            .Select(edge =>
                graph.Nodes.TryGetValue(edge.From, out var fromNode)
                && graph.Nodes.TryGetValue(edge.To, out var toNode)
                    ? AtlasEdgeClassifier.Classify(
                        edge,
                        fromNode.IsAccessible,
                        toNode.IsAccessible)
                    : edge)
            .ToArray();

        var currentGrid = tree.CurrentGrid is { } candidate
            && graph.Nodes.ContainsKey(candidate)
            ? candidate
            : graph.Nodes.Values.FirstOrDefault(node => node.IsCurrent)?.Grid;
        var projectionScale = float.IsFinite(tree.Scale) && tree.Scale > 0
            ? tree.Scale
            : 1f;
        var nodeSize = IsValidSize(tree.NodeSize)
            ? tree.NodeSize
            : new Vector2(_profile.ExpectedNodeSize, _profile.ExpectedNodeSize);
        var (canvasScale, atlasZoom) = ResolveProjectionScales(
            tree.CanvasScale,
            projectionScale);
        var projection = _viewportProvider?.Invoke() is { } viewport
            ? AtlasProjection.FromCanvas(
                viewport,
                tree.CanvasRelativePosition,
                canvasScale,
                atlasZoom,
                nodeSize)
            : new AtlasProjection(
                projectionScale,
                projectionScale,
                0f,
                0f,
                1f,
                projectionScale);

        return new AtlasSnapshot(
            DateTimeOffset.UtcNow,
            AtlasSnapshotStatus.Loading,
            graph.Nodes.Count,
            edges.Length,
            graph.Nodes.Values.ToArray(),
            edges,
            currentGrid,
            projection,
            CreateSignature(tree, graph, edges),
            IsAtlasOpen: true);
    }

    private static bool IsValidSize(Vector2 size)
        => float.IsFinite(size.X)
           && float.IsFinite(size.Y)
           && size.X > 0f
           && size.Y > 0f;

    private static (float CanvasScale, float AtlasZoom) ResolveProjectionScales(
        float canvasScale,
        float nodeScale)
    {
        var hasCanvasScale = float.IsFinite(canvasScale) && canvasScale > 0f;
        var hasNodeScale = float.IsFinite(nodeScale) && nodeScale > 0f;

        if (!hasCanvasScale)
        {
            return (1f, hasNodeScale ? nodeScale : 1f);
        }

        if (!hasNodeScale)
        {
            return (canvasScale, 1f);
        }

        return MathF.Abs(canvasScale - nodeScale) <= 0.01f
            ? (1f, nodeScale)
            : (canvasScale, nodeScale);
    }

    private static bool IsValidNode(AtlasUiNodeData node)
        => IsValidSize(new Vector2(node.Width, node.Height));

    private static AtlasSnapshot EmptySnapshot(
        AtlasUiTreeSnapshot tree,
        string reason)
        => new(
            DateTimeOffset.UtcNow,
            AtlasSnapshotStatus.Loading,
            0,
            0,
            [],
            [],
            null,
            AtlasProjection.Identity,
            $"{tree.CanvasToken}|{reason}",
            IsAtlasOpen: false);

    private static string CreateSignature(
        AtlasUiTreeSnapshot tree,
        AtlasGraph graph,
        IReadOnlyList<AtlasEdgeSnapshot> edges)
    {
        var nodes = graph.Nodes.Values
            .OrderBy(node => node.Grid.X)
            .ThenBy(node => node.Grid.Y)
            .Select(node => string.Join(
                ",",
                node.Grid.X,
                node.Grid.Y,
                node.MapId,
                node.DisplayName,
                node.IsVisible ? "v" : "h",
                node.IsDiscovered is true
                    ? "d"
                    : node.IsDiscovered is false
                        ? "f"
                        : "?",
                node.IsAccessible ? "a" : "l",
                node.IsCompleted ? "c" : "u",
                node.IsCurrent ? "q" : "n",
                node.Kind,
                node.MapType,
                node.MapGroup,
                string.Join("+", node.MapDataTags.Order(StringComparer.OrdinalIgnoreCase)),
                string.Join(
                    "+",
                    node.Contents
                        .OrderBy(content => content.ContentId)
                        .Select(content => content.ContentId)),
                node.RawContentValue,
                string.Join(",", node.ContentVectorValues),
                node.IconType,
                string.Join("+", node.ContentBadges.Order(StringComparer.OrdinalIgnoreCase))));

        var edgeSignature = edges
            .OrderBy(edge => edge.From.X)
            .ThenBy(edge => edge.From.Y)
            .ThenBy(edge => edge.To.X)
            .ThenBy(edge => edge.To.Y)
            .Select(edge => string.Join(
                ",",
                edge.From.X,
                edge.From.Y,
                edge.To.X,
                edge.To.Y,
                edge.State,
                edge.RenderColor));

        return string.Join(
            "|",
            tree.CanvasToken,
            tree.CurrentGrid?.X.ToString(CultureInfo.InvariantCulture),
            tree.CurrentGrid?.Y.ToString(CultureInfo.InvariantCulture),
            $"nodes:{string.Join(";", nodes)}",
            $"edges:{string.Join(";", edgeSignature)}");
    }
}
