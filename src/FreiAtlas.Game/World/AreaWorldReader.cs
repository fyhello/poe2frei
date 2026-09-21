using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Memory;
using FreiAtlas.Game.Entities;
using FreiAtlas.Game.Memory;

namespace FreiAtlas.Game.World;

internal sealed record AreaWorldReadResult(
    AreaMapSnapshotStatus Status,
    AreaIdentity? Area,
    AreaPlayerSnapshot? Player,
    IReadOnlyList<AreaEntitySnapshot> Entities,
    IReadOnlyList<AreaReadDiagnostic> Diagnostics);

internal sealed class AreaWorldReader
{
    private readonly IProcessMemory _memory;
    private readonly Poe2MemoryProfile _profile;
    private readonly GameMemoryReader _reader;
    private readonly EntityComponentResolver _components;
    private readonly AreaEntityReader _entities;

    public AreaWorldReader(
        IProcessMemory memory,
        EntityComponentResolver components,
        AreaEntityReader entities,
        Poe2MemoryProfile? profile = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _components = components ?? throw new ArgumentNullException(nameof(components));
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        _profile = profile ?? Poe2MemoryProfile.Current;
        _reader = new GameMemoryReader(memory, _profile);
    }

    public AreaWorldReadResult Read(
        GameRootState root,
        long sessionSequence,
        IReadOnlyList<RawEntityRef> entityReferences)
    {
        ArgumentNullException.ThrowIfNull(entityReferences);
        if (!TryReadArea(root.AreaInstance, sessionSequence, out var area))
        {
            return new AreaWorldReadResult(
                AreaMapSnapshotStatus.Loading,
                null,
                null,
                [],
                [new AreaReadDiagnostic(
                    "area-identity-unavailable",
                    "The resolved AreaInstance did not contain a valid area identity.",
                    AreaDiagnosticSeverity.Error)]);
        }

        var entityResult = _entities.Read(sessionSequence, entityReferences);
        var diagnostics = entityResult.Diagnostics.ToList();
        AreaPlayerSnapshot? player = null;
        var playerReference = entityReferences.FirstOrDefault(
            entity => entity.EntityAddress == root.LocalPlayer);
        if (root.LocalPlayer == 0
            || !TryReadPlayer(
                playerReference.EntityAddress == 0
                    ? new RawEntityRef(0, root.LocalPlayer)
                    : playerReference,
                out player))
        {
            diagnostics.Add(new AreaReadDiagnostic(
                "player-read-failed",
                "The local player facts could not be read consistently.",
                AreaDiagnosticSeverity.Warning));
        }

        return new AreaWorldReadResult(
            diagnostics.Any(diagnostic =>
                diagnostic.Severity is AreaDiagnosticSeverity.Warning
                    or AreaDiagnosticSeverity.Error)
                ? AreaMapSnapshotStatus.Degraded
                : AreaMapSnapshotStatus.Stable,
            area,
            player,
            entityResult.Entities,
            diagnostics);
    }

    public AreaPlayerSnapshot? ReadPlayer(
        GameRootState root,
        long sessionSequence,
        out AreaReadDiagnostic? diagnostic)
    {
        diagnostic = null;
        _components.BeginSample(sessionSequence);
        if (root.LocalPlayer != 0
            && TryReadPlayer(
                new RawEntityRef(0, root.LocalPlayer),
                out var player))
        {
            return player;
        }

        diagnostic = new AreaReadDiagnostic(
            "player-read-failed",
            "The local player facts could not be read consistently.",
            AreaDiagnosticSeverity.Warning);
        return null;
    }

    private bool TryReadArea(
        nint areaInstance,
        long sessionSequence,
        out AreaIdentity area)
    {
        area = default!;
        if (!_reader.TryReadPointer(
                areaInstance + _profile.AreaInstance.AreaInfoOffset,
                out var areaInfo)
            || !_reader.TryReadPointer(areaInfo, out var areaCodeAddress)
            || !_memory.TryReadUtf16(
                areaCodeAddress,
                64,
                out var areaCode)
            || string.IsNullOrWhiteSpace(areaCode)
            || !_memory.TryReadInt32(
                areaInstance + _profile.AreaInstance.AreaLevelOffset,
                out var areaLevel)
            || areaLevel <= 0
            || !_reader.TryReadUInt32(
                areaInstance + _profile.AreaInstance.AreaHashOffset,
                out var areaHash))
        {
            return false;
        }

        area = new AreaIdentity(
            areaHash,
            areaCode,
            areaLevel,
            sessionSequence);
        return true;
    }

    private bool TryReadPlayer(
        RawEntityRef playerReference,
        out AreaPlayerSnapshot player)
    {
        player = default!;
        if (!_components.TryResolve(
                playerReference,
                "Player",
                out var playerComponent)
            || playerComponent == 0
            || !_reader.TryReadStdWString(
                playerComponent + _profile.Player.NameOffset,
                _profile.Player.MaximumNameLength,
                out var characterName)
            || string.IsNullOrWhiteSpace(characterName)
            || !_reader.TryReadByte(
                playerComponent + _profile.Player.LevelOffset,
                out var level)
            || level == 0
            || !_components.TryResolve(
                playerReference,
                "Render",
                out var render)
            || render == 0
            || !_reader.TryReadVector3(
                render + _profile.Render.WorldPositionOffset,
                out var worldPosition))
        {
            return false;
        }

        player = new AreaPlayerSnapshot(
            characterName,
            level,
            worldPosition,
            new Vector2(
                worldPosition.X / _profile.WorldToGridRatio,
                worldPosition.Y / _profile.WorldToGridRatio));
        return true;
    }
}
