using FreiAtlas.Core.Memory;
using FreiAtlas.Game.Entities;
using FreiAtlas.Game.Tests.Memory;

namespace FreiAtlas.Game.Tests.Entities;

public sealed class AreaEntityTreeReaderTests
{
    private static readonly nint Area = 0x200000;
    private static readonly nint Head = 0x210000;
    private static readonly nint Root = 0x220000;
    private static readonly nint Right = 0x230000;

    [Fact]
    public void TryRead_ReadsEachMapNodeOnceAndReturnsRealEntities()
    {
        var memory = CreateTree(size: 2);
        WriteNode(memory, Root, Head, Right, 11, 0x310000);
        WriteNode(memory, Right, Head, Head, 12, 0x320000);
        var reader = new AreaEntityTreeReader(memory, Poe2MemoryProfile.Current);

        var before = memory.ReadOperationCount;
        var success = reader.TryRead(Area, out var entities, out var diagnostic);

        Assert.True(success);
        Assert.Null(diagnostic);
        Assert.Equal(2, memory.ReadOperationCount - before - 3);
        Assert.Equal(
            [new RawEntityRef(11, 0x310000), new RawEntityRef(12, 0x320000)],
            entities);
    }

    [Fact]
    public void TryRead_AcceptsEmptyTreeWithValidHead()
    {
        var memory = CreateTree(size: 0);
        var reader = new AreaEntityTreeReader(memory);

        Assert.True(reader.TryRead(Area, out var entities, out _));
        Assert.Empty(entities);
    }

    [Fact]
    public void TryRead_SkipsVisualIds()
    {
        var memory = CreateTree(size: 1);
        WriteNode(memory, Root, Head, Head, 0x40000000, 0x310000);
        var reader = new AreaEntityTreeReader(memory);

        Assert.True(reader.TryRead(Area, out var entities, out _));
        Assert.Empty(entities);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100001)]
    public void TryRead_RejectsInvalidHeadOrSize(int size)
    {
        var memory = CreateTree(size, head: size == 0 ? 0 : Head);
        var reader = new AreaEntityTreeReader(memory);

        Assert.False(reader.TryRead(Area, out var entities, out var diagnostic));
        Assert.Empty(entities);
        Assert.NotNull(diagnostic);
    }

    [Fact]
    public void TryRead_RejectsPointerCycles()
    {
        var memory = CreateTree(size: 1);
        WriteNode(memory, Root, Root, Head, 11, 0x310000);
        var reader = new AreaEntityTreeReader(memory);

        Assert.False(reader.TryRead(Area, out _, out _));
    }

    [Fact]
    public void TryRead_RejectsNilRootWhenSizeIsNonZero()
    {
        var memory = CreateTree(size: 1);
        WriteNode(memory, Root, Head, Head, 11, 0x310000, isNil: true);
        var reader = new AreaEntityTreeReader(memory);

        Assert.False(reader.TryRead(Area, out _, out _));
    }

    [Fact]
    public void TryRead_RejectsAnUnreadableNodeWithoutPartialResults()
    {
        var memory = CreateTree(size: 1);
        WriteNode(memory, Root, Head, Head, 11, 0x310000);
        memory.FailRange(Root, 0x30);
        var reader = new AreaEntityTreeReader(memory);

        Assert.False(reader.TryRead(Area, out var entities, out _));
        Assert.Empty(entities);
    }

    private static SyntheticProcessMemory CreateTree(int size, nint? head = null)
    {
        var memory = new SyntheticProcessMemory();
        var actualHead = head ?? Head;
        memory.WritePointer(Area + 0x6F0, actualHead);
        memory.WriteInt32(Area + 0x6F8, size);
        if (actualHead != 0)
        {
            memory.WriteBytes(actualHead, new byte[0x30]);
            memory.WritePointer(actualHead + 0x08, Root);
        }

        return memory;
    }

    private static void WriteNode(
        SyntheticProcessMemory memory,
        nint node,
        nint left,
        nint right,
        uint id,
        nint entity,
        bool isNil = false)
    {
        memory.WriteBytes(node, new byte[0x30]);
        memory.WritePointer(node, left);
        memory.WritePointer(node + 0x10, right);
        memory.WriteByte(node + 0x19, isNil ? (byte)1 : (byte)0);
        memory.WriteUInt32(node + 0x20, id);
        memory.WritePointer(node + 0x28, entity);
    }
}
