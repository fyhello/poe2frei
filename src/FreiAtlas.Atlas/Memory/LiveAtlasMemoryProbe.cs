using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using FreiAtlas.Atlas.Metadata;
using FreiAtlas.Core.Atlas;
using FreiAtlas.Core.Memory;

namespace FreiAtlas.Atlas.Memory;

public sealed class LiveAtlasMemoryProbe
    : IAtlasMemoryProbe,
      IAtlasTransformProbe,
      IAtlasSelectiveLiveGeometryProbe,
      IAtlasLiveRenderGeometryProbe
{
    private const ulong MinCanonicalPointer = 0x10000;
    private const ulong MaxCanonicalPointer = 0x7FFF_FFFF_FFFF;
    private const int MaxChildrenPerElement = 16_384;
    private const int CurrentMarkerRediscoverySampleInterval = 8;
    private const int MaximumCurrentMarkerRediscoverySampleInterval = 256;
    private const int CurrentMarkerCandidateSampleLimit = 32;
    private const float LiveZoomStabilityEpsilon = 0.001f;

    private int _cachedProcessId;
    private nint _cachedInGameState;
    private nint _cachedCanvas;
    private nint _cachedNodeVtable;
    private nint _cachedCurrentMarker;
    private nint[] _cachedCurrentMarkerCandidates = [];
    private int _cachedCurrentMarkerCandidateIndex;
    private AtlasGridPos? _cachedCurrentGrid;
    private int _currentMarkerReadMisses;
    private int _currentMarkerRediscoverySampleInterval =
        CurrentMarkerRediscoverySampleInterval;
    private Vector2 _cachedCanvasParentOffset;
    private bool _cachedCanvasParentOffsetValid;
    private readonly Dictionary<nint, CachedNodeData> _cachedNodeData = [];
    private readonly Dictionary<AtlasGridPos, nint> _cachedNodeElements = [];
    private LiveGeometryCache? _publishedLiveGeometry;
    private readonly object _liveGeometryGate = new();
    private nint _lastLiveCanvas;
    private float _lastLiveZoom;
    private IReadOnlyDictionary<AtlasGridPos, Vector2> _lastStableLivePositions =
        new Dictionary<AtlasGridPos, Vector2>();

    private sealed record CachedNodeData(
        AtlasGridPos Grid,
        string? MapId,
        string? DisplayName,
        IReadOnlyList<string> RawContentCodes,
        uint RawContentValue,
        IReadOnlyList<uint> ContentVectorValues,
        int IconType,
        IReadOnlyList<string> ContentBadges,
        string Kind,
        string MapType,
        string MapGroup,
        IReadOnlyList<string> MapDataTags);

    private sealed record LiveGeometryCache(
        int ProcessId,
        nint Canvas,
        IReadOnlyDictionary<AtlasGridPos, nint> NodeElements,
        Vector2 CanvasParentOffset,
        bool CanvasParentOffsetValid);

    public IReadOnlyList<string> Diagnostics { get; private set; } = [];

    public bool TryReadTransform(
        IProcessMemory memory,
        AtlasLayoutProfile profile,
        out AtlasUiTransform transform)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(profile);

        transform = default;
        var cache = Volatile.Read(ref _publishedLiveGeometry);
        return TryReadTransform(memory, profile, cache, out transform);
    }

    private bool TryReadTransform(
        IProcessMemory memory,
        AtlasLayoutProfile profile,
        LiveGeometryCache? cache,
        out AtlasUiTransform transform)
    {
        transform = default;
        if (cache is null
            || cache.ProcessId != memory.ProcessId
            || cache.Canvas == 0
            || !IsUiElement(memory, cache.Canvas, profile)
            || !TryReadUInt32(
                memory,
                cache.Canvas + profile.Game.UiElement.FlagsOffset,
                out var flags)
            || ((flags >> profile.Game.UiElement.VisibleBit) & 1u) == 0)
        {
            return false;
        }

        if (!TryReadFloat(
                memory,
                cache.Canvas + profile.Game.UiElement.RelativePositionOffset,
                out var positionX)
            || !TryReadFloat(
                memory,
                cache.Canvas + profile.Game.UiElement.RelativePositionOffset + 4,
                out var positionY))
        {
            return false;
        }

        var position = new Vector2(positionX, positionY);
        if (cache.CanvasParentOffsetValid)
        {
            position += cache.CanvasParentOffset;
        }
        else
        {
            position = ReadUnscaledPosition(
                memory,
                cache.Canvas,
                profile);
        }
        var scale = TryReadScale(memory, cache.Canvas, profile);
        if (!float.IsFinite(position.X)
            || !float.IsFinite(position.Y)
            || !float.IsFinite(scale)
            || scale <= 0f)
        {
            return false;
        }

        transform = new AtlasUiTransform(position, scale);
        return true;
    }

    public bool TryReadLiveGeometry(
        IProcessMemory memory,
        AtlasLayoutProfile profile,
        out AtlasUiLiveGeometry geometry)
        => TryReadLiveGeometryCore(
            memory,
            profile,
            requestedGrids: null,
            renderRateRead: false,
            out geometry);

    public bool TryReadLiveGeometry(
        IProcessMemory memory,
        AtlasLayoutProfile profile,
        IReadOnlySet<AtlasGridPos> grids,
        out AtlasUiLiveGeometry geometry)
        => TryReadLiveGeometryCore(
            memory,
            profile,
            grids,
            renderRateRead: false,
            out geometry);

    public bool TryReadLiveRenderGeometry(
        IProcessMemory memory,
        AtlasLayoutProfile profile,
        IReadOnlySet<AtlasGridPos> grids,
        out AtlasUiLiveGeometry geometry)
        => TryReadLiveGeometryCore(
            memory,
            profile,
            grids,
            renderRateRead: true,
            out geometry);

    private bool TryReadLiveGeometryCore(
        IProcessMemory memory,
        AtlasLayoutProfile profile,
        IReadOnlySet<AtlasGridPos>? requestedGrids,
        bool renderRateRead,
        out AtlasUiLiveGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(profile);

        geometry = default;
        var cache = Volatile.Read(ref _publishedLiveGeometry);
        if (cache is null
            || cache.ProcessId != memory.ProcessId
            || cache.Canvas == 0
            || cache.NodeElements.Count == 0
            || !TryReadTransform(memory, profile, cache, out var transform))
        {
            return false;
        }

        var selectedNodeElements = requestedGrids is null
            ? cache.NodeElements
            : cache.NodeElements
                .Where(pair => requestedGrids.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
        var scaleElements = selectedNodeElements.Count > 0
            ? selectedNodeElements.Values
            : cache.NodeElements.Values.Take(1);
        var zoomBefore = TryReadNodeScale(
            memory,
            scaleElements,
            profile);
        var positions = new Dictionary<AtlasGridPos, Vector2>(
            selectedNodeElements.Count);
        IReadOnlyDictionary<AtlasGridPos, Vector2> previousPositions;
        lock (_liveGeometryGate)
        {
            previousPositions = requestedGrids is null
                ? new Dictionary<AtlasGridPos, Vector2>(
                    _lastStableLivePositions)
                : _lastStableLivePositions
                    .Where(pair => requestedGrids.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
        }

        Span<byte> positionBytes = stackalloc byte[sizeof(float) * 2];
        foreach (var (grid, element) in selectedNodeElements)
        {
            if (!IsUiElement(memory, element, profile)
                || !memory.TryRead(
                    element + profile.Game.UiElement.RelativePositionOffset,
                    positionBytes))
            {
                if (previousPositions.TryGetValue(grid, out var previous))
                {
                    positions[grid] = previous;
                }

                continue;
            }

            var position = MemoryMarshal.Read<Vector2>(positionBytes);
            if (!float.IsFinite(position.X)
                || !float.IsFinite(position.Y)
                || MathF.Abs(position.X) > 200_000f
                || MathF.Abs(position.Y) > 200_000f)
            {
                if (previousPositions.TryGetValue(grid, out var previous))
                {
                    positions[grid] = previous;
                }

                continue;
            }

            positions[grid] = position;
        }

        if (selectedNodeElements.Count > 0 && positions.Count == 0)
        {
            if (previousPositions.Count == 0)
            {
                return false;
            }

            positions = new Dictionary<AtlasGridPos, Vector2>(
                previousPositions);
        }

        var zoomAfter = TryReadNodeScale(
            memory,
            scaleElements,
            profile);
        var zoomStable = MathF.Abs(zoomAfter - zoomBefore)
                         <= LiveZoomStabilityEpsilon;
        IReadOnlyDictionary<AtlasGridPos, Vector2> stabilizedPositions;
        var positionsStable = true;
        lock (_liveGeometryGate)
        {
            if (renderRateRead)
            {
                // Radar's render thread reads only the nodes it is about to draw. Do not
                // compare that subset against a full-atlas median: culled nodes can stay
                // frozen while visible nodes move with the atlas viewport.
                stabilizedPositions = new Dictionary<AtlasGridPos, Vector2>(
                    positions);
                _lastLiveCanvas = cache.Canvas;
                _lastLiveZoom = zoomAfter;
                _lastStableLivePositions = MergeStablePositions(
                    _lastStableLivePositions,
                    stabilizedPositions);
                positionsStable = zoomStable;
            }
            else if (_lastLiveCanvas != cache.Canvas
                || _lastStableLivePositions.Count == 0)
            {
                stabilizedPositions = new Dictionary<AtlasGridPos, Vector2>(
                    positions);
                _lastLiveCanvas = cache.Canvas;
                _lastLiveZoom = zoomAfter;
                _lastStableLivePositions = MergeStablePositions(
                    _lastStableLivePositions,
                    stabilizedPositions);
            }
            else if (!zoomStable
                     || MathF.Abs(_lastLiveZoom - zoomAfter)
                        > LiveZoomStabilityEpsilon)
            {
                stabilizedPositions = new Dictionary<AtlasGridPos, Vector2>(
                    positions);
                _lastLiveZoom = zoomAfter;
                _lastStableLivePositions = MergeStablePositions(
                    _lastStableLivePositions,
                    stabilizedPositions);
                positionsStable = false;
            }
            else if (AtlasLivePositionFilter.TryStabilize(
                         previousPositions,
                         positions,
                         out var filtered))
            {
                stabilizedPositions = filtered;
                _lastStableLivePositions = MergeStablePositions(
                    _lastStableLivePositions,
                    filtered);
                _lastLiveZoom = zoomAfter;
            }
            else
            {
                stabilizedPositions = previousPositions;
                positionsStable = false;
            }
        }

        geometry = new AtlasUiLiveGeometry(
            stabilizedPositions,
            transform,
            zoomAfter,
            zoomStable && positionsStable);
        return true;
    }

    private static IReadOnlyDictionary<AtlasGridPos, Vector2> MergeStablePositions(
        IReadOnlyDictionary<AtlasGridPos, Vector2> previous,
        IReadOnlyDictionary<AtlasGridPos, Vector2> current)
    {
        var merged = new Dictionary<AtlasGridPos, Vector2>(previous);
        foreach (var pair in current)
        {
            merged[pair.Key] = pair.Value;
        }

        return merged;
    }

    public AtlasUiTreeSnapshot Read(
        IProcessMemory memory,
        AtlasLayoutProfile profile)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(profile);

        if (_cachedProcessId != memory.ProcessId)
        {
            ResetCache();
            _cachedProcessId = memory.ProcessId;
        }

        var diagnostics = new List<string>();
        Diagnostics = diagnostics;

        if (memory is not IProcessMemoryLayout layout)
        {
            diagnostics.Add("Process memory does not expose module layout metadata.");
            return EmptySnapshot(false, false, "layout-unavailable");
        }

        if (!TryResolveInGameState(memory, layout, profile, diagnostics, out var inGameState))
        {
            return EmptySnapshot(false, false, "game-state-unavailable");
        }

        if (!TryReadPointer(
                memory,
                inGameState + profile.Game.InGameState.UiRootOffset,
                out var uiRoot))
        {
            diagnostics.Add(
                $"InGameState UI root pointer unreadable at +0x{profile.Game.InGameState.UiRootOffset:X}.");
            return EmptySnapshot(false, false, "ui-root-unavailable");
        }

        if (!IsUiElement(memory, uiRoot, profile))
        {
            diagnostics.Add($"UI root is not a self-referential UiElement: 0x{uiRoot:X}.");
            return EmptySnapshot(false, false, "ui-root-invalid");
        }

        if (!TryDiscoverAtlas(
                memory,
                uiRoot,
                profile,
                diagnostics,
                out var canvas,
                out var nodeElements,
                out var currentGrid))
        {
            return EmptySnapshot(true, false, $"ui-root:{uiRoot:X}");
        }

        if (!IsHierarchicallyVisible(memory, canvas, profile, diagnostics))
        {
            diagnostics.Add($"Atlas canvas 0x{canvas:X} is not hierarchically visible.");
            return EmptySnapshot(true, true, $"canvas:{canvas:X}");
        }

        var nodes = new List<AtlasUiNodeData>(nodeElements.Count);
        var failedNodeReads = 0;
        foreach (var element in nodeElements)
        {
            if (!TryReadNode(
                    memory,
                    element,
                    canvas,
                    currentGrid,
                    profile,
                    diagnostics,
                    out var node))
            {
                failedNodeReads++;
                continue;
            }

            nodes.Add(node);
        }

        var gridSet = nodes.Select(node => node.Grid).ToHashSet();
        diagnostics.Add(
            $"Atlas node pipeline=canvasChildren:{nodeElements.Count};"
            + $"decoded:{nodes.Count};failed:{failedNodeReads};"
            + $"distinctGrids:{gridSet.Count}.");
        var edges = ReadEdges(
            memory,
            canvas,
            gridSet,
            profile,
            diagnostics);

        var liveZoom = TryReadNodeScale(memory, nodeElements, profile);
        var canvasPosition = ReadUnscaledPosition(
            memory,
            canvas,
            profile);
        UpdateCanvasParentOffset(
            memory,
            canvas,
            canvasPosition,
            profile);
        var canvasScale = TryReadScale(
            memory,
            canvas,
            profile);
        var nodeSize = ReadNodeSize(
            memory,
            nodeElements,
            profile);
        PublishLiveGeometryCache(memory.ProcessId);
        return new AtlasUiTreeSnapshot(
            HasUiRoot: true,
            HasAtlasCanvas: true,
            CanvasToken: $"canvas:{canvas:X}",
            Scale: liveZoom,
            Nodes: nodes,
            Edges: edges,
            CurrentGrid: currentGrid is { } candidate && gridSet.Contains(candidate)
                ? candidate
                : null,
            CanvasRelativePosition: canvasPosition,
            CanvasScale: canvasScale,
            NodeSize: nodeSize,
            IsAtlasOpen: true);
    }

    private bool TryResolveInGameState(
        IProcessMemory memory,
        IProcessMemoryLayout layout,
        AtlasLayoutProfile profile,
        ICollection<string> diagnostics,
        out nint inGameState)
    {
        inGameState = 0;
        if (_cachedInGameState != 0
            && TryReadPointer(
                memory,
                _cachedInGameState + profile.Game.InGameState.UiRootOffset,
                out var cachedUiRoot)
            && IsUiElement(memory, cachedUiRoot, profile))
        {
            inGameState = _cachedInGameState;
            return true;
        }

        _cachedInGameState = 0;
        var pattern = profile.Game.GameStateReference.Pattern;
        if (pattern.Count == 0)
        {
            diagnostics.Add("Game-state reference pattern is empty.");
            return false;
        }

        foreach (var region in layout.EnumerateMainModuleRegions())
        {
            if (!region.IsReadable
                || !region.IsExecutable
                || region.Size <= 0
                || region.Size > int.MaxValue)
            {
                continue;
            }

            var bytes = new byte[(int)region.Size];
            if (!memory.TryRead(region.BaseAddress, bytes))
            {
                continue;
            }

            for (var offset = 0; offset <= bytes.Length - pattern.Count; offset++)
            {
                if (!Matches(bytes, offset, pattern))
                {
                    continue;
                }

                var displacementOffset = offset + profile.Game.GameStateReference.DisplacementOffset;
                if (displacementOffset + sizeof(int) > bytes.Length)
                {
                    continue;
                }

                var displacement = BitConverter.ToInt32(bytes, displacementOffset);
                var slotAddress = region.BaseAddress
                    + offset
                    + profile.Game.GameStateReference.InstructionLength
                    + displacement;
                if (!TryReadPointer(memory, slotAddress, out var gameState))
                {
                    continue;
                }

                if (TryResolveInGameStateCandidate(
                        memory,
                        gameState,
                        profile,
                        out inGameState))
                {
                    _cachedInGameState = inGameState;
                    return true;
                }
            }
        }

        diagnostics.Add(
            $"No valid game-state AOB reference found in module 0x{layout.MainModuleBase:X}.");
        return false;
    }

    private static bool TryResolveInGameStateCandidate(
        IProcessMemory memory,
        nint gameState,
        AtlasLayoutProfile profile,
        out nint inGameState)
    {
        inGameState = 0;
        if (!IsCanonical(gameState))
        {
            return false;
        }

        var candidates = new List<nint>();
        if (TryReadPointer(
                memory,
                gameState + profile.Game.GameState.CurrentStateVectorOffset,
                out var currentVector)
            && TryReadPointer(memory, currentVector, out var currentState))
        {
            candidates.Add(currentState);
        }

        for (var index = 0; index < profile.Game.GameState.StateSlotCount; index++)
        {
            if (TryReadPointer(
                    memory,
                    gameState
                    + profile.Game.GameState.StateSlotsOffset
                    + index * profile.Game.GameState.StateSlotStride,
                    out var candidate))
            {
                candidates.Add(candidate);
            }
        }

        foreach (var candidate in candidates.Distinct())
        {
            if (!IsCanonical(candidate)
                || !TryReadPointer(
                    memory,
                    candidate + profile.Game.InGameState.UiRootOffset,
                    out var uiRoot)
                || !IsCanonical(uiRoot)
                || !IsUiElement(memory, uiRoot, profile))
            {
                continue;
            }

            inGameState = candidate;
            return true;
        }

        return false;
    }

    private bool TryDiscoverAtlas(
        IProcessMemory memory,
        nint uiRoot,
        AtlasLayoutProfile profile,
        ICollection<string> diagnostics,
        out nint canvas,
        out IReadOnlyList<nint> nodeElements,
        out AtlasGridPos? currentGrid)
    {
        canvas = 0;
        nodeElements = [];
        currentGrid = null;
        IReadOnlyList<nint>? fallbackNodeElements = null;
        var fallbackCanvas = (nint)0;
        AtlasGridPos? fallbackCurrentGrid = null;

        if (_cachedCanvas != 0 && _cachedNodeVtable != 0)
        {
            if (!IsUiElement(memory, _cachedCanvas, profile))
            {
                ResetAtlasCache();
            }
            else
            {
                var cachedNodes = ReadCachedNodeElements(memory, profile);
                if (cachedNodes.Count >= profile.MinimumNodeClassCount)
                {
                    diagnostics.Add(
                        $"Atlas cache-hit canvas=0x{_cachedCanvas:X};"
                        + $"vtable=0x{_cachedNodeVtable:X};"
                        + $"nodes={cachedNodes.Count};"
                        + $"first=0x{cachedNodes[0]:X}.");
                    canvas = _cachedCanvas;
                    nodeElements = cachedNodes;
                    if (!IsHierarchicallyVisible(
                            memory,
                            _cachedCanvas,
                            profile,
                            diagnostics))
                    {
                        currentGrid = _cachedCurrentGrid;
                        return true;
                    }

                    CacheNodeElements(memory, cachedNodes, profile);
                    var cachedNodeSet = cachedNodes.ToHashSet();
                    var markerGrid = TryReadMarkerCurrentGrid(
                        memory,
                        _cachedCurrentMarker,
                        cachedNodeSet,
                        profile);
                    if (markerGrid is not null
                        && !IsCurrentMarkerAttached(
                            memory,
                            _cachedCurrentMarker,
                            uiRoot,
                            profile))
                    {
                        markerGrid = null;
                    }

                    if (markerGrid is null)
                    {
                        if (TryFindCachedCurrentMarker(
                            memory,
                            cachedNodeSet,
                            uiRoot,
                            profile,
                            out var rediscoveredMarker,
                            out var rediscoveredGrid))
                        {
                            _cachedCurrentMarker = rediscoveredMarker;
                            markerGrid = rediscoveredGrid;
                        }
                    }

                    if (markerGrid is { } resolvedGrid)
                    {
                        _cachedCurrentGrid = resolvedGrid;
                        _currentMarkerReadMisses = 0;
                        _currentMarkerRediscoverySampleInterval =
                            CurrentMarkerRediscoverySampleInterval;
                        currentGrid = resolvedGrid;
                        return true;
                    }

                    _currentMarkerReadMisses++;
                    if (_currentMarkerReadMisses
                        < _currentMarkerRediscoverySampleInterval)
                    {
                        currentGrid = _cachedCurrentGrid;
                        return true;
                    }

                    diagnostics.Add(
                        "Atlas current marker cache miss threshold reached; rediscovering atlas UI tree.");
                    var nextRediscoveryInterval = Math.Min(
                        _currentMarkerRediscoverySampleInterval * 2,
                        MaximumCurrentMarkerRediscoverySampleInterval);
                    fallbackCanvas = _cachedCanvas;
                    fallbackNodeElements = cachedNodes;
                    fallbackCurrentGrid = _cachedCurrentGrid;
                    _currentMarkerReadMisses = 0;
                    _currentMarkerRediscoverySampleInterval =
                        nextRediscoveryInterval;
                }

                else if (!IsHierarchicallyVisible(
                        memory,
                        _cachedCanvas,
                        profile,
                        diagnostics))
                {
                    canvas = _cachedCanvas;
                    currentGrid = _cachedCurrentGrid;
                    return true;
                }
                else
                {
                    ResetAtlasCache();
                }
            }
        }

        var traversalRoot = uiRoot;
        if (TryReadPointer(
                memory,
                uiRoot + profile.Game.UiElement.ParentOffset,
                out var trueRoot)
            && IsUiElement(memory, trueRoot, profile))
        {
            traversalRoot = trueRoot;
        }

        var queue = new Queue<nint>();
        var visited = new HashSet<nint>();
        var byVtable = new Dictionary<nint, List<nint>>();
        queue.Enqueue(traversalRoot);

        while (queue.Count > 0 && visited.Count < profile.UiTreeNodeLimit)
        {
            var element = queue.Dequeue();
            if (!IsUiElement(memory, element, profile)
                || !visited.Add(element))
            {
                continue;
            }

            if (TryReadPointer(memory, element, out var vtable)
                && IsCanonical(vtable))
            {
                if (!byVtable.TryGetValue(vtable, out var bucket))
                {
                    bucket = [];
                    byVtable[vtable] = bucket;
                }

                bucket.Add(element);
            }

            foreach (var child in ReadChildren(memory, element, profile))
            {
                queue.Enqueue(child);
            }
        }

        var candidates = byVtable
            .Where(pair => pair.Value.Count >= profile.MinimumNodeClassCount)
            .Select(pair => new
            {
                Vtable = pair.Key,
                Elements = pair.Value,
                Biomes = ReadBiomeKinds(memory, pair.Value, profile),
                ModalWidth = ReadModalWidth(memory, pair.Value, profile)
            })
            .Where(candidate =>
                candidate.Biomes.Count >= profile.MinimumBiomeKinds
                && candidate.ModalWidth is >= 28f and <= 56f)
            .OrderByDescending(candidate => candidate.Elements.Count)
            .FirstOrDefault();

        if (candidates is null)
        {
            diagnostics.Add(
                $"Atlas node class not found; visited={visited.Count}, vtables={byVtable.Count}.");
            if (fallbackNodeElements is not null)
            {
                canvas = fallbackCanvas;
                nodeElements = fallbackNodeElements;
                currentGrid = fallbackCurrentGrid;
                return true;
            }

            return false;
        }
        diagnostics.Add(
            $"Atlas node class=0x{candidates.Vtable:X}; "
            + $"instances={candidates.Elements.Count}; "
            + $"biomes={candidates.Biomes.Count}; "
            + $"modalWidth={candidates.ModalWidth:0.##}.");

        var nodeSet = candidates.Elements.ToHashSet();
        var parentCounts = new Dictionary<nint, int>();
        foreach (var element in candidates.Elements)
        {
            if (!TryReadPointer(
                    memory,
                    element + profile.Game.UiElement.ParentOffset,
                    out var parent)
                || !IsCanonical(parent))
            {
                continue;
            }

            parentCounts[parent] = parentCounts.GetValueOrDefault(parent) + 1;
        }

        canvas = parentCounts
            .OrderByDescending(pair => pair.Value)
            .Select(pair => pair.Key)
            .FirstOrDefault();
        if (canvas == 0)
        {
            diagnostics.Add("Atlas node class was found, but its parent canvas was not.");
            if (fallbackNodeElements is not null)
            {
                canvas = fallbackCanvas;
                nodeElements = fallbackNodeElements;
                currentGrid = fallbackCurrentGrid;
                return true;
            }

            return false;
        }

        var canvasNodes = ReadChildren(memory, canvas, profile)
            .Where(node =>
                nodeSet.Contains(node))
            .Distinct()
            .ToArray();
        if (canvasNodes.Length == 0)
        {
            diagnostics.Add($"Atlas canvas 0x{canvas:X} has no node children.");
            if (fallbackNodeElements is not null)
            {
                canvas = fallbackCanvas;
                nodeElements = fallbackNodeElements;
                currentGrid = fallbackCurrentGrid;
                return true;
            }

            return false;
        }
        diagnostics.Add(
            $"Atlas canvas=0x{canvas:X}; directNodes={canvasNodes.Length}; "
            + $"parentCandidates={parentCounts.Count}.");
        foreach (var element in canvasNodes.Take(5))
        {
            var position = new Vector2(
                TryReadFloat(
                    memory,
                    element + profile.Game.UiElement.RelativePositionOffset,
                    out var x)
                    ? x
                    : float.NaN,
                TryReadFloat(
                    memory,
                    element + profile.Game.UiElement.RelativePositionOffset + 4,
                    out var y)
                    ? y
                    : float.NaN);
            diagnostics.Add(
                $"Atlas node sample=0x{element:X}; "
                + $"relative={position.X:0.##},{position.Y:0.##}.");
        }

        nodeElements = canvasNodes;
        CacheNodeElements(memory, canvasNodes, profile);

        currentGrid = null;
        var currentNodeSet = canvasNodes.ToHashSet();
        _cachedCurrentMarkerCandidates = visited
            .Where(element => !currentNodeSet.Contains(element))
            .ToArray();
        _cachedCurrentMarkerCandidateIndex = 0;
        if (TryFindCurrentMarker(
            memory,
            _cachedCurrentMarkerCandidates,
            currentNodeSet,
            uiRoot,
            profile,
            startIndex: 0,
            candidateCount: _cachedCurrentMarkerCandidates.Length,
            out var discoveredMarker,
            out var discoveredGrid))
        {
            _cachedCurrentMarker = discoveredMarker;
            currentGrid = discoveredGrid;
        }
        else
        {
            _cachedCurrentMarker = 0;
        }

        _cachedCanvas = canvas;
        _cachedNodeVtable = candidates.Vtable;
        _cachedCurrentGrid = currentGrid;
        _currentMarkerReadMisses = 0;
        if (currentGrid is not null)
        {
            _currentMarkerRediscoverySampleInterval =
                CurrentMarkerRediscoverySampleInterval;
        }
        _cachedNodeData.Clear();
        return true;
    }

    private IReadOnlyList<nint> ReadCachedNodeElements(
        IProcessMemory memory,
        AtlasLayoutProfile profile)
    {
        var result = new List<nint>();
        foreach (var child in ReadChildren(memory, _cachedCanvas, profile))
        {
            if (!TryReadPointer(memory, child, out var vtable)
                || vtable != _cachedNodeVtable
                || !IsUiElement(memory, child, profile))
            {
                continue;
            }

            result.Add(child);
        }

        return result.Distinct().ToArray();
    }

    private bool TryFindCachedCurrentMarker(
        IProcessMemory memory,
        IReadOnlySet<nint> nodeSet,
        nint uiRoot,
        AtlasLayoutProfile profile,
        out nint marker,
        out AtlasGridPos grid)
    {
        if (_cachedCurrentMarkerCandidates.Length == 0)
        {
            marker = 0;
            grid = default;
            return false;
        }

        var startIndex = _cachedCurrentMarkerCandidateIndex
            % _cachedCurrentMarkerCandidates.Length;
        var candidateCount = Math.Min(
            CurrentMarkerCandidateSampleLimit,
            _cachedCurrentMarkerCandidates.Length);
        var found = TryFindCurrentMarker(
            memory,
            _cachedCurrentMarkerCandidates,
            nodeSet,
            uiRoot,
            profile,
            startIndex,
            candidateCount,
            out marker,
            out grid);
        _cachedCurrentMarkerCandidateIndex =
            (startIndex + candidateCount)
            % _cachedCurrentMarkerCandidates.Length;
        return found;
    }

    private static bool TryFindCurrentMarker(
        IProcessMemory memory,
        IReadOnlyList<nint> candidates,
        IReadOnlySet<nint> nodeSet,
        nint uiRoot,
        AtlasLayoutProfile profile,
        int startIndex,
        int candidateCount,
        out nint marker,
        out AtlasGridPos grid)
    {
        for (var offset = 0; offset < candidateCount; offset++)
        {
            var element = candidates[
                (startIndex + offset) % candidates.Count];
            if (nodeSet.Contains(element)
                || !TryReadPointer(
                    memory,
                    element + profile.AtlasCurrentMarkerNodeOffset,
                    out var target)
                || !nodeSet.Contains(target)
                || !IsCurrentMarkerAttached(
                    memory,
                    element,
                    uiRoot,
                    profile)
                || !TryReadGrid(memory, target, profile, out grid))
            {
                continue;
            }

            marker = element;
            return true;
        }

        marker = 0;
        grid = default;
        return false;
    }

    private static bool IsCurrentMarkerAttached(
        IProcessMemory memory,
        nint marker,
        nint uiRoot,
        AtlasLayoutProfile profile)
    {
        var visited = new HashSet<nint>();
        var current = marker;
        for (var depth = 0; depth < 64; depth++)
        {
            if (!visited.Add(current)
                || !IsUiElement(memory, current, profile)
                || !TryReadPointer(
                    memory,
                    current + profile.Game.UiElement.ParentOffset,
                    out var parent)
                || parent == current
                || !IsUiElement(memory, parent, profile)
                || !ReadChildren(memory, parent, profile).Contains(current))
            {
                return false;
            }

            if (parent == uiRoot)
            {
                return true;
            }

            current = parent;
        }

        return false;
    }

    private static AtlasGridPos? TryReadMarkerCurrentGrid(
        IProcessMemory memory,
        nint marker,
        IReadOnlySet<nint> nodeSet,
        AtlasLayoutProfile profile)
    {
        if (marker == 0
            || !TryReadPointer(
                memory,
                marker + profile.AtlasCurrentMarkerNodeOffset,
                out var target)
            || !nodeSet.Contains(target)
            || !TryReadGrid(memory, target, profile, out var grid))
        {
            return null;
        }

        return grid;
    }

    private void CacheNodeElements(
        IProcessMemory memory,
        IEnumerable<nint> elements,
        AtlasLayoutProfile profile)
    {
        _cachedNodeElements.Clear();
        foreach (var element in elements)
        {
            if (IsUiElement(memory, element, profile)
                && TryReadGrid(memory, element, profile, out var grid))
            {
                _cachedNodeElements[grid] = element;
            }
        }
    }

    private bool TryReadNode(
        IProcessMemory memory,
        nint element,
        nint canvas,
        AtlasGridPos? currentGrid,
        AtlasLayoutProfile profile,
        ICollection<string> diagnostics,
        out AtlasUiNodeData node)
    {
        node = default!;
        if (!IsUiElement(memory, element, profile)
            || !TryReadGrid(memory, element, profile, out var grid)
            || !TryReadFloat(memory, element + profile.Game.UiElement.RelativePositionOffset, out var relativeX)
            || !TryReadFloat(memory, element + profile.Game.UiElement.RelativePositionOffset + 4, out var relativeY)
            || !TryReadFloat(memory, element + profile.Game.UiElement.WidthOffset, out var width)
            || !TryReadFloat(memory, element + profile.Game.UiElement.HeightOffset, out var height))
        {
            diagnostics.Add($"Atlas node 0x{element:X} has unreadable geometry.");
            return false;
        }

        var flags = TryReadUInt32(
            memory,
            element + profile.Game.UiElement.FlagsOffset,
            out var rawFlags)
            ? rawFlags
            : 0u;
        var visible = ((flags >> profile.Game.UiElement.VisibleBit) & 1u) != 0;
        var accessible = false;
        var completed = false;
        if (TryReadPointer(
                memory,
                element + profile.AtlasDataStorageOffset,
                out var storage)
            && TryReadPointer(
                memory,
                storage + profile.AtlasDataModelOffset,
                out var model)
            && TryReadByte(
                memory,
                model + profile.AtlasDataStatusOffset,
                out var status))
        {
            accessible = (status & 0x01) != 0;
            completed = (status & 0x02) != 0;
        }

        bool? isDiscovered = null;
        if (TryReadByte(
                memory,
                element + profile.AtlasFlagsOffset,
                out var atlasFlags))
        {
            isDiscovered = (atlasFlags & 0x03) != 0;
            if (!isDiscovered.Value && (accessible || completed))
            {
                isDiscovered = true;
            }
        }

        if (!_cachedNodeData.TryGetValue(element, out var cachedData)
            || cachedData.Grid != grid)
        {
            var (mapId, displayName) = ReadMapName(memory, element, profile);
            var mapMetadata = AtlasMetadataCatalog.Embedded.TryGetMap(
                mapId,
                out var resolvedMetadata)
                ? resolvedMetadata
                : default;
            var rawContentValue = TryReadUInt32(
                memory,
                element + profile.AtlasContentRowOffset,
                out var contentValue)
                ? contentValue
                : 0u;
            var contentVectorValues = ReadContentVector(
                memory,
                element,
                profile);
            var iconType = ReadIconType(
                memory,
                element,
                profile);
            var contentBadges = ReadContentBadges(
                memory,
                element,
                profile);
            cachedData = new CachedNodeData(
                grid,
                mapId,
                displayName,
                ReadContentCodes(
                    memory,
                    element,
                    profile,
                    contentBadges),
                rawContentValue,
                contentVectorValues,
                iconType,
                contentBadges,
                AtlasMapKindClassifier.Classify(mapId),
                mapMetadata.Type ?? string.Empty,
                mapMetadata.Group ?? string.Empty,
                mapMetadata.Tags ?? Array.Empty<string>());
            _cachedNodeData[element] = cachedData;
        }

        node = new AtlasUiNodeData(
            grid,
            cachedData.MapId,
            cachedData.DisplayName,
            relativeX,
            relativeY,
            width,
            height,
            visible,
            accessible,
            completed,
            currentGrid == grid,
            cachedData.RawContentCodes,
            isDiscovered)
        {
            Kind = cachedData.Kind,
            MapType = cachedData.MapType,
            MapGroup = cachedData.MapGroup,
            MapDataTags = cachedData.MapDataTags,
            RawContentValue = cachedData.RawContentValue,
            ContentVectorValues = cachedData.ContentVectorValues,
            IconType = cachedData.IconType,
            ContentBadges = cachedData.ContentBadges,
            RegionKey = TryReadUInt32(
                memory,
                element + profile.AtlasRegionKeyOffset,
                out var regionKey)
                ? regionKey
                : 0u,
            RawState = TryReadByte(
                memory,
                element + profile.AtlasStateOffset,
                out var rawState)
                ? rawState
                : (byte)0,
            Biome = TryReadByte(
                memory,
                element + profile.AtlasBiomeOffset,
                out var biome)
                ? biome
                : (byte)0,
            RawFlags = TryReadByte(
                memory,
                element + profile.AtlasFlagsOffset,
                out var atlasFlagsValue)
                ? atlasFlagsValue
                : (byte)0,
            Completion = TryReadByte(
                memory,
                element + profile.AtlasCompletionOffset,
                out var completion)
                ? completion
                : (byte)0
        };
        return true;
    }

    private static IReadOnlyList<AtlasUiEdgeData> ReadEdges(
        IProcessMemory memory,
        nint canvas,
        IReadOnlySet<AtlasGridPos> grids,
        AtlasLayoutProfile profile,
        ICollection<string> diagnostics)
    {
        if (!TryReadPointer(
                memory,
                canvas + profile.AtlasConnectionsOffset,
                out var begin)
            || !TryReadPointer(
                memory,
                canvas + profile.AtlasConnectionsOffset + 8,
                out var end))
        {
            diagnostics.Add("Atlas connection vector is unreadable.");
            return [];
        }

        var byteCount = (long)end - (long)begin;
        if (begin == 0
            || byteCount <= 0
            || byteCount % profile.AtlasEdgeStride != 0)
        {
            diagnostics.Add("Atlas connection vector has an invalid span.");
            return [];
        }

        var count = Math.Min(
            (int)(byteCount / profile.AtlasEdgeStride),
            profile.MaxEdgeCount);
        var edges = new List<AtlasUiEdgeData>(count);
        for (var index = 0; index < count; index++)
        {
            var edge = begin + index * profile.AtlasEdgeStride;
            if (!TryReadInt32(memory, edge + profile.AtlasEdgeSourceOffset, out var sourceX)
                || !TryReadInt32(memory, edge + profile.AtlasEdgeSourceOffset + 4, out var sourceY)
                || !TryReadInt32(memory, edge + profile.AtlasEdgeTargetOffset, out var targetX)
                || !TryReadInt32(memory, edge + profile.AtlasEdgeTargetOffset + 4, out var targetY))
            {
                continue;
            }

            var source = new AtlasGridPos(sourceX, sourceY);
            var target = new AtlasGridPos(targetX, targetY);
            if (source == target
                || !grids.Contains(source)
                || !grids.Contains(target))
            {
                continue;
            }

            edges.Add(new AtlasUiEdgeData(source, target));
        }

        return edges;
    }

    private static (string? MapId, string? DisplayName) ReadMapName(
        IProcessMemory memory,
        nint element,
        AtlasLayoutProfile profile)
    {
        if (!TryReadPointer(
                memory,
                element + profile.AtlasMapRowOffset,
                out var mapRow)
            || !TryReadPointer(
                memory,
                mapRow + profile.AtlasMapWorldAreaOffset,
                out var worldArea))
        {
            return (null, null);
        }

        var mapId = ReadPointerString(
            memory,
            worldArea + profile.AtlasMapCodeOffset,
            64);
        var displayName = ReadPointerString(
            memory,
            worldArea + profile.AtlasMapNameOffset,
            64);
        return (
            string.IsNullOrWhiteSpace(mapId) ? null : mapId,
            string.IsNullOrWhiteSpace(displayName) ? null : displayName);
    }

    private static IReadOnlyList<uint> ReadContentVector(
        IProcessMemory memory,
        nint element,
        AtlasLayoutProfile profile)
    {
        if (!TryReadPointer(
                memory,
                element + profile.AtlasContentVectorBeginOffset,
                out var begin)
            || !TryReadPointer(
                memory,
                element + profile.AtlasContentVectorEndOffset,
                out var end))
        {
            return [];
        }

        var byteCount = (long)end - (long)begin;
        if (begin == 0
            || byteCount <= 0
            || byteCount > profile.AtlasContentVectorMaxBytes
            || byteCount % sizeof(uint) != 0)
        {
            return [];
        }

        var bytes = new byte[(int)byteCount];
        if (!memory.TryRead(begin, bytes))
        {
            return [];
        }

        var values = new uint[bytes.Length / sizeof(uint)];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = MemoryMarshal.Read<uint>(
                bytes.AsSpan(index * sizeof(uint), sizeof(uint)));
        }

        return values;
    }

    private static int ReadIconType(
        IProcessMemory memory,
        nint element,
        AtlasLayoutProfile profile)
    {
        var current = element;
        for (var level = 0; level < 5 && current != 0; level++)
        {
            if (TryReadUInt32(
                    memory,
                    current + profile.AtlasContentRowOffset,
                    out var value)
                && value is > 0 and < 256)
            {
                return (int)value;
            }

            current = ReadFirstChild(memory, current, profile);
        }

        return 0;
    }

    private IReadOnlyList<string> ReadContentCodes(
        IProcessMemory memory,
        nint element,
        AtlasLayoutProfile profile,
        IReadOnlyList<string> contentBadges)
    {
        var result = new List<string>();
        if (TryReadPointer(
                memory,
                element + profile.AtlasContentRowOffset,
                out var row))
        {
            if (TryReadPointer(
                    memory,
                    row + profile.AtlasContentHeadlineOffset,
                    out var contentRow)
                && TryReadPointer(
                    memory,
                    contentRow + profile.AtlasContentNameOffset,
                    out var contentName))
            {
                AddContent(result, ReadUtf16(memory, contentName, 96));
            }

            ReadContentStats(
                memory,
                row,
                profile,
                result);
        }

        foreach (var badge in contentBadges)
        {
            AddContent(result, badge);
        }

        return result;
    }

    private static void ReadContentStats(
        IProcessMemory memory,
        nint row,
        AtlasLayoutProfile profile,
        ICollection<string> result)
    {
        if (!TryReadPointer(
                memory,
                row + profile.AtlasContentStatsOffset,
                out var stats))
        {
            return;
        }

        var bytes = new byte[profile.AtlasContentStatScanBytes];
        if (!memory.TryRead(stats, bytes))
        {
            return;
        }

        const string prefix = "map_atlas_node_has_";
        for (var offset = 0;
             offset + IntPtr.Size <= bytes.Length;
             offset += IntPtr.Size)
        {
            var pointer = MemoryMarshal.Read<nint>(
                bytes.AsSpan(offset, IntPtr.Size));
            if (!IsCanonical(pointer))
            {
                continue;
            }

            var stat = ReadAtlasStatId(memory, pointer, prefix);

            if (!stat.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var display = TitleCase(
                stat[prefix.Length..].Replace('_', ' '));
            AddContent(result, display);
        }
    }

    private static string ReadAtlasStatId(
        IProcessMemory memory,
        nint pointer,
        string prefix)
    {
        var stat = ReadAscii(memory, pointer, 96);
        if (stat.StartsWith(prefix, StringComparison.Ordinal))
        {
            return stat;
        }

        stat = ReadUtf16(memory, pointer, 96) ?? string.Empty;
        if (stat.StartsWith(prefix, StringComparison.Ordinal))
        {
            return stat;
        }

        if (!TryReadPointer(memory, pointer, out var nested))
        {
            return string.Empty;
        }

        stat = ReadAscii(memory, nested, 96);
        return stat.StartsWith(prefix, StringComparison.Ordinal)
            ? stat
            : ReadUtf16(memory, nested, 96) ?? string.Empty;
    }

    private static IReadOnlyList<string> ReadContentBadges(
        IProcessMemory memory,
        nint element,
        AtlasLayoutProfile profile)
    {
        var result = new List<string>();
        var firstChild = ReadFirstChild(memory, element, profile);
        var badgeContainer = ReadFirstChild(memory, firstChild, profile);
        if (badgeContainer == 0)
        {
            return result;
        }

        foreach (var child in ReadChildren(memory, badgeContainer, profile))
        {
            if (!TryReadPointer(
                    memory,
                    child + profile.AtlasContentBadgeStringOffset,
                    out var textPointer))
            {
                continue;
            }

            var display = ParseBadgeName(
                ReadUtf16(memory, textPointer, 96));
            AddContent(result, display);
        }

        return result;
    }

    private static nint ReadFirstChild(
        IProcessMemory memory,
        nint element,
        AtlasLayoutProfile profile)
        => element != 0
           && TryReadPointer(
               memory,
               element + profile.Game.UiElement.ChildrenOffset,
               out var begin)
            ? TryReadPointer(memory, begin, out var child)
                ? child
                : 0
            : 0;

    private static string ReadAscii(
        IProcessMemory memory,
        nint address,
        int maxBytes)
    {
        var bytes = new byte[maxBytes];
        Span<byte> character = stackalloc byte[1];
        for (var index = 0; index < maxBytes; index++)
        {
            if (!memory.TryRead(
                    address + index,
                    character)
                || character[0] == 0
                || character[0] is < 0x20 or >= 0x7F)
            {
                break;
            }

            bytes[index] = character[0];
        }

        var length = Array.IndexOf(bytes, (byte)0);
        return Encoding.ASCII.GetString(
            bytes,
            0,
            length < 0 ? maxBytes : length);
    }

    private static string TitleCase(string value)
    {
        var parts = value.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < parts.Length; index++)
        {
            parts[index] = char.ToUpperInvariant(parts[index][0])
                + parts[index][1..];
        }

        return string.Join(' ', parts);
    }

    private static string ParseBadgeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var value = raw.Trim();
        var left = value.IndexOf('[');
        var right = value.LastIndexOf(']');
        if (left >= 0 && right > left)
        {
            value = value[(left + 1)..right];
        }

        var pipe = value.IndexOf('|');
        if (pipe >= 0)
        {
            value = value[(pipe + 1)..];
        }

        return value.Trim();
    }

    private static void AddContent(ICollection<string> result, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && !result.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            result.Add(value);
        }
    }

    private static HashSet<int> ReadBiomeKinds(
        IProcessMemory memory,
        IEnumerable<nint> elements,
        AtlasLayoutProfile profile)
    {
        var result = new HashSet<int>();
        foreach (var element in elements.Take(400))
        {
            if (TryReadByte(
                    memory,
                    element + profile.AtlasBiomeOffset,
                    out var biome))
            {
                result.Add(biome);
            }
        }

        return result;
    }

    private static float ReadModalWidth(
        IProcessMemory memory,
        IEnumerable<nint> elements,
        AtlasLayoutProfile profile)
    {
        var widths = elements
            .Take(400)
            .Select(element =>
                TryReadFloat(memory, element + profile.Game.UiElement.WidthOffset, out var width)
                    ? width
                    : 0f)
            .Where(width => float.IsFinite(width) && width > 0f)
            .GroupBy(width => MathF.Round(width))
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault();
        return widths;
    }

    private static float TryReadNodeScale(
        IProcessMemory memory,
        IEnumerable<nint> elements,
        AtlasLayoutProfile profile)
    {
        var scales = elements
            .Take(400)
            .Select(element => TryReadScale(memory, element, profile))
            .Where(scale => scale > 0f)
            .Order()
            .ToArray();
        return scales.Length == 0
            ? 1f
            : scales[scales.Length / 2];
    }

    private static float TryReadScale(
        IProcessMemory memory,
        nint element,
        AtlasLayoutProfile profile)
    {
        if (TryReadFloat(
                memory,
                element + profile.Game.UiElement.ScaleOffset,
                out var scale)
            && float.IsFinite(scale)
            && scale > 0f)
        {
            return scale;
        }

        return 1f;
    }

    private static Vector2 ReadNodeSize(
        IProcessMemory memory,
        IEnumerable<nint> elements,
        AtlasLayoutProfile profile)
    {
        var sizes = elements
            .Take(400)
            .Select(element =>
            {
                var width = TryReadFloat(
                    memory,
                    element + profile.Game.UiElement.WidthOffset,
                    out var rawWidth)
                    ? rawWidth
                    : 0f;
                var height = TryReadFloat(
                    memory,
                    element + profile.Game.UiElement.HeightOffset,
                    out var rawHeight)
                    ? rawHeight
                    : 0f;
                return new Vector2(width, height);
            })
            .Where(size =>
                float.IsFinite(size.X)
                && float.IsFinite(size.Y)
                && size.X > 0f
                && size.Y > 0f)
            .GroupBy(size => (Width: MathF.Round(size.X), Height: MathF.Round(size.Y)))
            .OrderByDescending(group => group.Count())
            .Select(group => new Vector2(group.Key.Width, group.Key.Height))
            .FirstOrDefault();

        return sizes is { X: > 0f, Y: > 0f }
            ? sizes
            : new Vector2(profile.ExpectedNodeSize, profile.ExpectedNodeSize);
    }

    private static Vector2 ReadUnscaledPosition(
        IProcessMemory memory,
        nint element,
        AtlasLayoutProfile profile)
    {
        var position = Vector2.Zero;
        var current = element;
        for (var depth = 0; depth < 64 && IsCanonical(current); depth++)
        {
            if (!TryReadFloat(
                    memory,
                    current + profile.Game.UiElement.RelativePositionOffset,
                    out var x)
                || !TryReadFloat(
                    memory,
                    current + profile.Game.UiElement.RelativePositionOffset + 4,
                    out var y)
                || !float.IsFinite(x)
                || !float.IsFinite(y))
            {
                break;
            }

            position += new Vector2(x, y);
            if (!TryReadPointer(
                    memory,
                    current + profile.Game.UiElement.ParentOffset,
                    out var parent)
                || parent == current)
            {
                break;
            }

            current = parent;
        }

        return position;
    }

    private void UpdateCanvasParentOffset(
        IProcessMemory memory,
        nint canvas,
        Vector2 canvasPosition,
        AtlasLayoutProfile profile)
    {
        if (!TryReadFloat(
                memory,
                canvas + profile.Game.UiElement.RelativePositionOffset,
                out var localX)
            || !TryReadFloat(
                memory,
                canvas + profile.Game.UiElement.RelativePositionOffset + 4,
                out var localY))
        {
            return;
        }

        var parentOffset = canvasPosition - new Vector2(localX, localY);
        if (float.IsFinite(parentOffset.X)
            && float.IsFinite(parentOffset.Y))
        {
            _cachedCanvasParentOffset = parentOffset;
            _cachedCanvasParentOffsetValid = true;
        }
    }

    private static IEnumerable<nint> ReadChildren(
        IProcessMemory memory,
        nint element,
        AtlasLayoutProfile profile)
    {
        if (!TryReadPointer(
                memory,
                element + profile.Game.UiElement.ChildrenOffset,
                out var first)
            || !TryReadPointer(
                memory,
                element + profile.Game.UiElement.ChildrenEndOffset,
                out var last)
            || first == 0
            || last < first)
        {
            yield break;
        }

        var byteCount = (long)last - (long)first;
        if (byteCount % IntPtr.Size != 0)
        {
            yield break;
        }

        var count = byteCount / IntPtr.Size;
        if (count > MaxChildrenPerElement)
        {
            yield break;
        }

        for (var index = 0L; index < count; index++)
        {
            if (TryReadPointer(
                    memory,
                    first + (nint)(index * IntPtr.Size),
                    out var child)
                && IsCanonical(child))
            {
                yield return child;
            }
        }
    }

    private static bool TryReadGrid(
        IProcessMemory memory,
        nint element,
        AtlasLayoutProfile profile,
        out AtlasGridPos grid)
    {
        if (TryReadInt32(memory, element + profile.AtlasGridOffset, out var x)
            && TryReadInt32(memory, element + profile.AtlasGridOffset + 4, out var y))
        {
            grid = new AtlasGridPos(x, y);
            return true;
        }

        grid = default;
        return false;
    }

    private static bool IsUiElement(
        IProcessMemory memory,
        nint element,
        AtlasLayoutProfile profile)
        => IsCanonical(element)
           && TryReadPointer(
               memory,
               element + profile.Game.UiElement.SelfOffset,
               out var self)
           && self == element;

    private static bool IsHierarchicallyVisible(
        IProcessMemory memory,
        nint element,
        AtlasLayoutProfile profile,
        ICollection<string> diagnostics)
    {
        var current = element;
        for (var depth = 0; depth < 16 && IsCanonical(current); depth++)
        {
            if (!TryReadUInt32(
                    memory,
                    current + profile.Game.UiElement.FlagsOffset,
                    out var flags)
                || ((flags >> profile.Game.UiElement.VisibleBit) & 1u) == 0)
            {
                diagnostics.Add(
                    $"Atlas visibility depth={depth}; element=0x{current:X}; "
                    + $"flags={(TryReadUInt32(
                        memory,
                        current + profile.Game.UiElement.FlagsOffset,
                        out var failedFlags)
                        ? $"0x{failedFlags:X8}"
                        : "unreadable")}; "
                    + $"visibleBit={profile.Game.UiElement.VisibleBit}.");
                return false;
            }

            if (!TryReadPointer(
                    memory,
                    current + profile.Game.UiElement.ParentOffset,
                    out var parent))
            {
                diagnostics.Add(
                    $"Atlas visibility depth={depth}; element=0x{current:X}; "
                    + "parent=unreadable; treating as visible root boundary.");
                return true;
            }

            diagnostics.Add(
                $"Atlas visibility depth={depth}; element=0x{current:X}; "
                + $"flags=0x{flags:X8}; parent=0x{parent:X}.");

            if (parent == current)
            {
                return true;
            }

            current = parent;
        }

        return false;
    }

    private static string? ReadPointerString(
        IProcessMemory memory,
        nint address,
        int maxChars)
        => TryReadPointer(memory, address, out var text)
            ? ReadUtf16(memory, text, maxChars)
            : null;

    private static string? ReadUtf16(
        IProcessMemory memory,
        nint address,
        int maxChars)
        => memory.TryReadUtf16(address, maxChars, out var value)
            ? value
            : null;

    private static bool TryReadInt32(
        IProcessMemory memory,
        nint address,
        out int value)
        => memory.TryReadInt32(address, out value);

    private static bool TryReadUInt32(
        IProcessMemory memory,
        nint address,
        out uint value)
    {
        if (memory.TryReadInt32(address, out var signed))
        {
            value = unchecked((uint)signed);
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryReadFloat(
        IProcessMemory memory,
        nint address,
        out float value)
        => memory.TryReadFloat(address, out value);

    private static bool TryReadByte(
        IProcessMemory memory,
        nint address,
        out byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        if (memory.TryRead(address, buffer))
        {
            value = buffer[0];
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryReadPointer(
        IProcessMemory memory,
        nint address,
        out nint value)
        => memory.TryReadPointer(address, out value)
           && IsCanonical(value);

    private static bool Matches(
        ReadOnlySpan<byte> bytes,
        int offset,
        IReadOnlyList<byte?> pattern)
    {
        for (var index = 0; index < pattern.Count; index++)
        {
            if (pattern[index] is { } expected
                && bytes[offset + index] != expected)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsCanonical(nint value)
    {
        var unsigned = unchecked((ulong)value.ToInt64());
        return unsigned is >= MinCanonicalPointer and <= MaxCanonicalPointer;
    }

    private void ResetCache()
    {
        _cachedInGameState = 0;
        ResetAtlasCache();
    }

    private void ResetAtlasCache()
    {
        _cachedCanvas = 0;
        _cachedNodeVtable = 0;
        _cachedCurrentMarker = 0;
        _cachedCurrentMarkerCandidates = [];
        _cachedCurrentMarkerCandidateIndex = 0;
        _cachedCurrentGrid = null;
        _currentMarkerReadMisses = 0;
        _currentMarkerRediscoverySampleInterval =
            CurrentMarkerRediscoverySampleInterval;
        _cachedCanvasParentOffset = Vector2.Zero;
        _cachedCanvasParentOffsetValid = false;
        _cachedNodeData.Clear();
        _cachedNodeElements.Clear();
        lock (_liveGeometryGate)
        {
            _lastLiveCanvas = 0;
            _lastLiveZoom = 0f;
            _lastStableLivePositions =
                new Dictionary<AtlasGridPos, Vector2>();
        }
        Volatile.Write(ref _publishedLiveGeometry, null);
    }

    private void PublishLiveGeometryCache(int processId)
    {
        var nodeElements = new Dictionary<AtlasGridPos, nint>(
            _cachedNodeElements);
        Volatile.Write(
            ref _publishedLiveGeometry,
            new LiveGeometryCache(
                processId,
                _cachedCanvas,
                nodeElements,
                _cachedCanvasParentOffset,
                _cachedCanvasParentOffsetValid));
    }

    private static AtlasUiTreeSnapshot EmptySnapshot(
        bool hasUiRoot,
        bool hasCanvas,
        string token)
        => new(
            hasUiRoot,
            hasCanvas,
            token,
            1f,
            [],
            [],
            null,
            IsAtlasOpen: false);
}
