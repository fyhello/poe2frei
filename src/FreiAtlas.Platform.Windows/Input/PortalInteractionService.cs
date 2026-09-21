using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using FreiAtlas.Core.Contracts;
using FreiAtlas.Core.Memory;
using FreiAtlas.Core.PortalSqueeze;
using FreiAtlas.Platform.Windows.Native;
using FreiAtlas.Platform.Windows.Process;

namespace FreiAtlas.Platform.Windows.Input;

/// <summary>
/// 执行一次经过运行时校验的传送门内部交互。该类不改变现有只读内存句柄。
/// </summary>
public sealed class PortalInteractionService : IPortalInteraction, IDisposable
{
    private const int MaxContextSearchOffset = 0x800;
    private const int VirtualTableScanBytes = 0x400;
    private const int StubSize = 102;

    private static readonly byte[] PortalInteractPattern =
    [
        0x48, 0x89, 0x5C, 0x24, 0x10, 0x55, 0x56, 0x57,
        0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57,
        0x48, 0x83, 0xEC, 0x30, 0x49, 0x8B, 0xF1,
        0x45, 0x0F, 0xB7, 0xF8, 0x4C, 0x8B, 0xE2,
        0x4C, 0x8B, 0xE9, 0xBF, 0x66, 0xC2, 0x00, 0x00
    ];

    private static readonly byte[] ReferenceStub =
    [
        0x48, 0xBA, 0, 0, 0, 0, 0, 0, 0, 0, 0x48, 0xB9, 0x08, 0x61, 0x54, 0xB4,
        0xF6, 0x7F, 0, 0, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90,
        0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x48, 0x8B, 0xF1,
        0x49, 0xB9, 0x8C, 0x4D, 0xFA, 0xB2, 0xF6, 0x7F, 0, 0, 0x4D, 0x8B,
        0x11, 0x49, 0x83, 0x7A, 0x08, 0, 0x74, 0x29, 0x45, 0x31, 0xC0,
        0x0F, 0x57, 0xC0, 0xF3, 0x0F, 0x7F, 0x84, 0x24, 0x20, 0, 0, 0,
        0x48, 0xB8, 0x80, 0xA7, 0x34, 0xB0, 0xF6, 0x7F, 0, 0, 0x48, 0x81,
        0xEC, 0x08, 0x01, 0, 0, 0xFF, 0xD0, 0x48, 0x81, 0xC4, 0x08, 0x01,
        0, 0, 0xC3
    ];

    private readonly object _gate = new();
    private readonly Dictionary<LayoutCacheKey, Layout> _layouts = [];
    private readonly Dictionary<int, PendingInteraction> _pending = [];
    private bool _disposed;

    public PortalInteractionService()
    {
        if (ReferenceStub.Length != StubSize)
        {
            throw new InvalidOperationException("挤门交互 stub 长度不符合契约。");
        }
    }

    public PortalInteractionResult Interact(
        PortalCandidateScan scan,
        PortalCandidate target,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var timeoutMs = (uint)Math.Clamp(
            (long)timeout.TotalMilliseconds,
            250L,
            60_000L);
        lock (_gate)
        {
            if (TryCompletePending(scan, target, out var pendingResult))
            {
                return pendingResult;
            }

            if (_pending.ContainsKey(scan.ProcessId))
            {
                return Result(false, false, false, "上一次挤门线程仍在执行", scan.AreaInstance, 0);
            }

            if (!TryOpenReadOnly(scan.ProcessId, out var readMemory, out var readError))
            {
                return Result(false, false, false, readError, scan.AreaInstance, 0);
            }

            using (readMemory)
            {
                if (!ValidateScan(readMemory, scan, target, out var validationError))
                {
                    return Result(false, false, false, validationError, scan.AreaInstance, 0);
                }

                if (!TryResolveLayout(readMemory, scan, out var layout, out var layoutError))
                {
                    return Result(false, false, layoutError == "正在解析挤门地址", layoutError, scan.AreaInstance, 0);
                }

                var access = NativeMethods.ProcessCreateThread
                             | NativeMethods.ProcessVmOperation
                             | NativeMethods.ProcessVmRead
                             | NativeMethods.ProcessVmWrite
                             | NativeMethods.ProcessQueryInformation
                             | NativeMethods.ProcessQueryLimitedInformation
                             | NativeMethods.Synchronize;
                var process = NativeMethods.OpenProcess(access, false, scan.ProcessId);
                if (process == 0)
                {
                    return Result(false, false, false, $"无法打开游戏交互句柄：{Marshal.GetLastWin32Error()}", scan.AreaInstance, 0);
                }

                var retainedProcess = false;
                try
                {
                    if (!ValidateLiveState(readMemory, scan, target, layout, out var liveError))
                    {
                        return Result(false, false, false, liveError, scan.AreaInstance, 0);
                    }

                    var allocation = NativeMethods.VirtualAllocEx(
                        process,
                        0,
                        (nuint)(StubSize + sizeof(long)),
                        NativeMethods.MemCommit | NativeMethods.MemReserve,
                        NativeMethods.PageExecuteReadWrite);
                    if (allocation == 0)
                    {
                        return Result(false, false, false, $"分配交互 stub 失败：{Marshal.GetLastWin32Error()}", scan.AreaInstance, 0);
                    }

                    var stub = BuildStub(layout.ContextAddress, layout.TargetFunction, allocation);
                    if (!WriteRemote(process, allocation, stub)
                        || !WriteRemote(process, allocation + StubSize, target.EntityAddress))
                    {
                        NativeMethods.VirtualFreeEx(process, allocation, 0, NativeMethods.MemRelease);
                        return Result(false, false, false, $"写入交互 stub 失败：{Marshal.GetLastWin32Error()}", scan.AreaInstance, 0);
                    }

                    NativeMethods.FlushInstructionCache(process, allocation, (nuint)(StubSize + sizeof(long)));
                    var thread = NativeMethods.CreateRemoteThread(
                        process,
                        0,
                        0,
                        allocation,
                        0,
                        0,
                        out _);
                    if (thread == 0)
                    {
                        NativeMethods.VirtualFreeEx(process, allocation, 0, NativeMethods.MemRelease);
                        return Result(false, false, false, $"启动交互线程失败：{Marshal.GetLastWin32Error()}", scan.AreaInstance, 0);
                    }

                    var wait = NativeMethods.WaitForSingleObject(thread, timeoutMs);
                    if (wait == NativeMethods.WaitObject0)
                    {
                        NativeMethods.CloseHandle(thread);
                        NativeMethods.VirtualFreeEx(process, allocation, 0, NativeMethods.MemRelease);
                        NativeMethods.CloseHandle(process);
                        process = 0;
                        var after = ReadAreaInstance(readMemory, scan.InGameState);
                        return Result(true, after != 0 && after != scan.AreaInstance, false,
                            after == scan.AreaInstance ? "交互线程已完成，等待游戏切换区域" : "挤门交互已完成",
                            scan.AreaInstance,
                            after);
                    }

                    if (wait == NativeMethods.WaitFailed)
                    {
                        NativeMethods.CloseHandle(thread);
                        NativeMethods.VirtualFreeEx(process, allocation, 0, NativeMethods.MemRelease);
                        return Result(false, false, false,
                            $"等待交互线程失败：{Marshal.GetLastWin32Error()}",
                            scan.AreaInstance,
                            0);
                    }

                    _pending[scan.ProcessId] = new PendingInteraction(
                        process,
                        thread,
                        allocation,
                        scan.AreaInstance,
                        target.EntityAddress);
                    retainedProcess = true;
                    return Result(true, false, false, "交互线程仍在执行，等待完成", scan.AreaInstance, 0);
                }
                finally
                {
                    if (!retainedProcess && process != 0)
                    {
                        NativeMethods.CloseHandle(process);
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var pending in _pending.Values)
            {
                var wait = NativeMethods.WaitForSingleObject(pending.Thread, 1_000);
                NativeMethods.CloseHandle(pending.Thread);
                if (wait != NativeMethods.WaitTimeout && wait != NativeMethods.WaitFailed)
                {
                    NativeMethods.VirtualFreeEx(pending.Process, pending.Stub, 0, NativeMethods.MemRelease);
                }
                // 线程仍在运行时保留远程内存，避免释放后线程继续执行造成崩溃。
                NativeMethods.CloseHandle(pending.Process);
            }

            _pending.Clear();
            _layouts.Clear();
        }
    }

    private bool TryCompletePending(
        PortalCandidateScan scan,
        PortalCandidate target,
        out PortalInteractionResult result)
    {
        result = default!;
        if (!_pending.TryGetValue(scan.ProcessId, out var pending))
        {
            return false;
        }

        var wait = NativeMethods.WaitForSingleObject(pending.Thread, 0);
        if (wait == NativeMethods.WaitTimeout)
        {
            result = Result(true, false, false, "上一次挤门线程仍在执行", pending.AreaInstance, 0);
            return true;
        }

        NativeMethods.CloseHandle(pending.Thread);
        NativeMethods.VirtualFreeEx(pending.Process, pending.Stub, 0, NativeMethods.MemRelease);
        NativeMethods.CloseHandle(pending.Process);
        _pending.Remove(scan.ProcessId);
        result = Result(true, false, false, "挤门线程已完成，未重复启动", pending.AreaInstance, 0);
        return true;
    }

    private static bool TryOpenReadOnly(
        int processId,
        out NativeProcessMemory memory,
        out string error)
    {
        if (NativeProcessMemory.TryOpen(processId, out var opened)
            && opened is not null)
        {
            memory = opened;
            error = string.Empty;
            return true;
        }

        memory = null!;
        error = "无法只读打开游戏进程";
        return false;
    }

    private static bool ValidateScan(
        NativeProcessMemory memory,
        PortalCandidateScan scan,
        PortalCandidate target,
        out string error)
    {
        error = string.Empty;
        if (scan.ProcessId != memory.ProcessId
            || !scan.IsValid
            || target.EntityAddress == 0
            || !IsDirectPortal(target.MetadataPath)
            || !float.IsFinite(target.DistanceToPlayer)
            || target.DistanceToPlayer < 0
            || !scan.Candidates.Any(candidate => candidate.EntityAddress == target.EntityAddress))
        {
            error = "挤门目标已失效，请重新扫描";
            return false;
        }

        var currentArea = ReadAreaInstance(memory, scan.InGameState);
        if (currentArea != scan.AreaInstance)
        {
            error = "区域在交互前已变化";
            return false;
        }

        return true;
    }

    private static bool IsDirectPortal(string metadata)
        => metadata.Contains("MultiplexPortal", StringComparison.OrdinalIgnoreCase)
           || metadata.Contains("TownPortal", StringComparison.OrdinalIgnoreCase);

    private static bool ValidateLiveState(
        NativeProcessMemory memory,
        PortalCandidateScan scan,
        PortalCandidate target,
        Layout layout,
        out string error)
    {
        error = string.Empty;
        if (ReadAreaInstance(memory, scan.InGameState) != scan.AreaInstance
            || !TryReadPointer(memory, target.EntityAddress, out var first)
            || !TryReadPointer(memory, target.EntityAddress + 8, out var second)
            || first == 0
            || !IsCanonical(second)
            || !TryReadPointer(memory, layout.ContextAddress, out var vtable)
            || !TryReadPointer(memory, vtable + layout.VirtualSlotOffset, out var targetFunction)
            || targetFunction != layout.TargetFunction)
        {
            error = "交互目标或动态布局在启动前已变化";
            return false;
        }

        return true;
    }

    private bool TryResolveLayout(
        NativeProcessMemory memory,
        PortalCandidateScan scan,
        out Layout layout,
        out string error)
    {
        layout = default;
        error = string.Empty;
        if (!TryReadTextSection(memory, out var text, out var textRva, out var textHash, out error))
        {
            return false;
        }

        var key = new LayoutCacheKey(memory.ProcessId, memory.MainModuleBase, textHash);
        if (_layouts.TryGetValue(key, out var cached)
            && ValidateCachedLayout(memory, scan, cached))
        {
            layout = cached;
            return true;
        }

        var matches = FindPattern(text, PortalInteractPattern);
        if (matches.Count != 1)
        {
            error = matches.Count == 0 ? "未找到挤门交互函数" : "挤门交互函数特征不唯一";
            return false;
        }

        var targetFunction = memory.MainModuleBase + textRva + matches[0];
        var started = Stopwatch.GetTimestamp();
        if (!TryFindContextChain(memory, scan.InGameState, targetFunction, out var firstOffset, out var secondOffset, out var context, out var slot))
        {
            error = Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(3)
                ? "挤门地址解析超时"
                : "未找到经过校验的挤门上下文";
            return false;
        }

        layout = new Layout(
            targetFunction,
            context,
            firstOffset,
            secondOffset,
            slot);
        _layouts[key] = layout;
        return true;
    }

    private static bool ValidateCachedLayout(NativeProcessMemory memory, PortalCandidateScan scan, Layout layout)
        => TryReadPointer(memory, scan.InGameState + layout.FirstOffset, out var first)
           && TryReadPointer(memory, first + layout.SecondOffset, out var context)
           && context == layout.ContextAddress
           && TryReadPointer(memory, context, out var vtable)
           && TryReadPointer(memory, vtable + layout.VirtualSlotOffset, out var target)
           && target == layout.TargetFunction;

    private static bool TryFindContextChain(
        NativeProcessMemory memory,
        nint inGameState,
        nint targetFunction,
        out int firstOffset,
        out int secondOffset,
        out nint context,
        out int virtualSlotOffset)
    {
        firstOffset = secondOffset = virtualSlotOffset = 0;
        context = 0;
        var orderedFirst = CandidateOffsets();
        var orderedSecond = CandidateOffsets();
        var started = Stopwatch.GetTimestamp();

        foreach (var first in orderedFirst)
        {
            if (!TryReadPointer(memory, inGameState + first, out var middle)) continue;
            foreach (var second in orderedSecond)
            {
                if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(3)) return false;
                if (!TryReadPointer(memory, middle + second, out var candidate)
                    || !TryFindVirtualSlot(memory, candidate, targetFunction, out var slot)) continue;
                firstOffset = first;
                secondOffset = second;
                context = candidate;
                virtualSlotOffset = slot;
                return true;
            }
        }

        return false;
    }

    private static bool TryFindVirtualSlot(
        NativeProcessMemory memory,
        nint candidate,
        nint targetFunction,
        out int slot)
    {
        slot = 0;
        if (!IsCanonical(candidate)
            || !TryReadPointer(memory, candidate, out var vtable)
            || !IsCanonical(vtable))
        {
            return false;
        }

        for (var offset = 0; offset < VirtualTableScanBytes; offset += IntPtr.Size)
        {
            if (TryReadPointer(memory, vtable + offset, out var function)
                && function == targetFunction)
            {
                slot = offset;
                return true;
            }
        }

        return false;
    }

    private static int[] CandidateOffsets()
        => Enumerable.Range(0, MaxContextSearchOffset / IntPtr.Size)
            .Select(index => index * IntPtr.Size)
            .OrderBy(offset => offset is 0x300 or 0x3E8 or 0x400 ? 0 : 1)
            .ToArray();

    private static byte[] BuildStub(nint context, nint targetFunction, nint allocation)
    {
        var stub = (byte[])ReferenceStub.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(stub.AsSpan(0x0C, 8), (ulong)context);
        BinaryPrimitives.WriteUInt64LittleEndian(stub.AsSpan(0x2A, 8), (ulong)(allocation + 0x66));
        BinaryPrimitives.WriteUInt64LittleEndian(stub.AsSpan(0x4D, 8), (ulong)targetFunction);
        return stub;
    }

    private static bool WriteRemote(nint process, nint address, nint value)
    {
        unsafe
        {
            var local = value;
            return NativeMethods.WriteProcessMemory(
                process,
                address,
                &local,
                (nuint)IntPtr.Size,
                out var written)
                && written == (nuint)IntPtr.Size;
        }
    }

    private static bool WriteRemote(nint process, nint address, byte[] bytes)
    {
        unsafe
        {
            fixed (byte* buffer = bytes)
            {
                return NativeMethods.WriteProcessMemory(
                    process,
                    address,
                    buffer,
                    (nuint)bytes.Length,
                    out var written)
                    && written == (nuint)bytes.Length;
            }
        }
    }

    private static bool TryReadTextSection(
        NativeProcessMemory memory,
        out byte[] text,
        out int textRva,
        out string hash,
        out string error)
    {
        text = [];
        textRva = 0;
        hash = string.Empty;
        error = string.Empty;
        Span<byte> dos = stackalloc byte[0x1000];
        if (!memory.TryRead(memory.MainModuleBase, dos)
            || BinaryPrimitives.ReadUInt16LittleEndian(dos) != 0x5A4D)
        {
            error = "游戏 PE 头不可读";
            return false;
        }

        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(dos[0x3C..]);
        if (peOffset < 0 || peOffset + 0x100 > dos.Length
            || BinaryPrimitives.ReadUInt32LittleEndian(dos[peOffset..]) != 0x00004550)
        {
            error = "游戏 PE 头无效";
            return false;
        }

        var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(dos[(peOffset + 6)..]);
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(dos[(peOffset + 20)..]);
        var sectionOffset = peOffset + 24 + optionalSize;
        for (var index = 0; index < sectionCount; index++)
        {
            var offset = sectionOffset + index * 40;
            if (offset + 40 > dos.Length) break;
            var name = System.Text.Encoding.ASCII.GetString(dos[offset..(offset + 8)]).TrimEnd('\0');
            if (!string.Equals(name, ".text", StringComparison.Ordinal)) continue;
            var size = BinaryPrimitives.ReadUInt32LittleEndian(dos[(offset + 8)..]);
            textRva = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(dos[(offset + 12)..]));
            if (size is 0 or > 128 * 1024 * 1024)
            {
                error = "游戏代码段大小无效";
                return false;
            }

            text = new byte[checked((int)size)];
            if (!memory.TryRead(memory.MainModuleBase + textRva, text))
            {
                text = [];
                error = "游戏代码段不可读";
                return false;
            }

            hash = Convert.ToHexString(SHA256.HashData(text));
            return true;
        }

        error = "未找到游戏代码段";
        return false;
    }

    private static List<int> FindPattern(byte[] bytes, byte[] pattern)
    {
        var matches = new List<int>();
        for (var index = 0; index <= bytes.Length - pattern.Length; index++)
        {
            if (!bytes.AsSpan(index, pattern.Length).SequenceEqual(pattern)) continue;
            matches.Add(index);
        }

        return matches;
    }

    private static bool TryReadPointer(NativeProcessMemory memory, nint address, out nint value)
        => memory.TryReadPointer(address, out value) && IsCanonical(value);

    private static nint ReadAreaInstance(NativeProcessMemory memory, nint inGameState)
        => TryReadPointer(
                memory,
                inGameState + Poe2MemoryProfile.Current.InGameState.AreaInstanceOffset,
                out var area)
            ? area
            : 0;

    private static bool IsCanonical(nint value)
    {
        var address = (ulong)value.ToInt64();
        return address >= 0x10000 && address <= 0x0000_7FFF_FFFF_FFFF;
    }

    private static PortalInteractionResult Result(
        bool started,
        bool completed,
        bool pending,
        string message,
        nint before,
        nint after)
        => new(started, completed, pending, message, before, after);

    private readonly record struct LayoutCacheKey(int ProcessId, nint ModuleBase, string TextHash);

    private readonly record struct Layout(
        nint TargetFunction,
        nint ContextAddress,
        int FirstOffset,
        int SecondOffset,
        int VirtualSlotOffset);

    private readonly record struct PendingInteraction(
        nint Process,
        nint Thread,
        nint Stub,
        nint AreaInstance,
        nint EntityAddress);
}
