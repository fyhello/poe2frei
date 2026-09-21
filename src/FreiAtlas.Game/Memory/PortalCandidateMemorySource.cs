using System.Numerics;
using FreiAtlas.Core.Contracts;
using FreiAtlas.Core.PortalSqueeze;
using FreiAtlas.Core.Memory;
using FreiAtlas.Game.Entities;

namespace FreiAtlas.Game.Memory;

/// <summary>
/// 读取当前区域内可直接交互的传送门候选。实体地址只在内存读取与交互链路内部使用。
/// </summary>
public sealed class PortalCandidateMemorySource : IPortalCandidateSource
{
    private readonly IProcessMemory _memory;
    private readonly Poe2MemoryProfile _profile;
    private readonly GameMemorySession _session;
    private readonly AreaEntityTreeReader _entityTree;
    private readonly EntityComponentResolver _components;
    private readonly GameMemoryReader _reader;

    public PortalCandidateMemorySource(
        IProcessMemory memory,
        Poe2MemoryProfile? profile = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _profile = profile ?? Poe2MemoryProfile.Current;
        _session = new GameMemorySession(
            memory,
            _profile,
            new GameStateLocator(_profile));
        _entityTree = new AreaEntityTreeReader(memory, _profile);
        _components = new EntityComponentResolver(memory, _profile);
        _reader = new GameMemoryReader(memory, _profile);
    }

    public PortalCandidateScan Scan(float maxDistanceGrid)
    {
        if (!float.IsFinite(maxDistanceGrid) || maxDistanceGrid <= 0f)
        {
            return Failure("挤门距离无效");
        }

        if (!_session.TryRefresh(out var root, out var area, out var diagnostics))
        {
            return Failure(string.Join("；", diagnostics.Select(item => item.Message)));
        }

        _components.BeginSample(area.SessionSequence);
        if (!_components.TryResolve(
                new RawEntityRef(0, root.LocalPlayer),
                "Render",
                out var playerRender)
            || playerRender == 0
            || !_reader.TryReadVector3(
                playerRender + _profile.Render.WorldPositionOffset,
                out var playerWorld))
        {
            return Failure("无法读取角色位置");
        }

        var playerGrid = new Vector2(
            playerWorld.X / _profile.WorldToGridRatio,
            playerWorld.Y / _profile.WorldToGridRatio);
        if (!IsFinite(playerGrid))
        {
            return Failure("角色位置无效");
        }

        if (!_entityTree.TryRead(
                root.AreaInstance,
                out var rawEntities,
                out var treeDiagnostic))
        {
            return Failure(treeDiagnostic?.Message ?? "实体树读取失败");
        }

        var candidates = new List<PortalCandidate>();
        foreach (var entity in rawEntities)
        {
            if (!TryReadMetadata(entity, out var metadata)
                || !IsDirectPortal(metadata)
                || !_components.TryResolve(entity, "Render", out var render)
                || render == 0
                || !_reader.TryReadVector3(
                    render + _profile.Render.WorldPositionOffset,
                    out var world))
            {
                continue;
            }

            var grid = new Vector2(
                world.X / _profile.WorldToGridRatio,
                world.Y / _profile.WorldToGridRatio);
            if (!IsFinite(grid))
            {
                continue;
            }

            var distance = Vector2.Distance(playerGrid, grid);
            if (!float.IsFinite(distance) || distance > maxDistanceGrid)
            {
                continue;
            }

            candidates.Add(new PortalCandidate(
                entity.EntityId,
                entity.EntityAddress,
                metadata,
                grid,
                distance));
        }

        return new PortalCandidateScan(
            _memory.ProcessId,
            area.SessionSequence,
            root.GameStateSlot,
            root.InGameState,
            root.AreaInstance,
            area.AreaHash,
            playerGrid,
            candidates
                .OrderBy(candidate => candidate.DistanceToPlayer)
                .ThenBy(candidate => candidate.EntityId)
                .ToArray(),
            null);
    }

    private bool TryReadMetadata(RawEntityRef entity, out string metadata)
    {
        metadata = string.Empty;
        if (!_reader.TryReadPointer(
                entity.EntityAddress + _profile.Entity.DetailsOffset,
                out var details)
            || !_reader.TryReadStdWString(
                details + _profile.EntityDetails.NameOffset,
                1024,
                out metadata))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(metadata);
    }

    private static bool IsDirectPortal(string metadata)
        => metadata.Contains("MultiplexPortal", StringComparison.OrdinalIgnoreCase)
           || metadata.Contains("TownPortal", StringComparison.OrdinalIgnoreCase);

    private static bool IsFinite(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y);

    public void Dispose()
        => _memory.Dispose();

    private PortalCandidateScan Failure(string message)
        => new(
            _memory.ProcessId,
            _session.SessionSequence,
            0,
            0,
            0,
            0,
            Vector2.Zero,
            [],
            string.IsNullOrWhiteSpace(message) ? "挤门候选读取失败" : message);
}
