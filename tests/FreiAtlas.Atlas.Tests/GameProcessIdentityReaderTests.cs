using System.Text;
using FreiAtlas.Atlas.Memory;
using FreiAtlas.Core.Memory;

namespace FreiAtlas.Atlas.Tests;

public sealed class GameProcessIdentityReaderTests
{
    [Fact]
    public void Read_WithLoadedPlayer_ReturnsCharacterName()
    {
        var memory = IdentityMemory.CreateLoaded("逐风者");

        var identity = new GameProcessIdentityReader().Read(
            memory,
            AtlasLayoutProfile.Default);

        Assert.Equal(GameProcessIdentityState.InGame, identity.State);
        Assert.Equal("逐风者", identity.CharacterName);
        Assert.Null(identity.StatusMessage);
    }

    [Fact]
    public void Read_WithNoLocalPlayer_ReturnsCharacterNotLoaded()
    {
        var memory = IdentityMemory.CreateLoaded("逐风者");
        memory.WritePointer(
            IdentityMemory.AreaInstance
            + AtlasLayoutProfile.Default.Game.AreaInstance.LocalPlayerOffset,
            0);

        var identity = new GameProcessIdentityReader().Read(
            memory,
            AtlasLayoutProfile.Default);

        Assert.Equal(GameProcessIdentityState.CharacterNotLoaded, identity.State);
        Assert.Null(identity.CharacterName);
    }

    [Fact]
    public void Read_WithCorruptComponentVector_ReturnsReadFailedWithoutAddresses()
    {
        var memory = IdentityMemory.CreateLoaded("逐风者");
        memory.WritePointer(
            IdentityMemory.LocalPlayer
            + AtlasLayoutProfile.Default.Game.Entity.ComponentsOffset
            + sizeof(long),
            new IntPtr(0x7FFF_FFFF_0000L));

        var identity = new GameProcessIdentityReader().Read(
            memory,
            AtlasLayoutProfile.Default);

        Assert.Equal(GameProcessIdentityState.ReadFailed, identity.State);
        Assert.Null(identity.CharacterName);
        Assert.DoesNotContain("0x", identity.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_WithNoGameStatePattern_ReturnsReadFailed()
    {
        var memory = new IdentityMemory();

        var identity = new GameProcessIdentityReader().Read(
            memory,
            AtlasLayoutProfile.Default);

        Assert.Equal(GameProcessIdentityState.ReadFailed, identity.State);
        Assert.Null(identity.CharacterName);
    }

    [Fact]
    public void Read_WithHeapCharacterName_ReturnsFullName()
    {
        const string characterName = "逐风者远征队长";
        var memory = IdentityMemory.CreateLoaded(characterName);

        var identity = new GameProcessIdentityReader().Read(
            memory,
            AtlasLayoutProfile.Default);

        Assert.Equal(GameProcessIdentityState.InGame, identity.State);
        Assert.Equal(characterName, identity.CharacterName);
    }

    [Fact]
    public void Read_WithCorruptHeapNamePointer_ReturnsReadFailed()
    {
        var memory = IdentityMemory.CreateLoaded("逐风者远征队长");
        memory.WritePointer(
            IdentityMemory.PlayerComponent
            + AtlasLayoutProfile.Default.Game.Player.NameOffset,
            (nint)1);

        var identity = new GameProcessIdentityReader().Read(
            memory,
            AtlasLayoutProfile.Default);

        Assert.Equal(GameProcessIdentityState.ReadFailed, identity.State);
        Assert.DoesNotContain(
            "0x",
            identity.StatusMessage,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_WithOverlongCharacterNameLength_ReturnsReadFailed()
    {
        var memory = IdentityMemory.CreateLoaded("逐风者");
        memory.SetCharacterNameLength(
            AtlasLayoutProfile.Default.Game.Player.MaximumNameLength + 1);

        var identity = new GameProcessIdentityReader().Read(
            memory,
            AtlasLayoutProfile.Default);

        Assert.Equal(GameProcessIdentityState.ReadFailed, identity.State);
    }

    [Fact]
    public void Read_WithControlCharacterInName_ReturnsReadFailed()
    {
        var memory = IdentityMemory.CreateLoaded("逐\u0001风者");

        var identity = new GameProcessIdentityReader().Read(
            memory,
            AtlasLayoutProfile.Default);

        Assert.Equal(GameProcessIdentityState.ReadFailed, identity.State);
    }

    [Fact]
    public void Read_ReusesLocatedGameStateSlotForSameProcessModule()
    {
        var memory = IdentityMemory.CreateLoaded("逐风者");
        var reader = new GameProcessIdentityReader();

        Assert.Equal(GameProcessIdentityState.InGame,
            reader.Read(memory, AtlasLayoutProfile.Default).State);
        Assert.Equal(GameProcessIdentityState.InGame,
            reader.Read(memory, AtlasLayoutProfile.Default).State);

        Assert.Equal(1, memory.RegionEnumerationCount);
    }

    [Fact]
    public void Read_WhenCachedSlotIsInvalid_RescansAndRecovers()
    {
        var memory = IdentityMemory.CreateLoaded("逐风者");
        var reader = new GameProcessIdentityReader();
        Assert.Equal(GameProcessIdentityState.InGame,
            reader.Read(memory, AtlasLayoutProfile.Default).State);
        memory.MoveGameStateReference();

        var identity = reader.Read(memory, AtlasLayoutProfile.Default);

        Assert.Equal(GameProcessIdentityState.InGame, identity.State);
        Assert.Equal("逐风者", identity.CharacterName);
        Assert.Equal(2, memory.RegionEnumerationCount);
    }

    private sealed class IdentityMemory : IProcessMemory, IProcessMemoryLayout
    {
        public static readonly nint AreaInstance = (nint)0x330000;
        public static readonly nint LocalPlayer = (nint)0x340000;
        public static readonly nint PlayerComponent = (nint)0x3A0000;

        private static readonly nint PatternAddress = (nint)0x100100;
        private static readonly nint SlotAddress = (nint)0x200000;
        private static readonly nint GameState = (nint)0x300000;
        private static readonly nint HeapCharacterName = (nint)0x3B0000;

        private readonly Dictionary<long, byte> _bytes = [];

        public IdentityMemory()
        {
            WriteBytes(MainModuleBase, new byte[(int)MainModuleSize]);
        }

        public int ProcessId => 7928;

        public int RegionEnumerationCount { get; private set; }

        public nint MainModuleBase => (nint)0x100000;

        public long MainModuleSize => 0x1000;

        public static IdentityMemory CreateLoaded(string characterName)
        {
            var profile = AtlasLayoutProfile.Default;
            var memory = new IdentityMemory();
            var currentVector = (nint)0x310000;
            var inGameState = (nint)0x320000;
            var details = (nint)0x350000;
            var componentList = (nint)0x360000;
            var lookup = (nint)0x370000;
            var lookupEntry = (nint)0x380000;
            var componentName = (nint)0x390000;
            memory.WriteGameStateReference(PatternAddress, SlotAddress);
            memory.WritePointer(
                GameState + profile.Game.GameState.CurrentStateVectorOffset,
                currentVector);
            memory.WritePointer(currentVector, inGameState);
            memory.WritePointer(
                inGameState + profile.Game.InGameState.AreaInstanceOffset,
                AreaInstance);
            memory.WritePointer(
                AreaInstance + profile.Game.AreaInstance.LocalPlayerOffset,
                LocalPlayer);
            memory.WritePointer(
                LocalPlayer + profile.Game.Entity.DetailsOffset,
                details);
            memory.WritePointer(
                LocalPlayer + profile.Game.Entity.ComponentsOffset,
                componentList);
            memory.WritePointer(
                LocalPlayer + profile.Game.Entity.ComponentsOffset + sizeof(long),
                componentList + sizeof(long));
            memory.WritePointer(
                details + profile.Game.EntityDetails.ComponentLookupOffset,
                lookup);
            memory.WritePointer(
                lookup + profile.Game.ComponentLookup.BucketOffset,
                lookupEntry);
            memory.WritePointer(
                lookup + profile.Game.ComponentLookup.BucketOffset + sizeof(long),
                lookupEntry + profile.Game.ComponentLookup.EntryStride);
            memory.WritePointer(lookupEntry, componentName);
            memory.WriteInt32(lookupEntry + sizeof(long), 0);
            memory.WriteAscii(componentName, "Player");
            memory.WritePointer(componentList, PlayerComponent);
            memory.WriteStdWString(
                PlayerComponent + profile.Game.Player.NameOffset,
                characterName,
                profile);
            return memory;
        }

        public IEnumerable<ProcessMemoryRegion> EnumerateMainModuleRegions()
        {
            RegionEnumerationCount++;
            yield return new ProcessMemoryRegion(
                MainModuleBase,
                MainModuleSize,
                IsReadable: true,
                IsExecutable: true,
                IsImage: true);
        }

        public bool TryRead(nint address, Span<byte> destination)
        {
            for (var index = 0; index < destination.Length; index++)
            {
                if (!_bytes.TryGetValue(address.ToInt64() + index, out var value))
                {
                    destination.Clear();
                    return false;
                }

                destination[index] = value;
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
            if (!TryReadInt64(address, out var raw))
            {
                value = default;
                return false;
            }

            value = (nint)raw;
            return true;
        }

        public bool TryReadUtf16(nint address, int maxChars, out string? value)
        {
            var bytes = new List<byte>(maxChars * sizeof(char));
            for (var index = 0; index < maxChars; index++)
            {
                if (!_bytes.TryGetValue(address.ToInt64() + index * 2, out var low)
                    || !_bytes.TryGetValue(address.ToInt64() + index * 2 + 1, out var high))
                {
                    value = null;
                    return false;
                }

                if (low == 0 && high == 0)
                {
                    value = Encoding.Unicode.GetString(bytes.ToArray());
                    return true;
                }

                bytes.Add(low);
                bytes.Add(high);
            }

            value = null;
            return false;
        }

        public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
        {
            for (var index = 0; index < bytes.Length; index++)
            {
                _bytes[address.ToInt64() + index] = bytes[index];
            }
        }

        public void WritePointer(nint address, nint value)
            => WriteBytes(address, BitConverter.GetBytes(value.ToInt64()));

        public void SetCharacterNameLength(int length)
            => WriteInt32(
                PlayerComponent
                + AtlasLayoutProfile.Default.Game.Player.NameOffset
                + AtlasLayoutProfile.Default.Game.StdWString.LengthOffset,
                length);

        public void MoveGameStateReference()
        {
            WritePointer(SlotAddress, 0);
            WriteGameStateReference((nint)0x100300, (nint)0x200100);
        }

        private void WriteInt32(nint address, int value)
            => WriteBytes(address, BitConverter.GetBytes(value));

        private void WriteAscii(nint address, string value)
        {
            var bytes = new byte[32];
            Encoding.ASCII.GetBytes(value).CopyTo(bytes, 0);
            WriteBytes(address, bytes);
        }

        private void WriteGameStateReference(
            nint patternAddress,
            nint slotAddress)
        {
            var profile = AtlasLayoutProfile.Default;
            var pattern = profile.Game.GameStateReference.Pattern
                .Select(value => value ?? 0)
                .ToArray();
            var displacement = checked((int)(
                slotAddress.ToInt64()
                - (patternAddress.ToInt64()
                   + profile.Game.GameStateReference.InstructionLength)));
            BitConverter.GetBytes(displacement).CopyTo(
                pattern,
                profile.Game.GameStateReference.DisplacementOffset);
            WriteBytes(patternAddress, pattern);
            WritePointer(slotAddress, GameState);
        }

        private void WriteStdWString(
            nint address,
            string value,
            AtlasLayoutProfile profile)
        {
            if (value.Length >= profile.Game.StdWString.InlineCapacity)
            {
                WritePointer(address, HeapCharacterName);
                WriteBytes(
                    HeapCharacterName,
                    Encoding.Unicode.GetBytes(value + "\0"));
            }
            else
            {
                WriteBytes(address, Encoding.Unicode.GetBytes(value + "\0"));
            }

            WriteInt32(address + profile.Game.StdWString.LengthOffset, value.Length);
        }

        public void Dispose()
        {
        }
    }
}
