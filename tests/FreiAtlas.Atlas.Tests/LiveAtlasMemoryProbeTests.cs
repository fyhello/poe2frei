using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using FreiAtlas.Atlas.Memory;
using FreiAtlas.Core.Atlas;
using FreiAtlas.Core.Memory;
using FreiAtlas.Core.Settings;
using FreiAtlas.Platform.Windows.Process;

namespace FreiAtlas.Atlas.Tests;

public sealed class LiveAtlasMemoryProbeTests
{
    [Fact]
    public void ImplementsLiveRenderGeometryProbe()
    {
        var probe = new LiveAtlasMemoryProbe();

        Assert.IsAssignableFrom<IAtlasLiveRenderGeometryProbe>(probe);
    }

    [Fact]
    public void DefaultProfile_AllowsObservedAtlasNodeCountAboveLegacyLimit()
    {
        Assert.True(
            AtlasLayoutProfile.Default.MaxNodeCount >= 2063,
            $"Default node limit was {AtlasLayoutProfile.Default.MaxNodeCount}.");
    }

    [Fact]
    public void DefaultProfile_UsesRadarCompatibleNodeSafetyBound()
    {
        Assert.True(
            AtlasLayoutProfile.Default.MaxNodeCount >= 20_000,
            $"Default node limit was {AtlasLayoutProfile.Default.MaxNodeCount}.");
    }

    [Fact]
    public void NativeMemory_ReadsUtf16StringOnlyUntilFirstNull()
    {
        var address = Marshal.StringToCoTaskMemUni("MapPort\0Port");
        try
        {
            Assert.True(
                NativeProcessMemory.TryOpen(
                    Environment.ProcessId,
                    out var memory));
            using (memory)
            {
                Assert.True(
                    memory!.TryReadUtf16(
                        address,
                        64,
                        out var value));
                Assert.Equal("MapPort", value);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(address);
        }
    }

    [Fact]
    public void Read_WhenAtlasCanvasIsHidden_DoesNotExposeNodes()
    {
        var memory = ProbeMemory.CreateMinimalAtlas();
        memory.WriteUInt32((nint)0x410000 + 0x168, 0u);

        var probe = new LiveAtlasMemoryProbe();
        var snapshot = probe.Read(memory, AtlasLayoutProfile.Default with
        {
            MinimumNodeClassCount = 2,
            MinimumBiomeKinds = 2,
            MaxNodeCount = 10,
            MaxEdgeCount = 10
        });

        Assert.True(snapshot.HasUiRoot);
        Assert.True(snapshot.HasAtlasCanvas);
        Assert.False(snapshot.IsAtlasOpen);
        Assert.Empty(snapshot.Nodes);
        Assert.Empty(snapshot.Edges);
    }

    [Fact]
    public void Read_ResolvesUiRootCanvasNodesAndConnections()
    {
        var memory = ProbeMemory.CreateMinimalAtlas();
        var profile = AtlasLayoutProfile.Default with
        {
            MinimumNodeClassCount = 2,
            MinimumBiomeKinds = 2,
            MaxNodeCount = 10,
            MaxEdgeCount = 10
        };

        var probe = new LiveAtlasMemoryProbe();
        var snapshot = probe.Read(memory, profile);

        Assert.True(snapshot.HasUiRoot);
        Assert.True(
            snapshot.HasAtlasCanvas,
            string.Join(" | ", probe.Diagnostics));
        Assert.True(snapshot.IsAtlasOpen);
        Assert.Equal(2, snapshot.Nodes.Count);
        Assert.Single(snapshot.Edges);
        Assert.Equal(new AtlasGridPos(0, 0), snapshot.Edges[0].From);
        Assert.Equal(new AtlasGridPos(1, 0), snapshot.Edges[0].To);

        var bossNode = Assert.Single(snapshot.Nodes, node => node.Grid == new AtlasGridPos(0, 0));
        Assert.Equal("MapMeadow", bossNode.MapId);
        Assert.Equal("Meadow[★]", bossNode.DisplayName);
        Assert.True(bossNode.IsAccessible);
        Assert.False(bossNode.IsCompleted);
        Assert.Contains("Powerful Map Boss", bossNode.RawContentCodes);
        Assert.Contains("Breach", bossNode.RawContentCodes);
    }

    [Fact]
    public void Read_ReadsRegionKeyFromObservedNodeOffset()
    {
        var memory = ProbeMemory.CreateMinimalAtlas();
        memory.WriteUInt32((nint)0x420000 + 0x318, 0x6276CAA0u);
        var profile = AtlasLayoutProfile.Default with
        {
            MinimumNodeClassCount = 2,
            MinimumBiomeKinds = 2,
            MaxNodeCount = 10,
            MaxEdgeCount = 10
        };

        var snapshot = new LiveAtlasMemoryProbe().Read(memory, profile);
        var node = Assert.Single(
            snapshot.Nodes,
            candidate => candidate.Grid == new AtlasGridPos(0, 0));

        Assert.Equal(0x6276CAA0u, node.RegionKey);
    }

    [Theory]
    [InlineData(0x00, 0xFF, false, false, AtlasNodeCategory.Locked)]
    [InlineData(0x01, 0x00, true, false, AtlasNodeCategory.Unlocked)]
    [InlineData(0x03, 0x00, true, true, AtlasNodeCategory.Completed)]
    [InlineData(0x02, 0x01, false, true, AtlasNodeCategory.Completed)]
    [InlineData(0x10, 0xFF, false, false, AtlasNodeCategory.Locked)]
    [InlineData(0x05, 0x02, true, false, AtlasNodeCategory.Unlocked)]
    [InlineData(0x13, 0x00, true, true, AtlasNodeCategory.Completed)]
    public void Read_UsesObservedNodeStatusInsteadOfLegacyByte(
        byte status,
        byte legacyByte,
        bool accessible,
        bool completed,
        AtlasNodeCategory category)
    {
        var memory = ProbeMemory.CreateMinimalAtlas();
        var element = (nint)0x420000;
        // 实机链路的 model 指向 element + 0x60；旧地址包含与分类无关的字节。
        memory.WritePointer(element + 0x120, element + 0x60);
        memory.WriteByte(element + 0x31F, status);
        memory.WriteByte(element + 0x32F, legacyByte);
        var profile = AtlasLayoutProfile.Default with
        {
            MinimumNodeClassCount = 2,
            MinimumBiomeKinds = 2,
            MaxNodeCount = 10,
            MaxEdgeCount = 10
        };

        var snapshot = new LiveAtlasMemoryProbe().Read(memory, profile);
        var node = Assert.Single(snapshot.Nodes, candidate => candidate.Grid == new AtlasGridPos(0, 0));

        Assert.Equal(accessible, node.IsAccessible);
        Assert.Equal(completed, node.IsCompleted);
        Assert.Equal(category, AtlasNodeCategoryClassifier.Classify(node.IsCompleted, node.IsAccessible));
    }

    [Fact]
    public void Read_RefreshesObservedNodeStatusAcrossUnlockAndCompletion()
    {
        var memory = ProbeMemory.CreateMinimalAtlas();
        var element = (nint)0x420000;
        memory.WritePointer(element + 0x120, element + 0x60);
        memory.WriteByte(element + 0x32F, 0xFF);
        var profile = AtlasLayoutProfile.Default with
        {
            MinimumNodeClassCount = 2,
            MinimumBiomeKinds = 2,
            MaxNodeCount = 10,
            MaxEdgeCount = 10
        };
        var probe = new LiveAtlasMemoryProbe();

        foreach (var (status, expected) in new[]
                 {
                     ((byte)0x00, AtlasNodeCategory.Locked),
                     ((byte)0x01, AtlasNodeCategory.Unlocked),
                     ((byte)0x03, AtlasNodeCategory.Completed)
                 })
        {
            memory.WriteByte(element + 0x31F, status);
            var snapshot = probe.Read(memory, profile);
            var node = Assert.Single(snapshot.Nodes, candidate => candidate.Grid == new AtlasGridPos(0, 0));
            Assert.Equal(expected, AtlasNodeCategoryClassifier.Classify(node.IsCompleted, node.IsAccessible));
        }
    }

    [Fact]
    public void Read_ReadsContentStats_WhenLegacyHeadlineIsMissing()
    {
        var memory = ProbeMemory.CreateStatsOnlyContentAtlas();
        var profile = AtlasLayoutProfile.Default with
        {
            MinimumNodeClassCount = 2,
            MinimumBiomeKinds = 2,
            MaxNodeCount = 10,
            MaxEdgeCount = 10
        };

        var snapshot = new LiveAtlasMemoryProbe().Read(memory, profile);
        var node = Assert.Single(
            snapshot.Nodes,
            candidate => candidate.Grid == new AtlasGridPos(0, 0));

        Assert.Contains("Abyss", node.RawContentCodes);
        Assert.Contains("Ritual", node.RawContentCodes);
    }

    [Fact]
    public void Read_ReadsCurrentUtf16Stat_WithoutScanningAdjacentRow()
    {
        var memory = ProbeMemory.CreateCurrentStatsContentAtlas();
        var profile = AtlasLayoutProfile.Default with
        {
            MinimumNodeClassCount = 2,
            MinimumBiomeKinds = 2,
            MaxNodeCount = 10,
            MaxEdgeCount = 10
        };

        var snapshot = new LiveAtlasMemoryProbe().Read(memory, profile);
        var node = Assert.Single(
            snapshot.Nodes,
            candidate => candidate.Grid == new AtlasGridPos(0, 0));

        Assert.Contains("Abyss", node.RawContentCodes);
        Assert.DoesNotContain("Ritual", node.RawContentCodes);
    }

    [Fact]
    public void Read_UsesReferenceValidatedCanvasCurrentNodePointers()
    {
        var memory = ProbeMemory.CreateMinimalAtlas();
        var profile = AtlasLayoutProfile.Default with
        {
            MinimumNodeClassCount = 2,
            MinimumBiomeKinds = 2,
            MaxNodeCount = 10,
            MaxEdgeCount = 10
        };

        var snapshot = new LiveAtlasMemoryProbe().Read(memory, profile);

        Assert.Equal(new AtlasGridPos(1, 0), snapshot.CurrentGrid);
    }

    [Fact]
    public void Read_RecoversWhenCurrentMarkerAppearsAfterAtlasDiscovery()
    {
        var memory = ProbeMemory.CreateMinimalAtlas();
        var rootChildren = (nint)0x413000;
        var currentMarker = (nint)0x430000;
        memory.WritePointer(rootChildren + 8, 0);
        var profile = AtlasLayoutProfile.Default with
        {
            MinimumNodeClassCount = 2,
            MinimumBiomeKinds = 2,
            MaxNodeCount = 10,
            MaxEdgeCount = 10
        };
        var probe = new LiveAtlasMemoryProbe();

        var initial = probe.Read(memory, profile);
        memory.WritePointer(rootChildren + 8, currentMarker);
        for (var sample = 1; sample < 8; sample++)
        {
            Assert.Null(probe.Read(memory, profile).CurrentGrid);
        }

        var recovered = probe.Read(memory, profile);

        Assert.Null(initial.CurrentGrid);
        Assert.Equal(new AtlasGridPos(1, 0), recovered.CurrentGrid);
        Assert.Contains(
            probe.Diagnostics,
            diagnostic => diagnostic.Contains(
                "current marker cache miss threshold",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Read_RecoversDelayedCurrentMarkerTargetWithoutFullRediscovery()
    {
        var memory = ProbeMemory.CreateMinimalAtlas();
        var currentMarker = (nint)0x430000;
        var currentNode = (nint)0x421000;
        memory.WritePointer(currentMarker + 0x2F0, 0);
        var profile = AtlasLayoutProfile.Default with
        {
            MinimumNodeClassCount = 2,
            MinimumBiomeKinds = 2,
            MaxNodeCount = 10,
            MaxEdgeCount = 10
        };
        var probe = new LiveAtlasMemoryProbe();

        var initial = probe.Read(memory, profile);
        var initialReadCount = memory.ReadCount;
        memory.WritePointer(currentMarker + 0x2F0, currentNode);
        var recovered = probe.Read(memory, profile);
        var recoveryReadCount = memory.ReadCount - initialReadCount;

        Assert.Null(initial.CurrentGrid);
        Assert.Equal(new AtlasGridPos(1, 0), recovered.CurrentGrid);
        Assert.DoesNotContain(
            probe.Diagnostics,
            diagnostic => diagnostic.Contains(
                "current marker cache miss threshold",
                StringComparison.Ordinal));
        Assert.True(recoveryReadCount < initialReadCount / 2);
    }

    [Fact]
    public void Read_ReplacesDetachedButStillReadableCurrentMarker()
    {
        var memory = ProbeMemory.CreateMinimalAtlas();
        var rootChildren = (nint)0x413000;
        var uiRoot = (nint)0x400000;
        var replacementMarker = (nint)0x440000;
        var replacementTarget = (nint)0x420000;
        memory.WritePointer(replacementMarker + 0x00, (nint)0x502000);
        memory.WritePointer(replacementMarker + 0x08, replacementMarker);
        memory.WritePointer(replacementMarker + 0xB8, uiRoot);
        memory.WriteUInt32(replacementMarker + 0x168, 0u);
        memory.WritePointer(replacementMarker + 0x2F0, replacementTarget);
        var profile = AtlasLayoutProfile.Default with
        {
            MinimumNodeClassCount = 2,
            MinimumBiomeKinds = 2,
            MaxNodeCount = 10,
            MaxEdgeCount = 10
        };
        var probe = new LiveAtlasMemoryProbe();

        var initial = probe.Read(memory, profile);
        memory.WritePointer(rootChildren + 8, replacementMarker);
        var recovered = initial;
        for (var sample = 0;
             sample < 12 && recovered.CurrentGrid != new AtlasGridPos(0, 0);
             sample++)
        {
            recovered = probe.Read(memory, profile);
        }

        Assert.Equal(new AtlasGridPos(1, 0), initial.CurrentGrid);
        Assert.Equal(new AtlasGridPos(0, 0), recovered.CurrentGrid);
    }

    [Fact]
    public void Read_ReplacesCurrentMarkerWhenItsWholeOldSubtreeIsDetached()
    {
        var memory = ProbeMemory.CreateMinimalAtlas();
        var rootChildren = (nint)0x413000;
        var uiRoot = (nint)0x400000;
        var oldParent = (nint)0x450000;
        var oldParentChildren = (nint)0x451000;
        var oldMarker = (nint)0x430000;
        var replacementParent = (nint)0x452000;
        var replacementParentChildren = (nint)0x453000;
        var replacementMarker = (nint)0x440000;
        memory.WritePointer(rootChildren + 8, oldParent);
        WriteUiElement(memory, oldParent, uiRoot, (nint)0x503000);
        memory.WritePointer(oldParent + 0x10, oldParentChildren);
        memory.WritePointer(oldParent + 0x18, oldParentChildren + 8);
        memory.WritePointer(oldParentChildren, oldMarker);
        memory.WritePointer(oldMarker + 0xB8, oldParent);
        WriteUiElement(memory, replacementParent, uiRoot, (nint)0x504000);
        memory.WritePointer(replacementParent + 0x10, replacementParentChildren);
        memory.WritePointer(replacementParent + 0x18, replacementParentChildren + 8);
        memory.WritePointer(replacementParentChildren, replacementMarker);
        WriteUiElement(memory, replacementMarker, replacementParent, (nint)0x502000);
        memory.WritePointer(replacementMarker + 0x2F0, (nint)0x420000);
        var profile = MinimalProfile();
        var probe = new LiveAtlasMemoryProbe();

        var initial = probe.Read(memory, profile);
        memory.WritePointer(rootChildren + 8, replacementParent);
        for (var sample = 1; sample < 8; sample++)
        {
            Assert.Equal(
                new AtlasGridPos(1, 0),
                probe.Read(memory, profile).CurrentGrid);
        }

        var recovered = probe.Read(memory, profile);

        Assert.Equal(new AtlasGridPos(1, 0), initial.CurrentGrid);
        Assert.Equal(new AtlasGridPos(0, 0), recovered.CurrentGrid);
    }

    [Fact]
    public void Read_PermanentlyMissingCurrentMarkerBoundsCandidateReadsAndBacksOffExactly()
    {
        var memory = ProbeMemory.CreateMinimalAtlas();
        ConfigureLargeMissingMarkerTree(memory, candidateCount: 256);
        var profile = MinimalProfile() with { UiTreeNodeLimit = 512 };
        var probe = new LiveAtlasMemoryProbe();

        Assert.Null(probe.Read(memory, profile).CurrentGrid);
        var initialReadCount = memory.ReadCount;
        var rediscoverySamples = new List<int>();
        var ordinarySampleReadCounts = new List<int>();
        for (var sample = 1; sample <= 64; sample++)
        {
            var before = memory.ReadCount;
            probe.Read(memory, profile);
            if (probe.Diagnostics.Any(diagnostic => diagnostic.Contains(
                    "current marker cache miss threshold",
                    StringComparison.Ordinal)))
            {
                rediscoverySamples.Add(sample);
            }
            else
            {
                ordinarySampleReadCounts.Add(memory.ReadCount - before);
            }
        }

        Assert.Equal([8, 24, 56], rediscoverySamples);
        Assert.All(
            ordinarySampleReadCounts,
            readCount => Assert.True(
                readCount < initialReadCount / 4,
                $"Cached marker-miss sample used {readCount} reads; initial discovery used {initialReadCount}."));
    }

    [Fact]
    public void Read_HiddenAtlasAvoidsCurrentMarkerCandidateScanAndResumesRediscoveryProgress()
    {
        var memory = ProbeMemory.CreateMinimalAtlas();
        var canvas = (nint)0x410000;
        ConfigureLargeMissingMarkerTree(memory, candidateCount: 256);
        var profile = MinimalProfile() with { UiTreeNodeLimit = 512 };
        var probe = new LiveAtlasMemoryProbe();

        Assert.Null(probe.Read(memory, profile).CurrentGrid);
        var initialReadCount = memory.ReadCount;
        for (var sample = 0; sample < 5; sample++)
        {
            probe.Read(memory, profile);
        }

        memory.WriteUInt32(canvas + 0x168, 0u);
        var hiddenReadCounts = new List<int>();
        for (var sample = 0; sample < 16; sample++)
        {
            var before = memory.ReadCount;
            probe.Read(memory, profile);
            hiddenReadCounts.Add(memory.ReadCount - before);
            Assert.DoesNotContain(
                probe.Diagnostics,
                diagnostic => diagnostic.Contains(
                    "current marker cache miss threshold",
                    StringComparison.Ordinal));
        }

        Assert.All(
            hiddenReadCounts,
            readCount => Assert.True(
                readCount < initialReadCount / 4,
                $"Hidden marker-miss sample used {readCount} reads; initial discovery used {initialReadCount}."));

        memory.WriteUInt32(canvas + 0x168, 0x800u);
        for (var sample = 0; sample < 2; sample++)
        {
            probe.Read(memory, profile);
            Assert.DoesNotContain(
                probe.Diagnostics,
                diagnostic => diagnostic.Contains(
                    "current marker cache miss threshold",
                    StringComparison.Ordinal));
        }

        probe.Read(memory, profile);
        Assert.Contains(
            probe.Diagnostics,
            diagnostic => diagnostic.Contains(
                "current marker cache miss threshold",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Read_FailedForcedCurrentMarkerRediscoveryKeepsCacheAndDoesNotScanTreeEverySample()
    {
        var memory = ProbeMemory.CreateMinimalAtlas();
        var uiRoot = (nint)0x400000;
        var rootChildren = (nint)0x413000;
        memory.WritePointer(rootChildren + 8, 0);
        var profile = MinimalProfile();
        var probe = new LiveAtlasMemoryProbe();

        Assert.Null(probe.Read(memory, profile).CurrentGrid);
        var ordinaryReadCount = 0;
        for (var sample = 1; sample < 8; sample++)
        {
            var before = memory.ReadCount;
            probe.Read(memory, profile);
            ordinaryReadCount = memory.ReadCount - before;
        }

        memory.WritePointer(uiRoot + 0x10, 0);
        memory.WritePointer(uiRoot + 0x18, 0);
        var failedRediscovery = probe.Read(memory, profile);
        memory.WritePointer(uiRoot + 0x10, rootChildren);
        memory.WritePointer(uiRoot + 0x18, rootChildren + 16);
        Assert.Contains(
            probe.Diagnostics,
            diagnostic => diagnostic.Contains(
                "current marker cache miss threshold",
                StringComparison.Ordinal));
        Assert.True(failedRediscovery.HasAtlasCanvas);

        for (var sample = 0; sample < 3; sample++)
        {
            var before = memory.ReadCount;
            var cached = probe.Read(memory, profile);
            var readCount = memory.ReadCount - before;

            Assert.True(cached.HasAtlasCanvas);
            Assert.Contains(
                probe.Diagnostics,
                diagnostic => diagnostic.Contains(
                    "Atlas cache-hit",
                    StringComparison.Ordinal));
            Assert.True(
                readCount <= ordinaryReadCount + 16,
                $"Post-failure sample used {readCount} reads; ordinary cache sample used {ordinaryReadCount}.");
        }
    }

    private static AtlasLayoutProfile MinimalProfile()
        => AtlasLayoutProfile.Default with
        {
            MinimumNodeClassCount = 2,
            MinimumBiomeKinds = 2,
            MaxNodeCount = 10,
            MaxEdgeCount = 10
        };

    private static void ConfigureLargeMissingMarkerTree(
        ProbeMemory memory,
        int candidateCount)
    {
        var uiRoot = (nint)0x400000;
        var canvas = (nint)0x410000;
        var children = (nint)0x480000;
        memory.WritePointer(uiRoot + 0x10, children);
        memory.WritePointer(
            uiRoot + 0x18,
            children + (candidateCount + 1) * IntPtr.Size);
        memory.WritePointer(children, canvas);

        for (var index = 0; index < candidateCount; index++)
        {
            var candidate = (nint)(0x600000 + index * 0x1000);
            memory.WritePointer(
                children + (index + 1) * IntPtr.Size,
                candidate);
            WriteUiElement(
                memory,
                candidate,
                uiRoot,
                (nint)(0x800000 + index * 0x1000));
            memory.WritePointer(candidate + 0x10, 0);
            memory.WritePointer(candidate + 0x18, 0);
            memory.WritePointer(candidate + 0x2F0, 0);
        }
    }

    private static void WriteUiElement(
        ProbeMemory memory,
        nint element,
        nint parent,
        nint vtable)
    {
        memory.WritePointer(element, vtable);
        memory.WritePointer(element + 0x08, element);
        memory.WritePointer(element + 0xB8, parent);
        memory.WriteUInt32(element + 0x168, 0x800u);
    }

    [Fact]
    public void Read_PreservesRadarContentLayers_WhenCurrentVersionUsesRawContentAndContentVector()
    {
        var memory = ProbeMemory.CreateCurrentVersionContentAtlas();
        var profile = AtlasLayoutProfile.Default with
        {
            MinimumNodeClassCount = 2,
            MinimumBiomeKinds = 2,
            MaxNodeCount = 10,
            MaxEdgeCount = 10
        };

        var snapshot = new LiveAtlasMemoryProbe().Read(memory, profile);
        var node = Assert.Single(
            snapshot.Nodes,
            candidate => candidate.Grid == new AtlasGridPos(0, 0));

        var nodeType = node.GetType();
        var rawContentProperty = nodeType.GetProperty("RawContentValue");
        var contentVectorProperty = nodeType.GetProperty("ContentVectorValues");
        var iconTypeProperty = nodeType.GetProperty("IconType");
        var badgesProperty = nodeType.GetProperty("ContentBadges");

        Assert.NotNull(rawContentProperty);
        Assert.NotNull(contentVectorProperty);
        Assert.NotNull(iconTypeProperty);
        Assert.NotNull(badgesProperty);
        Assert.Equal(
            (uint)3_654_887_217,
            rawContentProperty!.GetValue(node));
        Assert.Equal(
            new uint[] { 4_221_045, 4_213_849 },
            Assert.IsAssignableFrom<IReadOnlyList<uint>>(
                contentVectorProperty!.GetValue(node)));
        Assert.Equal(7, iconTypeProperty!.GetValue(node));
        Assert.Contains(
            "Map Boss",
            Assert.IsAssignableFrom<IReadOnlyList<string>>(
                badgesProperty!.GetValue(node)));
    }

    [Fact]
    public void TryReadLiveGeometry_ReportsCurrentNodeScale()
    {
        var memory = ProbeMemory.CreateMinimalAtlas();
        var profile = AtlasLayoutProfile.Default with
        {
            MinimumNodeClassCount = 2,
            MinimumBiomeKinds = 2,
            MaxNodeCount = 10,
            MaxEdgeCount = 10
        };
        var probe = new LiveAtlasMemoryProbe();

        memory.WriteFloat((nint)0x410000 + 0x100, 0f);
        memory.WriteFloat((nint)0x410000 + 0x104, 0f);
        probe.Read(memory, profile);
        memory.WriteFloat((nint)0x420000 + 0x118, 0.5f);
        memory.WriteFloat((nint)0x421000 + 0x118, 0.5f);

        Assert.True(
            probe.TryReadLiveGeometry(memory, profile, out var geometry),
            string.Join(" | ", probe.Diagnostics));
        Assert.Equal(0.5f, geometry.AtlasZoom, 3);
        Assert.True(geometry.IsStable);
    }

    [Fact]
    public void TryReadLiveGeometry_RejectsStaleNodeSelfAndKeepsLastPosition()
    {
        var memory = ProbeMemory.CreateMinimalAtlas();
        var profile = AtlasLayoutProfile.Default with
        {
            MinimumNodeClassCount = 2,
            MinimumBiomeKinds = 2,
            MaxNodeCount = 10,
            MaxEdgeCount = 10
        };
        var probe = new LiveAtlasMemoryProbe();

        memory.WriteFloat((nint)0x410000 + 0x100, 0f);
        memory.WriteFloat((nint)0x410000 + 0x104, 0f);
        probe.Read(memory, profile);
        Assert.True(
            probe.TryReadLiveGeometry(
                memory,
                profile,
                out var firstGeometry),
            string.Join(" | ", probe.Diagnostics));
        var firstPosition = firstGeometry.NodePositions[
            new AtlasGridPos(0, 0)];

        memory.WritePointer((nint)0x420000 + 0x08, 0);
        memory.WriteFloat((nint)0x420000 + 0x100, 999_999f);
        memory.WriteFloat((nint)0x420000 + 0x104, -999_999f);

        Assert.True(
            probe.TryReadLiveGeometry(
                memory,
                profile,
                out var secondGeometry));
        Assert.Equal(
            firstPosition,
            secondGeometry.NodePositions[new AtlasGridPos(0, 0)]);
    }

    [Fact]
    public void TryReadLiveRenderGeometry_PreservesPartialPanForDrawnNodes()
    {
        var memory = ProbeMemory.CreateMinimalAtlas();
        var profile = AtlasLayoutProfile.Default with
        {
            MinimumNodeClassCount = 2,
            MinimumBiomeKinds = 2,
            MaxNodeCount = 10,
            MaxEdgeCount = 10
        };
        var probe = new LiveAtlasMemoryProbe();
        memory.WriteFloat((nint)0x410000 + 0x100, 0f);
        memory.WriteFloat((nint)0x410000 + 0x104, 0f);
        probe.Read(memory, profile);

        memory.WriteFloat((nint)0x420000 + 0x100, 120f);
        memory.WriteFloat((nint)0x420000 + 0x104, 195f);

        Assert.True(
            probe.TryReadLiveRenderGeometry(
                memory,
                profile,
                new HashSet<AtlasGridPos>
                {
                    new(0, 0),
                    new(1, 0)
                },
                out var geometry),
            string.Join(" | ", probe.Diagnostics));
        Assert.Equal(
            new Vector2(120, 195),
            geometry.NodePositions[new AtlasGridPos(0, 0)]);
        Assert.Equal(
            new Vector2(140, 200),
            geometry.NodePositions[new AtlasGridPos(1, 0)]);
    }

    [Fact]
    public void Read_ReusesAtlasDiscoveryOnSubsequentSamples()
    {
        var memory = ProbeMemory.CreateMinimalAtlas();
        var profile = AtlasLayoutProfile.Default with
        {
            MinimumNodeClassCount = 2,
            MinimumBiomeKinds = 2,
            MaxNodeCount = 10,
            MaxEdgeCount = 10
        };
        var probe = new LiveAtlasMemoryProbe();

        probe.Read(memory, profile);
        var firstReadCount = memory.ReadCount;

        probe.Read(memory, profile);
        var secondReadCount = memory.ReadCount - firstReadCount;

        Assert.True(
            secondReadCount < firstReadCount / 2,
            $"Expected cached read to use fewer than half the reads. "
            + $"first={firstReadCount}, second={secondReadCount}.");
    }

    private sealed class ProbeMemory : IProcessMemory, IProcessMemoryLayout
    {
        private readonly Dictionary<nint, byte> _bytes = [];

        public int ProcessId => 7928;
        public nint MainModuleBase => (nint)0x100000;
        public long MainModuleSize => 0x10000;
        public int ReadCount { get; private set; }

        public IReadOnlyList<ProcessMemoryRegion> MainModuleRegions { get; } =
        [
            new ProcessMemoryRegion(
                (nint)0x100000,
                0x10000,
                IsReadable: true,
                IsExecutable: true,
                IsImage: true)
        ];

        public IEnumerable<ProcessMemoryRegion> EnumerateMainModuleRegions()
            => MainModuleRegions;

        public static ProbeMemory CreateMinimalAtlas()
        {
            var memory = new ProbeMemory();
            memory.Fill((nint)0x100000, 0x10000);
            var codeAddress = (nint)0x101000;
            var slotAddress = (nint)0x102000;
            var gameState = (nint)0x200000;
            var currentStateVector = (nint)0x201000;
            var inGameState = (nint)0x300000;
            var uiRoot = (nint)0x400000;
            var canvas = (nint)0x410000;
            var canvasChildren = (nint)0x411000;
            var edgeData = (nint)0x412000;
            var rootChildren = (nint)0x413000;
            var currentMarker = (nint)0x430000;
            var nodeVtable = (nint)0x500000;
            var canvasVtable = (nint)0x501000;
            var markerVtable = (nint)0x502000;

            var displacement = checked((int)((long)slotAddress - ((long)codeAddress + 7)));
            memory.WriteBytes(
                codeAddress,
                [
                    0x48, 0x39, 0x2D,
                    ..BitConverter.GetBytes(displacement),
                    0x0F, 0x85, 0x20, 0x01, 0x00, 0x00
                ]);
            memory.WritePointer(slotAddress, gameState);
            memory.WritePointer(gameState + 0x10, currentStateVector);
            memory.WritePointer(currentStateVector, inGameState);
            memory.WritePointer(inGameState + 0x2F0, uiRoot);

            memory.WritePointer(uiRoot + 0x08, uiRoot);
            memory.WritePointer(uiRoot + 0xB8, uiRoot);
            memory.WriteUInt32(uiRoot + 0x168, 0x800u);
            memory.WritePointer(uiRoot + 0x10, rootChildren);
            memory.WritePointer(uiRoot + 0x18, rootChildren + 16);
            memory.WritePointer(rootChildren, canvas);
            memory.WritePointer(rootChildren + 8, currentMarker);

            memory.WritePointer(canvas + 0x00, canvasVtable);
            memory.WritePointer(canvas + 0x08, canvas);
            memory.WritePointer(canvas + 0xB8, uiRoot);
            memory.WriteUInt32(canvas + 0x168, 0x800u);
            memory.WritePointer(canvas + 0x10, canvasChildren + 0x10);
            memory.WritePointer(canvas + 0x18, canvasChildren + 0x20);
            memory.WritePointer(canvasChildren + 0x10, (nint)0x420000);
            memory.WritePointer(canvasChildren + 0x18, (nint)0x420010);
            memory.WritePointer(canvasChildren + 0x20, (nint)0x420010);
            memory.WritePointer(canvas + 0x590, edgeData);
            memory.WritePointer(canvas + 0x598, edgeData + 20);
            memory.WriteInt32(edgeData + 0x04, 0);
            memory.WriteInt32(edgeData + 0x08, 0);
            memory.WriteInt32(edgeData + 0x0C, 1);
            memory.WriteInt32(edgeData + 0x10, 0);

            CreateNode(
                memory,
                nodeVtable,
                canvas,
                canvasChildren + 0x10,
                new AtlasGridPos(0, 0),
                relative: new Vector2(100, 200),
                biome: 1,
                visible: true,
                status: 0x01,
                mapCode: "MapMeadow",
                mapName: "Meadow[★]",
                content: "Powerful Map Boss");
            CreateNode(
                memory,
                nodeVtable,
                canvas,
                canvasChildren + 0x18,
                new AtlasGridPos(1, 0),
                relative: new Vector2(140, 200),
                biome: 2,
                visible: false,
                status: 0x02,
                mapCode: "MapMarsh",
                mapName: "Marsh",
                content: null);
            memory.WritePointer(currentMarker + 0x00, markerVtable);
            memory.WritePointer(currentMarker + 0x08, currentMarker);
            memory.WritePointer(currentMarker + 0xB8, uiRoot);
            memory.WriteUInt32(currentMarker + 0x168, 0u);
            memory.WritePointer(currentMarker + 0x2F0, (nint)0x421000);

            return memory;
        }

        public static ProbeMemory CreateCurrentVersionContentAtlas()
        {
            var memory = CreateMinimalAtlas();
            var node = (nint)0x420000;
            var firstChild = (nint)0x440000;
            var badgeContainer = (nint)0x441000;
            var badge = (nint)0x442000;
            var childVector = (nint)0x443000;
            var badgeVector = (nint)0x444000;
            var contentVector = (nint)0x470500;
            var badgeText = (nint)0x460000;

            memory.WriteUInt32(node + 0x300, 3_654_887_217);
            memory.WritePointer(node + 0x340, contentVector);
            memory.WritePointer(node + 0x348, contentVector + 8);
            memory.WriteUInt32(contentVector, 4_221_045);
            memory.WriteUInt32(contentVector + 4, 4_213_849);

            memory.WritePointer(node + 0x10, childVector);
            memory.WritePointer(node + 0x18, childVector + 8);
            memory.WritePointer(childVector, firstChild);
            memory.WritePointer(firstChild + 0x10, badgeVector);
            memory.WritePointer(firstChild + 0x18, badgeVector + 8);
            memory.WritePointer(badgeVector, badgeContainer);
            memory.WritePointer(badgeContainer + 0x10, badgeVector + 8);
            memory.WritePointer(badgeContainer + 0x18, badgeVector + 16);
            memory.WritePointer(badgeVector + 8, badge);
            memory.WritePointer(badge + 0x2E8, badgeText);
            memory.WriteUtf16(badgeText, "[MapBoss|Map Boss]");

            memory.WriteUInt32(firstChild + 0x300, 7);
            return memory;
        }

        public static ProbeMemory CreateStatsOnlyContentAtlas()
        {
            var memory = CreateMinimalAtlas();
            var node = (nint)0x420000;
            var row = node + 0x3100;
            var stats = node + 0x3600;
            var abyssStat = node + 0x3700;
            var ritualStat = node + 0x3800;

            memory.WritePointer(row + 0x38, 0);
            memory.WritePointer(row + 0x50, stats);
            memory.WritePointer(stats, abyssStat);
            memory.WritePointer(stats + 8, ritualStat);
            memory.WriteAscii(abyssStat, "map_atlas_node_has_abyss");
            memory.WriteAscii(ritualStat, "map_atlas_node_has_ritual");

            return memory;
        }

        public static ProbeMemory CreateCurrentStatsContentAtlas()
        {
            var memory = CreateMinimalAtlas();
            var node = (nint)0x420000;
            var row = node + 0x3100;
            var stats = node + 0x3600;
            var abyssStat = node + 0x3700;
            var abyssId = node + 0x3800;
            var adjacentRitualStat = node + 0x3900;
            var adjacentRitualId = node + 0x3A00;

            memory.WritePointer(row + 0x38, 0);
            memory.WritePointer(row + 0x50, stats);
            memory.Fill(stats, 0x100);
            memory.WritePointer(stats, abyssStat);
            memory.WritePointer(abyssStat, abyssId);
            memory.WriteUtf16(abyssId, "map_atlas_node_has_abyss");
            memory.WritePointer(stats + 0x10, adjacentRitualStat);
            memory.WritePointer(adjacentRitualStat, adjacentRitualId);
            memory.WriteUtf16(
                adjacentRitualId,
                "map_atlas_node_has_ritual");

            return memory;
        }

        private static void CreateNode(
            ProbeMemory memory,
            nint nodeVtable,
            nint canvas,
            nint childAddress,
            AtlasGridPos grid,
            Vector2 relative,
            byte biome,
            bool visible,
            byte status,
            string mapCode,
            string mapName,
            string? content)
        {
            var node = childAddress == (nint)0x411010
                ? (nint)0x420000
                : (nint)0x421000;
            var storage = node + 0x100;
            var data = node + 0x200;
            var mapRow = node + 0x3000;
            var worldArea = node + 0x3100;
            var mapCodePtr = node + 0x3200;
            var mapNamePtr = node + 0x3300;

            memory.WritePointer(childAddress, node);
            memory.WritePointer(node + 0x00, nodeVtable);
            memory.WritePointer(node + 0x08, node);
            memory.WritePointer(node + 0xB8, canvas);
            memory.WritePointer(node + 0x10, storage);
            memory.WritePointer(node + 0x18, storage);
            memory.WriteFloat(node + 0x100, relative.X);
            memory.WriteFloat(node + 0x104, relative.Y);
            memory.WriteFloat(node + 0x118, 0.85f);
            memory.WriteUInt32(node + 0x168, visible ? 0x800u : 0u);
            memory.WriteFloat(node + 0x270, 40f);
            memory.WriteFloat(node + 0x274, 40f);
            memory.WriteInt32(node + 0x310, grid.X);
            memory.WriteInt32(node + 0x314, grid.Y);
            memory.WriteByte(node + 0x31E, biome);
            memory.WriteByte(node + 0x31F, visible ? (byte)0x03 : (byte)0x00);
            memory.WriteByte(node + 0x329, status);
            memory.WritePointer(storage + 0x20, data);
            memory.WriteByte(data + 0x2BF, status);
            memory.WritePointer(node + 0x2F0, mapRow);
            memory.WritePointer(mapRow + 0x00, worldArea);
            memory.WritePointer(worldArea + 0x00, mapCodePtr);
            memory.WritePointer(worldArea + 0x08, mapNamePtr);
            memory.WriteUtf16(mapCodePtr, mapCode);
            memory.WriteUtf16(mapNamePtr, mapName);

            if (content is not null)
            {
                var contentRow = node + 0x3400;
                var contentName = node + 0x3500;
                var stats = node + 0x3600;
                var statId = node + 0x3700;
                memory.WritePointer(node + 0x300, mapRow + 0x100);
                memory.WritePointer(mapRow + 0x100 + 0x38, contentRow);
                memory.WritePointer(contentRow + 0x30, contentName);
                memory.WriteUtf16(contentName, content);
                memory.Fill(stats, 0x100);
                memory.WritePointer(mapRow + 0x100 + 0x50, stats);
                memory.WritePointer(stats, statId);
                memory.WriteAscii(statId, "map_atlas_node_has_breach");
            }
        }

        public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
        {
            for (var index = 0; index < bytes.Length; index++)
            {
                _bytes[address + index] = bytes[index];
            }
        }

        public void Fill(nint address, int length)
        {
            for (var index = 0; index < length; index++)
            {
                _bytes[address + index] = 0;
            }
        }

        public void WritePointer(nint address, nint value)
            => WriteBytes(address, BitConverter.GetBytes(value.ToInt64()));

        public void WriteInt32(nint address, int value)
            => WriteBytes(address, BitConverter.GetBytes(value));

        public void WriteUInt32(nint address, uint value)
            => WriteBytes(address, BitConverter.GetBytes(value));

        public void WriteByte(nint address, byte value)
            => _bytes[address] = value;

        public void WriteFloat(nint address, float value)
            => WriteBytes(address, BitConverter.GetBytes(value));

        public void WriteUtf16(nint address, string value)
            => WriteBytes(address, Encoding.Unicode.GetBytes(value + "\0"));

        public void WriteAscii(nint address, string value)
            => WriteBytes(address, Encoding.ASCII.GetBytes(value + "\0"));

        public bool TryRead(nint address, Span<byte> destination)
        {
            ReadCount++;
            for (var index = 0; index < destination.Length; index++)
            {
                if (!_bytes.TryGetValue(address + index, out var value))
                {
                    return false;
                }

                destination[index] = value;
            }

            return true;
        }

        public bool TryReadInt32(nint address, out int value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(int)];
            if (!TryRead(address, buffer))
            {
                value = default;
                return false;
            }

            value = MemoryMarshal.Read<int>(buffer);
            return true;
        }

        public bool TryReadInt64(nint address, out long value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(long)];
            if (!TryRead(address, buffer))
            {
                value = default;
                return false;
            }

            value = MemoryMarshal.Read<long>(buffer);
            return true;
        }

        public bool TryReadFloat(nint address, out float value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(float)];
            if (!TryRead(address, buffer))
            {
                value = default;
                return false;
            }

            value = MemoryMarshal.Read<float>(buffer);
            return true;
        }

        public bool TryReadPointer(nint address, out nint value)
        {
            if (!TryReadInt64(address, out var raw))
            {
                value = 0;
                return false;
            }

            value = (nint)raw;
            return true;
        }

        public bool TryReadUtf16(nint address, int maxChars, out string? value)
        {
            var chars = new StringBuilder();
            Span<byte> character = stackalloc byte[sizeof(char)];
            for (var index = 0; index < maxChars; index++)
            {
                if (!TryRead(address + index * sizeof(char), character))
                {
                    value = null;
                    return false;
                }

                var current = BitConverter.ToChar(character);
                if (current == '\0')
                {
                    break;
                }

                chars.Append(current);
            }

            value = chars.ToString();
            return true;
        }

        public void Dispose()
        {
        }
    }
}
