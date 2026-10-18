using System.Numerics;
using System.Runtime.InteropServices;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Memory;
using FreiAtlas.Core.Pickup;
using FreiAtlas.Game.Views;

namespace FreiAtlas.Game.Memory;

/// <summary>Reads on-screen ground item label elements from IngameUI.</summary>
public sealed class GroundItemLabelReader
{
    private readonly IProcessMemory _memory;
    private readonly Poe2MemoryProfile _profile;
    private readonly GameMemoryReader _reader;
    private readonly UiElementClientGeometryReader _geometryReader;

    public GroundItemLabelReader(IProcessMemory memory, Poe2MemoryProfile? profile = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _profile = profile ?? Poe2MemoryProfile.Current;
        _reader = new GameMemoryReader(memory, _profile);
        _geometryReader = new UiElementClientGeometryReader(memory, _profile);
    }

    public IReadOnlyList<GroundItemLabel> ReadLabels(nint inGameState, Vector2? clientViewport = null)
    {
        if (inGameState == 0) return [];
        if (!_reader.TryReadPointer(inGameState + _profile.InGameState.UiRootOffset, out var uiRoot) || uiRoot == 0)
            return [];

        var layout = _profile.UiElement;
        var directChildren = ReadChildren(uiRoot, layout);
        if (directChildren.Length <= 8 || directChildren[8] == 0)
            return [];

        // uiRoot[8] -> ItemsOnGroundLabels
        var labelsRoot = directChildren[8];
        var sub0 = ReadChildren(labelsRoot, layout);
        if (sub0.Length == 0 || sub0[0] == 0)
            return [];

        // uiRoot[8][0]
        var sub00 = ReadChildren(sub0[0], layout);
        if (sub00.Length == 0 || sub00[0] == 0)
            return [];

        // uiRoot[8][0][0] -> Label Container
        var container = sub00[0];
        var labelElements = ReadChildren(container, layout);
        if (labelElements.Length == 0)
            return [];

        var results = new List<GroundItemLabel>(labelElements.Length);
        Span<byte> buf = stackalloc byte[0x600];
        var effectiveViewport = new AreaUiRect(0, 0,
            clientViewport is { X: > 0 } ? clientViewport.Value.X : _profile.UiElement.BaseResolutionWidth,
            clientViewport is { Y: > 0 } ? clientViewport.Value.Y : _profile.UiElement.BaseResolutionHeight);

        foreach (var labelElem in labelElements)
        {
            if (labelElem == 0 || !_memory.TryRead(labelElem, buf)) continue;

            var width = layout.WidthOffset + 4 <= buf.Length
                ? MemoryMarshal.Read<float>(buf[layout.WidthOffset..])
                : 0f;
            var height = layout.HeightOffset + 4 <= buf.Length
                ? MemoryMarshal.Read<float>(buf[layout.HeightOffset..])
                : 0f;
            var rel = layout.RelativePositionOffset + 8 <= buf.Length
                ? MemoryMarshal.Read<Vector2>(buf[layout.RelativePositionOffset..])
                : Vector2.Zero;

            // 地面物品标签的标准高度通常在 25~100 之间，放宽阈值避免带有堆叠数量或高光边框的标签被丢弃
            if (width <= 0f || height < 15f || height > 250f) continue;

            var foundTexts = new List<string>();
            var directText = ExtractLabelText(labelElem);
            if (!string.IsNullOrWhiteSpace(directText))
            {
                foundTexts.Add(directText);
            }

            CollectUiTexts(labelElem, foundTexts, maxDepth: 3);

            string rawText;
            if (!string.IsNullOrWhiteSpace(directText))
            {
                rawText = directText;
            }
            else
            {
                // 过滤系统字体、渲染通道指令和无意义内部字符
                var cleanCandidates = foundTexts
                    .Where(t => t.Length >= 2
                                && !t.Contains("Noto Sans", StringComparison.OrdinalIgnoreCase)
                                && !t.Contains("SUB-PASS", StringComparison.OrdinalIgnoreCase)
                                && t.Any(c => c >= 0x4E00 && c <= 0x9FFF))
                    .ToList();

                rawText = cleanCandidates.FirstOrDefault()
                          ?? foundTexts.FirstOrDefault(t => !t.Contains("Noto Sans", StringComparison.OrdinalIgnoreCase) && !t.Contains("SUB-PASS", StringComparison.OrdinalIgnoreCase))
                          ?? string.Empty;
            }

            var cleanText = PickupTargetSelector.Normalize(rawText);
            Vector2 center;
            Vector2 actualSize;
            Vector2 actualPos;

            if (_geometryReader.TryReadRect(labelElem, 0, Vector2.Zero, effectiveViewport, out var bounds, out _)
                && bounds.Width > 0f && bounds.Height > 0f)
            {
                actualPos = new Vector2(bounds.X, bounds.Y);
                actualSize = new Vector2(bounds.Width, bounds.Height);
                center = new Vector2(bounds.X + (bounds.Width * 0.5f), bounds.Y + (bounds.Height * 0.5f));
            }
            else
            {
                actualPos = rel;
                actualSize = new Vector2(width, height);
                center = new Vector2(rel.X + (width * 0.5f), rel.Y + (height * 0.5f));
            }

            results.Add(new GroundItemLabel(
                labelElem,
                rawText,
                cleanText,
                actualSize,
                actualPos,
                center,
                foundTexts));
        }

        return results;
    }

    private string ExtractLabelText(nint element)
    {
        // 1. 首选: 复合标签主文本 std::wstring (位于 element + 0x538)
        if (TryReadStdWString(element + 0x538, out var text) && !string.IsNullOrWhiteSpace(text))
        {
            return CleanLabelText(text);
        }

        // 2. 备选: 普通标签 std::wstring (位于 element + 0x520)
        if (TryReadStdWString(element + 0x520, out text) && !string.IsNullOrWhiteSpace(text))
        {
            return CleanLabelText(text);
        }

        return string.Empty;
    }

    private bool TryReadStdWString(nint wstringAddress, out string result)
    {
        result = string.Empty;
        Span<byte> buf = stackalloc byte[32];
        if (!_memory.TryRead(wstringAddress, buf)) return false;

        var mySize = MemoryMarshal.Read<ulong>(buf[16..]);
        var myRes = MemoryMarshal.Read<ulong>(buf[24..]);

        if (mySize is 0 or > 256 || myRes is 0 or > 2048 || mySize > myRes)
            return false;

        if (myRes < 8)
        {
            result = System.Text.Encoding.Unicode.GetString(buf[..(int)(mySize * 2)]);
            return true;
        }
        else
        {
            var ptr = (nint)MemoryMarshal.Read<ulong>(buf[..8]);
            if ((ulong)ptr is < 0x10000 or > 0x7FFF_FFFF_FFFF) return false;
            var strBytes = new byte[(int)(mySize * 2)];
            if (!_memory.TryRead(ptr, strBytes)) return false;
            result = System.Text.Encoding.Unicode.GetString(strBytes);
            return true;
        }
    }

    private static string CleanLabelText(string raw)
    {
        return new string(raw.SkipWhile(c => c >= 0xFF00 || char.IsControl(c) || c == ' ' || c == '\t').ToArray()).Trim();
    }

    private void CollectUiTexts(nint element, List<string> results, int maxDepth, int currentDepth = 0)
    {
        if (element == 0 || currentDepth > maxDepth || results.Count > 60) return;
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
