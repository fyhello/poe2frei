namespace FreiAtlas.Core.Memory;

public readonly record struct ProcessMemoryRegion(
    nint BaseAddress,
    long Size,
    bool IsReadable,
    bool IsExecutable,
    bool IsImage);

public interface IProcessMemory : IDisposable
{
    int ProcessId { get; }

    bool TryRead(nint address, Span<byte> destination);
    bool TryReadInt32(nint address, out int value);
    bool TryReadInt64(nint address, out long value);
    bool TryReadFloat(nint address, out float value);
    bool TryReadPointer(nint address, out nint value);
    bool TryReadUtf16(nint address, int maxChars, out string? value);
}

public interface IProcessMemoryLayout
{
    nint MainModuleBase { get; }
    long MainModuleSize { get; }
    IEnumerable<ProcessMemoryRegion> EnumerateMainModuleRegions();
}
