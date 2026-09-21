using System.Buffers.Binary;
using FreiAtlas.Core.Memory;
using FreiAtlas.Core.Player;
using FreiAtlas.Game.Entities;

namespace FreiAtlas.Game.Memory;

public sealed class PlayerVitalsReader
{
    private readonly IProcessMemory _memory;
    private readonly Poe2MemoryProfile _profile;
    private readonly GameMemorySession _session;
    private readonly EntityComponentResolver _components;
    private readonly TimeProvider _clock;

    public PlayerVitalsReader(IProcessMemory memory, Poe2MemoryProfile? profile = null, TimeProvider? clock = null)
    {
        _memory = memory;
        _profile = profile ?? Poe2MemoryProfile.Current;
        _clock = clock ?? TimeProvider.System;
        _session = new GameMemorySession(memory, _profile, new GameStateLocator(_profile));
        _components = new EntityComponentResolver(memory, _profile);
    }

    public PlayerVitalsSnapshot Read()
    {
        var capturedAt = _clock.GetUtcNow();
        if (!_session.TryRefresh(out var root, out var area, out var diagnostics))
            return Unavailable("角色数据不可用：" + string.Join(", ", diagnostics.Select(item => item.Code)));

        _components.BeginSample(area.SessionSequence);
        if (!_components.TryResolve(new RawEntityRef(0, root.LocalPlayer), "Life", out var life)
            || life == 0
            || !_memory.TryReadPointer(life + _profile.Life.OwnerOffset, out var owner)
            || owner != root.LocalPlayer)
            return Unavailable("角色生命组件不可用");

        var health = ReadPool(life + _profile.Life.HealthOffset);
        var mana = ReadPool(life + _profile.Life.ManaOffset);
        var shield = ReadPool(life + _profile.Life.EnergyShieldOffset);
        if (health is null || health.Maximum <= 0)
            return Unavailable("生命读数无效");

        // 采样跨越切区或角色替换时，整帧失效，消费者不能继续使用旧角色的数据。
        if (!_memory.TryReadPointer(root.InGameState + _profile.InGameState.AreaInstanceOffset, out var currentArea)
            || currentArea != root.AreaInstance
            || !_memory.TryReadPointer(currentArea + _profile.AreaInstance.LocalPlayerOffset, out var currentPlayer)
            || currentPlayer != root.LocalPlayer
            || !_memory.TryReadInt32(currentArea + _profile.AreaInstance.AreaHashOffset, out var hash)
            || unchecked((uint)hash) != area.AreaHash)
            return Unavailable("区域切换中");

        return new PlayerVitalsSnapshot(_memory.ProcessId, area.SessionSequence, capturedAt, health, mana, shield, null);
    }

    private VitalPool? ReadPool(nint address)
    {
        var layout = _profile.Vital;
        var length = new[] { layout.CurrentOffset, layout.MaximumOffset, layout.ReservedFlatOffset, layout.ReservedFractionOffset }.Max() + sizeof(int);
        if (length is < 4 or > 256) return null;
        Span<byte> bytes = stackalloc byte[length];
        if (!_memory.TryRead(address, bytes)) return null;
        var pool = new VitalPool(
            BinaryPrimitives.ReadInt32LittleEndian(bytes[layout.CurrentOffset..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[layout.MaximumOffset..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[layout.ReservedFlatOffset..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[layout.ReservedFractionOffset..]));
        return pool.Maximum is >= 0 and <= 10_000_000
               && pool.Current is >= 0 and <= 10_000_000
               && pool.Current <= pool.Maximum
               && (pool.Maximum > 0 || pool.Current == 0)
               && pool.ReservedFlat >= 0 && pool.ReservedFlat <= pool.Maximum
               && pool.ReservedFraction is >= 0 and <= 10000
               && pool.AvailableMaximum >= 0
            ? pool : null;
    }

    private PlayerVitalsSnapshot Unavailable(string reason)
    {
        _components.Reset();
        return PlayerVitalsSnapshot.Unavailable(_memory.ProcessId, _clock.GetUtcNow(), reason);
    }
}
