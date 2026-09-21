using System.Numerics;
using FreiAtlas.Game.Memory;

namespace FreiAtlas.Game.Tests.Memory;

public sealed class GameMemoryReaderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(0xFFFF)]
    [InlineData(0x0000_8000_0000_0000)]
    public void TryReadPointer_RejectsNonCanonicalPointers(long pointer)
    {
        using var memory = new SyntheticProcessMemory();
        memory.WriteInt64(0x100000, pointer);
        var reader = new GameMemoryReader(memory);

        Assert.False(reader.TryReadPointer(0x100000, out _));
    }

    [Fact]
    public void TryReadPointer_ReturnsCanonicalUserPointer()
    {
        using var memory = new SyntheticProcessMemory();
        memory.WritePointer(0x100000, 0x220000);
        var reader = new GameMemoryReader(memory);

        Assert.True(reader.TryReadPointer(0x100000, out var pointer));
        Assert.Equal((nint)0x220000, pointer);
    }

    [Fact]
    public void TryReadVectors_ReturnsFiniteValues()
    {
        using var memory = new SyntheticProcessMemory();
        memory.WriteVector2(0x100000, new Vector2(12.5f, -8f));
        memory.WriteVector3(0x100100, new Vector3(1f, 2f, 3f));
        var reader = new GameMemoryReader(memory);

        Assert.True(reader.TryReadVector2(0x100000, out var vector2));
        Assert.Equal(new Vector2(12.5f, -8f), vector2);
        Assert.True(reader.TryReadVector3(0x100100, out var vector3));
        Assert.Equal(new Vector3(1f, 2f, 3f), vector3);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void TryReadVector2_RejectsNonFiniteCoordinates(float value)
    {
        using var memory = new SyntheticProcessMemory();
        memory.WriteVector2(0x100000, new Vector2(value, 1f));
        var reader = new GameMemoryReader(memory);

        Assert.False(reader.TryReadVector2(0x100000, out _));
    }

    [Fact]
    public void TryReadStdVector_ReturnsValidatedRange()
    {
        using var memory = new SyntheticProcessMemory();
        memory.WritePointer(0x100000, 0x200000);
        memory.WritePointer(0x100008, 0x200018);
        var reader = new GameMemoryReader(memory);

        Assert.True(reader.TryReadStdVector(0x100000, 8, 8, out var range));
        Assert.Equal((nint)0x200000, range.First);
        Assert.Equal((nint)0x200018, range.Last);
        Assert.Equal(3, range.Count);
    }

    [Fact]
    public void TryReadStdVector_RejectsOversizedRange()
    {
        using var memory = new SyntheticProcessMemory();
        memory.WritePointer(0x100000, 0x200000);
        memory.WritePointer(0x100008, 0x400000);
        var reader = new GameMemoryReader(memory);

        Assert.False(reader.TryReadStdVector(0x100000, 8, 256, out _));
    }

    [Theory]
    [InlineData(0x200010, 0x200000)]
    [InlineData(0x200000, 0x20000A)]
    public void TryReadStdVector_RejectsInvalidRange(long first, long last)
    {
        using var memory = new SyntheticProcessMemory();
        memory.WritePointer(0x100000, (nint)first);
        memory.WritePointer(0x100008, (nint)last);
        var reader = new GameMemoryReader(memory);

        Assert.False(reader.TryReadStdVector(0x100000, 8, 256, out _));
    }

    [Theory]
    [InlineData("Atlas")]
    [InlineData("Long Atlas Name")]
    public void TryReadStdWString_ReadsInlineAndHeapStorage(string expected)
    {
        using var memory = new SyntheticProcessMemory();
        memory.WriteStdWString(0x100000, expected);
        var reader = new GameMemoryReader(memory);

        Assert.True(reader.TryReadStdWString(0x100000, 64, out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TryReadStdWString_RejectsLengthBeyondLimit()
    {
        using var memory = new SyntheticProcessMemory();
        memory.WriteInt32(0x100010, 65);
        var reader = new GameMemoryReader(memory);

        Assert.False(reader.TryReadStdWString(0x100000, 64, out _));
    }

    [Fact]
    public void TryReadUtf8_StopsAtNullTerminator()
    {
        using var memory = new SyntheticProcessMemory();
        memory.WriteUtf8(0x100000, "Render");
        var reader = new GameMemoryReader(memory);
        var readsBefore = memory.ReadOperationCount;

        Assert.True(reader.TryReadUtf8(0x100000, 32, out var value));
        Assert.Equal("Render", value);
        Assert.Equal(1, memory.ReadOperationCount - readsBefore);
    }

    [Fact]
    public void FailedRange_ReturnsFalseWithoutThrowing()
    {
        using var memory = new SyntheticProcessMemory();
        memory.WriteVector3(0x100000, new Vector3(1f, 2f, 3f));
        memory.FailRange(0x100004, sizeof(float));
        var reader = new GameMemoryReader(memory);

        Assert.False(reader.TryReadVector3(0x100000, out _));
    }
}
