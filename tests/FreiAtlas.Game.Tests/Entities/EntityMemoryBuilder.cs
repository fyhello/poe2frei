using System.Numerics;
using FreiAtlas.Game.Tests.Memory;

namespace FreiAtlas.Game.Tests.Entities;

internal sealed class EntityMemoryBuilder
{
    private readonly SyntheticProcessMemory _memory;
    private long _nextAddress = 0x500000;

    public EntityMemoryBuilder(SyntheticProcessMemory memory)
    {
        _memory = memory;
    }

    public SyntheticProcessMemory Memory => _memory;

    public nint Allocate(int size = 0x400)
    {
        var address = (nint)_nextAddress;
        _nextAddress += Math.Max(size, 0x100) + 0x100;
        _memory.WriteBytes(address, new byte[size]);
        return address;
    }

    public nint WriteEntity(
        nint entityAddress,
        string metadata,
        params (string Name, nint Address)[] components)
    {
        _memory.WriteBytes(entityAddress, new byte[0x100]);
        var details = Allocate();
        var lookup = Allocate();
        var componentList = Allocate(Math.Max(components.Length * IntPtr.Size, 8));
        var entries = Allocate(Math.Max(components.Length * 0x10, 0x10));

        _memory.WritePointer(entityAddress + 0x08, details);
        _memory.WriteStdWString(details + 0x08, metadata);
        _memory.WritePointer(details + 0x28, lookup);

        _memory.WritePointer(entityAddress + 0x10, componentList);
        _memory.WritePointer(
            entityAddress + 0x18,
            componentList + (components.Length * IntPtr.Size));

        _memory.WritePointer(lookup + 0x28, entries);
        _memory.WritePointer(lookup + 0x30, entries + (components.Length * 0x10));

        for (var index = 0; index < components.Length; index++)
        {
            var name = Allocate(32);
            _memory.WriteUtf8(name, components[index].Name, 32);
            _memory.WritePointer(entries + (index * 0x10), name);
            _memory.WriteInt32(entries + (index * 0x10) + 0x08, index);
            _memory.WritePointer(
                componentList + (index * IntPtr.Size),
                components[index].Address);
        }

        return entityAddress;
    }

    public nint WriteRender(Vector3 position)
    {
        var component = Allocate(0x200);
        _memory.WriteVector3(component + 0x138, position);
        return component;
    }

    public nint WritePositioned(byte reaction)
    {
        var component = Allocate(0x220);
        _memory.WriteByte(component + 0x1E0, reaction);
        return component;
    }

    public nint WriteLife(int current, int maximum)
    {
        var component = Allocate(0x240);
        _memory.WriteInt32(component + 0x1B0 + 0x2C, maximum);
        _memory.WriteInt32(component + 0x1B0 + 0x30, current);
        return component;
    }

    public nint WriteMagicProperties(int rarity, params string[] modIds)
    {
        var component = Allocate(0x220);
        _memory.WriteInt32(component + 0x144, rarity);

        if (modIds.Length == 0)
        {
            _memory.WritePointer(component + 0x168, 0);
            _memory.WritePointer(component + 0x170, 0);
            return component;
        }

        var elements = Allocate(modIds.Length * 0x20);
        _memory.WritePointer(component + 0x168, elements);
        _memory.WritePointer(component + 0x170, elements + (modIds.Length * 0x20));
        for (var index = 0; index < modIds.Length; index++)
        {
            var record = Allocate(0x20);
            var id = Allocate(128);
            _memory.WriteUtf16Buffer(id, modIds[index], 64);
            _memory.WritePointer(elements + (index * 0x20) + 0x08, record);
            _memory.WritePointer(record, id);
        }

        return component;
    }

    public nint WriteMinimapIcon(int completedState)
    {
        var component = Allocate(0x40);
        _memory.WriteInt32(component + 0x10, completedState);
        return component;
    }

    public nint WriteChest(byte openState)
    {
        var component = Allocate(0x190);
        _memory.WriteByte(component + 0x168, openState);
        return component;
    }
}
