using System.Numerics;
using System.Text;
using FreiAtlas.Core.Memory;

namespace FreiAtlas.Game.Tests.Memory;

internal sealed class SyntheticProcessMemory : IProcessMemory, IProcessMemoryLayout
{
    private readonly Dictionary<long, byte> _bytes = [];
    private readonly List<(long Start, long End)> _failedRanges = [];
    private long _nextStringAddress = 0x700000;

    public SyntheticProcessMemory(
        int processId = 7928,
        nint mainModuleBase = default,
        long mainModuleSize = 0x10000)
    {
        ProcessId = processId;
        MainModuleBase = mainModuleBase == 0 ? (nint)0x100000 : mainModuleBase;
        MainModuleSize = mainModuleSize;
    }

    public int ProcessId { get; }
    public nint MainModuleBase { get; }
    public long MainModuleSize { get; }
    public int ReadOperationCount { get; private set; }

    public IEnumerable<ProcessMemoryRegion> EnumerateMainModuleRegions()
    {
        yield return new ProcessMemoryRegion(
            MainModuleBase,
            MainModuleSize,
            IsReadable: true,
            IsExecutable: true,
            IsImage: true);
    }

    public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
    {
        for (var index = 0; index < bytes.Length; index++)
        {
            _bytes[(long)address + index] = bytes[index];
        }
    }

    public void WriteByte(nint address, byte value)
        => WriteBytes(address, [value]);

    public void WriteInt32(nint address, int value)
        => WriteBytes(address, BitConverter.GetBytes(value));

    public void WriteUInt32(nint address, uint value)
        => WriteBytes(address, BitConverter.GetBytes(value));

    public void WriteInt64(nint address, long value)
        => WriteBytes(address, BitConverter.GetBytes(value));

    public void WriteFloat(nint address, float value)
        => WriteBytes(address, BitConverter.GetBytes(value));

    public void WritePointer(nint address, nint value)
        => WriteInt64(address, value.ToInt64());

    public void WriteVector2(nint address, Vector2 value)
    {
        WriteFloat(address, value.X);
        WriteFloat(address + sizeof(float), value.Y);
    }

    public void WriteVector3(nint address, Vector3 value)
    {
        WriteVector2(address, new Vector2(value.X, value.Y));
        WriteFloat(address + (2 * sizeof(float)), value.Z);
    }

    public void WriteUtf8(nint address, string value, int capacity = 256)
    {
        var encoded = Encoding.UTF8.GetBytes(value);
        if (capacity <= encoded.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        var bytes = new byte[capacity];
        encoded.CopyTo(bytes, 0);
        WriteBytes(address, bytes);
    }

    public void WriteUtf16(nint address, string value)
        => WriteBytes(address, Encoding.Unicode.GetBytes(value + "\0"));

    public void WriteUtf16Buffer(nint address, string value, int capacityChars)
    {
        if (capacityChars <= value.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityChars));
        }

        var bytes = new byte[checked(capacityChars * sizeof(char))];
        Encoding.Unicode.GetBytes(value).CopyTo(bytes, 0);
        WriteBytes(address, bytes);
    }

    public void WriteStdWString(nint address, string value)
    {
        WriteInt32(address + 0x10, value.Length);
        if (value.Length < 8)
        {
            WriteUtf16(address, value);
            return;
        }

        var characters = (nint)_nextStringAddress;
        _nextStringAddress += checked((value.Length + 1L) * sizeof(char) + 0x20);
        WritePointer(address, characters);
        WriteUtf16(characters, value);
    }

    public void FailRange(nint address, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        _failedRanges.Add(((long)address, checked((long)address + length)));
    }

    public bool TryRead(nint address, Span<byte> destination)
    {
        ReadOperationCount++;
        if (address == 0 || destination.Length == 0)
        {
            return false;
        }

        var start = (long)address;
        var end = checked(start + destination.Length);
        if (_failedRanges.Any(range => start < range.End && end > range.Start))
        {
            return false;
        }

        for (var index = 0; index < destination.Length; index++)
        {
            if (!_bytes.TryGetValue(start + index, out destination[index])
                && !IsInsideMainModule(start + index))
            {
                destination.Clear();
                return false;
            }
        }

        return true;
    }

    public bool TryReadInt32(nint address, out int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        if (!TryRead(address, bytes))
        {
            value = default;
            return false;
        }

        value = BitConverter.ToInt32(bytes);
        return true;
    }

    public bool TryReadInt64(nint address, out long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        if (!TryRead(address, bytes))
        {
            value = default;
            return false;
        }

        value = BitConverter.ToInt64(bytes);
        return true;
    }

    public bool TryReadFloat(nint address, out float value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(float)];
        if (!TryRead(address, bytes))
        {
            value = default;
            return false;
        }

        value = BitConverter.ToSingle(bytes);
        return true;
    }

    public bool TryReadPointer(nint address, out nint value)
    {
        if (TryReadInt64(address, out var raw))
        {
            value = (nint)raw;
            return true;
        }

        value = 0;
        return false;
    }

    public bool TryReadUtf16(nint address, int maxChars, out string? value)
    {
        if (maxChars <= 0)
        {
            value = null;
            return false;
        }

        var bytes = new byte[checked(maxChars * sizeof(char))];
        if (!TryRead(address, bytes))
        {
            value = null;
            return false;
        }

        var decoded = Encoding.Unicode.GetString(bytes);
        var terminator = decoded.IndexOf('\0');
        value = terminator >= 0 ? decoded[..terminator] : decoded;
        return true;
    }

    public void Dispose()
    {
    }

    private bool IsInsideMainModule(long address)
        => address >= (long)MainModuleBase
           && address < checked((long)MainModuleBase + MainModuleSize);
}
