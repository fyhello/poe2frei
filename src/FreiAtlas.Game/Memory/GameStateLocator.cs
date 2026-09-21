using System.Buffers.Binary;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Memory;

namespace FreiAtlas.Game.Memory;

internal sealed class GameStateLocator
{
    private const int ScanChunkSize = 1024 * 1024;
    private const int MaximumComponents = 256;

    private readonly Poe2MemoryProfile _profile;
    private GameStateSlotCache? _cache;

    public GameStateLocator(Poe2MemoryProfile profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    public bool TryResolve(
        IProcessMemory memory,
        out GameRootState root,
        out IReadOnlyList<AreaReadDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(memory);

        root = default;
        var messages = new List<AreaReadDiagnostic>();
        diagnostics = messages;
        if (memory is not IProcessMemoryLayout layout
            || layout.MainModuleBase == 0
            || layout.MainModuleSize <= 0)
        {
            messages.Add(Error(
                "root-layout-unavailable",
                "Process memory does not expose a readable main module layout."));
            return false;
        }

        if (CacheMatches(memory, layout)
            && TryResolveSlot(memory, _cache!.SlotAddress, messages, out root))
        {
            return true;
        }

        _cache = null;
        var foundPattern = false;
        foreach (var slotAddress in FindGameStateSlots(memory, layout))
        {
            foundPattern = true;
            if (!TryResolveSlot(memory, slotAddress, messages, out root))
            {
                continue;
            }

            _cache = new GameStateSlotCache(
                memory.ProcessId,
                layout.MainModuleBase,
                layout.MainModuleSize,
                slotAddress);
            return true;
        }

        messages.Add(Error(
            foundPattern ? "root-candidate-invalid" : "root-aob-not-found",
            foundPattern
                ? "GameState AOB matches were found, but no candidate passed root validation."
                : "GameState AOB pattern was not found in executable image regions."));
        return false;
    }

    internal bool TryResolveComponent(
        IProcessMemory memory,
        nint entity,
        string componentName,
        out nint component)
    {
        component = 0;
        if (string.IsNullOrWhiteSpace(componentName))
        {
            return false;
        }

        var reader = new GameMemoryReader(memory, _profile);
        if (!reader.TryReadPointer(
                entity + _profile.Entity.DetailsOffset,
                out var details)
            || !reader.TryReadPointer(
                details + _profile.EntityDetails.ComponentLookupOffset,
                out var lookup)
            || !reader.TryReadStdVector(
                entity + _profile.Entity.ComponentsOffset,
                IntPtr.Size,
                MaximumComponents,
                out var components)
            || components.Count == 0
            || !reader.TryReadStdVector(
                lookup + _profile.ComponentLookup.BucketOffset,
                _profile.ComponentLookup.EntryStride,
                MaximumComponents,
                out var names))
        {
            return false;
        }

        for (var index = 0; index < names.Count; index++)
        {
            var entry = names.First
                        + (index * _profile.ComponentLookup.EntryStride);
            if (!reader.TryReadPointer(entry, out var nameAddress)
                || !memory.TryReadInt32(
                    entry + IntPtr.Size,
                    out var componentIndex)
                || componentIndex < 0
                || componentIndex >= components.Count
                || !reader.TryReadUtf8(nameAddress, 32, out var name)
                || !string.Equals(
                    name,
                    componentName,
                    StringComparison.Ordinal))
            {
                continue;
            }

            return reader.TryReadPointer(
                components.First + (componentIndex * IntPtr.Size),
                out component);
        }

        return false;
    }

    internal bool TryReadMetadata(
        IProcessMemory memory,
        nint entity,
        out string metadata)
    {
        metadata = string.Empty;
        var reader = new GameMemoryReader(memory, _profile);
        return reader.TryReadPointer(
                   entity + _profile.Entity.DetailsOffset,
                   out var details)
               && reader.TryReadStdWString(
                   details + _profile.EntityDetails.NameOffset,
                   1024,
                   out metadata);
    }

    private bool CacheMatches(
        IProcessMemory memory,
        IProcessMemoryLayout layout)
        => _cache is
        {
            ProcessId: var processId,
            MainModuleBase: var moduleBase,
            MainModuleSize: var moduleSize
        }
           && processId == memory.ProcessId
           && moduleBase == layout.MainModuleBase
           && moduleSize == layout.MainModuleSize;

    private bool TryResolveSlot(
        IProcessMemory memory,
        nint slotAddress,
        List<AreaReadDiagnostic> diagnostics,
        out GameRootState root)
    {
        root = default;
        var reader = new GameMemoryReader(memory, _profile);
        if (!reader.TryReadPointer(slotAddress, out var gameState))
        {
            return false;
        }

        var candidates = new List<(nint Address, bool IsFallback)>();
        if (reader.TryReadPointer(
                gameState + _profile.GameState.CurrentStateVectorOffset,
                out var currentVector)
            && reader.TryReadPointer(currentVector, out var current))
        {
            candidates.Add((current, false));
        }

        for (var index = 0; index < _profile.GameState.StateSlotCount; index++)
        {
            if (reader.TryReadPointer(
                    gameState
                    + _profile.GameState.StateSlotsOffset
                    + (index * _profile.GameState.StateSlotStride),
                    out var candidate)
                && candidates.All(item => item.Address != candidate))
            {
                candidates.Add((candidate, true));
            }
        }

        foreach (var candidate in candidates)
        {
            if (!TryValidateInGameState(
                    memory,
                    slotAddress,
                    gameState,
                    candidate.Address,
                    out root))
            {
                continue;
            }

            if (candidate.IsFallback)
            {
                diagnostics.Add(new AreaReadDiagnostic(
                    "root-fallback-slot",
                    "The current-state vector was invalid; a validated fallback state slot was used.",
                    AreaDiagnosticSeverity.Info));
            }

            return true;
        }

        return false;
    }

    private bool TryValidateInGameState(
        IProcessMemory memory,
        nint slotAddress,
        nint gameState,
        nint inGameState,
        out GameRootState root)
    {
        root = default;
        var reader = new GameMemoryReader(memory, _profile);
        if (!reader.TryReadPointer(
                inGameState + _profile.InGameState.AreaInstanceOffset,
                out var areaInstance)
            || !reader.TryReadPointer(
                areaInstance + _profile.AreaInstance.LocalPlayerOffset,
                out var localPlayer)
            || !TryReadMetadata(memory, localPlayer, out var metadata)
            || !metadata.StartsWith("Metadata/", StringComparison.Ordinal)
            || !TryResolveComponent(
                memory,
                localPlayer,
                "Render",
                out var render)
            || !reader.TryReadVector3(
                render + _profile.Render.WorldPositionOffset,
                out _))
        {
            return false;
        }

        root = new GameRootState(
            slotAddress,
            gameState,
            inGameState,
            areaInstance,
            localPlayer);
        return true;
    }

    private IEnumerable<nint> FindGameStateSlots(
        IProcessMemory memory,
        IProcessMemoryLayout layout)
    {
        var pattern = _profile.GameStateReference.Pattern;
        var overlap = pattern.Count - 1;
        var emitted = new HashSet<nint>();
        foreach (var region in layout.EnumerateMainModuleRegions())
        {
            if (!region.IsReadable
                || !region.IsExecutable
                || !region.IsImage
                || region.Size < pattern.Count)
            {
                continue;
            }

            for (long offset = 0; offset < region.Size; offset += ScanChunkSize)
            {
                var readStartOffset = offset == 0
                    ? 0
                    : offset - overlap;
                var available = region.Size - readStartOffset;
                var readLength = (int)Math.Min(
                    available,
                    ScanChunkSize + (offset == 0 ? 0 : overlap));
                if (readLength < pattern.Count)
                {
                    continue;
                }

                var bytes = new byte[readLength];
                var readAddress = region.BaseAddress + (nint)readStartOffset;
                if (!memory.TryRead(readAddress, bytes))
                {
                    continue;
                }

                for (var index = 0;
                     index <= bytes.Length - pattern.Count;
                     index++)
                {
                    if (!Matches(bytes, index, pattern))
                    {
                        continue;
                    }

                    var displacementIndex = checked(
                        index
                        + _profile.GameStateReference.DisplacementOffset);
                    var displacement = BinaryPrimitives.ReadInt32LittleEndian(
                        bytes.AsSpan(displacementIndex, sizeof(int)));
                    var instructionAddress = readAddress + index;
                    var slotAddress = instructionAddress
                                      + _profile.GameStateReference.InstructionLength
                                      + displacement;
                    if (emitted.Add(slotAddress))
                    {
                        yield return slotAddress;
                    }
                }
            }
        }
    }

    private static bool Matches(
        IReadOnlyList<byte> bytes,
        int offset,
        IReadOnlyList<byte?> pattern)
    {
        for (var index = 0; index < pattern.Count; index++)
        {
            if (pattern[index] is { } expected
                && bytes[offset + index] != expected)
            {
                return false;
            }
        }

        return true;
    }

    private static AreaReadDiagnostic Error(string code, string message)
        => new(code, message, AreaDiagnosticSeverity.Error);

    private sealed record GameStateSlotCache(
        int ProcessId,
        nint MainModuleBase,
        long MainModuleSize,
        nint SlotAddress);
}
