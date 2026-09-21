using System.Runtime.InteropServices;
using System.Text;
using FreiAtlas.Core.Memory;
using FreiAtlas.Platform.Windows.Native;

namespace FreiAtlas.Platform.Windows.Process;

public sealed class NativeProcessMemory : IProcessMemory, IProcessMemoryLayout
{
    private nint _handle;

    private NativeProcessMemory(
        int processId,
        nint handle,
        nint mainModuleBase,
        long mainModuleSize)
    {
        ProcessId = processId;
        _handle = handle;
        MainModuleBase = mainModuleBase;
        MainModuleSize = mainModuleSize;
    }

    public int ProcessId { get; }
    public nint MainModuleBase { get; }
    public long MainModuleSize { get; }

    public static bool TryOpen(int processId, out NativeProcessMemory? memory)
    {
        var handle = NativeMethods.OpenProcess(
            NativeMethods.ProcessVmRead | NativeMethods.ProcessQueryInformation,
            false,
            processId);
        if (handle == 0)
        {
            memory = null;
            return false;
        }

        var moduleBase = nint.Zero;
        var moduleSize = 0L;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            if (process.MainModule is { } module)
            {
                moduleBase = module.BaseAddress;
                moduleSize = module.ModuleMemorySize;
            }
        }
        catch
        {
            // The read handle is still useful even when module metadata is restricted.
        }

        memory = new NativeProcessMemory(
            processId,
            handle,
            moduleBase,
            moduleSize);
        return true;
    }

    public IEnumerable<ProcessMemoryRegion> EnumerateMainModuleRegions()
    {
        var handle = _handle;
        if (handle == 0 || MainModuleBase == 0 || MainModuleSize <= 0)
        {
            yield break;
        }

        var moduleStart = (long)MainModuleBase;
        var moduleEnd = checked(moduleStart + MainModuleSize);
        var address = moduleStart;
        var infoSize = (nuint)Marshal.SizeOf<NativeMethods.MemoryBasicInformation>();

        while (address < moduleEnd)
        {
            if (NativeMethods.VirtualQueryEx(
                    handle,
                    (nint)address,
                    out var info,
                    infoSize) == 0)
            {
                yield break;
            }

            var regionStart = Math.Max((long)info.BaseAddress, moduleStart);
            var regionEnd = Math.Min(
                checked((long)info.BaseAddress + (long)info.RegionSize),
                moduleEnd);
            if (regionEnd > regionStart
                && info.State == NativeMethods.MemCommit
                && (info.Protect & NativeMethods.PageGuard) == 0
                && info.Type == NativeMethods.MemImage)
            {
                var readable = IsReadableProtection(info.Protect);
                var executable = IsExecutableProtection(info.Protect);
                yield return new ProcessMemoryRegion(
                    (nint)regionStart,
                    regionEnd - regionStart,
                    readable,
                    executable,
                    IsImage: true);
            }

            var next = checked((long)info.BaseAddress + (long)info.RegionSize);
            if (next <= address)
            {
                yield break;
            }

            address = next;
        }
    }

    public unsafe bool TryRead(nint address, Span<byte> destination)
    {
        if (_handle == 0 || address == 0 || destination.Length == 0)
        {
            return false;
        }

        fixed (byte* buffer = destination)
        {
            return NativeMethods.ReadProcessMemory(
                       _handle,
                       address,
                       buffer,
                       (nuint)destination.Length,
                       out var read)
                   && read == (nuint)destination.Length;
        }
    }

    public bool TryReadInt32(nint address, out int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        if (!TryRead(address, buffer))
        {
            value = default;
            return false;
        }

        value = MemoryMarshal.Read<int>(buffer);
        return true;
    }

    public bool TryReadInt64(nint address, out long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        if (!TryRead(address, buffer))
        {
            value = default;
            return false;
        }

        value = MemoryMarshal.Read<long>(buffer);
        return true;
    }

    public bool TryReadFloat(nint address, out float value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(float)];
        if (!TryRead(address, buffer))
        {
            value = default;
            return false;
        }

        value = MemoryMarshal.Read<float>(buffer);
        return true;
    }

    public bool TryReadPointer(nint address, out nint value)
    {
        if (IntPtr.Size == sizeof(long))
        {
            if (TryReadInt64(address, out var pointer))
            {
                value = (nint)pointer;
                return true;
            }
        }
        else if (TryReadInt32(address, out var pointer))
        {
            value = (nint)pointer;
            return true;
        }

        value = 0;
        return false;
    }

    public bool TryReadUtf16(nint address, int maxChars, out string? value)
    {
        if (maxChars <= 0 || maxChars > 4096)
        {
            value = null;
            return false;
        }

        var buffer = new byte[maxChars * sizeof(char)];
        if (!TryRead(address, buffer))
        {
            value = null;
            return false;
        }

        var decoded = Encoding.Unicode.GetString(buffer);
        var terminator = decoded.IndexOf('\0');
        value = terminator >= 0
            ? decoded[..terminator]
            : decoded;
        return true;
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static bool IsReadableProtection(uint protection)
        => protection is NativeMethods.PageReadOnly
            or NativeMethods.PageReadWrite
            or NativeMethods.PageWriteCopy
            or NativeMethods.PageExecuteRead
            or NativeMethods.PageExecuteReadWrite
            or NativeMethods.PageExecuteWriteCopy;

    private static bool IsExecutableProtection(uint protection)
        => protection is NativeMethods.PageExecute
            or NativeMethods.PageExecuteRead
            or NativeMethods.PageExecuteReadWrite
            or NativeMethods.PageExecuteWriteCopy;
}
