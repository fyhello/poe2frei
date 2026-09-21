using System.Collections.Immutable;
using System.Numerics;
using System.Reflection;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Contracts;
using FreiAtlas.Core.Settings;
using FreiAtlas.Plugin.AreaMap;

namespace FreiAtlas.Plugin.AreaMap.Tests;

public sealed class AreaMapOverlayPluginTests
{
    private static readonly AreaMapProjectionProfile Profile = new(
        "test-profile",
        new AreaMapProjectionParameters(
            AreaMapViewKind.LargeMap,
            AreaMapLinearTransform.Identity,
            AreaMapLinearTransform.Identity,
            Vector2.Zero),
        new AreaMapProjectionParameters(
            AreaMapViewKind.MiniMap,
            AreaMapLinearTransform.Identity,
            AreaMapLinearTransform.Identity,
            Vector2.Zero),
        0.08f);

    [Theory]
    [InlineData(AreaMapSnapshotStatus.Stable, AreaMapViewKind.LargeMap)]
    [InlineData(AreaMapSnapshotStatus.Degraded, AreaMapViewKind.MiniMap)]
    public void Build_CreatesSceneForUsableSnapshotAndOneVerifiedVisibleView(
        AreaMapSnapshotStatus status,
        AreaMapViewKind kind)
    {
        var snapshot = Snapshot(
            status: status,
            views: ViewsWithOnly(kind),
            contents:
            [
                Content(
                    "boss-1",
                    AreaContentKind.Boss,
                    AreaContentPhase.Available,
                    Vector2.Zero)
            ]);

        var scene = Assert.IsType<AreaMapOverlayScene>(Build(snapshot));

        Assert.Equal(kind, scene.ViewKind);
        Assert.Equal(View(kind).Viewport, scene.Viewport);
        var placement = Assert.Single(scene.Placements);
        Assert.Equal("boss-1", placement.InstanceId);
        Assert.Equal(AreaContentKind.Boss, placement.Kind);
        Assert.Equal(AreaMapOverlayMarkerKind.Boss, placement.MarkerKind);
        Assert.Equal(AreaContentPhase.Available, placement.Phase);
        Assert.Equal(new Vector2(100, 50), placement.Center);
        Assert.Equal(AreaMapOverlayVisualState.BossAvailable, placement.VisualState);
    }

    [Theory]
    [InlineData(AreaMapSnapshotStatus.Detached)]
    [InlineData(AreaMapSnapshotStatus.Loading)]
    public void Build_RejectsSnapshotsThatAreNotStableOrDegraded(AreaMapSnapshotStatus status)
    {
        Assert.Null(Build(Snapshot(status: status)));
    }

    [Fact]
    public void Build_RejectsMissingAreaIdentityOrPlayer()
    {
        var valid = Snapshot();

        Assert.Null(Build(valid with { Area = valid.Area with { AreaHash = 0 } }));
        Assert.Null(Build(valid with { Player = null }));
    }

    [Theory]
    [InlineData(float.NaN, 0f)]
    [InlineData(0f, float.PositiveInfinity)]
    public void Build_RejectsNonFinitePlayerGridPosition(float x, float y)
    {
        var snapshot = Snapshot();

        Assert.Null(Build(snapshot with
        {
            Player = snapshot.Player! with { GridPosition = new Vector2(x, y) }
        }));
    }

    [Fact]
    public void Build_RejectsNonFinitePlayerWorldPosition()
    {
        var snapshot = Snapshot();

        Assert.Null(Build(snapshot with
        {
            Player = snapshot.Player! with
            {
                WorldPosition = new Vector3(0f, float.NaN, 0f)
            }
        }));
    }

    [Fact]
    public void Build_RejectsMismatchedProfile()
    {
        var snapshot = Snapshot() with { ProfileId = "another-profile" };

        Assert.Null(Build(snapshot));
    }

    [Fact]
    public void Build_RequiresExactlyOneVerifiedVisibleView()
    {
        var large = View(AreaMapViewKind.LargeMap);
        var mini = View(AreaMapViewKind.MiniMap);
        AreaMapViewsSnapshot[] invalidViews =
        [
            new(large with { IsVisible = false }, mini with { IsVisible = false }),
            new(large, mini),
            new(
                large with { Availability = AreaMapViewAvailability.Unverified },
                mini with { IsVisible = false }),
            new(
                large with { IsVisible = false },
                mini with { Availability = AreaMapViewAvailability.Unavailable })
        ];

        foreach (var views in invalidViews)
        {
            Assert.Null(Build(Snapshot(views: views)));
        }
    }

    [Fact]
    public void Build_RejectsInvalidActiveViewGeometryAndRotation()
    {
        var valid = View(AreaMapViewKind.MiniMap);
        AreaMapViewSnapshot[] invalidViews =
        [
            valid with { Zoom = 0f },
            valid with { Zoom = float.PositiveInfinity },
            valid with { Shift = new Vector2(float.NaN, 0f) },
            valid with { Shift = new Vector2(0f, float.NegativeInfinity) },
            valid with { Viewport = null },
            valid with { Viewport = new AreaUiRect(0f, 0f, 0f, 100f) },
            valid with { Viewport = new AreaUiRect(0f, 0f, 200f, float.NaN) },
            valid with { RotationRadians = 0.01f },
            valid with { RotationRadians = float.NaN },
            valid with { RotatesWithPlayer = true }
        ];

        foreach (var view in invalidViews)
        {
            Assert.Null(Build(Snapshot(views: ViewsWithOnly(view))));
        }
    }

    [Fact]
    public void Build_IgnoresInvalidParametersOnInactiveView()
    {
        var large = View(AreaMapViewKind.LargeMap);
        var inactiveMini = View(AreaMapViewKind.MiniMap) with
        {
            IsVisible = false,
            Zoom = float.NaN,
            Shift = new Vector2(float.NaN, float.PositiveInfinity),
            RotationRadians = 1f,
            RotatesWithPlayer = true,
            Viewport = new AreaUiRect(float.NaN, 0f, -1f, 0f)
        };

        var scene = Build(Snapshot(views: new AreaMapViewsSnapshot(large, inactiveMini)));

        Assert.NotNull(scene);
        Assert.Equal(AreaMapViewKind.LargeMap, scene.ViewKind);
    }

    [Fact]
    public void Build_ProjectsAllSupportedContentKinds()
    {
        AreaContentSnapshot[] contents =
        [
            Content("candidate", AreaContentKind.BossCandidate, AreaContentPhase.Unknown, Vector2.Zero),
            Content("boss", AreaContentKind.Boss, AreaContentPhase.Active, Vector2.Zero),
            Content("expedition", AreaContentKind.Expedition, AreaContentPhase.Available, Vector2.Zero),
            Content("ritual", AreaContentKind.Ritual, AreaContentPhase.Available, Vector2.Zero),
            Content("abyss", AreaContentKind.Abyss, AreaContentPhase.Available, Vector2.Zero),
            Content("breach", AreaContentKind.Breach, AreaContentPhase.Available, Vector2.Zero),
            Content("essence", AreaContentKind.Essence, AreaContentPhase.Available, Vector2.Zero),
            Content("incursion", AreaContentKind.Incursion, AreaContentPhase.Available, Vector2.Zero),
            Content("strongbox", AreaContentKind.Strongbox, AreaContentPhase.Available, Vector2.Zero),
            Content("unknown", AreaContentKind.Unknown, AreaContentPhase.Available, Vector2.Zero)
        ];

        var scene = Assert.IsType<AreaMapOverlayScene>(Build(Snapshot(contents: contents)));

        Assert.Equal(
            ["candidate", "boss", "expedition", "ritual", "abyss", "breach", "essence", "incursion", "strongbox"],
            scene.Placements.Select(placement => placement.InstanceId));
    }

    [Fact]
    public void Build_HidesExpeditionWithoutAffectingBossContent()
    {
        var scene = Assert.IsType<AreaMapOverlayScene>(Build(
            Snapshot(contents:
            [
                Content("candidate", AreaContentKind.BossCandidate, AreaContentPhase.Unknown, Vector2.Zero),
                Content("boss", AreaContentKind.Boss, AreaContentPhase.Active, new Vector2(10, 0)),
                Content("expedition", AreaContentKind.Expedition, AreaContentPhase.Available, new Vector2(-10, 0))
            ]),
            settings: new AreaMapDisplaySettings(false, true, 13f)));

        Assert.Equal(
            ["candidate", "boss"],
            scene.Placements.Select(placement => placement.InstanceId));
    }

    [Fact]
    public void Build_HidesBossAndCandidateWithoutAffectingExpedition()
    {
        var scene = Assert.IsType<AreaMapOverlayScene>(Build(
            Snapshot(contents:
            [
                Content("candidate", AreaContentKind.BossCandidate, AreaContentPhase.Unknown, Vector2.Zero),
                Content("boss", AreaContentKind.Boss, AreaContentPhase.Active, new Vector2(10, 0)),
                Content("expedition", AreaContentKind.Expedition, AreaContentPhase.Available, new Vector2(-10, 0))
            ]),
            settings: new AreaMapDisplaySettings(true, false, 13f)));

        Assert.Equal(
            ["expedition"],
            scene.Placements.Select(placement => placement.InstanceId));
    }

    [Fact]
    public void Build_HidesAllSupportedContentWhenBothFiltersAreDisabled()
    {
        var scene = Assert.IsType<AreaMapOverlayScene>(Build(
            Snapshot(contents:
            [
                Content("boss", AreaContentKind.Boss, AreaContentPhase.Active, Vector2.Zero),
                Content("expedition", AreaContentKind.Expedition, AreaContentPhase.Available, new Vector2(-10, 0))
            ]),
            settings: new AreaMapDisplaySettings(false, false, 13f)));

        Assert.Empty(scene.Placements);
    }

    [Fact]
    public void Build_RejectsRawProjectedCenterOutsideViewport()
    {
        AreaContentSnapshot[] contents =
        [
            Content("left", AreaContentKind.Boss, AreaContentPhase.Available, new Vector2(-101, 0)),
            Content("right", AreaContentKind.Boss, AreaContentPhase.Available, new Vector2(101, 0)),
            Content("top", AreaContentKind.Boss, AreaContentPhase.Available, new Vector2(0, -51)),
            Content("bottom", AreaContentKind.Boss, AreaContentPhase.Available, new Vector2(0, 51)),
            Content("edge", AreaContentKind.Boss, AreaContentPhase.Available, new Vector2(100, 50))
        ];

        var scene = Assert.IsType<AreaMapOverlayScene>(Build(Snapshot(contents: contents)));

        var placement = Assert.Single(scene.Placements);
        Assert.Equal("edge", placement.InstanceId);
    }

    [Fact]
    public void Build_ClampsVisibleMiniMapCenterByConfiguredViewportHeightInset()
    {
        var content = Content(
            "near-edge",
            AreaContentKind.Expedition,
            AreaContentPhase.Selected,
            new Vector2(-99, 49));

        var scene = Assert.IsType<AreaMapOverlayScene>(Build(Snapshot(
            views: ViewsWithOnly(AreaMapViewKind.MiniMap),
            contents: [content])));

        var placement = Assert.Single(scene.Placements);
        Assert.Equal(new Vector2(8, 92), placement.Center);
    }

    [Fact]
    public void Build_DoesNotClampVisibleLargeMapCenter()
    {
        var content = Content(
            "near-edge",
            AreaContentKind.Expedition,
            AreaContentPhase.Selected,
            new Vector2(-99, 49));

        var scene = Assert.IsType<AreaMapOverlayScene>(Build(Snapshot(
            views: ViewsWithOnly(AreaMapViewKind.LargeMap),
            contents: [content])));

        var placement = Assert.Single(scene.Placements);
        Assert.Equal(new Vector2(1, 99), placement.Center);
    }

    [Theory]
    [InlineData(AreaContentKind.BossCandidate, AreaContentPhase.Unknown, AreaMapOverlayVisualState.BossInactive)]
    [InlineData(AreaContentKind.Boss, AreaContentPhase.Available, AreaMapOverlayVisualState.BossAvailable)]
    [InlineData(AreaContentKind.Boss, AreaContentPhase.Active, AreaMapOverlayVisualState.BossAvailable)]
    [InlineData(AreaContentKind.Boss, AreaContentPhase.Selected, AreaMapOverlayVisualState.BossAvailable)]
    [InlineData(AreaContentKind.Boss, AreaContentPhase.Completed, AreaMapOverlayVisualState.BossCompleted)]
    [InlineData(AreaContentKind.Expedition, AreaContentPhase.Available, AreaMapOverlayVisualState.ExpeditionAvailable)]
    [InlineData(AreaContentKind.Expedition, AreaContentPhase.Selected, AreaMapOverlayVisualState.ExpeditionSelected)]
    [InlineData(AreaContentKind.Expedition, AreaContentPhase.Active, AreaMapOverlayVisualState.ExpeditionSelected)]
    [InlineData(AreaContentKind.Expedition, AreaContentPhase.Completed, AreaMapOverlayVisualState.ExpeditionCompleted)]
    [InlineData(AreaContentKind.Abyss, AreaContentPhase.Available, AreaMapOverlayVisualState.AbyssAvailable)]
    [InlineData(AreaContentKind.Abyss, AreaContentPhase.Completed, AreaMapOverlayVisualState.AbyssCompleted)]
    [InlineData(AreaContentKind.Ritual, AreaContentPhase.Available, AreaMapOverlayVisualState.RitualAvailable)]
    [InlineData(AreaContentKind.Ritual, AreaContentPhase.Completed, AreaMapOverlayVisualState.RitualCompleted)]
    [InlineData(AreaContentKind.Breach, AreaContentPhase.Available, AreaMapOverlayVisualState.BreachAvailable)]
    [InlineData(AreaContentKind.Breach, AreaContentPhase.Completed, AreaMapOverlayVisualState.BreachCompleted)]
    [InlineData(AreaContentKind.Essence, AreaContentPhase.Available, AreaMapOverlayVisualState.EssenceAvailable)]
    [InlineData(AreaContentKind.Essence, AreaContentPhase.Completed, AreaMapOverlayVisualState.EssenceCompleted)]
    [InlineData(AreaContentKind.Incursion, AreaContentPhase.Available, AreaMapOverlayVisualState.IncursionAvailable)]
    [InlineData(AreaContentKind.Strongbox, AreaContentPhase.Available, AreaMapOverlayVisualState.StrongboxAvailable)]
    public void Build_MapsSupportedContentToSemanticVisualState(
        AreaContentKind kind,
        AreaContentPhase phase,
        AreaMapOverlayVisualState expected)
    {
        var scene = Assert.IsType<AreaMapOverlayScene>(Build(Snapshot(contents:
        [
            Content("mapped", kind, phase, Vector2.Zero)
        ])));

        var placement = Assert.Single(scene.Placements);
        Assert.Equal(expected, placement.VisualState);
        Assert.Equal(kind, placement.Kind);
        Assert.Equal(phase, placement.Phase);
        Assert.Equal(ExpectedMarkerKind(kind), placement.MarkerKind);
    }

    [Theory]
    [InlineData(AreaContentKind.Boss, AreaContentPhase.Unknown)]
    [InlineData(AreaContentKind.Expedition, AreaContentPhase.Unknown)]
    public void Build_SkipsUnknownOrUnsupportedStateCombinations(
        AreaContentKind kind,
        AreaContentPhase phase)
    {
        var scene = Assert.IsType<AreaMapOverlayScene>(Build(Snapshot(contents:
        [
            Content("unsupported", kind, phase, Vector2.Zero)
        ])));

        Assert.Empty(scene.Placements);
    }

    [Fact]
    public void Build_DerivesOnlyLiveRareMonsterAndClosedRareOrUniqueChests()
    {
        AreaEntitySnapshot[] entities =
        [
            Entity(101, AreaEntityCategory.Monster, AreaEntityRarity.Rare, currentLife: 50),
            Entity(102, AreaEntityCategory.Monster, AreaEntityRarity.Rare, currentLife: 0),
            Entity(103, AreaEntityCategory.Monster, AreaEntityRarity.Unique, currentLife: 50),
            Entity(201, AreaEntityCategory.Chest, AreaEntityRarity.Rare, chestState: AreaChestState.Closed),
            Entity(202, AreaEntityCategory.Chest, AreaEntityRarity.Unique, chestState: AreaChestState.Closed),
            Entity(203, AreaEntityCategory.Chest, AreaEntityRarity.Rare, chestState: AreaChestState.Opened),
            Entity(204, AreaEntityCategory.Chest, AreaEntityRarity.Unique, chestState: AreaChestState.Unknown)
        ];

        var scene = Assert.IsType<AreaMapOverlayScene>(Build(Snapshot(entities: entities)));

        Assert.Equal(
            [
                AreaMapOverlayMarkerKind.RareMonster,
                AreaMapOverlayMarkerKind.RareChest,
                AreaMapOverlayMarkerKind.UniqueChest
            ],
            scene.Placements.Select(placement => placement.MarkerKind));
        Assert.All(scene.Placements, placement => Assert.NotNull(placement.SourceEntityId));
        Assert.All(scene.Placements, placement => Assert.Equal(AreaContentKind.Unknown, placement.Kind));
    }

    [Fact]
    public void Build_DoesNotDuplicateStrongboxAsRareChestEvenWhenStrongboxIsHidden()
    {
        const uint strongboxId = 301;
        var strongbox = Content(
            "strongbox",
            AreaContentKind.Strongbox,
            AreaContentPhase.Available,
            Vector2.Zero,
            sourceEntityId: strongboxId);
        var entity = Entity(
            strongboxId,
            AreaEntityCategory.Chest,
            AreaEntityRarity.Rare,
            chestState: AreaChestState.Closed);

        var visible = Assert.IsType<AreaMapOverlayScene>(Build(Snapshot(
            contents: [strongbox],
            entities: [entity])));
        var hidden = Assert.IsType<AreaMapOverlayScene>(Build(
            Snapshot(contents: [strongbox], entities: [entity]),
            settings: AtlasDisplaySettings.Default.AreaMap with { ShowStrongbox = false }));

        Assert.Equal(AreaMapOverlayMarkerKind.Strongbox, Assert.Single(visible.Placements).MarkerKind);
        Assert.Empty(hidden.Placements);
    }

    [Theory]
    [InlineData(nameof(AreaMapDisplaySettings.ShowAbyss), AreaContentKind.Abyss)]
    [InlineData(nameof(AreaMapDisplaySettings.ShowRitual), AreaContentKind.Ritual)]
    [InlineData(nameof(AreaMapDisplaySettings.ShowBreach), AreaContentKind.Breach)]
    [InlineData(nameof(AreaMapDisplaySettings.ShowEssence), AreaContentKind.Essence)]
    [InlineData(nameof(AreaMapDisplaySettings.ShowIncursion), AreaContentKind.Incursion)]
    [InlineData(nameof(AreaMapDisplaySettings.ShowStrongbox), AreaContentKind.Strongbox)]
    public void Build_AppliesEachMechanicSwitchIndependently(
        string settingName,
        AreaContentKind kind)
    {
        var settings = DisableSetting(AtlasDisplaySettings.Default.AreaMap, settingName);

        var scene = Assert.IsType<AreaMapOverlayScene>(Build(
            Snapshot(contents:
            [
                Content("target", kind, AreaContentPhase.Available, Vector2.Zero),
                Content("boss", AreaContentKind.Boss, AreaContentPhase.Available, new Vector2(1, 0))
            ]),
            settings: settings));

        Assert.Equal("boss", Assert.Single(scene.Placements).InstanceId);
    }

    [Theory]
    [InlineData(nameof(AreaMapDisplaySettings.ShowAbyss), AreaContentKind.Abyss)]
    [InlineData(nameof(AreaMapDisplaySettings.ShowBreach), AreaContentKind.Breach)]
    [InlineData(nameof(AreaMapDisplaySettings.ShowEssence), AreaContentKind.Essence)]
    public void Build_HidesAvailableAndCompletedMechanicStatesWhenSwitchIsDisabled(
        string settingName,
        AreaContentKind kind)
    {
        var settings = DisableSetting(AtlasDisplaySettings.Default.AreaMap, settingName);

        var scene = Assert.IsType<AreaMapOverlayScene>(Build(
            Snapshot(contents:
            [
                Content("available", kind, AreaContentPhase.Available, Vector2.Zero),
                Content("completed", kind, AreaContentPhase.Completed, new Vector2(1, 0)),
                Content("boss", AreaContentKind.Boss, AreaContentPhase.Available, new Vector2(2, 0))
            ]),
            settings: settings));

        Assert.Equal("boss", Assert.Single(scene.Placements).InstanceId);
    }

    [Fact]
    public void Build_AppliesRareMonsterAndRareChestSwitchesIndependently()
    {
        AreaEntitySnapshot[] entities =
        [
            Entity(401, AreaEntityCategory.Monster, AreaEntityRarity.Rare, currentLife: 50),
            Entity(402, AreaEntityCategory.Chest, AreaEntityRarity.Rare, chestState: AreaChestState.Closed)
        ];

        var noMonsters = Assert.IsType<AreaMapOverlayScene>(Build(
            Snapshot(entities: entities),
            settings: AtlasDisplaySettings.Default.AreaMap with { ShowRareMonster = false }));
        var noChests = Assert.IsType<AreaMapOverlayScene>(Build(
            Snapshot(entities: entities),
            settings: AtlasDisplaySettings.Default.AreaMap with { ShowRareChests = false }));

        Assert.Equal(AreaMapOverlayMarkerKind.RareChest, Assert.Single(noMonsters.Placements).MarkerKind);
        Assert.Equal(AreaMapOverlayMarkerKind.RareMonster, Assert.Single(noChests.Placements).MarkerKind);
    }

    [Fact]
    public void Build_ReadsCurrentExactlyOncePerFrame()
    {
        var current = Snapshot(contents:
        [
            Content("first", AreaContentKind.Boss, AreaContentPhase.Available, Vector2.Zero)
        ]);
        var later = current with { Status = AreaMapSnapshotStatus.Detached };
        var api = new SequencedAreaMapApi(current, later);
        var plugin = new AreaMapOverlayPlugin(api, Profile);

        var scene = Assert.IsType<AreaMapOverlayScene>(
            plugin.Build(AtlasDisplaySettings.Default.AreaMap));

        Assert.Equal(1, api.CurrentReads);
        Assert.Equal("first", Assert.Single(scene.Placements).InstanceId);
    }

    [Fact]
    public void Build_WithNullSettings_RejectsBeforeReadingCurrent()
    {
        var api = new SequencedAreaMapApi(Snapshot());
        var plugin = new AreaMapOverlayPlugin(api, Profile);

        Assert.Throws<ArgumentNullException>(() => plugin.Build(null!));
        Assert.Equal(0, api.CurrentReads);
    }

    [Fact]
    public void Build_PreservesIndependentInstanceIdsForMultipleContents()
    {
        var scene = Assert.IsType<AreaMapOverlayScene>(Build(Snapshot(contents:
        [
            Content("boss-a", AreaContentKind.Boss, AreaContentPhase.Available, new Vector2(-10, 0)),
            Content("boss-b", AreaContentKind.Boss, AreaContentPhase.Available, new Vector2(10, 0)),
            Content("expedition-a", AreaContentKind.Expedition, AreaContentPhase.Active, Vector2.Zero)
        ])));

        Assert.Equal(3, scene.Placements.Length);
        Assert.Equal(
            ["boss-a", "boss-b", "expedition-a"],
            scene.Placements.Select(placement => placement.InstanceId));
        Assert.Equal(3, scene.Placements.Select(placement => placement.Center).Distinct().Count());
    }

    [Fact]
    public void Build_DoesNotAttachExpeditionMarkerToBoss()
    {
        var scene = Assert.IsType<AreaMapOverlayScene>(Build(Snapshot(contents:
        [
            Content("boss", AreaContentKind.Boss, AreaContentPhase.Available, Vector2.Zero)
        ])));

        Assert.Null(Assert.Single(scene.Placements).ExpeditionMarker);
    }

    [Theory]
    [InlineData(AreaMapViewKind.LargeMap)]
    [InlineData(AreaMapViewKind.MiniMap)]
    public void Build_AttachesHoleCountAndFormattedValueOnlyToExpedition(
        AreaMapViewKind viewKind)
    {
        var provider = new RecordingValueProvider("6.5D");
        var plugin = new AreaMapOverlayPlugin(
            new SequencedAreaMapApi(Snapshot(
                views: ViewsWithOnly(viewKind),
                contents:
                [
                    Content(
                        "boss",
                        AreaContentKind.Boss,
                        AreaContentPhase.Available,
                        Vector2.Zero),
                    Content(
                        "expedition",
                        AreaContentKind.Expedition,
                        AreaContentPhase.Available,
                        new Vector2(10, 0),
                        6,
                        includeExpeditionDetails: true)
                ])),
            Profile,
            provider);

        var scene = Assert.IsType<AreaMapOverlayScene>(
            plugin.Build(AtlasDisplaySettings.Default.AreaMap));

        Assert.Null(scene.Placements.Single(p => p.Kind == AreaContentKind.Boss).ExpeditionMarker);
        var marker = scene.Placements
            .Single(p => p.Kind == AreaContentKind.Expedition)
            .ExpeditionMarker;
        Assert.NotNull(marker);
        Assert.Equal(6, marker!.HoleCount);
        Assert.Equal("6.5D", marker.ValueText);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public void PublicOutputModels_AreImmutable()
    {
        Assert.Equal(
            typeof(ImmutableArray<AreaMapOverlayPlacement>),
            typeof(AreaMapOverlayScene).GetProperty(nameof(AreaMapOverlayScene.Placements))!.PropertyType);
        Assert.All(
            typeof(AreaMapOverlayScene).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            property => Assert.Null(property.SetMethod));
        Assert.All(
            typeof(AreaMapOverlayPlacement).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            property => Assert.Null(property.SetMethod));
        Assert.All(
            typeof(AreaMapExpeditionMarker).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            property => Assert.Null(property.SetMethod));
    }

    [Fact]
    public void Plugin_HasOnlySnapshotProjectionAndOptionalValueProviderConstructor()
    {
        var constructor = Assert.Single(typeof(AreaMapOverlayPlugin).GetConstructors());

        Assert.Equal(
            [
                typeof(IAreaMapApi),
                typeof(AreaMapProjectionProfile),
                typeof(IAreaExpeditionValueProvider)
            ],
            constructor.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.True(constructor.GetParameters()[2].IsOptional);
    }

    private static AreaMapOverlayScene? Build(
        AreaMapSnapshot snapshot,
        AreaMapProjectionProfile? profile = null,
        AreaMapDisplaySettings? settings = null)
        => new AreaMapOverlayPlugin(
            new SequencedAreaMapApi(snapshot),
            profile ?? Profile).Build(settings ?? AtlasDisplaySettings.Default.AreaMap);

    private static AreaMapSnapshot Snapshot(
        AreaMapSnapshotStatus status = AreaMapSnapshotStatus.Stable,
        AreaMapViewsSnapshot? views = null,
        IReadOnlyList<AreaContentSnapshot>? contents = null,
        IReadOnlyList<AreaEntitySnapshot>? entities = null)
        => new(
            DateTimeOffset.UnixEpoch,
            42,
            Profile.ProfileId,
            status,
            new AreaIdentity(123, "test-area", 80, 7),
            new AreaPlayerSnapshot("Test", 90, Vector3.Zero, Vector2.Zero),
            entities ?? [],
            contents ?? [],
            [],
            null,
            views ?? ViewsWithOnly(AreaMapViewKind.LargeMap),
            []);

    private static AreaMapViewsSnapshot ViewsWithOnly(AreaMapViewKind kind)
        => kind == AreaMapViewKind.LargeMap
            ? new(
                View(AreaMapViewKind.LargeMap),
                View(AreaMapViewKind.MiniMap) with { IsVisible = false })
            : new(
                View(AreaMapViewKind.LargeMap) with { IsVisible = false },
                View(AreaMapViewKind.MiniMap));

    private static AreaMapViewsSnapshot ViewsWithOnly(AreaMapViewSnapshot view)
        => view.Kind == AreaMapViewKind.LargeMap
            ? new(view, View(AreaMapViewKind.MiniMap) with { IsVisible = false })
            : new(View(AreaMapViewKind.LargeMap) with { IsVisible = false }, view);

    private static AreaMapViewSnapshot View(AreaMapViewKind kind)
        => new(
            kind,
            AreaMapViewAvailability.Verified,
            true,
            Vector2.Zero,
            0.01f,
            0f,
            false,
            new AreaUiRect(0f, 0f, 200f, 100f),
            1f);

    private static AreaContentSnapshot Content(
        string instanceId,
        AreaContentKind kind,
        AreaContentPhase phase,
        Vector2 gridPosition,
        int? holeCount = null,
        bool includeExpeditionDetails = false,
        uint? sourceEntityId = null)
        => new(
            instanceId,
            $"content-{instanceId}",
            instanceId,
            kind,
            phase,
            Vector3.Zero,
            gridPosition,
            1f,
            sourceEntityId,
            [],
            includeExpeditionDetails
                ? new AreaExpeditionDetails(holeCount)
                : null);

    private static AreaEntitySnapshot Entity(
        uint entityId,
        AreaEntityCategory category,
        AreaEntityRarity rarity,
        int currentLife = 0,
        AreaChestState chestState = AreaChestState.NotApplicable)
        => new(
            entityId,
            $"Metadata/Test/{entityId}",
            $"Entity {entityId}",
            category,
            Vector3.Zero,
            new Vector2(entityId % 10, entityId % 7),
            category == AreaEntityCategory.Monster
                ? AreaEntityDisposition.Hostile
                : AreaEntityDisposition.Neutral,
            rarity,
            currentLife,
            category == AreaEntityCategory.Monster ? 100 : 0,
            false,
            false,
            chestState,
            []);

    private static AreaMapOverlayMarkerKind ExpectedMarkerKind(AreaContentKind kind)
        => kind switch
        {
            AreaContentKind.BossCandidate or AreaContentKind.Boss => AreaMapOverlayMarkerKind.Boss,
            AreaContentKind.Expedition => AreaMapOverlayMarkerKind.Expedition,
            AreaContentKind.Abyss => AreaMapOverlayMarkerKind.Abyss,
            AreaContentKind.Ritual => AreaMapOverlayMarkerKind.Ritual,
            AreaContentKind.Breach => AreaMapOverlayMarkerKind.Breach,
            AreaContentKind.Essence => AreaMapOverlayMarkerKind.Essence,
            AreaContentKind.Incursion => AreaMapOverlayMarkerKind.Incursion,
            AreaContentKind.Strongbox => AreaMapOverlayMarkerKind.Strongbox,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private static AreaMapDisplaySettings DisableSetting(
        AreaMapDisplaySettings settings,
        string settingName)
        => settingName switch
        {
            nameof(AreaMapDisplaySettings.ShowAbyss) => settings with { ShowAbyss = false },
            nameof(AreaMapDisplaySettings.ShowRitual) => settings with { ShowRitual = false },
            nameof(AreaMapDisplaySettings.ShowBreach) => settings with { ShowBreach = false },
            nameof(AreaMapDisplaySettings.ShowEssence) => settings with { ShowEssence = false },
            nameof(AreaMapDisplaySettings.ShowIncursion) => settings with { ShowIncursion = false },
            nameof(AreaMapDisplaySettings.ShowStrongbox) => settings with { ShowStrongbox = false },
            _ => throw new ArgumentOutOfRangeException(nameof(settingName), settingName, null)
        };

    private sealed class SequencedAreaMapApi(params AreaMapSnapshot[] snapshots) : IAreaMapApi
    {
        private readonly AreaMapSnapshot[] _snapshots = snapshots;

        public int CurrentReads { get; private set; }

        public AreaMapSnapshot Current
        {
            get
            {
                var index = Math.Min(CurrentReads, _snapshots.Length - 1);
                CurrentReads++;
                return _snapshots[index];
            }
        }

        public event Action<AreaMapSnapshot>? SnapshotChanged
        {
            add { }
            remove { }
        }
    }

    private sealed class RecordingValueProvider(string valueText)
        : IAreaExpeditionValueProvider
    {
        public int Calls { get; private set; }

        public string? GetValueText(AreaExpeditionDetails? details)
        {
            Calls++;
            return valueText;
        }
    }
}
