using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Game.Tests.Memory;
using FreiAtlas.Game.Views;

namespace FreiAtlas.Game.Tests.Views;

public sealed class MapUiCandidateProbeTests
{
    [Fact]
    public void ProbeDirect_UsesTypedMapPointersAndPublishesRealMiniMapGeometry()
    {
        using var memory = new SyntheticProcessMemory();
        var tree = new UiTreeBuilder(memory);
        var misleadingTreeCandidate = tree.AddElement(
            relativePosition: new Vector2(640, 400),
            size: new Vector2(1100, 700),
            isMap: true);
        var large = tree.AddElement(
            visible: false,
            size: Vector2.Zero,
            isMap: true,
            shift: new Vector2(12, -8),
            zoom: 0.75f);
        var mini = tree.AddElement(
            relativePosition: new Vector2(1500, 20),
            size: new Vector2(380, 300));
        memory.WriteVector2(mini + 0x350, new Vector2(-2, 3));
        memory.WriteVector2(mini + 0x358, Vector2.Zero);
        memory.WriteFloat(mini + 0x390, 0.5f);
        tree.SetChildren(tree.Root, misleadingTreeCandidate, large, mini);

        const nint mapParent = 0xA00000;
        memory.WritePointer(tree.Root + 0x7B0, mapParent);
        memory.WritePointer(mapParent + 0x28, large);
        memory.WritePointer(mapParent + 0x30, mini);

        var result = new MapUiCandidateProbe(memory).ProbeDirect(
            tree.InGameState,
            windowWidth: 1920,
            windowHeight: 1080);

        Assert.False(result.IsTruncated);
        Assert.Empty(result.Diagnostics);
        Assert.Collection(
            result.Candidates.OrderBy(candidate => candidate.Fingerprint),
            candidate =>
            {
                Assert.Equal("direct/large", candidate.Fingerprint);
                Assert.Equal(new AreaUiRect(0, 0, 1920, 1080), candidate.Viewport);
                Assert.Equal(new Vector2(12, -8), candidate.Shift);
                Assert.False(candidate.IsVisible);
            },
            candidate =>
            {
                Assert.Equal("direct/mini", candidate.Fingerprint);
                Assert.Equal(new AreaUiRect(1500, 20, 380, 300), candidate.Viewport);
                Assert.Equal(new Vector2(-2, 3), candidate.Shift);
                Assert.True(candidate.IsVisible);
            });
    }

    [Fact]
    public void ProbeDirect_DoesNotFallBackToTreeCandidatesWhenTypedChainIsUnavailable()
    {
        using var memory = new SyntheticProcessMemory();
        var tree = new UiTreeBuilder(memory);
        var misleadingTreeCandidate = tree.AddElement(isMap: true);
        tree.SetChildren(tree.Root, misleadingTreeCandidate);

        var result = new MapUiCandidateProbe(memory).ProbeDirect(tree.InGameState);

        Assert.Empty(result.Candidates);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "map-ui-direct-parent-unavailable");
    }

    [Fact]
    public void Probe_FindsMapElementAndUsesAddressFreeFingerprint()
    {
        using var memory = new SyntheticProcessMemory();
        var tree = new UiTreeBuilder(memory);
        var map = tree.AddElement(
            visible: true,
            relativePosition: new Vector2(100, 60),
            size: new Vector2(1200, 800),
            isMap: true,
            shift: new Vector2(12, -8),
            zoom: 0.75f);
        tree.SetChildren(tree.Root, map);

        var result = new MapUiCandidateProbe(memory).Probe(tree.InGameState);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("root/0", candidate.Fingerprint);
        Assert.DoesNotContain("0x", candidate.Fingerprint, StringComparison.OrdinalIgnoreCase);
        Assert.True(candidate.IsVisible);
        Assert.Equal(new Vector2(12, -8), candidate.Shift);
        Assert.Equal(0.75f, candidate.Zoom);
        Assert.Equal(100, candidate.Viewport.X);
        Assert.Equal(60, candidate.Viewport.Y);
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public void Probe_RejectsInvalidSelfDefaultShiftAndZoom()
    {
        using var memory = new SyntheticProcessMemory();
        var tree = new UiTreeBuilder(memory);
        var invalidSelf = tree.AddElement(isMap: true);
        var invalidShift = tree.AddElement(isMap: true);
        var invalidZoom = tree.AddElement(isMap: true, zoom: 9f);
        memory.WritePointer(invalidSelf + 0x08, invalidSelf + 8);
        memory.WriteVector2(invalidShift + 0x358, Vector2.Zero);
        tree.SetChildren(tree.Root, invalidSelf, invalidShift, invalidZoom);

        var result = new MapUiCandidateProbe(memory).Probe(tree.InGameState);

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Probe_MapVisibilityUsesElementFlagEvenWhenMapContainerIsHidden()
    {
        using var memory = new SyntheticProcessMemory();
        var tree = new UiTreeBuilder(memory);
        var hiddenParent = tree.AddElement(visible: false);
        var map = tree.AddElement(visible: true, isMap: true);
        tree.SetChildren(tree.Root, hiddenParent);
        tree.SetChildren(hiddenParent, map, tree.Root);

        var result = new MapUiCandidateProbe(memory).Probe(tree.InGameState);

        Assert.True(Assert.Single(result.Candidates).IsVisible);
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public void Probe_UsesNearestValidAncestorViewportWhenMapElementHasNoSize()
    {
        using var memory = new SyntheticProcessMemory();
        var tree = new UiTreeBuilder(memory);
        var mapParent = tree.AddElement(
            relativePosition: new Vector2(20, 30),
            size: new Vector2(1000, 800));
        var map = tree.AddElement(
            relativePosition: new Vector2(5, 7),
            size: Vector2.Zero,
            isMap: true,
            shift: new Vector2(4, -3));
        tree.SetChildren(tree.Root, mapParent);
        tree.SetChildren(mapParent, map);

        var result = new MapUiCandidateProbe(memory).Probe(tree.InGameState);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(new AreaUiRect(20, 30, 1000, 800), candidate.Viewport);
        Assert.Equal(new Vector2(4, -3), candidate.Shift);
    }

    [Fact]
    public void Probe_AccumulatesParentModifierAndScalesViewportToClientResolution()
    {
        using var memory = new SyntheticProcessMemory();
        var tree = new UiTreeBuilder(memory);
        var root = tree.AddElement(
            relativePosition: new Vector2(100, 50),
            size: new Vector2(2000, 1200));
        var parent = tree.AddElement(relativePosition: new Vector2(40, 30));
        var map = tree.AddElement(
            relativePosition: new Vector2(5, 7),
            size: new Vector2(200, 100),
            isMap: true);
        tree.SetChildren(tree.Root, root);
        tree.SetChildren(root, parent);
        tree.SetChildren(parent, map);

        memory.WriteByte(root + 0x172, 3);
        memory.WriteByte(parent + 0x172, 3);
        memory.WriteByte(map + 0x172, 3);
        memory.WriteFloat(map + 0x118, 1f);
        memory.WriteUInt32(map + 0x168, (1u << 11) | (1u << 10));
        memory.WriteVector2(parent + 0x108, new Vector2(10, 5));
        memory.WriteVector2(map + 0x108, new Vector2(900, 900));

        var result = new MapUiCandidateProbe(memory).Probe(
            tree.InGameState,
            windowWidth: 1280,
            windowHeight: 800);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(77.5f, candidate.Viewport.X, 3);
        Assert.Equal(46f, candidate.Viewport.Y, 3);
        Assert.Equal(100f, candidate.Viewport.Width, 3);
        Assert.Equal(50f, candidate.Viewport.Height, 3);
    }

    [Fact]
    public void Probe_UsesLocalScaleMultiplierWithSingleAxisScaleIndex()
    {
        using var memory = new SyntheticProcessMemory();
        var tree = new UiTreeBuilder(memory);
        var map = tree.AddElement(
            relativePosition: new Vector2(100, 80),
            size: new Vector2(200, 100),
            isMap: true);
        tree.SetChildren(tree.Root, map);
        memory.WriteByte(map + 0x172, 1);
        memory.WriteFloat(map + 0x118, 0.5f);

        var result = new MapUiCandidateProbe(memory).Probe(
            tree.InGameState,
            windowWidth: 1280,
            windowHeight: 800);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(25f, candidate.Viewport.X, 3);
        Assert.Equal(20f, candidate.Viewport.Y, 3);
        Assert.Equal(50f, candidate.Viewport.Width, 3);
        Assert.Equal(25f, candidate.Viewport.Height, 3);
    }

    [Fact]
    public void Probe_RejectsCandidateWhenParentChainContainsCycle()
    {
        using var memory = new SyntheticProcessMemory();
        var tree = new UiTreeBuilder(memory);
        var parent = tree.AddElement(relativePosition: new Vector2(20, 30));
        var map = tree.AddElement(
            relativePosition: new Vector2(5, 7),
            size: new Vector2(200, 100),
            isMap: true);
        tree.SetChildren(tree.Root, map);

        memory.WritePointer(map + 0xB8, parent);
        memory.WritePointer(parent + 0xB8, map);

        var result = new MapUiCandidateProbe(memory).Probe(tree.InGameState);

        Assert.Empty(result.Candidates);
        Assert.Contains(result.Diagnostics, item => item.Code == "map-ui-parent-cycle");
    }

    [Fact]
    public void Probe_ReusesDiscoveredCandidatesForRealtimeRefresh()
    {
        using var memory = new SyntheticProcessMemory();
        var tree = new UiTreeBuilder(memory);
        var map = tree.AddElement(
            relativePosition: new Vector2(100, 60),
            size: new Vector2(1200, 800),
            isMap: true,
            shift: new Vector2(4, -2));
        tree.SetChildren(tree.Root, map);

        var probe = new MapUiCandidateProbe(memory);
        Assert.Single(probe.Probe(tree.InGameState).Candidates);

        memory.FailRange(tree.Root + 0x10, 16);
        memory.WriteVector2(map + 0x350, new Vector2(8, -5));

        var refreshed = probe.Probe(tree.InGameState);

        var candidate = Assert.Single(refreshed.Candidates);
        Assert.Equal(new Vector2(8, -5), candidate.Shift);
        Assert.DoesNotContain(refreshed.Diagnostics, item => item.Code == "map-ui-children-invalid");
    }

    [Fact]
    public void Probe_RejectsChildVectorsOver8192Entries()
    {
        using var memory = new SyntheticProcessMemory();
        var tree = new UiTreeBuilder(memory);
        memory.WritePointer(tree.Root + 0x10, 0x900000);
        memory.WritePointer(tree.Root + 0x18, 0x900000 + (8193 * 8));

        var result = new MapUiCandidateProbe(memory).Probe(tree.InGameState);

        Assert.Empty(result.Candidates);
        Assert.Contains(result.Diagnostics, item => item.Code == "map-ui-children-invalid");
        Assert.Equal(30_000, MapUiCandidateProbe.MaximumNodes);
        Assert.Equal(8192, MapUiCandidateProbe.MaximumChildrenPerNode);
    }

    [Fact]
    public void Probe_NodeLimitIsInformationalWhenMapCandidateWasFound()
    {
        using var memory = new SyntheticProcessMemory();
        var tree = new UiTreeBuilder(memory);
        var map = tree.AddElement(isMap: true);
        var overflow = tree.AddElement();
        tree.SetChildren(tree.Root, map, overflow);

        var result = new MapUiCandidateProbe(memory, maximumNodes: 2)
            .Probe(tree.InGameState);

        Assert.True(result.IsTruncated);
        Assert.Single(result.Candidates);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("map-ui-node-limit", diagnostic.Code);
        Assert.Equal(AreaDiagnosticSeverity.Info, diagnostic.Severity);
    }

    private sealed class UiTreeBuilder
    {
        private readonly SyntheticProcessMemory _memory;
        private long _nextElement = 0x400000;
        private long _nextChildren = 0x800000;

        public UiTreeBuilder(SyntheticProcessMemory memory)
        {
            _memory = memory;
            InGameState = 0x300000;
            Root = AddElement();
            _memory.WritePointer(InGameState + 0x2F0, Root);
        }

        public nint InGameState { get; }
        public nint Root { get; }

        public nint AddElement(
            bool visible = true,
            Vector2? relativePosition = null,
            Vector2? size = null,
            bool isMap = false,
            Vector2? shift = null,
            float zoom = 0.5f)
        {
            var address = (nint)_nextElement;
            _nextElement += 0x500;
            _memory.WriteBytes(address, new byte[0x400]);
            _memory.WritePointer(address + 0x08, address);
            _memory.WriteUInt32(address + 0x168, visible ? 1u << 11 : 0u);
            _memory.WriteVector2(address + 0x100, relativePosition ?? Vector2.Zero);
            _memory.WriteVector2(address + 0x270, size ?? new Vector2(100, 100));
            if (isMap)
            {
                _memory.WriteVector2(address + 0x350, shift ?? Vector2.Zero);
                _memory.WriteVector2(address + 0x358, new Vector2(0, -20));
                _memory.WriteFloat(address + 0x390, zoom);
            }

            return address;
        }

        public void SetChildren(nint parent, params nint[] children)
        {
            var vector = (nint)_nextChildren;
            _nextChildren += Math.Max(0x100, children.Length * 8L);
            for (var index = 0; index < children.Length; index++)
            {
                _memory.WritePointer(vector + (index * 8), children[index]);
                if (children[index] != Root)
                {
                    _memory.WritePointer(children[index] + 0xB8, parent);
                }
            }

            _memory.WritePointer(parent + 0x10, vector);
            _memory.WritePointer(parent + 0x18, vector + (children.Length * 8));
        }
    }
}
