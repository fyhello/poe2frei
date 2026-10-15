using System.Numerics;
using FreiAtlas.Core.Memory;
using FreiAtlas.Game.Memory;
using FreiAtlas.Game.Tests.Entities;
using FreiAtlas.Game.Tests.Memory;

namespace FreiAtlas.Game.Tests.Memory;

public sealed class GroundItemResearchProbeTests
{
    [Fact]
    public void Capture_ReturnsNamedWorldItemsWithGridPositionAndSkipsOtherEntities()
    {
        using var fixture = RootMemoryBuilder.Create();
        var profile = Poe2MemoryProfile.Current;
        WriteEntityTree(fixture.Memory, profile,
        [
            (11u, (nint)0x610000, "Metadata/MiscellaneousObjects/WorldItem", true, "崇高石", "CurrencyAddModToRare", new Vector3(1200, 2400, 0)),
            (12u, (nint)0x620000, "Metadata/MiscellaneousObjects/WorldItem", true, "完美工匠石", "CurrencyRerollSocketNumbers03", new Vector3(1450, 2400, 0)),
            (13u, (nint)0x630000, "Metadata/Monsters/Test/Monster", false, "", "", new Vector3(1300, 2400, 0))
        ]);

        var result = new GroundItemResearchProbe(fixture.Memory).Capture();

        Assert.Equal("MapCurrent", result.Area?.AreaCode);
        Assert.Equal(new Vector2(1200f / profile.WorldToGridRatio, 2400f / profile.WorldToGridRatio), result.PlayerGridPosition);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal([11u, 12u], result.Candidates.Select(item => item.EntityId));
        Assert.Equal("崇高石", result.Candidates[0].ItemName);
        Assert.Equal("CurrencyAddModToRare", result.Candidates[0].ArtName);
        Assert.Equal("完美工匠石", result.Candidates[1].ItemName);
        Assert.Equal("CurrencyRerollSocketNumbers03", result.Candidates[1].ArtName);
        Assert.True(result.Candidates.All(item => item.ItemEntityReady));
        Assert.Equal(250f / profile.WorldToGridRatio, result.Candidates[1].DistanceToPlayer, 3);
    }

    [Fact]
    public void Capture_SkipsWorldItemWithoutValidRenderPosition()
    {
        using var fixture = RootMemoryBuilder.Create();
        var profile = Poe2MemoryProfile.Current;
        WriteEntityTree(fixture.Memory, profile,
        [
            (21u, (nint)0x640000, "Metadata/MiscellaneousObjects/WorldItem", true, "崇高石", "CurrencyAddModToRare", new Vector3(float.NaN, 0, 0))
        ]);

        var result = new GroundItemResearchProbe(fixture.Memory).Capture();

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void CaptureHover_DefaultProfileReadsConfiguredHoverPointers()
    {
        using var fixture = RootMemoryBuilder.Create();
        WriteEntityTree(fixture.Memory, Poe2MemoryProfile.Current,
        [
            (30u, (nint)0x648000, "Metadata/MiscellaneousObjects/WorldItem", true, "崇高石", "CurrencyAddModToRare", new Vector3(1200, 2400, 0))
        ], setHover: true);

        var result = new GroundItemResearchProbe(fixture.Memory).CaptureHover();

        Assert.True(result.HasHoverData);
        Assert.Equal(30u, result.MatchedGroundItemEntityId);
        Assert.False(result.AmbiguousMatch);
    }

    [Fact]
    public void CaptureHover_MatchesCurrentWorldItemEntityFromHoverPointers()
    {
        using var fixture = RootMemoryBuilder.Create();
        var profile = Poe2MemoryProfile.Current;
        var verifiedProfile = profile with { HoverOffsetsVerified = true };
        WriteEntityTree(fixture.Memory, verifiedProfile,
        [
            (31u, (nint)0x650000, "Metadata/MiscellaneousObjects/WorldItem", true, "崇高石", "CurrencyAddModToRare", new Vector3(1200, 2400, 0))
        ], setHover: true);

        Assert.True(fixture.Memory.TryReadPointer(RootMemoryBuilder.InGameStateAddress + profile.Hover.MouseOverHostFromInGameStateOffset, out var writtenHost));
        Assert.Equal((nint)0x700000, writtenHost);

        var result = new GroundItemResearchProbe(fixture.Memory, verifiedProfile).CaptureHover();
        Assert.True(result.HasHoverData);
        Assert.Equal(31u, result.MatchedGroundItemEntityId);
        Assert.False(result.AmbiguousMatch);
        Assert.Equal([31u], result.MatchedGroundItemEntityIds);
    }

    [Fact]
    public void CaptureHover_ReportsNoMatchWhenHoverPointersAreEmpty()
    {
        using var fixture = RootMemoryBuilder.Create();
        var profile = Poe2MemoryProfile.Current;
        WriteEntityTree(fixture.Memory, profile,
        [
            (32u, (nint)0x660000, "Metadata/MiscellaneousObjects/WorldItem", true, "崇高石", "CurrencyAddModToRare", new Vector3(1200, 2400, 0))
        ]);

        var result = new GroundItemResearchProbe(fixture.Memory).CaptureHover();

        Assert.False(result.HasHoverData);
        Assert.Null(result.MatchedGroundItemEntityId);
        Assert.Empty(result.MatchedGroundItemEntityIds);
    }

    private static void WriteEntityTree(
        SyntheticProcessMemory memory,
        Poe2MemoryProfile profile,
        (uint Id, nint Address, string Metadata, bool IsWorldItem, string Name, string Art, Vector3 Position)[] entities,
        bool setHover = false)
    {
        var head = (nint)0x600000;
        memory.WriteBytes(head, new byte[0x30]);
        var nodes = Enumerable.Range(0, entities.Length)
            .Select(index => (nint)(0x601000 + index * 0x1000))
            .ToArray();
        memory.WritePointer(RootMemoryBuilder.AreaAddress + profile.AreaInstance.AwakeEntitiesOffset, head);
        memory.WriteInt32(RootMemoryBuilder.AreaAddress + profile.AreaInstance.AwakeEntitiesOffset + IntPtr.Size, entities.Length);
        memory.WritePointer(head + profile.StdMapNode.ParentOffset, nodes[0]);

        for (var index = 0; index < entities.Length; index++)
        {
            var node = nodes[index];
            memory.WriteBytes(node, new byte[0x30]);
            memory.WritePointer(node + profile.StdMapNode.LeftOffset, head);
            memory.WritePointer(node + profile.StdMapNode.RightOffset, index + 1 < nodes.Length ? nodes[index + 1] : head);
            memory.WriteByte(node + profile.StdMapNode.IsNilOffset, 0);
            memory.WriteUInt32(node + profile.StdMapNode.KeyIdOffset, entities[index].Id);
            memory.WritePointer(node + profile.StdMapNode.EntityOffset, entities[index].Address);
        }

        if (setHover)
        {
            var hover = profile.Hover;
            var host = (nint)0x700000;
            var sub = (nint)0x701000;
            var tracker = (nint)0x702000;
            var worldTracker = (nint)0x703000;
            var uiRoot = (nint)0x704000;
            memory.WritePointer(RootMemoryBuilder.InGameStateAddress + hover.MouseOverHostFromInGameStateOffset, host);
            memory.WritePointer(host + hover.MouseOverSubFromHostOffset, sub);
            memory.WritePointer(sub + hover.MouseOverEntityFromSubOffset, entities[0].Address);
            memory.WritePointer(RootMemoryBuilder.InGameStateAddress + profile.InGameState.UiRootOffset, uiRoot);
            memory.WritePointer(uiRoot + hover.HoverTrackerFromUiRootOffset, tracker);
            memory.WritePointer(tracker + hover.HoverTrackerWorldTrackerOffset, worldTracker);
            memory.WritePointer(worldTracker + hover.HoverTrackerHoveredEntityOffset, entities[0].Address);
        }

        var builder = new EntityMemoryBuilder(memory, 0x800000);
        foreach (var item in entities)
        {
            var render = builder.WriteRender(item.Position);
            var components = new List<(string Name, nint Address)> { ("Render", render) };
            if (item.IsWorldItem)
            {
                var worldItem = builder.Allocate(0x80);
                components.Add(("WorldItem", worldItem));
                var itemEntity = builder.Allocate(0x100);
                memory.WritePointer(worldItem + profile.GroundItem.WorldItemEntityOffset, itemEntity);
                var baseComponent = builder.Allocate(0x80);
                var nameRow = builder.Allocate(0x80);
                memory.WritePointer(baseComponent + profile.GroundItem.BaseNameRowOffset, nameRow);
                memory.WriteStdWString(nameRow + profile.GroundItem.NameRowDisplayNameOffset, item.Name);
                var itemComponents = new List<(string Name, nint Address)> { ("Base", baseComponent) };
                var renderItem = builder.Allocate(0x80);
                var path = builder.Allocate(256);
                memory.WritePointer(renderItem + profile.GroundItem.RenderItemResourcePathOffset, path);
                memory.WriteUtf16Buffer(path, "Art/2DItems/Currency/" + item.Art + ".dds", 128);
                itemComponents.Add(("RenderItem", renderItem));
                builder.WriteEntity(itemEntity, "Metadata/Items/Test", itemComponents.ToArray());
                memory.WritePointer(worldItem + profile.GroundItem.WorldItemEntityOffset, itemEntity);
            }
            builder.WriteEntity(item.Address, item.Metadata, components.ToArray());
        }
    }

    [Fact]
    public void RealGame_InspectItemNames()
    {
        var processes = System.Diagnostics.Process.GetProcessesByName("PathofExile");
        if (processes.Length == 0) return;
        var pid = processes[0].Id;
        if (!FreiAtlas.Platform.Windows.Process.NativeProcessMemory.TryOpen(pid, out var memory) || memory is null) return;
        using (memory)
        {
            var probe = new GroundItemResearchProbe(memory);
            var recoveryInput = new FreiAtlas.Platform.Windows.Input.RecoveryInput();
            var clientSize = recoveryInput.GetEffectiveClientSize(pid);
            var result = probe.Capture(clientSize);

            Console.WriteLine($"[DIAG] PlayerGrid: {result.PlayerGridPosition}");
            var profile = FreiAtlas.Core.Memory.Poe2MemoryProfile.Current;
            var reader = new FreiAtlas.Game.Memory.GameMemoryReader(memory, profile);

            var compResolver = new FreiAtlas.Game.Entities.EntityComponentResolver(memory, profile);
            var candDetails = new List<(uint Id, nint Addr, nint WorldItem, nint Render, nint ItemEntity)>();
            foreach (var c in result.Candidates)
            {
                var entRef = new FreiAtlas.Game.Entities.RawEntityRef(c.EntityId, c.EntityAddress);
                compResolver.TryResolve(entRef, "WorldItem", out var worldItemComp);
                compResolver.TryResolve(entRef, "Render", out var renderComp);
                nint itemEntity = 0;
                if (worldItemComp != 0) reader.TryReadPointer(worldItemComp + profile.GroundItem.WorldItemEntityOffset, out itemEntity);
                candDetails.Add((c.EntityId, c.EntityAddress, worldItemComp, renderComp, itemEntity));
                Console.WriteLine($"[DIAG] Cand ID={c.EntityId}: Addr={c.EntityAddress:X}, WorldItem={worldItemComp:X}, Render={renderComp:X}, ItemEntity={itemEntity:X}");
            }

            var labelsList = result.Labels ?? [];
            Console.WriteLine($"[DIAG] Native ReadLabels returned {labelsList.Count} labels:");
            foreach (var l in labelsList)
            {
                Console.WriteLine($"  Label: Elem={l.Address:X}, CleanText='{l.CleanText}', RawText='{l.RawText}', Size={l.Size}, Center={l.Center}");
            }

            var settings = new FreiAtlas.Core.Pickup.PickupSettings
            {
                Enabled = true,
                PickupRangeGrid = 30f,
                Whitelist = new[] { "崇高石" }
            };

            var pickupCandidates = result.Candidates
                .Where(candidate => candidate.ItemEntityReady)
                .Select(candidate => new FreiAtlas.Core.Pickup.PickupItemCandidate(
                    candidate.EntityId,
                    candidate.ItemName,
                    candidate.ArtName,
                    candidate.GridPosition,
                    candidate.DistanceToPlayer,
                    candidate.WorldPosition,
                    candidate.EntityAddress))
                .ToArray();

            var pickupTargets = FreiAtlas.Core.Pickup.PickupTargetCorrelator.Correlate(
                settings,
                result.PlayerGridPosition,
                result.CameraWorldToScreenMatrix,
                clientSize is { } cs ? new Vector2(cs.X, cs.Y) : new Vector2(1920, 1080),
                pickupCandidates,
                labelsList);

            Console.WriteLine($"[DIAG] Native Correlated Targets Count: {pickupTargets.Count} (Expected 5)");
            foreach (var t in pickupTargets)
            {
                Console.WriteLine($"  Target: CandID={t.EntityId}, Name='{t.ItemName}', Center={t.LabelCenter}, Dist={t.DistanceToPlayer:F1}");
            }
        }
    }
}
