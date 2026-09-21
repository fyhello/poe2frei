using FreiAtlas.Atlas.Memory;
using FreiAtlas.Atlas.Metadata;
using FreiAtlas.Core.Atlas;
using System.Numerics;

namespace FreiAtlas.Atlas.Tests;

public sealed class AtlasUiTreeProviderTests
{
    [Fact]
    public void Read_BuildsProjectionFromCanvasTransformAndViewport()
    {
        var source = new FakeAtlasUiSource(
            new AtlasUiTreeSnapshot(
                HasUiRoot: true,
                HasAtlasCanvas: true,
                CanvasToken: "atlas-canvas-test",
                Scale: 0.85f,
                Nodes:
                [
                    TestNode(new AtlasGridPos(0, 0), "MapBoss")
                ],
                Edges: [],
                CurrentGrid: null,
                CanvasRelativePosition: new Vector2(10, 20),
                CanvasScale: 1f,
                NodeSize: new Vector2(40, 40)));
        var provider = new AtlasUiTreeProvider(
            source,
            AtlasLayoutProfile.Default,
            () => new AtlasViewport(1920, 1080));

        var snapshot = provider.Read();

        var factor = 1080f / 1600f * 0.85f;
        Assert.Equal(factor, snapshot.Projection.ScaleX, 5);
        Assert.Equal(factor, snapshot.Projection.ScaleY, 5);
        Assert.Equal(0f, snapshot.Projection.OffsetX, 5);
        Assert.Equal(0f, snapshot.Projection.OffsetY, 5);
    }

    [Fact]
    public void Read_DoesNotMultiplyDuplicateCanvasAndNodeScales()
    {
        var source = new FakeAtlasUiSource(
            new AtlasUiTreeSnapshot(
                HasUiRoot: true,
                HasAtlasCanvas: true,
                CanvasToken: "atlas-canvas-test",
                Scale: 0.85f,
                Nodes:
                [
                    TestNode(new AtlasGridPos(0, 0), "MapBoss")
                ],
                Edges: [],
                CurrentGrid: null,
                CanvasRelativePosition: Vector2.Zero,
                CanvasScale: 0.85f,
                NodeSize: new Vector2(40, 40)));
        var provider = new AtlasUiTreeProvider(
            source,
            AtlasLayoutProfile.Default,
            () => new AtlasViewport(1920, 1080));

        var snapshot = provider.Read();

        Assert.Equal(1080f / 1600f * 0.85f, snapshot.Projection.ScaleX, 5);
        Assert.Equal(1080f / 1600f * 0.85f, snapshot.Projection.ScaleY, 5);
    }

    [Fact]
    public void Read_SignatureIgnoresGeometryOnlyChanges()
    {
        var initialNode = TestNode(new AtlasGridPos(0, 0), "MapBoss");
        var initialTree = new AtlasUiTreeSnapshot(
            HasUiRoot: true,
            HasAtlasCanvas: true,
            CanvasToken: "atlas-canvas-test",
            Scale: 0.85f,
            Nodes: [initialNode],
            Edges: [],
            CurrentGrid: null,
            CanvasRelativePosition: Vector2.Zero,
            CanvasScale: 0.85f,
            NodeSize: new Vector2(40, 40));
        var source = new FakeAtlasUiSource(initialTree);
        var provider = new AtlasUiTreeProvider(source, AtlasLayoutProfile.Default);

        var first = provider.Read();
        source.Set(initialTree with
        {
            Nodes =
            [
                initialNode with
                {
                    RelativeX = 999.25f,
                    RelativeY = -222.5f
                }
            ],
            CanvasRelativePosition = new Vector2(40, 60),
            Scale = 1.1f
        });

        var second = provider.Read();

        Assert.Equal(first.Signature, second.Signature);
        Assert.Equal(999.25f, second.Nodes[0].RelativeX);
        Assert.Equal(-222.5f, second.Nodes[0].RelativeY);
    }

    [Fact]
    public void Read_SignatureIncludesDiscoveryStateChanges()
    {
        var initialNode = TestNode(
            new AtlasGridPos(0, 0),
            "MapBoss") with
        {
            IsDiscovered = false
        };
        var initialTree = new AtlasUiTreeSnapshot(
            HasUiRoot: true,
            HasAtlasCanvas: true,
            CanvasToken: "atlas-canvas-test",
            Scale: 0.85f,
            Nodes: [initialNode],
            Edges: [],
            CurrentGrid: null);
        var source = new FakeAtlasUiSource(initialTree);
        var provider = new AtlasUiTreeProvider(source, AtlasLayoutProfile.Default);

        var first = provider.Read();
        source.Set(initialTree with
        {
            Nodes = [initialNode with { IsDiscovered = true }]
        });

        var second = provider.Read();

        Assert.NotEqual(first.Signature, second.Signature);
    }

    [Fact]
    public void Read_PreservesObservedAtlasNodeCountAboveLegacyLimit()
    {
        const int observedNodeCount = 2063;
        var source = new FakeAtlasUiSource(
            new AtlasUiTreeSnapshot(
                HasUiRoot: true,
                HasAtlasCanvas: true,
                CanvasToken: "atlas-canvas-test",
                Scale: 1f,
                Nodes: Enumerable
                    .Range(0, observedNodeCount)
                    .Select(index => TestNode(
                        new AtlasGridPos(index, 0),
                        "MapBoss"))
                    .ToArray(),
                Edges: [],
                CurrentGrid: null));
        var provider = new AtlasUiTreeProvider(source, AtlasLayoutProfile.Default);

        var snapshot = provider.Read();

        Assert.Equal(observedNodeCount, snapshot.NodeCount);
        Assert.Equal(
            new AtlasGridPos(observedNodeCount - 1, 0),
            snapshot.Nodes[^1].Grid);
    }

    [Fact]
    public void Read_DoesNotDropRadarNodeClassMembersByUiSize()
    {
        var source = new FakeAtlasUiSource(
            new AtlasUiTreeSnapshot(
                HasUiRoot: true,
                HasAtlasCanvas: true,
                CanvasToken: "atlas-canvas-test",
                Scale: 1f,
                Nodes:
                [
                    TestNode(new AtlasGridPos(0, 0), "MapBoss") with
                    {
                        Width = 120,
                        Height = 165
                    }
                ],
                Edges: [],
                CurrentGrid: null));
        var provider = new AtlasUiTreeProvider(source, AtlasLayoutProfile.Default);

        var snapshot = provider.Read();

        Assert.Single(snapshot.Nodes);
    }

    [Fact]
    public void Read_NormalizesRadarRelativePositionForFoggedNode()
    {
        var hiddenGrid = new AtlasGridPos(1, 1);
        var source = new FakeAtlasUiSource(
            new AtlasUiTreeSnapshot(
                HasUiRoot: true,
                HasAtlasCanvas: true,
                CanvasToken: "atlas-canvas-test",
                Scale: 1f,
                Nodes:
                [
                    TestNode(new AtlasGridPos(0, 0), "MapBoss") with
                    {
                        RelativeX = 100,
                        RelativeY = 100
                    },
                    TestNode(new AtlasGridPos(1, 0), "MapBoss") with
                    {
                        RelativeX = 220,
                        RelativeY = 100
                    },
                    TestNode(new AtlasGridPos(0, 1), "MapBoss") with
                    {
                        RelativeX = 100,
                        RelativeY = 220
                    },
                    TestNode(hiddenGrid, "MapBoss") with
                    {
                        RelativeX = 999_999,
                        RelativeY = -999_999,
                        IsVisible = false,
                        IsDiscovered = false
                    }
                ],
                Edges: [],
                CurrentGrid: null));
        var provider = new AtlasUiTreeProvider(source, AtlasLayoutProfile.Default);

        var snapshot = provider.Read();

        var hiddenNode = Assert.Single(
            snapshot.Nodes,
            node => node.Grid == hiddenGrid);
        Assert.Equal(220f, hiddenNode.RelativeX, 3);
        Assert.Equal(220f, hiddenNode.RelativeY, 3);
    }

    [Fact]
    public void Read_NormalizesRadarRelativePositionForDiscoveredHiddenNode()
    {
        var hiddenGrid = new AtlasGridPos(1, 1);
        var source = new FakeAtlasUiSource(
            new AtlasUiTreeSnapshot(
                HasUiRoot: true,
                HasAtlasCanvas: true,
                CanvasToken: "atlas-canvas-test",
                Scale: 1f,
                Nodes:
                [
                    TestNode(new AtlasGridPos(0, 0), "MapBoss") with
                    {
                        RelativeX = 100,
                        RelativeY = 100
                    },
                    TestNode(new AtlasGridPos(1, 0), "MapBoss") with
                    {
                        RelativeX = 220,
                        RelativeY = 100
                    },
                    TestNode(new AtlasGridPos(0, 1), "MapBoss") with
                    {
                        RelativeX = 100,
                        RelativeY = 220
                    },
                    TestNode(hiddenGrid, "MapBoss") with
                    {
                        RelativeX = 999_999,
                        RelativeY = -999_999,
                        IsVisible = false,
                        IsDiscovered = true
                    }
                ],
                Edges: [],
                CurrentGrid: null));
        var provider = new AtlasUiTreeProvider(source, AtlasLayoutProfile.Default);

        var snapshot = provider.Read();

        var hiddenNode = Assert.Single(
            snapshot.Nodes,
            node => node.Grid == hiddenGrid);
        Assert.Equal(220f, hiddenNode.RelativeX, 3);
        Assert.Equal(220f, hiddenNode.RelativeY, 3);
    }

    [Fact]
    public void Read_NormalizesFoggedOutlierFromVisibleGridGeometry()
    {
        var hiddenGrid = new AtlasGridPos(1, 1);
        var source = new FakeAtlasUiSource(
            new AtlasUiTreeSnapshot(
                HasUiRoot: true,
                HasAtlasCanvas: true,
                CanvasToken: "atlas-canvas-test",
                Scale: 1f,
                Nodes:
                [
                    TestNode(new AtlasGridPos(0, 0), "MapBoss") with
                    {
                        RelativeX = 100,
                        RelativeY = 100
                    },
                    TestNode(new AtlasGridPos(1, 0), "MapBoss") with
                    {
                        RelativeX = 220,
                        RelativeY = 100
                    },
                    TestNode(new AtlasGridPos(0, 1), "MapBoss") with
                    {
                        RelativeX = 100,
                        RelativeY = 220
                    },
                    TestNode(hiddenGrid, "MapBoss") with
                    {
                        RelativeX = 999_999,
                        RelativeY = -999_999,
                        IsVisible = false,
                        IsDiscovered = false
                    }
                ],
                Edges: [],
                CurrentGrid: null));
        var provider = new AtlasUiTreeProvider(source, AtlasLayoutProfile.Default);

        var snapshot = provider.Read();

        var hiddenNode = Assert.Single(
            snapshot.Nodes,
            node => node.Grid == hiddenGrid);
        Assert.Equal(220f, hiddenNode.RelativeX, 3);
        Assert.Equal(220f, hiddenNode.RelativeY, 3);
    }

    [Fact]
    public void Read_DeduplicatesNodesAndIgnoresInvalidReferences()
    {
        var source = new FakeAtlasUiSource(
            new AtlasUiTreeSnapshot(
                HasUiRoot: true,
                HasAtlasCanvas: true,
                CanvasToken: "atlas-canvas-test",
                Scale: 1.25f,
                Nodes:
                [
                    new AtlasUiNodeData(
                        new AtlasGridPos(1, 2),
                        "map-a",
                        "地图 A",
                        100,
                        200,
                        40,
                        40,
                        true,
                        true,
                        false,
                        true,
                        ["MapBoss"]),
                    new AtlasUiNodeData(
                        new AtlasGridPos(1, 2),
                        "map-a",
                        "地图 A 更新",
                        101,
                        201,
                        40,
                        40,
                        true,
                        true,
                        true,
                        true,
                        []),
                    new AtlasUiNodeData(
                        new AtlasGridPos(9, 9),
                        "invalid-size",
                        "无效节点",
                        300,
                        400,
                        32,
                        40,
                        true,
                        false,
                        false,
                        false,
                        [])
                ],
                Edges:
                [
                    new AtlasUiEdgeData(
                        new AtlasGridPos(1, 2),
                        new AtlasGridPos(2, 2)),
                    new AtlasUiEdgeData(
                        new AtlasGridPos(1, 2),
                        new AtlasGridPos(99, 99))
                ],
                CurrentGrid: new AtlasGridPos(1, 2)));

        var provider = new AtlasUiTreeProvider(source, AtlasLayoutProfile.Default);

        var snapshot = provider.Read();

        Assert.Equal(2, snapshot.NodeCount);
        Assert.Equal(2, snapshot.Nodes.Count);
        Assert.Equal("地图 A 更新", snapshot.Nodes[0].DisplayName);
        Assert.Empty(snapshot.Nodes[0].Contents);
        Assert.Empty(snapshot.Edges);
        Assert.Contains(provider.Diagnostics, message => message.Contains("edge", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new AtlasGridPos(1, 2), snapshot.CurrentGrid);
        Assert.Equal(AtlasSnapshotStatus.Loading, snapshot.Status);
    }

    [Fact]
    public void Read_PublishesValidEdgesAndDecodesContent()
    {
        var source = new FakeAtlasUiSource(
            new AtlasUiTreeSnapshot(
                HasUiRoot: true,
                HasAtlasCanvas: true,
                CanvasToken: "atlas-canvas-test",
                Scale: 1f,
                Nodes:
                [
                    TestNode(new AtlasGridPos(0, 0), "Breach"),
                    TestNode(new AtlasGridPos(1, 0), "Delirium")
                ],
                Edges:
                [
                    new AtlasUiEdgeData(
                        new AtlasGridPos(1, 0),
                        new AtlasGridPos(0, 0))
                ],
                CurrentGrid: null));

        var provider = new AtlasUiTreeProvider(source, AtlasLayoutProfile.Default);

        var snapshot = provider.Read();

        Assert.Equal(2, snapshot.NodeCount);
        Assert.Single(snapshot.Edges);
        Assert.Equal(new AtlasGridPos(0, 0), snapshot.Edges[0].From);
        Assert.Equal(new AtlasGridPos(1, 0), snapshot.Edges[0].To);
        Assert.Contains(snapshot.Nodes[0].Contents, content => content.ContentId == "breach");
        Assert.Contains(snapshot.Nodes[1].Contents, content => content.ContentId == "delirium");
    }

    [Theory]
    [InlineData("死境探險")]
    [InlineData("大型探險")]
    public void Read_DecodesLocalizedExpeditionContent(string rawContent)
    {
        var source = new FakeAtlasUiSource(
            new AtlasUiTreeSnapshot(
                HasUiRoot: true,
                HasAtlasCanvas: true,
                CanvasToken: "atlas-canvas-test",
                Scale: 1f,
                Nodes:
                [
                    TestNode(new AtlasGridPos(0, 0), rawContent)
                ],
                Edges: [],
                CurrentGrid: null));
        var provider = new AtlasUiTreeProvider(source, AtlasLayoutProfile.Default);

        var snapshot = provider.Read();

        var content = Assert.Single(snapshot.Nodes[0].Contents);
        Assert.Equal("expedition", content.ContentId);
        Assert.Equal("AtlasIconContentExpedition", content.ReferenceIconId);
    }

    [Fact]
    public void Read_InfersExpeditionFromLogbookMapMetadata()
    {
        var source = new FakeAtlasUiSource(
            new AtlasUiTreeSnapshot(
                HasUiRoot: true,
                HasAtlasCanvas: true,
                CanvasToken: "atlas-canvas-test",
                Scale: 1f,
                Nodes:
                [
                    TestNode(new AtlasGridPos(-105, -75), string.Empty) with
                    {
                        MapId = "ExpeditionLogBook_Atoll",
                        DisplayName = "貧瘠環礁",
                        MapDataTags = ["expedition"],
                        RawContentValue = 0,
                        ContentVectorValues = [163_787_936, 4_417_993, 188_953_974],
                        IconType = 4,
                        RawFlags = 0
                    }
                ],
                Edges: [],
                CurrentGrid: null));
        var provider = new AtlasUiTreeProvider(source, AtlasLayoutProfile.Default);

        var snapshot = provider.Read();

        var content = Assert.Single(snapshot.Nodes[0].Contents);
        Assert.Equal("expedition", content.ContentId);
        Assert.Equal("AtlasIconContentExpedition", content.ReferenceIconId);
        Assert.Equal("atlas-map-metadata", content.Attributes["sourceField"]);
    }

    [Fact]
    public void Read_InfersBossAndExpeditionFromLockedHiddenTempleMetadata()
    {
        var source = new FakeAtlasUiSource(
            new AtlasUiTreeSnapshot(
                HasUiRoot: true,
                HasAtlasCanvas: true,
                CanvasToken: "atlas-canvas-test",
                Scale: 1f,
                Nodes:
                [
                    TestNode(new AtlasGridPos(-101, -60), string.Empty) with
                    {
                        MapId = "ExpeditionSubArea_UhtredBoss",
                        DisplayName = "隱密神廟",
                        IsVisible = false,
                        IsDiscovered = false,
                        MapDataTags = ["boss", "expedition"],
                        RawContentValue = 0,
                        ContentVectorValues = [],
                        IconType = 0,
                        RawFlags = 0x10
                    }
                ],
                Edges: [],
                CurrentGrid: null));
        var provider = new AtlasUiTreeProvider(source, AtlasLayoutProfile.Default);

        var snapshot = provider.Read();

        Assert.Collection(
            snapshot.Nodes[0].Contents,
            boss =>
            {
                Assert.Equal("map_boss", boss.ContentId);
                Assert.Equal("AtlasIconContentMapBoss", boss.ReferenceIconId);
                Assert.Equal("atlas-map-metadata", boss.Attributes["sourceField"]);
            },
            expedition => Assert.Equal("expedition", expedition.ContentId));
    }

    [Theory]
    [InlineData("ChayulaLeague_TowerBoss")]
    [InlineData("EndgameDoodad_BeastGroundZero")]
    [InlineData("ExpeditionSubArea_BlackKnightBoss")]
    [InlineData("ExpeditionSubArea_MedvedBoss")]
    [InlineData("ExpeditionSubArea_OlrothBoss")]
    [InlineData("RitualLeagueBoss")]
    [InlineData("ExpeditionSubArea_UhtredBoss")]
    [InlineData("ExpeditionSubArea_VoranaBoss")]
    public void Read_InfersBossForEveryBossTaggedSpecialMap(string mapId)
    {
        Assert.True(AtlasMetadataCatalog.Embedded.TryGetMap(mapId, out var metadata));
        Assert.Contains("boss", metadata.Tags);
        var source = new FakeAtlasUiSource(
            new AtlasUiTreeSnapshot(
                HasUiRoot: true,
                HasAtlasCanvas: true,
                CanvasToken: "atlas-canvas-test",
                Scale: 1f,
                Nodes:
                [
                    TestNode(new AtlasGridPos(0, 0), string.Empty) with
                    {
                        MapId = mapId,
                        DisplayName = metadata.Name,
                        IsVisible = false,
                        IsDiscovered = false,
                        MapDataTags = metadata.Tags,
                        RawContentValue = 0,
                        ContentVectorValues = [],
                        IconType = 0,
                        RawFlags = 0x10
                    }
                ],
                Edges: [],
                CurrentGrid: null));
        var provider = new AtlasUiTreeProvider(source, AtlasLayoutProfile.Default);

        var snapshot = provider.Read();

        var boss = Assert.Single(
            snapshot.Nodes[0].Contents,
            content => content.ContentId == "map_boss");
        Assert.Equal("atlas-map-metadata", boss.Attributes["sourceField"]);
    }

    [Fact]
    public void Read_DoesNotPublishDntPlaceholderAsContent()
    {
        var source = new FakeAtlasUiSource(
            new AtlasUiTreeSnapshot(
                HasUiRoot: true,
                HasAtlasCanvas: true,
                CanvasToken: "atlas-canvas-test",
                Scale: 1f,
                Nodes:
                [
                    TestNode(
                        new AtlasGridPos(0, 0),
                        "[DNT] Breach City - Not Shown to Players")
                ],
                Edges: [],
                CurrentGrid: null));

        var provider = new AtlasUiTreeProvider(source, AtlasLayoutProfile.Default);

        var snapshot = provider.Read();

        Assert.Empty(snapshot.Nodes[0].Contents);
    }

    [Fact]
    public void Read_InfersBossContentFromIconOnlyNode()
    {
        var source = new FakeAtlasUiSource(
            new AtlasUiTreeSnapshot(
                HasUiRoot: true,
                HasAtlasCanvas: true,
                CanvasToken: "atlas-canvas-test",
                Scale: 1f,
                Nodes:
                [
                    TestNode(new AtlasGridPos(0, 0), string.Empty) with
                    {
                        RawContentValue = 0,
                        IconType = 6,
                        RawFlags = 0x10
                    }
                ],
                Edges: [],
                CurrentGrid: null));
        var provider = new AtlasUiTreeProvider(source, AtlasLayoutProfile.Default);

        var snapshot = provider.Read();

        var content = Assert.Single(snapshot.Nodes[0].Contents);
        Assert.Equal("map_boss", content.ContentId);
        Assert.Equal("atlas-icon-type", content.Attributes["sourceField"]);
    }

    [Fact]
    public void Read_DoesNotDuplicateDecodedBossWhenFallbackSignalsAlsoExist()
    {
        var source = new FakeAtlasUiSource(
            new AtlasUiTreeSnapshot(
                HasUiRoot: true,
                HasAtlasCanvas: true,
                CanvasToken: "atlas-canvas-test",
                Scale: 1f,
                Nodes:
                [
                    TestNode(new AtlasGridPos(0, 0), "MapBoss") with
                    {
                        RawContentValue = 0,
                        IconType = 6,
                        RawFlags = 0x10
                    }
                ],
                Edges: [],
                CurrentGrid: null));
        var provider = new AtlasUiTreeProvider(source, AtlasLayoutProfile.Default);

        var snapshot = provider.Read();

        Assert.Single(
            snapshot.Nodes[0].Contents,
            content => content.ContentId == "map_boss");
    }

    [Fact]
    public void Read_ClassifiesEdgesFromAccessibleNodesAsReachable()
    {
        var source = new FakeAtlasUiSource(
            new AtlasUiTreeSnapshot(
                HasUiRoot: true,
                HasAtlasCanvas: true,
                CanvasToken: "atlas-canvas-test",
                Scale: 1f,
                Nodes:
                [
                    TestNode(
                        new AtlasGridPos(0, 0),
                        "Breach",
                        accessible: true),
                    TestNode(new AtlasGridPos(1, 0), "Delirium")
                ],
                Edges:
                [
                    new AtlasUiEdgeData(
                        new AtlasGridPos(0, 0),
                        new AtlasGridPos(1, 0))
                ],
                CurrentGrid: null));

        var provider = new AtlasUiTreeProvider(source, AtlasLayoutProfile.Default);

        var snapshot = provider.Read();

        var edge = Assert.Single(snapshot.Edges);
        Assert.Equal(AtlasEdgeState.Reachable, edge.State);
        Assert.Equal(AtlasEdgeColor.Green, edge.RenderColor);
    }

    [Fact]
    public void Read_WithoutAtlasCanvas_ReturnsLoadingSnapshotAndDiagnostic()
    {
        var source = new FakeAtlasUiSource(
            new AtlasUiTreeSnapshot(
                HasUiRoot: true,
                HasAtlasCanvas: false,
                CanvasToken: string.Empty,
                Scale: 1f,
                Nodes: [],
                Edges: [],
                CurrentGrid: null));

        var provider = new AtlasUiTreeProvider(source, AtlasLayoutProfile.Default);

        var snapshot = provider.Read();

        Assert.Equal(AtlasSnapshotStatus.Loading, snapshot.Status);
        Assert.Empty(snapshot.Nodes);
        Assert.Empty(snapshot.Edges);
        Assert.Contains(provider.Diagnostics, message => message.Contains("canvas", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RefreshProjection_RequestsAllCachedNodes()
    {
        var visibleGrid = new AtlasGridPos(0, 0);
        var offscreenGrid = new AtlasGridPos(1, 0);
        var source = new SelectiveFakeAtlasUiSource(
            new AtlasUiTreeSnapshot(
                HasUiRoot: true,
                HasAtlasCanvas: true,
                CanvasToken: "atlas-canvas-test",
                Scale: 1f,
                Nodes:
                [
                    TestNode(visibleGrid, "MapBoss") with
                    {
                        RelativeX = 200,
                        RelativeY = 200
                    },
                    TestNode(offscreenGrid, "MapBoss") with
                    {
                        RelativeX = 2200,
                        RelativeY = 2200
                    }
                ],
                Edges: [],
                CurrentGrid: null));
        var provider = new AtlasUiTreeProvider(
            source,
            AtlasLayoutProfile.Default,
            () => new AtlasViewport(1600, 1600));
        var snapshot = provider.Read() with
        {
            Status = AtlasSnapshotStatus.Stable
        };

        source.SetLiveGeometry(
            new AtlasUiLiveGeometry(
                new Dictionary<AtlasGridPos, Vector2>
                {
                    [visibleGrid] = new(240, 250),
                    [offscreenGrid] = new(2300, 2300)
                },
                new AtlasUiTransform(Vector2.Zero, 1f)));

        Assert.True(provider.TryRefreshProjection(snapshot, out var updated));
        Assert.Equal(
            new HashSet<AtlasGridPos> { visibleGrid, offscreenGrid },
            source.LastRequested);
        Assert.Equal(240f, updated.Nodes[0].RelativeX);
        Assert.Equal(250f, updated.Nodes[0].RelativeY);
        Assert.Equal(2300f, updated.Nodes[1].RelativeX);
        Assert.Equal(2300f, updated.Nodes[1].RelativeY);
    }

    [Fact]
    public void RefreshProjection_RequestsOffscreenNodesSoPanCanBringThemIntoView()
    {
        var visibleGrid = new AtlasGridPos(0, 0);
        var offscreenGrid = new AtlasGridPos(1, 0);
        var source = new SelectiveFakeAtlasUiSource(
            new AtlasUiTreeSnapshot(
                HasUiRoot: true,
                HasAtlasCanvas: true,
                CanvasToken: "atlas-canvas-test",
                Scale: 1f,
                Nodes:
                [
                    TestNode(visibleGrid, "MapBoss") with
                    {
                        RelativeX = 200,
                        RelativeY = 200
                    },
                    TestNode(offscreenGrid, "MapBoss") with
                    {
                        RelativeX = 2200,
                        RelativeY = 2200
                    }
                ],
                Edges: [],
                CurrentGrid: null));
        var provider = new AtlasUiTreeProvider(
            source,
            AtlasLayoutProfile.Default,
            () => new AtlasViewport(1600, 1600));
        var snapshot = provider.Read() with
        {
            Status = AtlasSnapshotStatus.Stable
        };

        source.SetLiveGeometry(
            new AtlasUiLiveGeometry(
                new Dictionary<AtlasGridPos, Vector2>
                {
                    [visibleGrid] = new(240, 250),
                    [offscreenGrid] = new(300, 320)
                },
                new AtlasUiTransform(Vector2.Zero, 1f)));

        Assert.True(provider.TryRefreshProjection(snapshot, out var updated));
        Assert.Contains(offscreenGrid, source.LastRequested);
        Assert.Equal(300f, updated.Nodes[1].RelativeX);
        Assert.Equal(320f, updated.Nodes[1].RelativeY);
    }

    [Fact]
    public void RefreshProjection_DoesNotReintroduceRawOutlierForFoggedNode()
    {
        var foggedGrid = new AtlasGridPos(1, 1);
        var source = new SelectiveFakeAtlasUiSource(
            new AtlasUiTreeSnapshot(
                HasUiRoot: true,
                HasAtlasCanvas: true,
                CanvasToken: "atlas-canvas-test",
                Scale: 1f,
                Nodes:
                [
                    TestNode(new AtlasGridPos(0, 0), "MapBoss") with
                    {
                        RelativeX = 100,
                        RelativeY = 100,
                        IsVisible = true,
                        IsDiscovered = true
                    },
                    TestNode(new AtlasGridPos(1, 0), "MapBoss") with
                    {
                        RelativeX = 220,
                        RelativeY = 100,
                        IsVisible = true,
                        IsDiscovered = true
                    },
                    TestNode(new AtlasGridPos(0, 1), "MapBoss") with
                    {
                        RelativeX = 100,
                        RelativeY = 220,
                        IsVisible = true,
                        IsDiscovered = true
                    },
                    TestNode(foggedGrid, "MapBoss") with
                    {
                        RelativeX = 220,
                        RelativeY = 220,
                        IsVisible = false,
                        IsDiscovered = false
                    }
                ],
                Edges: [],
                CurrentGrid: null));
        var provider = new AtlasUiTreeProvider(
            source,
            AtlasLayoutProfile.Default,
            () => new AtlasViewport(1600, 1600));
        var snapshot = provider.Read() with
        {
            Status = AtlasSnapshotStatus.Stable
        };

        source.SetLiveGeometry(
            new AtlasUiLiveGeometry(
                new Dictionary<AtlasGridPos, Vector2>
                {
                    [new AtlasGridPos(0, 0)] = new(100, 100),
                    [new AtlasGridPos(1, 0)] = new(220, 100),
                    [new AtlasGridPos(0, 1)] = new(100, 220),
                    [foggedGrid] = new(999_999, -999_999)
                },
                new AtlasUiTransform(Vector2.Zero, 1f)));

        Assert.True(provider.TryRefreshProjection(snapshot, out var updated));

        var foggedNode = Assert.Single(
            updated.Nodes,
            node => node.Grid == foggedGrid);
        Assert.Equal(220f, foggedNode.RelativeX, 3);
        Assert.Equal(220f, foggedNode.RelativeY, 3);
    }

    private static AtlasUiNodeData TestNode(
        AtlasGridPos grid,
        string rawContent,
        bool accessible = false)
        => new(
            grid,
            $"map-{grid.X}-{grid.Y}",
            $"地图 {grid.X},{grid.Y}",
            grid.X * 40,
            grid.Y * 40,
            40,
            40,
            true,
            accessible,
            false,
            false,
            [rawContent]);

    private sealed class FakeAtlasUiSource(AtlasUiTreeSnapshot snapshot) : IAtlasUiSource
    {
        private AtlasUiTreeSnapshot _snapshot = snapshot;

        public AtlasUiTreeSnapshot Read() => _snapshot;

        public void Set(AtlasUiTreeSnapshot value) => _snapshot = value;
    }

    private sealed class SelectiveFakeAtlasUiSource(AtlasUiTreeSnapshot snapshot)
        : IAtlasUiSource,
          IAtlasUiSelectiveLiveGeometrySource
    {
        private AtlasUiTreeSnapshot _snapshot = snapshot;
        private AtlasUiLiveGeometry _liveGeometry;

        public IReadOnlySet<AtlasGridPos> LastRequested { get; private set; } =
            new HashSet<AtlasGridPos>();

        public AtlasUiTreeSnapshot Read() => _snapshot;

        public void SetLiveGeometry(AtlasUiLiveGeometry geometry)
            => _liveGeometry = geometry;

        public bool TryReadLiveGeometry(
            IReadOnlySet<AtlasGridPos> grids,
            out AtlasUiLiveGeometry geometry)
        {
            LastRequested = grids;
            geometry = _liveGeometry with
            {
                NodePositions = _liveGeometry.NodePositions
                    .Where(pair => grids.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value)
            };
            return true;
        }

        public bool TryReadLiveGeometry(out AtlasUiLiveGeometry geometry)
        {
            geometry = _liveGeometry;
            return true;
        }
    }
}
