using System.Text;
using FreiAtlas.Core.Memory;

namespace FreiAtlas.Atlas.Memory;

public enum GameProcessIdentityState
{
    InGame,
    CharacterNotLoaded,
    ReadFailed
}

public sealed record GameProcessIdentity(
    GameProcessIdentityState State,
    string? CharacterName,
    string? StatusMessage = null);

public sealed class GameProcessIdentityReader
{
    private const ulong MinimumCanonicalPointer = 0x10000;
    private const ulong MaximumCanonicalPointer = 0x7FFF_FFFF_FFFF;
    private const int MaximumComponents = 256;

    private readonly object _cacheGate = new();
    private readonly Dictionary<int, GameStateReferenceCache> _gameStateReferences = [];

    public GameProcessIdentity Read(
        IProcessMemory memory,
        AtlasLayoutProfile profile)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(profile);

        if (memory is not IProcessMemoryLayout layout
            || !TryResolveGameState(memory, layout, profile, out var gameState))
        {
            return Failed("game-state-reference-unavailable");
        }

        var candidates = ReadStateCandidates(memory, gameState, profile);
        if (candidates.Count == 0)
        {
            return NotLoaded();
        }

        foreach (var inGameState in candidates)
        {
            if (!TryReadCanonicalPointer(
                    memory,
                    inGameState + profile.Game.InGameState.AreaInstanceOffset,
                    out var areaInstance))
            {
                continue;
            }

            if (!memory.TryReadPointer(
                    areaInstance + profile.Game.AreaInstance.LocalPlayerOffset,
                    out var localPlayer))
            {
                return Failed("local-player-unavailable");
            }

            if (localPlayer == 0)
            {
                return NotLoaded();
            }

            if (!IsCanonicalPointer(localPlayer))
            {
                return Failed("local-player-unavailable");
            }

            if (!TryResolveComponent(
                    memory,
                    localPlayer,
                    "Player",
                    profile,
                    out var playerComponent))
            {
                return Failed("player-component-unavailable");
            }

            var nameStatus = ReadStdWString(
                memory,
                playerComponent + profile.Game.Player.NameOffset,
                profile,
                out var characterName);
            if (nameStatus == StdWStringReadStatus.Empty)
            {
                return NotLoaded();
            }

            if (nameStatus != StdWStringReadStatus.Success
                || !IsValidCharacterName(
                    characterName,
                    profile.Game.Player.MaximumNameLength))
            {
                return Failed("player-name-unavailable");
            }

            return new GameProcessIdentity(
                GameProcessIdentityState.InGame,
                characterName);
        }

        return NotLoaded();
    }

    private bool TryResolveGameState(
        IProcessMemory memory,
        IProcessMemoryLayout layout,
        AtlasLayoutProfile profile,
        out nint gameState)
    {
        GameStateReferenceCache? cached;
        lock (_cacheGate)
        {
            _gameStateReferences.TryGetValue(memory.ProcessId, out cached);
            if (cached is not null
                && (cached.MainModuleBase != layout.MainModuleBase
                    || cached.MainModuleSize != layout.MainModuleSize))
            {
                _gameStateReferences.Remove(memory.ProcessId);
                cached = null;
            }
        }

        if (cached is not null
            && TryReadCanonicalPointer(memory, cached.SlotAddress, out gameState))
        {
            return true;
        }

        lock (_cacheGate)
        {
            _gameStateReferences.Remove(memory.ProcessId);
        }

        if (!TryFindGameState(
                memory,
                layout,
                profile,
                out var slotAddress,
                out gameState))
        {
            return false;
        }

        lock (_cacheGate)
        {
            _gameStateReferences[memory.ProcessId] = new GameStateReferenceCache(
                layout.MainModuleBase,
                layout.MainModuleSize,
                slotAddress);
        }

        return true;
    }

    private static bool TryFindGameState(
        IProcessMemory memory,
        IProcessMemoryLayout layout,
        AtlasLayoutProfile profile,
        out nint slotAddress,
        out nint gameState)
    {
        slotAddress = 0;
        gameState = 0;
        var pattern = profile.Game.GameStateReference.Pattern;
        if (pattern.Count == 0)
        {
            return false;
        }

        foreach (var region in layout.EnumerateMainModuleRegions())
        {
            if (!region.IsReadable
                || !region.IsExecutable
                || region.Size <= 0
                || region.Size > int.MaxValue)
            {
                continue;
            }

            var bytes = new byte[(int)region.Size];
            if (!memory.TryRead(region.BaseAddress, bytes))
            {
                continue;
            }

            for (var offset = 0; offset <= bytes.Length - pattern.Count; offset++)
            {
                if (!Matches(bytes, offset, pattern))
                {
                    continue;
                }

                var displacementOffset =
                    offset + profile.Game.GameStateReference.DisplacementOffset;
                if (displacementOffset + sizeof(int) > bytes.Length)
                {
                    continue;
                }

                var displacement = BitConverter.ToInt32(bytes, displacementOffset);
                var candidateSlotAddress = region.BaseAddress
                    + offset
                    + profile.Game.GameStateReference.InstructionLength
                    + displacement;
                if (TryReadCanonicalPointer(
                        memory,
                        candidateSlotAddress,
                        out gameState))
                {
                    slotAddress = candidateSlotAddress;
                    return true;
                }
            }
        }

        return false;
    }

    private static IReadOnlyList<nint> ReadStateCandidates(
        IProcessMemory memory,
        nint gameState,
        AtlasLayoutProfile profile)
    {
        var candidates = new List<nint>(profile.Game.GameState.StateSlotCount + 1);
        if (TryReadCanonicalPointer(
                memory,
                gameState + profile.Game.GameState.CurrentStateVectorOffset,
                out var currentVector)
            && TryReadCanonicalPointer(memory, currentVector, out var currentState))
        {
            candidates.Add(currentState);
        }

        for (var index = 0; index < profile.Game.GameState.StateSlotCount; index++)
        {
            if (TryReadCanonicalPointer(
                    memory,
                    gameState
                    + profile.Game.GameState.StateSlotsOffset
                    + index * profile.Game.GameState.StateSlotStride,
                    out var candidate))
            {
                candidates.Add(candidate);
            }
        }

        return candidates.Distinct().ToArray();
    }

    private static bool TryResolveComponent(
        IProcessMemory memory,
        nint entity,
        string componentName,
        AtlasLayoutProfile profile,
        out nint component)
    {
        component = 0;
        if (!TryReadCanonicalPointer(
                memory,
                entity + profile.Game.Entity.DetailsOffset,
                out var details)
            || !TryReadCanonicalPointer(
                memory,
                details + profile.Game.EntityDetails.ComponentLookupOffset,
                out var lookup)
            || !TryReadVector(
                memory,
                entity + profile.Game.Entity.ComponentsOffset,
                sizeof(long),
                out var componentFirst,
                out var componentCount)
            || componentCount > MaximumComponents
            || !TryReadVector(
                memory,
                lookup + profile.Game.ComponentLookup.BucketOffset,
                profile.Game.ComponentLookup.EntryStride,
                out var entryFirst,
                out var entryCount)
            || entryCount > MaximumComponents)
        {
            return false;
        }

        for (var index = 0; index < entryCount; index++)
        {
            var entry = entryFirst + index * profile.Game.ComponentLookup.EntryStride;
            if (!TryReadCanonicalPointer(memory, entry, out var namePointer)
                || !memory.TryReadInt32(entry + sizeof(long), out var componentIndex)
                || componentIndex < 0
                || componentIndex >= componentCount
                || !TryReadAscii(memory, namePointer, 32, out var name)
                || !string.Equals(name, componentName, StringComparison.Ordinal)
                || !TryReadCanonicalPointer(
                    memory,
                    componentFirst + componentIndex * sizeof(long),
                    out component))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool TryReadVector(
        IProcessMemory memory,
        nint address,
        int stride,
        out nint first,
        out int count)
    {
        first = 0;
        count = 0;
        if (!TryReadCanonicalPointer(memory, address, out first)
            || !TryReadCanonicalPointer(memory, address + sizeof(long), out var last))
        {
            return false;
        }

        var byteLength = last.ToInt64() - first.ToInt64();
        if (byteLength <= 0
            || byteLength % stride != 0
            || byteLength / stride > int.MaxValue)
        {
            return false;
        }

        count = (int)(byteLength / stride);
        return true;
    }

    private static StdWStringReadStatus ReadStdWString(
        IProcessMemory memory,
        nint address,
        AtlasLayoutProfile profile,
        out string? value)
    {
        value = null;
        if (!memory.TryReadInt32(
                address + profile.Game.StdWString.LengthOffset,
                out var length))
        {
            return StdWStringReadStatus.Failed;
        }

        if (length == 0)
        {
            return StdWStringReadStatus.Empty;
        }

        if (length < 0 || length > profile.Game.Player.MaximumNameLength)
        {
            return StdWStringReadStatus.Failed;
        }

        var characters = address;
        if (length >= profile.Game.StdWString.InlineCapacity
            && !TryReadCanonicalPointer(memory, address, out characters))
        {
            return StdWStringReadStatus.Failed;
        }

        return memory.TryReadUtf16(characters, length + 1, out value)
               && value is not null
               && value.Length == length
            ? StdWStringReadStatus.Success
            : StdWStringReadStatus.Failed;
    }

    private static bool TryReadAscii(
        IProcessMemory memory,
        nint address,
        int maximumBytes,
        out string value)
    {
        var bytes = new byte[maximumBytes];
        if (!memory.TryRead(address, bytes))
        {
            value = string.Empty;
            return false;
        }

        var length = Array.IndexOf(bytes, (byte)0);
        if (length <= 0)
        {
            value = string.Empty;
            return false;
        }

        value = Encoding.ASCII.GetString(bytes, 0, length);
        return true;
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

    private static bool TryReadCanonicalPointer(
        IProcessMemory memory,
        nint address,
        out nint pointer)
    {
        if (!memory.TryReadPointer(address, out pointer))
        {
            return false;
        }

        return IsCanonicalPointer(pointer);
    }

    private static bool IsCanonicalPointer(nint pointer)
    {
        var raw = unchecked((ulong)pointer.ToInt64());
        return raw is >= MinimumCanonicalPointer and <= MaximumCanonicalPointer;
    }

    private static bool IsValidCharacterName(string? value, int maximumLength)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length <= maximumLength
           && value.All(character => !char.IsControl(character));

    private static GameProcessIdentity NotLoaded()
        => new(
            GameProcessIdentityState.CharacterNotLoaded,
            null,
            "character-not-loaded");

    private static GameProcessIdentity Failed(string statusMessage)
        => new(GameProcessIdentityState.ReadFailed, null, statusMessage);

    private enum StdWStringReadStatus
    {
        Success,
        Empty,
        Failed
    }

    private sealed record GameStateReferenceCache(
        nint MainModuleBase,
        long MainModuleSize,
        nint SlotAddress);
}
