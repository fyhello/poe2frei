using System.Runtime.InteropServices;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Memory;
using FreiAtlas.Game.Memory;

namespace FreiAtlas.Game.Entities;

internal readonly record struct RawEntityRef(uint EntityId, nint EntityAddress);

internal sealed class AreaEntityTreeReader
{
    private const int MaximumEntityCount = 100_000;
    private const int MaximumVisitedNodes = 200_000;

    private readonly IProcessMemory _memory;
    private readonly Poe2MemoryProfile _profile;
    private readonly GameMemoryReader _reader;

    public AreaEntityTreeReader(
        IProcessMemory memory,
        Poe2MemoryProfile? profile = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _profile = profile ?? Poe2MemoryProfile.Current;
        _reader = new GameMemoryReader(memory, _profile);
    }

    public bool TryRead(
        nint areaInstance,
        out IReadOnlyList<RawEntityRef> entities,
        out AreaReadDiagnostic? diagnostic)
    {
        entities = [];
        diagnostic = null;
        var map = areaInstance + _profile.AreaInstance.AwakeEntitiesOffset;
        if (!_reader.TryReadPointer(map, out var head)
            || !_memory.TryReadInt32(map + IntPtr.Size, out var size)
            || size < 0
            || size > MaximumEntityCount)
        {
            diagnostic = InvalidTree("The AwakeEntities map header is invalid.");
            return false;
        }

        if (size == 0)
        {
            return true;
        }

        if (!_reader.TryReadPointer(
                head + _profile.StdMapNode.ParentOffset,
                out var root)
            || root == head)
        {
            diagnostic = InvalidTree("The AwakeEntities map root is invalid.");
            return false;
        }

        var queue = new Queue<nint>();
        var visited = new HashSet<nint>();
        var result = new List<RawEntityRef>(Math.Min(size, 1024));
        Span<byte> node = stackalloc byte[0x30];
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var nodeAddress = queue.Dequeue();
            if (nodeAddress == head)
            {
                continue;
            }

            if (nodeAddress == 0
                || !visited.Add(nodeAddress)
                || visited.Count > MaximumVisitedNodes)
            {
                diagnostic = InvalidTree("The AwakeEntities map contains a pointer cycle or invalid node.");
                return false;
            }

            if (!_memory.TryRead(nodeAddress, node)
                || node[_profile.StdMapNode.IsNilOffset] != 0)
            {
                diagnostic = InvalidTree("An AwakeEntities map node could not be read.");
                return false;
            }

            var left = ReadPointer(node, _profile.StdMapNode.LeftOffset);
            var right = ReadPointer(node, _profile.StdMapNode.RightOffset);
            queue.Enqueue(left);
            queue.Enqueue(right);

            var entityId = MemoryMarshal.Read<uint>(
                node[_profile.StdMapNode.KeyIdOffset..]);
            var entityAddress = ReadPointer(
                node,
                _profile.StdMapNode.EntityOffset);
            if (entityAddress != 0
                && entityId < _profile.StdMapNode.VisualIdThreshold)
            {
                result.Add(new RawEntityRef(entityId, entityAddress));
            }
        }

        if (visited.Count != size)
        {
            diagnostic = InvalidTree("The AwakeEntities map size does not match its node graph.");
            return false;
        }

        entities = result;
        return true;
    }

    private static nint ReadPointer(ReadOnlySpan<byte> buffer, int offset)
        => (nint)MemoryMarshal.Read<long>(buffer[offset..]);

    private static AreaReadDiagnostic InvalidTree(string message)
        => new(
            "entity-tree-invalid",
            message,
            AreaDiagnosticSeverity.Error);
}
