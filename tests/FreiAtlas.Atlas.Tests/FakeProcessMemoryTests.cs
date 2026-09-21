using System.Text;
using FreiAtlas.Core.Memory;

namespace FreiAtlas.Atlas.Tests;

public sealed class FakeProcessMemoryTests
{
    [Fact]
    public void TryReadInt32_ReturnsStoredValue()
    {
        var memory = new FakeProcessMemory();
        memory.SetInt32((nint)0x1000, 42);

        Assert.True(memory.TryReadInt32((nint)0x1000, out var value));
        Assert.Equal(42, value);
    }

    [Fact]
    public void TryReadUtf16_ReturnsStoredString()
    {
        var memory = new FakeProcessMemory();
        memory.SetUtf16((nint)0x2000, "Atlas");

        Assert.True(memory.TryReadUtf16((nint)0x2000, 32, out var value));
        Assert.Equal("Atlas", value);
    }

    [Fact]
    public void ReadUnknownAddress_ReturnsFalse()
    {
        var memory = new FakeProcessMemory();

        Assert.False(memory.TryReadInt32((nint)0x3000, out _));
    }
}

internal sealed class FakeProcessMemory : IProcessMemory
{
    private readonly Dictionary<nint, byte[]> _values = [];

    public int ProcessId => 7928;

    public void SetInt32(nint address, int value)
        => _values[address] = BitConverter.GetBytes(value);

    public void SetUtf16(nint address, string value)
        => _values[address] = Encoding.Unicode.GetBytes(value + "\0");

    public bool TryRead(nint address, Span<byte> destination)
    {
        if (!_values.TryGetValue(address, out var source)
            || source.Length < destination.Length)
        {
            return false;
        }

        source.AsSpan(0, destination.Length).CopyTo(destination);
        return true;
    }

    public bool TryReadInt32(nint address, out int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        if (!TryRead(address, buffer))
        {
            value = default;
            return false;
        }

        value = BitConverter.ToInt32(buffer);
        return true;
    }

    public bool TryReadInt64(nint address, out long value)
    {
        value = default;
        return false;
    }

    public bool TryReadFloat(nint address, out float value)
    {
        value = default;
        return false;
    }

    public bool TryReadPointer(nint address, out nint value)
    {
        value = default;
        return false;
    }

    public bool TryReadUtf16(nint address, int maxChars, out string? value)
    {
        if (!_values.TryGetValue(address, out var bytes))
        {
            value = null;
            return false;
        }

        var text = Encoding.Unicode.GetString(bytes);
        value = text.TrimEnd('\0');
        return value.Length <= maxChars;
    }

    public void Dispose()
    {
    }
}
