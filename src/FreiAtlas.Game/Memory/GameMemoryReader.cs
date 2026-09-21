using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using FreiAtlas.Core.Memory;

namespace FreiAtlas.Game.Memory;

internal readonly record struct MemoryRange(nint First, nint Last, int Count);

internal sealed class GameMemoryReader
{
    private const ulong MinimumCanonicalPointer = 0x10000;
    private const ulong MaximumCanonicalPointer = 0x7FFF_FFFF_FFFF;

    private readonly IProcessMemory _memory;
    private readonly Poe2MemoryProfile _profile;

    public GameMemoryReader(
        IProcessMemory memory,
        Poe2MemoryProfile? profile = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _profile = profile ?? Poe2MemoryProfile.Current;
    }

    public int ProcessId => _memory.ProcessId;

    public bool TryReadPointer(nint address, out nint value)
    {
        if (_memory.TryReadPointer(address, out value)
            && IsCanonicalPointer(value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    public bool TryReadByte(nint address, out byte value)
    {
        Span<byte> bytes = stackalloc byte[1];
        if (_memory.TryRead(address, bytes))
        {
            value = bytes[0];
            return true;
        }

        value = default;
        return false;
    }

    public bool TryReadUInt32(nint address, out uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        if (_memory.TryRead(address, bytes))
        {
            value = MemoryMarshal.Read<uint>(bytes);
            return true;
        }

        value = default;
        return false;
    }

    public bool TryReadVector2(nint address, out Vector2 value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(float) * 2];
        if (_memory.TryRead(address, bytes))
        {
            value = MemoryMarshal.Read<Vector2>(bytes);
            if (float.IsFinite(value.X) && float.IsFinite(value.Y))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    public bool TryReadVector3(nint address, out Vector3 value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(float) * 3];
        if (_memory.TryRead(address, bytes))
        {
            value = MemoryMarshal.Read<Vector3>(bytes);
            if (float.IsFinite(value.X)
                && float.IsFinite(value.Y)
                && float.IsFinite(value.Z))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    public bool TryReadStdVector(
        nint address,
        int stride,
        int maximumCount,
        out MemoryRange range)
    {
        range = default;
        if (stride <= 0 || maximumCount < 0)
        {
            return false;
        }

        if (!_memory.TryReadPointer(address, out var first)
            || !_memory.TryReadPointer(address + IntPtr.Size, out var last))
        {
            return false;
        }

        if (first == 0 && last == 0)
        {
            return true;
        }

        if (!IsCanonicalPointer(first) || !IsCanonicalPointer(last))
        {
            return false;
        }

        var byteCount = last.ToInt64() - first.ToInt64();
        if (byteCount < 0 || byteCount % stride != 0)
        {
            return false;
        }

        var count = byteCount / stride;
        if (count > maximumCount || count > int.MaxValue)
        {
            return false;
        }

        range = new MemoryRange(first, last, (int)count);
        return true;
    }

    public bool TryReadStdWString(
        nint address,
        int maximumChars,
        out string value)
    {
        value = string.Empty;
        if (maximumChars <= 0
            || !_memory.TryReadInt32(
                address + _profile.StdWString.LengthOffset,
                out var length)
            || length < 0
            || length > maximumChars)
        {
            return false;
        }

        if (length == 0)
        {
            return true;
        }

        var characters = address;
        if (length >= _profile.StdWString.InlineCapacity
            && !TryReadPointer(address, out characters))
        {
            return false;
        }

        if (!_memory.TryReadUtf16(characters, length + 1, out var decoded)
            || decoded is null
            || decoded.Length != length)
        {
            return false;
        }

        value = decoded;
        return true;
    }

    public bool TryReadUtf8(
        nint address,
        int maximumBytes,
        out string value)
    {
        value = string.Empty;
        if (maximumBytes <= 0 || maximumBytes > 4096)
        {
            return false;
        }

        var bytes = new byte[maximumBytes];
        if (!_memory.TryRead(address, bytes))
        {
            return false;
        }

        var terminator = Array.IndexOf(bytes, (byte)0);
        if (terminator < 0)
        {
            return false;
        }

        value = Encoding.UTF8.GetString(bytes, 0, terminator);
        return true;
    }

    private static bool IsCanonicalPointer(nint pointer)
    {
        var raw = unchecked((ulong)pointer.ToInt64());
        return raw is >= MinimumCanonicalPointer and <= MaximumCanonicalPointer;
    }
}
