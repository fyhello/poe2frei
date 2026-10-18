using System.Numerics;
using System.Runtime.InteropServices;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Memory;
using FreiAtlas.Core.Pickup;
using FreiAtlas.Game.Entities;

namespace FreiAtlas.Game.Memory;

/// <summary>Read-only discovery probe. It never sends input or exposes process addresses.</summary>
public sealed class GroundItemResearchProbe
{
    private const int MaximumMetadataLength = 1024;
    private readonly IProcessMemory _memory;
    private readonly Poe2MemoryProfile _profile;
    private readonly GameMemorySession _session;
    private readonly AreaEntityTreeReader _entityTree;
    private readonly EntityComponentResolver _components;
    private readonly GameMemoryReader _reader;
    private readonly GroundItemLabelReader _labelReader;

    public GroundItemResearchProbe(IProcessMemory memory, Poe2MemoryProfile? profile = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _profile = profile ?? Poe2MemoryProfile.Current;
        _session = new GameMemorySession(memory, _profile, new GameStateLocator(_profile));
        _entityTree = new AreaEntityTreeReader(memory, _profile);
        _components = new EntityComponentResolver(memory, _profile);
        _reader = new GameMemoryReader(memory, _profile);
        _labelReader = new GroundItemLabelReader(memory, _profile);
    }

    public GroundItemResearchResult Capture(Vector2? clientViewport = null)
    {
        if (!_session.TryRefresh(out var root, out var area, out var diagnostics))
            return new(_memory.ProcessId, _profile.ProfileId, null, null, [], diagnostics);

        var messages = diagnostics.ToList();
        _components.BeginSample(area.SessionSequence);
        if (!_components.TryResolve(new RawEntityRef(0, root.LocalPlayer), "Render", out var playerRender)
            || playerRender == 0
            || !_reader.TryReadVector3(playerRender + _profile.Render.WorldPositionOffset, out var playerWorld))
        {
            messages.Add(new AreaReadDiagnostic(
                "ground-item-player-position-unavailable",
                "Unable to read the local player's world position for ground-item research.",
                AreaDiagnosticSeverity.Warning));
            return new(_memory.ProcessId, _profile.ProfileId, area, null, [], messages);
        }

        var ratio = _profile.WorldToGridRatio;
        if (!float.IsFinite(ratio) || ratio <= 0f)
        {
            messages.Add(new AreaReadDiagnostic(
                "ground-item-grid-ratio-invalid",
                "The configured world-to-grid ratio is invalid.",
                AreaDiagnosticSeverity.Error));
            return new(_memory.ProcessId, _profile.ProfileId, area, null, [], messages);
        }

        var playerGrid = new Vector2(playerWorld.X / ratio, playerWorld.Y / ratio);
        var cameraMatrix = CaptureCameraMatrix(root.InGameState);
        if (!_entityTree.TryRead(root.AreaInstance, out var entities, out var treeDiagnostic))
        {
            if (treeDiagnostic is not null) messages.Add(treeDiagnostic);
            return new(_memory.ProcessId, _profile.ProfileId, area, playerGrid, [], messages, cameraMatrix);
        }

        var candidates = new List<GroundItemResearchCandidate>();
        foreach (var entity in entities)
        {
            if (entity.EntityId == 0 || entity.EntityAddress == root.LocalPlayer)
                continue;

            var componentNames = ReadComponentNames(entity);
            if (!componentNames.Contains("WorldItem", StringComparer.Ordinal)
                || !_reader.TryReadPointer(entity.EntityAddress + _profile.Entity.DetailsOffset, out var details)
                || !_reader.TryReadStdWString(details + _profile.EntityDetails.NameOffset, MaximumMetadataLength, out var metadata)
                || string.IsNullOrWhiteSpace(metadata)
                || !_components.TryResolve(entity, "Render", out var render)
                || render == 0
                || !_reader.TryReadVector3(render + _profile.Render.WorldPositionOffset, out var world))
                continue;

            var grid = new Vector2(world.X / ratio, world.Y / ratio);
            if (!float.IsFinite(grid.X) || !float.IsFinite(grid.Y)) continue;

            var itemEntityReady = _reader.TryReadPointer(
                _components.TryResolve(entity, "WorldItem", out var worldItem) && worldItem != 0
                    ? worldItem + _profile.GroundItem.WorldItemEntityOffset
                    : 0,
                out var itemEntityAddress);
            var itemName = string.Empty;
            var artName = string.Empty;
            int? rarity = null;
            bool? identified = null;
            if (itemEntityReady && itemEntityAddress != 0)
            {
                var itemEntity = new RawEntityRef(entity.EntityId, itemEntityAddress);
                if (_components.TryResolve(itemEntity, "Base", out var baseComponent) && baseComponent != 0
                    && _reader.TryReadPointer(baseComponent + _profile.GroundItem.BaseNameRowOffset, out var nameRow)
                    && nameRow != 0)
                {
                    _reader.TryReadStdWString(
                        nameRow + _profile.GroundItem.NameRowDisplayNameOffset,
                        MaximumMetadataLength,
                        out itemName);
                }

                if (_components.TryResolve(itemEntity, "Mods", out var mods) && mods != 0)
                {
                    if (_reader.TryReadUInt32(mods + _profile.GroundItem.ModsRarityOffset, out var rawRarity)
                        && rawRarity <= 3)
                        rarity = (int)rawRarity;
                    if (_reader.TryReadUInt32(mods + _profile.GroundItem.ModsIdentifiedOffset, out var rawIdentified)
                        && rawIdentified <= 1)
                        identified = rawIdentified == 1;
                }

                if (_components.TryResolve(itemEntity, "RenderItem", out var renderItem) && renderItem != 0
                    && _reader.TryReadPointer(
                        renderItem + _profile.GroundItem.RenderItemResourcePathOffset,
                        out var resourcePath)
                    && resourcePath != 0
                    && _memory.TryReadUtf16(resourcePath, 128, out var fullPath)
                    && !string.IsNullOrWhiteSpace(fullPath))
                {
                    var slash = fullPath.LastIndexOf('/');
                    var dot = fullPath.LastIndexOf('.');
                    var start = slash >= 0 ? slash + 1 : 0;
                    var end = dot > start ? dot : fullPath.Length;
                    if (end > start) artName = fullPath[start..end];
                }
            }

            candidates.Add(new GroundItemResearchCandidate(
                entity.EntityId,
                metadata,
                grid,
                Vector2.Distance(playerGrid, grid),
                componentNames,
                itemEntityReady && itemEntityAddress != 0,
                itemName,
                artName,
                rarity,
                identified,
                world,
                entity.EntityAddress));
        }

        var labels = _labelReader.ReadLabels(root.InGameState, clientViewport);

        return new(
            _memory.ProcessId,
            _profile.ProfileId,
            area,
            playerGrid,
            candidates.OrderBy(candidate => candidate.DistanceToPlayer).ThenBy(candidate => candidate.EntityId).ToArray(),
            messages,
            cameraMatrix,
            labels);
    }

    /// <summary>Samples two profile-based hover pointers and matches them to current ground-item entities. Read-only only.</summary>
    public GroundItemHoverSnapshot CaptureHover()
    {
        if (!_session.TryRefresh(out var root, out var area, out var diagnostics))
            return new(_memory.ProcessId, null, null, null, false, null, false, [],
                diagnostics.FirstOrDefault()?.Message ?? "游戏状态不可用");

        if (!_profile.HoverOffsetsVerified)
            return new(_memory.ProcessId, area.SessionSequence, null, null, false, null, false, [],
                "悬停偏移未经当前客户端版本验证；只读采样已跳过");
        var hover = _profile.Hover;
        var uiRoot = ReadPointerOrZero(root.InGameState + _profile.InGameState.UiRootOffset);
        var host = ReadPointerOrZero(root.InGameState + hover.MouseOverHostFromInGameStateOffset);
        var sub = ReadPointerOrZero(host + hover.MouseOverSubFromHostOffset);
        var mouseOver = ReadPointerOrZero(sub + hover.MouseOverEntityFromSubOffset);
        var tracker = ReadPointerOrZero(uiRoot + hover.HoverTrackerFromUiRootOffset);
        var worldTracker = ReadPointerOrZero(tracker + hover.HoverTrackerWorldTrackerOffset);
        var tracked = ReadPointerOrZero(worldTracker + hover.HoverTrackerHoveredEntityOffset);
        var chain = new GroundItemHoverChain(uiRoot, host, sub, mouseOver, tracker, worldTracker, tracked);

        var matches = new List<uint>();
        if (mouseOver != 0 || tracked != 0)
        {
            if (_entityTree.TryRead(root.AreaInstance, out var entities, out _))
            {
                foreach (var entity in entities)
                {
                    if (entity.EntityAddress != mouseOver && entity.EntityAddress != tracked) continue;
                    if (ReadComponentNames(entity).Contains("WorldItem", StringComparer.Ordinal))
                        matches.Add(entity.EntityId);
                }
            }
        }

        var unique = matches.Distinct().Order().ToArray();
        return new(_memory.ProcessId, area.SessionSequence, mouseOver == 0 ? null : mouseOver,
            tracked == 0 ? null : tracked, mouseOver != 0 || tracked != 0,
            unique.Length == 1 ? unique[0] : null, unique.Length > 1, unique, Chain: chain);
    }

    private float[]? CaptureCameraMatrix(nint inGameState)
    {
        var cameraAddress = ReadPointerOrZero(inGameState + _profile.Camera.InGameStateCameraOffset);
        if (cameraAddress == 0) return null;
        var matrix = new float[16];
        if (!_memory.TryRead(cameraAddress + _profile.Camera.WorldToScreenMatrixOffset,
                MemoryMarshal.AsBytes(matrix.AsSpan())))
            return null;
        return matrix.All(float.IsFinite) ? matrix : null;
    }

    private nint ReadPointerOrZero(nint address)
        => address != 0 && _reader.TryReadPointer(address, out var value) ? value : 0;

    private IReadOnlyList<string> ReadComponentNames(RawEntityRef entity)
    {
        if (!_reader.TryReadPointer(entity.EntityAddress + _profile.Entity.DetailsOffset, out var details)
            || !_reader.TryReadPointer(details + _profile.EntityDetails.ComponentLookupOffset, out var lookup)
            || !_reader.TryReadStdVector(entity.EntityAddress + _profile.Entity.ComponentsOffset, IntPtr.Size, 256, out var components)
            || !_reader.TryReadStdVector(lookup + _profile.ComponentLookup.BucketOffset, _profile.ComponentLookup.EntryStride, 256, out var entries))
            return [];

        var names = new List<string>();
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries.First + (index * _profile.ComponentLookup.EntryStride);
            if (!_reader.TryReadPointer(entry, out var nameAddress)
                || !_reader.TryReadUInt32(entry + IntPtr.Size, out var componentIndex)
                || componentIndex >= components.Count
                || !_reader.TryReadUtf8(nameAddress, 32, out var name))
                continue;
            names.Add(name);
        }

        return names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<GroundItemLabelCandidate> CaptureLabels()
    {
        if (!_session.TryRefresh(out var root, out _, out _))
            return [];

        var uiRoot = ReadPointerOrZero(root.InGameState + _profile.InGameState.UiRootOffset);
        if (uiRoot == 0) return [];

        var captureResult = Capture();
        var candidates = captureResult.Candidates;
        var layout = _profile.UiElement;
        var directChildren = ReadChildren(uiRoot, layout);

        var matches = new List<GroundItemLabelCandidate>();

        // 特别深度分析 uiRoot[8] (ItemsOnGroundLabels)
        if (directChildren.Length > 8 && directChildren[8] != 0)
        {
            var labelsRoot = directChildren[8];
            Console.WriteLine($"\n=== [ItemsOnGroundLabels Root Analysis] Addr=0x{labelsRoot:X} ===");
            var subChildren = ReadChildren(labelsRoot, layout);
            Console.WriteLine($"labelsRoot has {subChildren.Length} children");
            for (var i = 0; i < subChildren.Length; i++)
            {
                var c = subChildren[i];
                var subC = ReadChildren(c, layout);
                Console.WriteLine($"  labelsRoot[{i}] (0x{c:X}) has {subC.Length} children");
                for (var j = 0; j < subC.Length; j++)
                {
                    var cc = subC[j];
                    var subCC = ReadChildren(cc, layout);
                    Console.WriteLine($"    labelsRoot[{i}][{j}] (0x{cc:X}) has {subCC.Length} children");
                }
            }

            var sub0 = ReadChildren(labelsRoot, layout);
            if (sub0.Length > 0 && sub0[0] != 0)
            {
                var sub00 = ReadChildren(sub0[0], layout);
                if (sub00.Length > 0 && sub00[0] != 0)
                {
                    var container = sub00[0];
                    var labelElements = ReadChildren(container, layout);
                    Console.WriteLine($"Found Container at uiRoot[8][0][0] (Addr=0x{container:X}) with {labelElements.Length} labels");

                    Span<byte> nodeBuf = stackalloc byte[0x800];
                    Span<byte> arrBuf = stackalloc byte[0x400];
                    Span<byte> buf = stackalloc byte[0x800];
                    Span<byte> structBuf = stackalloc byte[0x100];

                    // 深入分析 container (0x5636B362CD0) 的所有平行数组
                    Console.WriteLine($"\n=== Deep Inspect Container (0x{container:X}) Parallel Arrays ===");
                    for (var cOff = 0; cOff <= 0x100; cOff += 8)
                    {
                        if (_reader.TryReadPointer(container + cOff, out var arrPtr) && (ulong)arrPtr is >= 0x10000 and <= 0x7FFF_FFFF_FFFF)
                        {
                            var testBuf = new byte[13 * 8];
                            if (_memory.TryRead(arrPtr, testBuf))
                            {
                                var pointers = new nint[13];
                                for (var i = 0; i < 13; i++) pointers[i] = (nint)MemoryMarshal.Read<ulong>(testBuf.AsSpan(i * 8));
                                // 检查 pointers 是否有规律，或者是否包含 entity
                                var entMatches = pointers.Count(p => candidates.Any(c => c.EntityAddress == p));
                                var lblMatches = pointers.Count(p => labelElements.Contains(p));
                                if (entMatches > 0 || lblMatches > 0 || pointers.All(p => (ulong)p is >= 0x10000 and <= 0x7FFF_FFFF_FFFF))
                                {
                                    Console.WriteLine($"Container+0x{cOff:X} -> 0x{arrPtr:X}: lblMatches={lblMatches}, entMatches={entMatches}");
                                    for (var i = 0; i < Math.Min(4, pointers.Length); i++)
                                    {
                                        var p = pointers[i];
                                        var cMatch = candidates.FirstOrDefault(c => c.EntityAddress == p);
                                        Console.WriteLine($"   [{i}] 0x{p:X} {(cMatch != null ? $"(Entity: {cMatch.ItemName})" : "")}");
                                    }
                                }
                            }
                        }
                    }

                    Console.WriteLine("\n=== Testing Entity World Projection vs UI Label Center Matching ===");
                    var cameraMatrix = CaptureCameraMatrix(root.InGameState);
                    var viewport = new Vector2(layout.BaseResolutionWidth, layout.BaseResolutionHeight);
                    if (cameraMatrix is not null)
                    {
                        foreach (var cand in candidates)
                        {
                            if (cand.WorldPosition.HasValue
                                && FreiAtlas.Core.Pickup.PickupScreenProjection.TryProject(
                                    cameraMatrix, cand.WorldPosition.Value, viewport, out var projScreen))
                            {
                                Console.WriteLine($"Entity(id={cand.EntityId}, '{cand.ItemName}'): World={cand.WorldPosition.Value} -> ProjScreen=({projScreen.X:F1}, {projScreen.Y:F1})");

                                // 找到屏幕标签中，名字匹配（包含）的标签
                                foreach (var label in matches)
                                {
                                    if (label.Text.Contains(cand.ItemName, StringComparison.OrdinalIgnoreCase)
                                        || cand.ItemName.Contains(label.Text, StringComparison.OrdinalIgnoreCase)
                                        || (cand.ItemName == "崇高石" && label.Text.Contains("崇高石")))
                                    {
                                        var labelCenter = new Vector2(label.RelativePosition.X + label.Size.X * 0.5f, label.RelativePosition.Y + label.Size.Y * 0.5f);
                                        var dist = Vector2.Distance(projScreen, labelCenter);
                                        Console.WriteLine($"    -> Matched Candidate Label: '{label.Text}' Center=({labelCenter.X:F1}, {labelCenter.Y:F1}), DistToProj={dist:F1}px");
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine("Camera matrix is null!");
                    }

                    for (var idx = 0; idx < labelElements.Length; idx++)
                    {
                        var labelElem = labelElements[idx];
                        if (labelElem == 0 || !_memory.TryRead(labelElem, buf)) continue;

                        var rel = MemoryMarshal.Read<Vector2>(buf[layout.RelativePositionOffset..]);
                        var width = MemoryMarshal.Read<float>(buf[layout.WidthOffset..]);
                        var height = MemoryMarshal.Read<float>(buf[layout.HeightOffset..]);
                        var center = new Vector2(rel.X + width * 0.5f, rel.Y + height * 0.5f);

                        var foundTexts = new List<string>();
                        CollectUiTexts(labelElem, foundTexts, maxDepth: 2);
                        var cleanText = foundTexts.FirstOrDefault(t => t.Any(c => c >= 0x4E00 && c <= 0x9FFF) && !t.Contains("Noto Sans")) ?? "unknown";

                        matches.Add(new GroundItemLabelCandidate(
                            labelElem,
                            cleanText,
                            $"Center=({center.X:F1}, {center.Y:F1})",
                            $"uiRoot[8][0][0][{idx}]",
                            [8, 0, 0, idx],
                            new Vector2(width, height),
                            rel,
                            1.0f,
                            0));
                    }
                }
            }
        }

        return matches;
    }

    private void CollectUiTexts(nint element, List<string> results, int maxDepth, int currentDepth = 0)
    {
        if (element == 0 || currentDepth > maxDepth || results.Count > 10) return;
        var layout = _profile.UiElement;

        Span<byte> buf = stackalloc byte[0x500];
        if (_memory.TryRead(element, buf))
        {
            for (var off = 0; off <= buf.Length - 8; off += 8)
            {
                var ptr = (nint)MemoryMarshal.Read<ulong>(buf[off..]);
                if ((ulong)ptr is >= 0x10000 and <= 0x7FFF_FFFF_FFFF)
                {
                    if (_memory.TryReadUtf16(ptr, 64, out var text)
                        && !string.IsNullOrWhiteSpace(text)
                        && !text.StartsWith("Metadata/", StringComparison.Ordinal)
                        && !results.Contains(text))
                    {
                        results.Add(text);
                    }
                }
            }
        }

        var children = ReadChildren(element, layout);
        foreach (var child in children)
        {
            if (child != 0) CollectUiTexts(child, results, maxDepth, currentDepth + 1);
        }
    }

    private void ScanUiElement(
        nint node,
        string path,
        int[] indexChain,
        List<string> targetNames,
        List<GroundItemLabelCandidate> matches,
        int depth,
        int maxDepth,
        HashSet<nint> visited)
    {
        if (node == 0 || depth > maxDepth || !visited.Add(node) || visited.Count > 30000) return;
        var layout = _profile.UiElement;

        Span<byte> buffer = stackalloc byte[0x500];
        if (_memory.TryRead(node, buffer))
        {
            for (var offset = 0; offset <= buffer.Length - 8; offset += 8)
            {
                var ptr = (nint)MemoryMarshal.Read<ulong>(buffer[offset..]);
                if ((ulong)ptr is < 0x10000 or > 0x7FFF_FFFF_FFFF) continue;

                if (_memory.TryReadUtf16(ptr, 64, out var text) && !string.IsNullOrWhiteSpace(text))
                {
                    foreach (var target in targetNames)
                    {
                        if (text.Contains(target, StringComparison.OrdinalIgnoreCase))
                        {
                            var width = layout.WidthOffset + 4 <= buffer.Length ? MemoryMarshal.Read<float>(buffer[layout.WidthOffset..]) : 0f;
                            var height = layout.HeightOffset + 4 <= buffer.Length ? MemoryMarshal.Read<float>(buffer[layout.HeightOffset..]) : 0f;
                            var rel = layout.RelativePositionOffset + 8 <= buffer.Length ? MemoryMarshal.Read<Vector2>(buffer[layout.RelativePositionOffset..]) : Vector2.Zero;
                            var scale = layout.ScaleOffset + 4 <= buffer.Length ? MemoryMarshal.Read<float>(buffer[layout.ScaleOffset..]) : 1f;

                            matches.Add(new GroundItemLabelCandidate(
                                node,
                                text,
                                target,
                                path,
                                indexChain,
                                new Vector2(width, height),
                                rel,
                                scale,
                                offset));
                            break;
                        }
                    }
                }
            }
        }

        var children = ReadChildren(node, layout);
        for (var c = 0; c < children.Length; c++)
        {
            if (children[c] == 0) continue;
            ScanUiElement(children[c], $"{path}[{c}]", [.. indexChain, c], targetNames, matches, depth + 1, maxDepth, visited);
        }
    }

    private nint[] ReadChildren(nint element, UiElementLayout layout)
    {
        if (!_reader.TryReadPointer(element + layout.ChildrenOffset, out var first)
            || !_reader.TryReadPointer(element + layout.ChildrenEndOffset, out var last))
            return [];

        if (first == 0 || last == 0 || last <= first) return [];
        var count = (int)((last - first) / IntPtr.Size);
        if (count is < 0 or > 2048) return [];

        var buffer = new byte[count * IntPtr.Size];
        if (!_memory.TryRead(first, buffer)) return [];
        var children = new nint[count];
        for (var i = 0; i < count; i++)
        {
            children[i] = (nint)MemoryMarshal.Read<ulong>(buffer.AsSpan(i * 8));
        }
        return children;
    }
}

public sealed record GroundItemResearchResult(
    int ProcessId,
    string ProfileId,
    AreaIdentity? Area,
    Vector2? PlayerGridPosition,
    IReadOnlyList<GroundItemResearchCandidate> Candidates,
    IReadOnlyList<AreaReadDiagnostic> Diagnostics,
    float[]? CameraWorldToScreenMatrix = null,
    IReadOnlyList<GroundItemLabel>? Labels = null);

public sealed record GroundItemResearchCandidate(
    uint EntityId,
    string MetadataPath,
    Vector2 GridPosition,
    float DistanceToPlayer,
    IReadOnlyList<string> Components,
    bool ItemEntityReady,
    string ItemName,
    string ArtName,
    int? Rarity,
    bool? Identified,
    Vector3? WorldPosition = null,
    nint EntityAddress = 0);

public sealed record GroundItemHoverSnapshot(
    int ProcessId,
    long? SessionSequence,
    nint? MouseOverEntityAddress,
    nint? TrackedEntityAddress,
    bool HasHoverData,
    uint? MatchedGroundItemEntityId,
    bool AmbiguousMatch,
    IReadOnlyList<uint> MatchedGroundItemEntityIds,
    string? Error = null,
    GroundItemHoverChain? Chain = null);

public sealed record GroundItemHoverChain(
    nint UiRoot,
    nint MouseOverHost,
    nint MouseOverSub,
    nint MouseOverEntity,
    nint HoverTracker,
    nint WorldTracker,
    nint TrackedEntity);

public sealed record GroundItemLabelCandidate(
    nint Address,
    string Text,
    string TargetItemName,
    string Path,
    int[] IndexChain,
    Vector2 Size,
    Vector2 RelativePosition,
    float Scale,
    int TextPointerOffset);

