using System.Collections.Concurrent;
using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Game;

namespace FreiAtlas.Game.Tests;

public sealed class AreaMapProviderServiceTests
{
    [Fact]
    public void Options_DefaultToRequiredSamplingCadenceAndGrace()
    {
        var options = new AreaMapProviderOptions();

        Assert.Equal(TimeSpan.FromMilliseconds(100), options.WorldInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(33), options.RealtimeInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.SameAreaRootGracePeriod);
        Assert.Equal(TimeSpan.FromSeconds(2), options.SameAreaEntityTreeGracePeriod);
        Assert.Equal(new AreaUiRect(0f, 0f, 1920f, 1080f), options.ClientViewport);
        Assert.False(options.IncludeExpeditionResearchEvidence);
    }

    [Fact]
    public void Options_RejectNegativeSameAreaEntityTreeGracePeriod()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AreaMapProviderService(
            new TestReadSource(),
            new AreaMapProviderOptions
            {
                SameAreaEntityTreeGracePeriod = TimeSpan.FromMilliseconds(-1)
            }));
    }

    [Fact]
    public void Options_PreservesExplicitExpeditionResearchSetting()
    {
        var options = new AreaMapProviderOptions
        {
            IncludeExpeditionResearchEvidence = true
        };

        Assert.True(options.IncludeExpeditionResearchEvidence);
    }

    [Fact]
    public void Options_RejectInvalidClientViewport()
    {
        var source = new TestReadSource();
        Assert.Throws<ArgumentOutOfRangeException>(() => new AreaMapProviderService(
            source,
            new AreaMapProviderOptions
            {
                ClientViewport = new AreaUiRect(0f, 0f, 0f, 1080f)
            }));
    }

    [Fact]
    public void Options_AcceptsCustomClientViewport()
    {
        var source = new TestReadSource();
        var options = new AreaMapProviderOptions
        {
            ClientViewport = new AreaUiRect(17f, 23f, 2560f, 1440f)
        };

        var service = new AreaMapProviderService(source, options);

        Assert.Equal(options.ClientViewport, service.ClientViewport);
    }

    [Fact]
    public void ContentNormalizationPlayer_IsSuppressedWhenWorldHasEntityReadFailure()
    {
        var player = new AreaPlayerSnapshot(
            "Player",
            1,
            Vector3.Zero,
            Vector2.Zero);
        var diagnostics = new[]
        {
            new AreaReadDiagnostic(
                "entity-read-failed",
                "one entity failed",
                AreaDiagnosticSeverity.Warning)
        };

        Assert.Null(MemoryAreaMapReadSource.GetContentNormalizationPlayer(
            diagnostics,
            player));
    }

    [Fact]
    public void ContentNormalizationPlayer_PreservesPlayerWithoutEntityReadFailure()
    {
        var player = new AreaPlayerSnapshot(
            "Player",
            1,
            Vector3.Zero,
            Vector2.Zero);
        var diagnostics = new[]
        {
            new AreaReadDiagnostic(
                "entity-unpositioned",
                "one entity has no position",
                AreaDiagnosticSeverity.Warning)
        };

        Assert.Same(
            player,
            MemoryAreaMapReadSource.GetContentNormalizationPlayer(
                diagnostics,
                player));
    }

    [Fact]
    public void UpdateClientViewport_PublishesOnlyChangedValidDimensionsToReadSource()
    {
        var source = new TestReadSource();
        var service = new AreaMapProviderService(source);
        var resized = new AreaUiRect(0f, 0f, 2560f, 1440f);

        Assert.True(service.UpdateClientViewport(resized));
        Assert.False(service.UpdateClientViewport(resized));

        Assert.Equal(resized, service.ClientViewport);
        Assert.Equal(resized, source.ClientViewport);
        Assert.Equal(1, source.ViewportUpdates);
    }

    [Fact]
    public void SampleRealtime_PublishesVerifiedNativeRecipePanelFromRealtimeResult()
    {
        var source = new TestReadSource();
        var service = new AreaMapProviderService(source);
        var panel = VerifiedPanel("expedition:1:42");
        var area = Area(0xA1, 1);
        source.SetWorld(Result(area, "world"));
        source.SetRealtime(Result(area, "live") with { ExpeditionRecipePanel = panel });

        service.SampleWorld();
        service.SampleRealtime();

        Assert.Same(panel, service.Current.ExpeditionRecipePanel);
    }

    [Fact]
    public void UpdateClientViewport_ImmediatelyClearsNativeRecipePanel()
    {
        var source = new TestReadSource();
        var service = new AreaMapProviderService(source);
        var area = Area(0xA1, 1);
        source.SetWorld(Result(area, "world"));
        source.SetRealtime(Result(area, "live") with
        {
            ExpeditionRecipePanel = VerifiedPanel("expedition:1:42")
        });
        service.SampleWorld();
        service.SampleRealtime();

        service.UpdateClientViewport(new AreaUiRect(0, 0, 1600, 900));

        Assert.Equal(
            AreaExpeditionRecipePanelSnapshot.Unavailable,
            service.Current.ExpeditionRecipePanel);
    }

    [Fact]
    public void UpdateClientViewport_RejectsInvalidDimensionsWithoutUpdatingReadSource()
    {
        var source = new TestReadSource();
        var service = new AreaMapProviderService(source);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            service.UpdateClientViewport(new AreaUiRect(0f, 0f, 0f, 1440f)));

        Assert.Equal(0, source.ViewportUpdates);
    }

    [Fact]
    public async Task UpdateClientViewport_IsSerializedWithSampling()
    {
        using var sampleEntered = new ManualResetEventSlim();
        using var releaseSample = new ManualResetEventSlim();
        var source = new TestReadSource
        {
            RealtimeSampleEntered = sampleEntered,
            ReleaseRealtimeSample = releaseSample
        };
        var service = new AreaMapProviderService(source);

        var sampling = Task.Run(service.SampleRealtime);
        Assert.True(sampleEntered.Wait(TimeSpan.FromSeconds(5)));

        var update = Task.Run(() => service.UpdateClientViewport(
            new AreaUiRect(0f, 0f, 1600f, 900f)));
        var firstCompleted = await Task.WhenAny(
            update,
            Task.Delay(TimeSpan.FromMilliseconds(100)));
        Assert.NotSame(update, firstCompleted);
        Assert.Equal(0, source.ViewportUpdates);

        releaseSample.Set();
        await Task.WhenAll(sampling, update);

        Assert.Equal(1, source.ViewportUpdates);
    }

    [Fact]
    public void UpdateClientViewport_InvalidatesPublishedViewsUntilResampled()
    {
        var source = new TestReadSource();
        var service = new AreaMapProviderService(source);
        var area = Area(0xA1, 1);
        var oldViews = new AreaMapViewsSnapshot(
            new AreaMapViewSnapshot(
                AreaMapViewKind.LargeMap,
                AreaMapViewAvailability.Verified,
                false,
                Vector2.Zero,
                0.5f,
                0f,
                false,
                new AreaUiRect(0f, 0f, 1920f, 1080f),
                0.85f),
            new AreaMapViewSnapshot(
                AreaMapViewKind.MiniMap,
                AreaMapViewAvailability.Verified,
                true,
                Vector2.Zero,
                0.5f,
                0f,
                false,
                new AreaUiRect(1590f, 5f, 270f, 270f),
                0.85f));
        source.SetWorld(Result(area, playerName: "world"));
        source.SetRealtime(Result(area, playerName: "realtime") with
        {
            MapViews = oldViews
        });
        service.SampleWorld();
        service.SampleRealtime();
        Assert.Equal(
            AreaMapViewAvailability.Verified,
            service.Current.MapViews.MiniMap.Availability);

        service.UpdateClientViewport(new AreaUiRect(0f, 0f, 1600f, 900f));

        Assert.Equal(AreaMapViewsSnapshot.Unavailable, service.Current.MapViews);
        Assert.Equal(0xA1u, service.Current.Area.AreaHash);
        Assert.NotEmpty(service.Current.Entities);
    }

    [Fact]
    public async Task StartAsync_SamplesWorldAndRealtimeAtConfiguredIntervals()
    {
        var clock = new ManualTimeProvider();
        var source = new TestReadSource();
        var service = new AreaMapProviderService(
            source,
            new AreaMapProviderOptions
            {
                WorldInterval = TimeSpan.FromMilliseconds(100),
                RealtimeInterval = TimeSpan.FromMilliseconds(33)
            },
            clock);
        using var cancellation = new CancellationTokenSource();

        var running = service.StartAsync(cancellation.Token);
        await Task.Yield();

        clock.Advance(TimeSpan.FromMilliseconds(32));
        await Task.Yield();
        Assert.Equal(0, source.RealtimeSamples);
        Assert.Equal(0, source.WorldSamples);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Task.Yield();
        Assert.Equal(1, source.RealtimeSamples);
        Assert.Equal(0, source.WorldSamples);

        clock.Advance(TimeSpan.FromMilliseconds(67));
        await Task.Yield();
        Assert.Equal(3, source.RealtimeSamples);
        Assert.Equal(1, source.WorldSamples);

        cancellation.Cancel();
        await running;
    }

    [Fact]
    public void WorldSampleBeforeRealtime_KeepsPublishedLoadingSnapshotEmpty()
    {
        var source = new TestReadSource();
        var service = new AreaMapProviderService(source);
        source.SetWorld(Result(Area(0xA1, 1), playerName: "A"));

        service.SampleWorld();

        Assert.Equal(AreaMapSnapshotStatus.Loading, service.Current.Status);
        Assert.Equal(0xA1u, service.Current.Area.AreaHash);
        Assert.Null(service.Current.Player);
        Assert.Empty(service.Current.Entities);
        Assert.Empty(service.Current.Contents);
        Assert.Empty(service.Current.Landmarks);
        Assert.Null(service.Current.Terrain);
        Assert.Equal(AreaMapViewsSnapshot.Unavailable, service.Current.MapViews);
    }

    [Fact]
    public void AreaChange_PublishesLoadingBeforeNewAreaAndClearsOldState()
    {
        var source = new TestReadSource();
        var service = new AreaMapProviderService(source);
        var observed = new List<AreaMapSnapshot>();
        service.SnapshotChanged += observed.Add;

        source.SetWorld(Result(Area(0xA1, 1), playerName: "A"));
        source.SetRealtime(Result(Area(0xA1, 1), playerName: "A-live"));
        service.SampleWorld();
        service.SampleRealtime();
        Assert.Equal(0xA1u, service.Current.Area.AreaHash);
        Assert.NotEmpty(service.Current.Entities);

        source.SetWorld(Result(Area(0xB2, 2), playerName: "B"));
        service.SampleWorld();

        Assert.Contains(observed, snapshot =>
            snapshot.Status == AreaMapSnapshotStatus.Loading
            && snapshot.Area.SessionSequence == 2
            && snapshot.Entities.Count == 0
            && snapshot.Terrain is null
            && snapshot.Contents.Count == 0);
        Assert.Equal(0xB2u, service.Current.Area.AreaHash);
        Assert.Empty(service.Current.Contents);
        Assert.Null(service.Current.Terrain);
        Assert.Equal(1, source.ResetCount);
    }

    [Fact]
    public void RealtimeAreaChange_PublishesLoadingImmediately()
    {
        var source = new TestReadSource();
        var service = new AreaMapProviderService(source);
        var observed = new List<AreaMapSnapshot>();
        service.SnapshotChanged += observed.Add;
        source.SetWorld(Result(Area(0xA1, 1), playerName: "A"));
        source.SetRealtime(Result(Area(0xA1, 1), playerName: "A-live"));
        service.SampleWorld();
        service.SampleRealtime();

        source.SetRealtime(Result(Area(0xB2, 2), playerName: "B-live"));
        service.SampleRealtime();

        Assert.Contains(observed, snapshot =>
            snapshot.Status == AreaMapSnapshotStatus.Loading
            && snapshot.Area.SessionSequence == 2
            && snapshot.Entities.Count == 0
            && snapshot.Player is null);
        Assert.Equal(AreaMapSnapshotStatus.Loading, service.Current.Status);
        Assert.Equal(0xB2u, service.Current.Area.AreaHash);
        Assert.Equal(1, source.ResetCount);
    }

    [Fact]
    public void RootChainFailure_KeepsRealtimeForAtMostGraceThenClears()
    {
        var clock = new ManualTimeProvider();
        var source = new TestReadSource();
        var service = new AreaMapProviderService(
            source,
            new AreaMapProviderOptions
            {
                SameAreaRootGracePeriod = TimeSpan.FromMilliseconds(250)
            },
            clock);

        source.SetWorld(Result(Area(0xA1, 1), playerName: "A"));
        source.SetRealtime(Result(Area(0xA1, 1), playerName: "A-live"));
        service.SampleWorld();
        service.SampleRealtime();
        var stable = service.Current;

        source.SetRealtime(RootFailure());
        service.SampleRealtime();
        Assert.Same(stable, service.Current);

        clock.Advance(TimeSpan.FromMilliseconds(250));
        service.SampleRealtime();
        Assert.Same(stable, service.Current);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        service.SampleRealtime();
        Assert.Equal(AreaMapSnapshotStatus.Loading, service.Current.Status);
        Assert.Empty(service.Current.Entities);
        Assert.Null(service.Current.Player);
        Assert.Null(service.Current.Terrain);
    }

    [Fact]
    public void EntityTreeFailure_WithinGraceRetainsLastCompleteWorldAndMarksDegraded()
    {
        var clock = new ManualTimeProvider();
        var source = new TestReadSource();
        var service = new AreaMapProviderService(source, timeProvider: clock);
        var area = Area(0xA1, 1);
        var entity = Entity(area, "retained");
        var content = Content("expedition:retained");
        var landmark = Landmark("landmark:retained");
        var terrain = new AreaTerrainSnapshot(1, 1, new byte[] { 1 }, ["tile-retained"]);
        source.SetWorld(Result(area, "world") with
        {
            Entities = [entity],
            Contents = [content],
            Landmarks = [landmark],
            Terrain = terrain
        });
        source.SetRealtime(Result(area, "live"));
        service.SampleWorld();
        service.SampleRealtime();

        source.SetWorld(EntityTreeFailure(area));
        var snapshot = service.SampleWorld();

        Assert.Equal(AreaMapSnapshotStatus.Degraded, snapshot.Status);
        Assert.Same(entity, Assert.Single(snapshot.Entities));
        Assert.Same(content, Assert.Single(snapshot.Contents));
        Assert.Same(landmark, Assert.Single(snapshot.Landmarks));
        Assert.Same(terrain, snapshot.Terrain);
        Assert.Contains(snapshot.Diagnostics, item => item.Code == "entity-tree-invalid");
        Assert.Contains(snapshot.Diagnostics, item => item.Code == "entity-tree-last-good-retained");
    }

    [Fact]
    public void EntityTreeFailure_WithinGraceUsesLatestRealtimeFacts()
    {
        var clock = new ManualTimeProvider();
        var source = new TestReadSource();
        var service = new AreaMapProviderService(source, timeProvider: clock);
        var area = Area(0xA1, 1);
        var content = Content("expedition:retained");
        source.SetWorld(Result(area, "world") with { Contents = [content] });
        source.SetRealtime(Result(area, "live-before"));
        service.SampleWorld();
        service.SampleRealtime();
        source.SetWorld(EntityTreeFailure(area));
        service.SampleWorld();

        var views = Views(new Vector2(40, 50));
        var panel = VerifiedPanel("expedition:retained");
        source.SetRealtime(Result(area, "live-after") with
        {
            MapViews = views,
            ExpeditionRecipePanel = panel
        });
        var snapshot = service.SampleRealtime();

        Assert.Equal("live-after", snapshot.Player?.CharacterName);
        Assert.Same(views, snapshot.MapViews);
        Assert.Same(panel, snapshot.ExpeditionRecipePanel);
        Assert.Same(content, Assert.Single(snapshot.Contents));
    }

    [Fact]
    public void EntityTreeFailure_WithinGraceDoesNotFallBackToWorldPlayer()
    {
        var clock = new ManualTimeProvider();
        var source = new TestReadSource();
        var service = new AreaMapProviderService(source, timeProvider: clock);
        var area = Area(0xA1, 1);
        var content = Content("expedition:retained");
        source.SetWorld(Result(area, "world") with { Contents = [content] });
        source.SetRealtime(Result(area, "live-before"));
        service.SampleWorld();
        service.SampleRealtime();
        source.SetWorld(EntityTreeFailure(area));
        service.SampleWorld();

        source.SetRealtime(Result(area, "missing-player") with { Player = null });
        var snapshot = service.SampleRealtime();

        Assert.Null(snapshot.Player);
        Assert.Same(content, Assert.Single(snapshot.Contents));
        Assert.Equal(AreaMapSnapshotStatus.Degraded, snapshot.Status);
    }

    [Fact]
    public void EntityTreeFailure_ExpiresFromRealtimeSamplingAndClearsWorld()
    {
        var clock = new ManualTimeProvider();
        var source = new TestReadSource();
        var service = new AreaMapProviderService(
            source,
            new AreaMapProviderOptions
            {
                SameAreaEntityTreeGracePeriod = TimeSpan.FromSeconds(2)
            },
            clock);
        var area = Area(0xA1, 1);
        source.SetWorld(Result(area, "world") with
        {
            Contents = [Content("expedition:retained")],
            Landmarks = [Landmark("landmark:retained")]
        });
        source.SetRealtime(Result(area, "live"));
        service.SampleWorld();
        service.SampleRealtime();
        source.SetWorld(EntityTreeFailure(area));
        service.SampleWorld();

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.NotEmpty(service.SampleRealtime().Contents);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        var expired = service.SampleRealtime();

        Assert.Equal(AreaMapSnapshotStatus.Degraded, expired.Status);
        Assert.Empty(expired.Entities);
        Assert.Empty(expired.Contents);
        Assert.Empty(expired.Landmarks);
        Assert.Null(expired.Terrain);
        Assert.Contains(expired.Diagnostics, item => item.Code == "entity-tree-invalid");
        Assert.DoesNotContain(
            expired.Diagnostics,
            item => item.Code == "entity-tree-last-good-retained");
    }

    [Fact]
    public void EntityTreeFailure_RecoveryImmediatelyReplacesRetainedWorld()
    {
        var clock = new ManualTimeProvider();
        var source = new TestReadSource();
        var service = new AreaMapProviderService(source, timeProvider: clock);
        var area = Area(0xA1, 1);
        source.SetWorld(Result(area, "old") with
        {
            Contents = [Content("expedition:old")]
        });
        source.SetRealtime(Result(area, "live"));
        service.SampleWorld();
        service.SampleRealtime();
        source.SetWorld(EntityTreeFailure(area));
        service.SampleWorld();

        var newTerrain = new AreaTerrainSnapshot(2, 1, new byte[] { 1, 1 }, ["tile-new"]);
        source.SetWorld(Result(area, "new") with
        {
            Contents = [Content("expedition:new")],
            Landmarks = [Landmark("landmark:new")],
            Terrain = newTerrain
        });
        var recovered = service.SampleWorld();

        Assert.Equal(AreaMapSnapshotStatus.Stable, recovered.Status);
        Assert.Equal("new", Assert.Single(recovered.Entities).DisplayName);
        Assert.Equal("expedition:new", Assert.Single(recovered.Contents).InstanceId);
        Assert.Equal("landmark:new", Assert.Single(recovered.Landmarks).LandmarkId);
        Assert.Same(newTerrain, recovered.Terrain);
        Assert.DoesNotContain(recovered.Diagnostics, item =>
            item.Code is "entity-tree-invalid" or "entity-tree-last-good-retained");
    }

    [Theory]
    [InlineData(0xB2, 1L)]
    [InlineData(0xA1, 2L)]
    public void EntityTreeFailure_AreaOrSessionChangeClearsRetainedWorldImmediately(
        uint incomingHash,
        long incomingSession)
    {
        var clock = new ManualTimeProvider();
        var source = new TestReadSource();
        var service = new AreaMapProviderService(source, timeProvider: clock);
        var area = Area(0xA1, 1);
        source.SetWorld(Result(area, "old") with
        {
            Contents = [Content("expedition:old")],
            Landmarks = [Landmark("landmark:old")]
        });
        source.SetRealtime(Result(area, "live"));
        service.SampleWorld();
        service.SampleRealtime();
        source.SetWorld(EntityTreeFailure(area));
        service.SampleWorld();

        source.SetRealtime(Result(Area(incomingHash, incomingSession), "new-live"));
        var changed = service.SampleRealtime();

        Assert.Equal(AreaMapSnapshotStatus.Loading, changed.Status);
        Assert.Equal(incomingHash, changed.Area.AreaHash);
        Assert.Equal(incomingSession, changed.Area.SessionSequence);
        Assert.Empty(changed.Entities);
        Assert.Empty(changed.Contents);
        Assert.Empty(changed.Landmarks);
        Assert.Null(changed.Terrain);
        Assert.Null(changed.Player);
        Assert.Equal(1, source.ResetCount);
    }

    [Fact]
    public void TerrainFailure_MarksPublishedSnapshotDegraded()
    {
        var source = new TestReadSource();
        var service = new AreaMapProviderService(source);
        var area = Area(0xA1, 1);
        source.SetWorld(Result(area, playerName: "old") with
        {
            Contents = [Content("expedition:old")]
        });
        source.SetRealtime(Result(area, playerName: "live"));
        service.SampleWorld();
        service.SampleRealtime();

        source.SetWorld(Result(area, playerName: "terrain-degraded") with
        {
            Contents = [Content("expedition:new")],
            Terrain = null,
            TerrainHealthy = false,
            Diagnostics =
            [
                new AreaReadDiagnostic(
                    "terrain-grid-invalid",
                    "terrain unavailable",
                AreaDiagnosticSeverity.Warning)
            ]
        });

        service.SampleWorld();

        Assert.Equal(AreaMapSnapshotStatus.Degraded, service.Current.Status);
        Assert.Equal("terrain-degraded", Assert.Single(service.Current.Entities).DisplayName);
        Assert.Equal("expedition:new", Assert.Single(service.Current.Contents).InstanceId);
        Assert.Contains(service.Current.Diagnostics, diagnostic =>
            diagnostic.Code == "terrain-grid-invalid");
    }

    [Fact]
    public void TerrainFailure_WithoutRealtimeStillMarksWorldSnapshotDegraded()
    {
        var source = new TestReadSource();
        var service = new AreaMapProviderService(source);
        source.SetWorld(Result(Area(0xA1, 1), playerName: "A") with
        {
            Terrain = null,
            TerrainHealthy = false,
            Diagnostics =
            [
                new AreaReadDiagnostic(
                    "terrain-grid-invalid",
                    "terrain unavailable",
                    AreaDiagnosticSeverity.Warning)
            ]
        });

        service.SampleWorld();

        Assert.Equal(AreaMapSnapshotStatus.Degraded, service.Current.Status);
        Assert.Contains(service.Current.Diagnostics, diagnostic =>
            diagnostic.Code == "terrain-grid-invalid");
    }

    [Fact]
    public void MismatchedAreaHash_DoesNotMergeWorldAndRealtimeCaches()
    {
        var source = new TestReadSource();
        var service = new AreaMapProviderService(source);
        source.SetWorld(Result(Area(0xA1, 1), playerName: "world-A"));
        source.SetRealtime(Result(Area(0xB2, 1), playerName: "live-B"));

        service.SampleWorld();
        service.SampleRealtime();

        Assert.Equal(AreaMapSnapshotStatus.Loading, service.Current.Status);
        Assert.Equal(0xB2u, service.Current.Area.AreaHash);
        Assert.Null(service.Current.Player);
        Assert.Empty(service.Current.Entities);
    }

    [Fact]
    public async Task ParallelSampling_PublishesOnlyCompleteSnapshots()
    {
        var source = new TestReadSource();
        var service = new AreaMapProviderService(source);
        source.SetWorld(Result(Area(0xA1, 1), playerName: "A"));
        source.SetRealtime(Result(Area(0xA1, 1), playerName: "A-live"));
        service.SampleWorld();
        service.SampleRealtime();

        var torn = new ConcurrentBag<AreaMapSnapshot>();
        await Task.WhenAll(
            Enumerable.Range(0, 1000).Select(_ => Task.Run(() =>
            {
                service.SampleWorld();
                service.SampleRealtime();
                var snapshot = service.Current;
                if (snapshot.Area.AreaHash != 0xA1u
                    || snapshot.Entities.Any(entity =>
                        !entity.MetadataPath.EndsWith("/A", StringComparison.Ordinal)))
                {
                    torn.Add(snapshot);
                }
            })));

        Assert.Empty(torn);
    }

    [Fact]
    public void ConsumerFailure_IsolatedAndRecordedAsDiagnostic()
    {
        var source = new TestReadSource();
        var service = new AreaMapProviderService(source);
        var secondConsumerCalled = false;
        service.SnapshotChanged += _ => throw new InvalidOperationException("consumer");
        service.SnapshotChanged += _ => secondConsumerCalled = true;
        source.SetWorld(Result(Area(0xA1, 1), playerName: "A"));

        service.SampleWorld();

        Assert.True(secondConsumerCalled);
        Assert.Contains(service.Current.Diagnostics, diagnostic =>
            diagnostic.Code == "snapshot-consumer-failed");
    }

    [Fact]
    public async Task ConcurrentSampling_NotifiesConsumersInPublicationOrder()
    {
        var source = new TestReadSource();
        var service = new AreaMapProviderService(source);
        var area = Area(0xA1, 1);
        source.SetWorld(Result(area, playerName: "baseline-world"));
        source.SetRealtime(Result(area, playerName: "baseline-live"));
        service.SampleWorld();
        service.SampleRealtime();

        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var observed = new ConcurrentQueue<string>();
        var invocation = 0;
        service.SnapshotChanged += snapshot =>
        {
            if (Interlocked.Increment(ref invocation) == 1)
            {
                firstEntered.Set();
                releaseFirst.Wait(TimeSpan.FromSeconds(5));
            }

            observed.Enqueue(snapshot.Player!.CharacterName);
        };

        source.SetRealtime(Result(area, playerName: "A-live"));
        var firstSample = Task.Run(service.SampleRealtime);
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(5)));

        source.SetRealtime(Result(area, playerName: "B-live"));
        var secondSample = Task.Run(service.SampleRealtime);
        Assert.True(SpinWait.SpinUntil(
            () => source.RealtimeSamples >= 3,
            TimeSpan.FromSeconds(5)));
        releaseFirst.Set();
        await Task.WhenAll(firstSample, secondSample);

        Assert.Equal(["A-live", "B-live"], observed);
    }

    private static AreaIdentity Area(uint hash, long sequence)
        => new(hash, $"Map{sequence}", 81, sequence);

    private static AreaExpeditionRecipePanelSnapshot VerifiedPanel(string instanceId)
        => new(
            AreaExpeditionRecipePanelAvailability.Verified,
            true,
            new AreaUiRect(20, 70, 360, 390),
            new AreaUiRect(31.5f, 99, 333, 346.5f),
            instanceId,
            [new AreaExpeditionRecipePanelRow(
                "recipe-1",
                20,
                new AreaUiRect(31.5f, 99, 333, 31.5f),
                true)]);

    private static AreaContentSnapshot Content(string instanceId)
        => new(
            instanceId,
            "expedition",
            "Expedition",
            AreaContentKind.Expedition,
            AreaContentPhase.Available,
            new Vector3(100, 200, 0),
            new Vector2(10, 20),
            1f,
            1,
            [],
            new AreaExpeditionDetails(4));

    private static AreaLandmarkSnapshot Landmark(string landmarkId)
        => new(
            landmarkId,
            "Retained landmark",
            "Metadata/Terrain/Retained",
            AreaLandmarkKind.Mechanic,
            new Vector2(30, 40),
            1);

    private static AreaMapViewsSnapshot Views(Vector2 shift)
        => new(
            new AreaMapViewSnapshot(
                AreaMapViewKind.LargeMap,
                AreaMapViewAvailability.Verified,
                true,
                shift,
                1f,
                0f,
                false,
                new AreaUiRect(0, 0, 1920, 1080),
                1f),
            AreaMapViewsSnapshot.Unavailable.MiniMap);

    private static AreaMapReadResult Result(
        AreaIdentity area,
        string playerName)
        => new(
            area,
            new AreaPlayerSnapshot(
                playerName,
                90,
                new Vector3(10, 20, 0),
                new Vector2(1, 2)),
            [Entity(area, playerName)],
            [],
            [],
            new AreaTerrainSnapshot(1, 1, new byte[] { 1 }, []),
            AreaMapViewsSnapshot.Unavailable,
            [],
            RootChainHealthy: true,
            EntityTreeHealthy: true,
            TerrainHealthy: true);

    private static AreaEntitySnapshot Entity(AreaIdentity area, string suffix)
        => new(
            1,
            $"Metadata/Entities/{suffix}",
            suffix,
            AreaEntityCategory.Monster,
            Vector3.Zero,
            Vector2.Zero,
            AreaEntityDisposition.Hostile,
            AreaEntityRarity.Normal,
            1,
            1,
            false,
            false,
            AreaChestState.NotApplicable,
            []);

    private static AreaMapReadResult RootFailure()
        => new(
            null,
            null,
            [],
            [],
            [],
            null,
            AreaMapViewsSnapshot.Unavailable,
            [new AreaReadDiagnostic(
                "root-chain-unavailable",
                "root unavailable",
                AreaDiagnosticSeverity.Warning)],
            RootChainHealthy: false,
            EntityTreeHealthy: false,
            TerrainHealthy: false);

    private static AreaMapReadResult EntityTreeFailure(AreaIdentity area)
        => new(
            area,
            null,
            [],
            [],
            [],
            null,
            AreaMapViewsSnapshot.Unavailable,
            [new AreaReadDiagnostic(
                "entity-tree-invalid",
                "entity tree changed during traversal",
                AreaDiagnosticSeverity.Warning)],
            RootChainHealthy: true,
            EntityTreeHealthy: false,
            TerrainHealthy: false);

    private sealed class TestReadSource : IAreaMapReadSource
    {
        private AreaMapReadResult _world = RootFailure();
        private AreaMapReadResult _realtime = RootFailure();
        private int _worldSamples;
        private int _realtimeSamples;
        private int _resetCount;

        public ManualResetEventSlim? RealtimeSampleEntered { get; init; }
        public ManualResetEventSlim? ReleaseRealtimeSample { get; init; }

        public int ProcessId => 30744;
        public string ProfileId => "test-profile";
        public int WorldSamples => Volatile.Read(ref _worldSamples);
        public int RealtimeSamples => Volatile.Read(ref _realtimeSamples);
        public int ResetCount => Volatile.Read(ref _resetCount);
        public int ViewportUpdates { get; private set; }
        public AreaUiRect? ClientViewport { get; private set; }

        public AreaMapReadResult SampleWorld()
        {
            Interlocked.Increment(ref _worldSamples);
            return _world;
        }

        public AreaMapReadResult SampleRealtime()
        {
            Interlocked.Increment(ref _realtimeSamples);
            RealtimeSampleEntered?.Set();
            ReleaseRealtimeSample?.Wait(TimeSpan.FromSeconds(5));
            return _realtime;
        }

        public void SetWorld(AreaMapReadResult result) => _world = result;
        public void SetRealtime(AreaMapReadResult result) => _realtime = result;
        public void ResetAreaCaches() => Interlocked.Increment(ref _resetCount);

        public void UpdateClientViewport(AreaUiRect clientViewport)
        {
            ClientViewport = clientViewport;
            ViewportUpdates++;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            lock (_gate)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        public void Advance(TimeSpan amount)
        {
            _utcNow += amount;
            while (true)
            {
                List<(TimerCallback Callback, object? State)> callbacks = [];
                lock (_gate)
                {
                    foreach (var timer in _timers)
                    {
                        if (timer.TryTakeDue(_utcNow, out var callback, out var state))
                        {
                            callbacks.Add((callback, state));
                        }
                    }
                }

                if (callbacks.Count == 0)
                {
                    return;
                }

                foreach (var (callback, state) in callbacks)
                {
                    callback(state);
                }
            }
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private TimeSpan _period;
            private DateTimeOffset _next;
            private bool _disposed;

            public ManualTimer(
                ManualTimeProvider owner,
                TimerCallback callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
                _period = period;
                _next = dueTime == Timeout.InfiniteTimeSpan
                    ? DateTimeOffset.MaxValue
                    : owner._utcNow + dueTime;
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed)
                {
                    return false;
                }

                _period = period;
                _next = dueTime == Timeout.InfiniteTimeSpan
                    ? DateTimeOffset.MaxValue
                    : _owner._utcNow + dueTime;
                return true;
            }

            public bool TryTakeDue(
                DateTimeOffset now,
                out TimerCallback callback,
                out object? state)
            {
                callback = _callback;
                state = _state;
                if (_disposed || now < _next)
                {
                    return false;
                }

                if (_period == Timeout.InfiniteTimeSpan)
                {
                    _next = DateTimeOffset.MaxValue;
                }
                else
                {
                    _next += _period;
                }

                return true;
            }

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
