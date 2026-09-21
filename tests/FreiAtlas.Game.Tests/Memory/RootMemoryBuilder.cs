using System.Numerics;
using FreiAtlas.Core.Memory;

namespace FreiAtlas.Game.Tests.Memory;

internal sealed class RootMemoryBuilder : IDisposable
{
    public static readonly nint PatternAddress = 0x100300;
    public static readonly nint SlotAddress = 0x110000;
    public static readonly nint GameStateAddress = 0x200000;
    public static readonly nint CurrentVectorAddress = 0x210000;
    public static readonly nint InGameStateAddress = 0x300000;
    public static readonly nint AreaAddress = 0x400000;
    public static readonly nint PlayerAddress = 0x500000;
    public static readonly nint RenderComponentAddress = 0x540000;
    public static readonly nint PlayerComponentAddress = 0x541000;

    private readonly Poe2MemoryProfile _profile = Poe2MemoryProfile.Current;
    private RootMemoryBuilder(
        bool currentStateValid,
        int? validFallbackSlot,
        int processId)
    {
        if (validFallbackSlot is < 0 or >= 12)
        {
            throw new ArgumentOutOfRangeException(nameof(validFallbackSlot));
        }

        Memory = new SyntheticProcessMemory(processId);
        WriteAobReference(PatternAddress, SlotAddress);
        Memory.WritePointer(SlotAddress, GameStateAddress);
        Memory.WritePointer(
            GameStateAddress + _profile.GameState.CurrentStateVectorOffset,
            CurrentVectorAddress);
        Memory.WritePointer(
            CurrentVectorAddress,
            currentStateValid ? InGameStateAddress : 0x310000);
        if (validFallbackSlot is { } slot)
        {
            Memory.WritePointer(
                GameStateAddress
                + _profile.GameState.StateSlotsOffset
                + (slot * _profile.GameState.StateSlotStride),
                InGameStateAddress);
        }

        WriteArea(AreaAddress, 0x12345678, "MapCurrent");
        WritePlayer();
    }

    public SyntheticProcessMemory Memory { get; }

    public static RootMemoryBuilder Create(
        bool currentStateValid = true,
        int? validFallbackSlot = null,
        int processId = 7928)
        => new(currentStateValid, validFallbackSlot, processId);

    public void AddInvalidAobReference(nint patternAddress, nint slotAddress)
    {
        WriteAobReference(patternAddress, slotAddress);
        Memory.WritePointer(slotAddress, 0x280000);
    }

    public void ChangeArea(nint areaAddress, uint hash, string areaCode)
    {
        WriteArea(areaAddress, hash, areaCode);
        Memory.WritePointer(
            InGameStateAddress + _profile.InGameState.AreaInstanceOffset,
            areaAddress);
    }

    public void ChangeAreaHash(uint hash)
        => Memory.WriteUInt32(
            AreaAddress + _profile.AreaInstance.AreaHashOffset,
            hash);

    public void InvalidateGameStateSlot()
        => Memory.WritePointer(SlotAddress, 0);

    public void Dispose() => Memory.Dispose();

    private void WriteAobReference(nint patternAddress, nint slotAddress)
    {
        var pattern = _profile.GameStateReference.Pattern
            .Select(value => value ?? (byte)0)
            .ToArray();
        var displacement = checked(
            (int)(slotAddress.ToInt64()
                  - patternAddress.ToInt64()
                  - _profile.GameStateReference.InstructionLength));
        BitConverter.GetBytes(displacement).CopyTo(
            pattern,
            _profile.GameStateReference.DisplacementOffset);
        Memory.WriteBytes(patternAddress, pattern);
    }

    private void WriteArea(nint areaAddress, uint hash, string areaCode)
    {
        var areaInfo = areaAddress + 0x1000;
        var areaCodeBuffer = areaAddress + 0x2000;
        Memory.WritePointer(
            InGameStateAddress + _profile.InGameState.AreaInstanceOffset,
            areaAddress);
        Memory.WritePointer(
            areaAddress + _profile.AreaInstance.LocalPlayerOffset,
            PlayerAddress);
        Memory.WritePointer(
            areaAddress + _profile.AreaInstance.AreaInfoOffset,
            areaInfo);
        Memory.WritePointer(areaInfo, areaCodeBuffer);
        Memory.WriteUtf16Buffer(areaCodeBuffer, areaCode, 64);
        Memory.WriteInt32(
            areaAddress + _profile.AreaInstance.AreaLevelOffset,
            81);
        Memory.WriteUInt32(
            areaAddress + _profile.AreaInstance.AreaHashOffset,
            hash);
    }

    private void WritePlayer()
    {
        nint details = 0x510000;
        nint lookup = 0x520000;
        nint componentList = 0x530000;
        nint componentBucket = 0x550000;
        nint renderName = 0x560000;
        nint playerName = 0x560100;

        Memory.WritePointer(
            PlayerAddress + _profile.Entity.DetailsOffset,
            details);
        Memory.WriteStdWString(
            details + _profile.EntityDetails.NameOffset,
            "Metadata/Characters/Player/TestCharacter");
        Memory.WritePointer(
            details + _profile.EntityDetails.ComponentLookupOffset,
            lookup);

        Memory.WritePointer(
            PlayerAddress + _profile.Entity.ComponentsOffset,
            componentList);
        Memory.WritePointer(
            PlayerAddress + _profile.Entity.ComponentsOffset + IntPtr.Size,
            componentList + (2 * IntPtr.Size));
        Memory.WritePointer(componentList, RenderComponentAddress);
        Memory.WritePointer(componentList + IntPtr.Size, PlayerComponentAddress);

        Memory.WritePointer(
            lookup + _profile.ComponentLookup.BucketOffset,
            componentBucket);
        Memory.WritePointer(
            lookup + _profile.ComponentLookup.BucketOffset + IntPtr.Size,
            componentBucket + (2 * _profile.ComponentLookup.EntryStride));
        Memory.WritePointer(componentBucket, renderName);
        Memory.WriteInt32(componentBucket + IntPtr.Size, 0);
        Memory.WritePointer(
            componentBucket + _profile.ComponentLookup.EntryStride,
            playerName);
        Memory.WriteInt32(
            componentBucket
            + _profile.ComponentLookup.EntryStride
            + IntPtr.Size,
            1);
        Memory.WriteUtf8(renderName, "Render", 32);
        Memory.WriteUtf8(playerName, "Player", 32);

        Memory.WriteVector3(
            RenderComponentAddress + _profile.Render.WorldPositionOffset,
            new Vector3(1200f, 2400f, 15f));
        Memory.WriteStdWString(
            PlayerComponentAddress + _profile.Player.NameOffset,
            "逐风者");
        Memory.WriteByte(
            PlayerComponentAddress + _profile.Player.LevelOffset,
            91);
    }
}
