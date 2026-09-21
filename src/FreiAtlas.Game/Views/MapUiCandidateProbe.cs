using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Memory;
using FreiAtlas.Game.Memory;

namespace FreiAtlas.Game.Views;

internal sealed record MapUiCandidate(
    string Fingerprint,
    bool IsVisible,
    Vector2 Shift,
    float Zoom,
    AreaUiRect Viewport,
    float RotationRaw,
    bool HasRotationEvidence)
{
    public nint Address { get; init; }
}

internal sealed record MapUiProbeResult(
    IReadOnlyList<MapUiCandidate> Candidates,
    bool IsTruncated,
    IReadOnlyList<AreaReadDiagnostic> Diagnostics);

internal sealed class MapUiCandidateProbe
{
    public const int MaximumNodes = 30_000;
    public const int MaximumChildrenPerNode = 8_192;
    public const string DirectLargeFingerprint = "direct/large";
    public const string DirectMiniFingerprint = "direct/mini";

    private readonly IProcessMemory _memory;
    private readonly Poe2MemoryProfile _profile;
    private readonly GameMemoryReader _reader;
    private readonly int _maximumNodes;
    private readonly List<CachedMapCandidate> _cachedCandidates = [];

    public MapUiCandidateProbe(
        IProcessMemory memory,
        Poe2MemoryProfile? profile = null,
        int maximumNodes = MaximumNodes)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _profile = profile ?? Poe2MemoryProfile.Current;
        _reader = new GameMemoryReader(memory, _profile);
        _maximumNodes = maximumNodes > 0
            ? maximumNodes
            : throw new ArgumentOutOfRangeException(nameof(maximumNodes));
    }

    public MapUiProbeResult Probe(nint inGameState)
        => Probe(inGameState, 1920f, 1080f);

    public MapUiProbeResult ProbeDirect(nint inGameState)
        => ProbeDirect(inGameState, 1920f, 1080f);

    public MapUiProbeResult ProbeDirect(
        nint inGameState,
        float windowWidth,
        float windowHeight)
    {
        if (!IsValidWindow(windowWidth, windowHeight))
        {
            return InvalidWindow();
        }

        if (!_reader.TryReadPointer(
                inGameState + _profile.InGameState.UiRootOffset,
                out var uiManager))
        {
            return new MapUiProbeResult(
                [],
                false,
                [DirectDiagnostic(
                    "map-ui-direct-manager-unavailable",
                    "The current UI manager pointer could not be read.")]);
        }

        if (!_reader.TryReadPointer(
                uiManager + _profile.ImportantUi.MapParentOffset,
                out var mapParent))
        {
            return new MapUiProbeResult(
                [],
                false,
                [DirectDiagnostic(
                    "map-ui-direct-parent-unavailable",
                    "The current map parent pointer could not be read.")]);
        }

        var candidates = new List<MapUiCandidate>(2);
        var diagnostics = new List<AreaReadDiagnostic>(2);
        ReadDirectCandidate(
            mapParent,
            _profile.MapParent.LargeMapOffset,
            DirectLargeFingerprint,
            isLargeMap: true,
            windowWidth,
            windowHeight,
            candidates,
            diagnostics);
        ReadDirectCandidate(
            mapParent,
            _profile.MapParent.MiniMapOffset,
            DirectMiniFingerprint,
            isLargeMap: false,
            windowWidth,
            windowHeight,
            candidates,
            diagnostics);

        if (candidates.Count == 2
            && candidates[0].Address == candidates[1].Address)
        {
            return new MapUiProbeResult(
                [],
                false,
                [DirectDiagnostic(
                    "map-ui-direct-identity-duplicate",
                    "The direct large-map and minimap pointers resolved to the same element.")]);
        }

        return new MapUiProbeResult(candidates, false, diagnostics);
    }

    public MapUiProbeResult Probe(
        nint inGameState,
        float windowWidth,
        float windowHeight)
    {
        if (!IsValidWindow(windowWidth, windowHeight))
        {
            return InvalidWindow();
        }

        var diagnostics = new List<AreaReadDiagnostic>();
        if (!_reader.TryReadPointer(
                inGameState + _profile.InGameState.UiRootOffset,
                out var uiRoot))
        {
            return new MapUiProbeResult(
                [],
                false,
                [new AreaReadDiagnostic(
                    "map-ui-root-unavailable",
                    "The UI root pointer could not be read.",
                    AreaDiagnosticSeverity.Warning)]);
        }

        var candidates = new List<MapUiCandidate>();
        if (_cachedCandidates.Count > 0)
        {
            var refreshed = RefreshCachedCandidates(
                windowWidth,
                windowHeight);
            if (refreshed.Candidates.Count > 0
                && refreshed.Diagnostics.Count == 0)
            {
                return refreshed;
            }

            diagnostics.AddRange(refreshed.Diagnostics);
            Reset();
        }

        _cachedCandidates.Clear();
        var visited = new HashSet<nint>();
        var queue = new Queue<UiWorkItem>();
        queue.Enqueue(new UiWorkItem(uiRoot, "root", true, null));
        var truncated = false;

        while (queue.Count > 0)
        {
            if (visited.Count >= _maximumNodes)
            {
                truncated = true;
                break;
            }

            var item = queue.Dequeue();
            if (!visited.Add(item.Address))
            {
                continue;
            }

            var selfValid = _reader.TryReadPointer(
                item.Address + _profile.UiElement.SelfOffset,
                out var self)
                && self == item.Address;
            if (!selfValid)
            {
                diagnostics.Add(new AreaReadDiagnostic(
                    "map-ui-self-invalid",
                    "A UI node failed its self-pointer liveness check.",
                    AreaDiagnosticSeverity.Warning));
            }

            var localVisible = IsLocallyVisible(item.Address);
            var visible = item.ParentVisible && localVisible;
            var hasLocalViewport = TryReadViewport(
                item.Address,
                windowWidth,
                windowHeight,
                out var localViewport);
            var viewportForDescendants = hasLocalViewport
                ? localViewport
                : item.AncestorViewport;

            string? candidateDiagnostic = null;
            if (selfValid
                && TryReadCandidate(
                    item.Address,
                    item.Fingerprint,
                    localVisible,
                    item.AncestorViewport,
                    windowWidth,
                    windowHeight,
                    out var candidate,
                    out candidateDiagnostic))
            {
                candidates.Add(candidate);
            }
            else if (candidateDiagnostic is not null)
            {
                diagnostics.Add(new AreaReadDiagnostic(
                    candidateDiagnostic,
                    "A map UI candidate matched its map signature but its viewport geometry was unavailable.",
                    AreaDiagnosticSeverity.Warning));
            }

            if (!_reader.TryReadStdVector(
                    item.Address + _profile.UiElement.ChildrenOffset,
                    IntPtr.Size,
                    MaximumChildrenPerNode,
                    out var children))
            {
                diagnostics.Add(new AreaReadDiagnostic(
                    "map-ui-children-invalid",
                    "A UI child vector was invalid or exceeded the bounded limit.",
                    AreaDiagnosticSeverity.Warning));
                continue;
            }

            for (var index = 0; index < children.Count; index++)
            {
                if (!_reader.TryReadPointer(
                        children.First + (index * IntPtr.Size),
                        out var child))
                {
                    continue;
                }

                queue.Enqueue(new UiWorkItem(
                    child,
                    $"{item.Fingerprint}/{index}",
                    visible,
                    viewportForDescendants));
            }
        }

        if (truncated)
        {
            diagnostics.Add(new AreaReadDiagnostic(
                "map-ui-node-limit",
                "The UI tree exceeded the bounded traversal limit.",
                candidates.Count > 0
                    ? AreaDiagnosticSeverity.Info
                    : AreaDiagnosticSeverity.Warning));
        }

        return new MapUiProbeResult(candidates, truncated, diagnostics);
    }

    public void Reset()
    {
        _cachedCandidates.Clear();
    }

    private void ReadDirectCandidate(
        nint mapParent,
        int pointerOffset,
        string fingerprint,
        bool isLargeMap,
        float windowWidth,
        float windowHeight,
        List<MapUiCandidate> candidates,
        List<AreaReadDiagnostic> diagnostics)
    {
        if (!_reader.TryReadPointer(mapParent + pointerOffset, out var address)
            || !_reader.TryReadPointer(
                address + _profile.UiElement.SelfOffset,
                out var self)
            || self != address)
        {
            diagnostics.Add(DirectDiagnostic(
                isLargeMap
                    ? "map-ui-direct-large-unavailable"
                    : "map-ui-direct-mini-unavailable",
                "A direct map element pointer failed its self-pointer liveness check."));
            return;
        }

        if (!_reader.TryReadVector2(
                address + _profile.MapUi.ShiftOffset,
                out var shift)
            || !_reader.TryReadVector2(
                address + _profile.MapUi.DefaultShiftOffset,
                out _)
            || !_memory.TryReadFloat(
                address + _profile.MapUi.ZoomOffset,
                out var zoom)
            || !float.IsFinite(zoom)
            || zoom <= 0.05f
            || zoom >= 8f)
        {
            diagnostics.Add(DirectDiagnostic(
                isLargeMap
                    ? "map-ui-direct-large-fields-invalid"
                    : "map-ui-direct-mini-fields-invalid",
                "A direct map element did not expose readable shift and zoom fields."));
            return;
        }

        AreaUiRect viewport;
        if (isLargeMap)
        {
            viewport = new AreaUiRect(0f, 0f, windowWidth, windowHeight);
        }
        else if (!TryReadViewport(
                     address,
                     windowWidth,
                     windowHeight,
                     out viewport,
                     out var viewportDiagnostic))
        {
            diagnostics.Add(DirectDiagnostic(
                viewportDiagnostic ?? "map-ui-direct-mini-geometry-invalid",
                "The direct minimap element did not expose valid client geometry."));
            return;
        }

        candidates.Add(new MapUiCandidate(
            fingerprint,
            IsLocallyVisible(address),
            shift,
            zoom,
            viewport,
            0f,
            false)
        {
            Address = address
        });
    }

    private static bool IsValidWindow(float windowWidth, float windowHeight)
        => float.IsFinite(windowWidth)
           && float.IsFinite(windowHeight)
           && windowWidth > 0f
           && windowHeight > 0f;

    private static MapUiProbeResult InvalidWindow()
        => new(
            [],
            false,
            [new AreaReadDiagnostic(
                "map-ui-window-invalid",
                "The client viewport dimensions were invalid.",
                AreaDiagnosticSeverity.Warning)]);

    private static AreaReadDiagnostic DirectDiagnostic(string code, string message)
        => new(code, message, AreaDiagnosticSeverity.Warning);

    private MapUiProbeResult RefreshCachedCandidates(
        float windowWidth,
        float windowHeight)
    {
        var candidates = new List<MapUiCandidate>(_cachedCandidates.Count);
        var diagnostics = new List<AreaReadDiagnostic>();
        for (var index = 0; index < _cachedCandidates.Count; index++)
        {
            var cached = _cachedCandidates[index];
            if (TryReadCandidate(
                    cached.Address,
                    cached.Fingerprint,
                    IsLocallyVisible(cached.Address),
                    cached.Viewport,
                    windowWidth,
                    windowHeight,
                    out var candidate,
                    out var diagnosticCode,
                    cacheCandidate: false))
            {
                candidates.Add(candidate);
                _cachedCandidates[index] = cached with { Viewport = candidate.Viewport };
            }
            else if (diagnosticCode is not null)
            {
                diagnostics.Add(new AreaReadDiagnostic(
                    diagnosticCode,
                    "A cached map UI candidate could not be refreshed.",
                    AreaDiagnosticSeverity.Warning));
            }
        }

        if (candidates.Count != _cachedCandidates.Count
            && diagnostics.Count == 0)
        {
            diagnostics.Add(new AreaReadDiagnostic(
                "map-ui-cache-refresh-empty",
                "Cached map UI candidates could not be refreshed; rediscovery is required.",
                AreaDiagnosticSeverity.Info));
        }

        return new MapUiProbeResult(candidates, false, diagnostics);
    }

    private bool TryReadCandidate(
        nint address,
        string fingerprint,
        bool visible,
        AreaUiRect? ancestorViewport,
        float windowWidth,
        float windowHeight,
        out MapUiCandidate candidate,
        out string? diagnosticCode,
        bool cacheCandidate = true)
    {
        candidate = default!;
        diagnosticCode = null;
        if (!TryReadFixedMapFields(address, out var shift, out var zoom))
        {
            Span<byte> body = stackalloc byte[0x400];
            if (!_memory.TryRead(address, body)
                || !TryReadMapFields(body, out shift, out zoom))
            {
                return false;
            }
        }

        if (!TryReadViewport(
                address,
                windowWidth,
                windowHeight,
                out var viewport,
                out var viewportDiagnostic))
        {
            diagnosticCode = viewportDiagnostic;
            if (viewportDiagnostic is not null)
            {
                return false;
            }

            if (ancestorViewport is not { } fallbackViewport)
            {
                diagnosticCode = "map-ui-geometry-invalid";
                return false;
            }

            viewport = fallbackViewport;
        }

        candidate = new MapUiCandidate(
            fingerprint,
            visible,
            shift,
            zoom,
            viewport,
            0f,
            false)
        {
            Address = address
        };
        if (cacheCandidate)
        {
            _cachedCandidates.Add(new CachedMapCandidate(
                address,
                fingerprint,
                viewport));
        }
        return true;
    }

    private bool TryReadViewport(
        nint address,
        float windowWidth,
        float windowHeight,
        out AreaUiRect viewport)
        => TryReadViewport(
            address,
            windowWidth,
            windowHeight,
            out viewport,
            out _);

    private bool TryReadViewport(
        nint address,
        float windowWidth,
        float windowHeight,
        out AreaUiRect viewport,
        out string? diagnosticCode)
    {
        viewport = new AreaUiRect(0f, 0f, 0f, 0f);
        diagnosticCode = null;
        if (!_reader.TryReadVector2(
                address + _profile.UiElement.WidthOffset,
                out var size)
            || size.X <= 1f
            || size.Y <= 1f)
        {
            return false;
        }

        if (!TryReadUnscaledPosition(address, 0, out var position, out var parentCycle)
            || !TryReadScale(address, windowWidth, windowHeight, out var scale))
        {
            if (parentCycle)
            {
                diagnosticCode = "map-ui-parent-cycle";
            }

            return false;
        }

        var screenPosition = new Vector2(
            position.X * scale.X,
            position.Y * scale.Y);
        var screenSize = new Vector2(
            size.X * scale.X,
            size.Y * scale.Y);
        if (!float.IsFinite(screenPosition.X)
            || !float.IsFinite(screenPosition.Y)
            || !float.IsFinite(screenSize.X)
            || !float.IsFinite(screenSize.Y)
            || screenSize.X <= 1f
            || screenSize.Y <= 1f)
        {
            return false;
        }

        viewport = new AreaUiRect(
            screenPosition.X,
            screenPosition.Y,
            screenSize.X,
            screenSize.Y);
        return true;
    }

    private bool TryReadScale(
        nint address,
        float windowWidth,
        float windowHeight,
        out Vector2 scale)
    {
        scale = Vector2.One;
        if (!_memory.TryReadFloat(
                address + _profile.UiElement.ScaleOffset,
                out var localScaleMultiplier))
        {
            return false;
        }

        if (!_reader.TryReadByte(
                address + _profile.UiElement.ScaleIndexOffset,
                out var scaleIndex))
        {
            return false;
        }

        if (!float.IsFinite(localScaleMultiplier))
        {
            return false;
        }

        if (localScaleMultiplier == 0f)
        {
            localScaleMultiplier = 1f;
        }

        var widthScale = windowWidth / _profile.UiElement.BaseResolutionWidth;
        var heightScale = windowHeight / _profile.UiElement.BaseResolutionHeight;
        scale = scaleIndex switch
        {
            1 => new Vector2(widthScale, widthScale) * localScaleMultiplier,
            2 => new Vector2(heightScale, heightScale) * localScaleMultiplier,
            3 => new Vector2(widthScale, heightScale) * localScaleMultiplier,
            _ => new Vector2(localScaleMultiplier, localScaleMultiplier)
        };
        return float.IsFinite(scale.X)
               && float.IsFinite(scale.Y)
               && scale.X > 0f
               && scale.Y > 0f;
    }

    private bool TryReadUnscaledPosition(
        nint address,
        int depth,
        out Vector2 position)
        => TryReadUnscaledPosition(
            address,
            depth,
            new HashSet<nint>(),
            out position,
            out _);

    private bool TryReadUnscaledPosition(
        nint address,
        int depth,
        out Vector2 position,
        out bool parentCycle)
        => TryReadUnscaledPosition(
            address,
            depth,
            new HashSet<nint>(),
            out position,
            out parentCycle);

    private bool TryReadUnscaledPosition(
        nint address,
        int depth,
        HashSet<nint> path,
        out Vector2 position,
        out bool parentCycle)
    {
        position = default;
        parentCycle = false;
        if (!path.Add(address))
        {
            parentCycle = true;
            return false;
        }

        if (!_reader.TryReadVector2(
                address + _profile.UiElement.RelativePositionOffset,
                out var relativePosition)
            || !float.IsFinite(relativePosition.X)
            || !float.IsFinite(relativePosition.Y))
        {
            path.Remove(address);
            return false;
        }

        if (depth >= 64
            || !_reader.TryReadPointer(
                address + _profile.UiElement.ParentOffset,
                out var parent)
            || parent == 0)
        {
            position = relativePosition;
            path.Remove(address);
            return true;
        }

        if (!TryReadUnscaledPosition(
                parent,
                depth + 1,
                path,
                out var parentPosition,
                out parentCycle))
        {
            path.Remove(address);
            return false;
        }

        Span<byte> flagBytes = stackalloc byte[sizeof(uint)];
        if (_memory.TryRead(
                address + _profile.UiElement.FlagsOffset,
                flagBytes))
        {
            var flags = BitConverter.ToUInt32(flagBytes);
            if ((flags & (1u << _profile.UiElement.ModifyPositionBit)) != 0)
            {
                if (!_reader.TryReadVector2(
                        parent + _profile.UiElement.PositionModifierOffset,
                        out var modifier))
                {
                    path.Remove(address);
                    return false;
                }
                parentPosition += modifier;
            }
        }

        position = parentPosition + relativePosition;
        path.Remove(address);
        return true;
    }

    private bool TryReadFixedMapFields(
        nint address,
        out Vector2 shift,
        out float zoom)
    {
        shift = default;
        zoom = default;
        return _reader.TryReadVector2(
                   address + _profile.MapUi.DefaultShiftOffset,
                   out var defaultShift)
               && MathF.Abs(defaultShift.X) <= 0.001f
               && MathF.Abs(defaultShift.Y + 20f) <= 0.001f
               && _reader.TryReadVector2(
                   address + _profile.MapUi.ShiftOffset,
                   out shift)
               && _memory.TryReadFloat(
                   address + _profile.MapUi.ZoomOffset,
                   out zoom)
               && float.IsFinite(zoom)
               && zoom > 0.05f
               && zoom < 8f;
    }

    private bool TryReadMapFields(
        ReadOnlySpan<byte> body,
        out Vector2 shift,
        out float zoom)
    {
        shift = default;
        zoom = default;
        var preferredOffset = _profile.MapUi.DefaultShiftOffset;
        if (TryReadMapFieldsAt(body, preferredOffset, out shift, out zoom))
        {
            return true;
        }

        // Radar discovers this pair dynamically because the map subclass moved between builds.
        // Keep the scan bounded to the verified UI element body and require a plausible zoom.
        for (var offset = 0x100; offset + 0x3C <= body.Length; offset += sizeof(float))
        {
            if (offset == preferredOffset)
            {
                continue;
            }

            if (TryReadMapFieldsAt(body, offset, out shift, out zoom))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadMapFieldsAt(
        ReadOnlySpan<byte> body,
        int defaultShiftOffset,
        out Vector2 shift,
        out float zoom)
    {
        shift = default;
        zoom = default;
        if (defaultShiftOffset < sizeof(float) * 2
            || defaultShiftOffset + 0x3C > body.Length)
        {
            return false;
        }

        var defaultX = BitConverter.ToSingle(body[defaultShiftOffset..]);
        var defaultY = BitConverter.ToSingle(body[(defaultShiftOffset + sizeof(float))..]);
        if (!float.IsFinite(defaultX)
            || !float.IsFinite(defaultY)
            || MathF.Abs(defaultX) > 0.001f
            || MathF.Abs(defaultY + 20f) > 0.001f)
        {
            return false;
        }

        zoom = BitConverter.ToSingle(body[(defaultShiftOffset + 0x38)..]);
        if (!float.IsFinite(zoom) || zoom <= 0.05f || zoom >= 8f)
        {
            return false;
        }

        var shiftOffset = defaultShiftOffset - (sizeof(float) * 2);
        shift = new Vector2(
            BitConverter.ToSingle(body[shiftOffset..]),
            BitConverter.ToSingle(body[(shiftOffset + sizeof(float))..]));
        return float.IsFinite(shift.X) && float.IsFinite(shift.Y);
    }

    private bool IsLocallyVisible(nint address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        return _memory.TryRead(
                   address + _profile.UiElement.FlagsOffset,
                   bytes)
               && (BitConverter.ToUInt32(bytes)
                   & (1u << _profile.UiElement.VisibleBit)) != 0;
    }

    private readonly record struct UiWorkItem(
        nint Address,
        string Fingerprint,
        bool ParentVisible,
        AreaUiRect? AncestorViewport);

    private readonly record struct CachedMapCandidate(
        nint Address,
        string Fingerprint,
        AreaUiRect Viewport);
}
