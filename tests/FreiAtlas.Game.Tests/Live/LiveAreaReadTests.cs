using FreiAtlas.Core.Memory;
using FreiAtlas.Core.Area;
using FreiAtlas.Game;
using FreiAtlas.Game.Entities;
using FreiAtlas.Game.Memory;
using FreiAtlas.Game.Content;
using FreiAtlas.Game.Terrain;
using FreiAtlas.Game.Views;
using FreiAtlas.Game.World;
using FreiAtlas.Platform.Windows.Process;
using FreiAtlas.Platform.Windows.Windows;
using System.Numerics;
using System.Reflection;
using Xunit.Abstractions;

namespace FreiAtlas.Game.Tests.Live;

public sealed class LiveAreaReadTests
{
    private readonly ITestOutputHelper _output;

    public LiveAreaReadTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    [Trait("Category", "Live")]
    public void RootProbe_ReadsSpecifiedPid()
    {
        var configuredPid = Environment.GetEnvironmentVariable("FREI_LIVE_PID");
        if (string.IsNullOrWhiteSpace(configuredPid))
        {
            _output.WriteLine("Skipped: FREI_LIVE_PID was not provided; no process was accessed.");
            return;
        }

        Assert.True(
            int.TryParse(configuredPid, out var processId) && processId > 0,
            "FREI_LIVE_PID must contain one positive process ID.");
        Assert.True(
            ProcessAttachment.TryAttach(processId, out var attachment),
            $"Could not attach read-only process memory to specified PID {processId}.");

        using (attachment)
        {
            var memory = attachment!.Memory;
            var profile = Poe2MemoryProfile.Current;
            var locator = new GameStateLocator(profile);
            var session = new GameMemorySession(memory, profile, locator);

            Assert.Equal(processId, session.ProcessId);
            if (!session.TryRefresh(out var root, out var area, out var diagnostics))
            {
                WriteRootEvidence(memory, profile, locator);
                Assert.Fail(FormatDiagnostics(diagnostics));
            }

            var reader = new GameMemoryReader(memory, profile);
            Assert.True(
                locator.TryResolveComponent(memory, root.LocalPlayer, "Render", out var render),
                "The validated local player did not expose a Render component.");
            Assert.True(
                reader.TryReadVector3(
                    render + profile.Render.WorldPositionOffset,
                    out var worldPosition),
                "The local player's Render world position was unavailable or non-finite.");

            Assert.True(
                locator.TryResolveComponent(memory, root.LocalPlayer, "Player", out var player),
                "The validated local player did not expose a Player component.");
            Assert.True(
                reader.TryReadStdWString(
                    player + profile.Player.NameOffset,
                    profile.Player.MaximumNameLength,
                    out var characterName)
                && !string.IsNullOrWhiteSpace(characterName),
                "The Player component did not contain a valid character name.");
            Assert.True(
                reader.TryReadByte(player + profile.Player.LevelOffset, out var characterLevel)
                && characterLevel > 0,
                "The Player component did not contain a valid character level.");

            _output.WriteLine($"ProcessId={processId}");
            _output.WriteLine($"ProfileId={profile.ProfileId}");
            _output.WriteLine($"InGameState=0x{root.InGameState.ToInt64():X}");
            _output.WriteLine($"AreaInstance=0x{root.AreaInstance.ToInt64():X}");
            _output.WriteLine($"LocalPlayer=0x{root.LocalPlayer.ToInt64():X}");
            _output.WriteLine($"AreaCode={area.AreaCode}");
            _output.WriteLine($"AreaHash=0x{area.AreaHash:X8}");
            _output.WriteLine($"AreaLevel={area.AreaLevel}");
            _output.WriteLine(
                $"RenderWorld=({worldPosition.X:F3}, {worldPosition.Y:F3}, {worldPosition.Z:F3})");
            _output.WriteLine($"PlayerName={characterName}");
            _output.WriteLine($"PlayerLevel={characterLevel}");
        }
    }

    [Fact]
    [Trait("Category", "Live")]
    public void EntityProbe_ReadsCurrentMapFacts()
    {
        var configuredPid = Environment.GetEnvironmentVariable("FREI_LIVE_PID");
        if (string.IsNullOrWhiteSpace(configuredPid))
        {
            _output.WriteLine("Skipped: FREI_LIVE_PID was not provided; no process was accessed.");
            return;
        }

        Assert.True(
            int.TryParse(configuredPid, out var processId) && processId > 0,
            "FREI_LIVE_PID must contain one positive process ID.");
        Assert.True(
            ProcessAttachment.TryAttach(processId, out var attachment),
            $"Could not attach read-only process memory to specified PID {processId}.");

        using (attachment)
        {
            var memory = attachment!.Memory;
            var profile = Poe2MemoryProfile.Current;
            var locator = new GameStateLocator(profile);
            var session = new GameMemorySession(memory, profile, locator);
            Assert.True(
                session.TryRefresh(out var root, out var area, out var rootDiagnostics),
                FormatDiagnostics(rootDiagnostics));

            var tree = new AreaEntityTreeReader(memory, profile);
            Assert.True(
                tree.TryRead(root.AreaInstance, out var rawEntities, out var treeDiagnostic),
                treeDiagnostic?.Message ?? "The AwakeEntities tree could not be read.");
            Assert.NotEmpty(rawEntities);
            Assert.Equal(
                rawEntities.Count,
                rawEntities.Select(entity => entity.EntityId).Distinct().Count());

            var names = EntityNameCatalog.LoadEmbedded();
            var components = new EntityComponentResolver(memory, profile);
            var reader = new AreaEntityReader(
                memory,
                components,
                names,
                profile);
            var memoryReader = new GameMemoryReader(memory, profile);
            var result = reader.Read(area.SessionSequence, rawEntities);
            Assert.NotEmpty(result.Entities);
            Assert.Contains(
                result.Entities,
                entity => entity.Category == Core.Area.AreaEntityCategory.Player);
            Assert.All(
                result.Entities,
                entity =>
                {
                    Assert.StartsWith("Metadata/", entity.MetadataPath, StringComparison.Ordinal);
                    Assert.True(float.IsFinite(entity.WorldPosition.X));
                    Assert.True(float.IsFinite(entity.WorldPosition.Y));
                    Assert.True(float.IsFinite(entity.WorldPosition.Z));
                    Assert.True(float.IsFinite(entity.GridPosition.X));
                    Assert.True(float.IsFinite(entity.GridPosition.Y));
                });

            var categoryCounts = result.Entities
                .GroupBy(entity => entity.Category)
                .OrderBy(group => group.Key)
                .Select(group => $"{group.Key}={group.Count()}");
            var unknownMetadata = result.Entities
                .Where(entity => names.Resolve(entity.MetadataPath) is null)
                .Select(entity => entity.MetadataPath)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var entityFailures = result.Diagnostics.Count(
                diagnostic => diagnostic.Code == "entity-read-failed");
            var failedEntityIds = rawEntities
                .Select(entity => entity.EntityId)
                .Except(result.Entities.Select(entity => entity.EntityId))
                .Order()
                .ToArray();

            _output.WriteLine($"ProcessId={processId}");
            _output.WriteLine($"ProfileId={profile.ProfileId}");
            _output.WriteLine($"AreaCode={area.AreaCode}");
            _output.WriteLine($"RawEntityCount={rawEntities.Count}");
            _output.WriteLine($"ReadableEntityCount={result.Entities.Count}");
            _output.WriteLine($"EntityReadFailureCount={entityFailures}");
            _output.WriteLine($"FailedEntityIds={string.Join(",", failedEntityIds)}");
            foreach (var failedEntity in rawEntities.Where(entity => failedEntityIds.Contains(entity.EntityId)))
            {
                var metadata = "<unreadable>";
                if (memoryReader.TryReadPointer(
                        failedEntity.EntityAddress + profile.Entity.DetailsOffset,
                        out var details)
                    && memoryReader.TryReadStdWString(
                        details + profile.EntityDetails.NameOffset,
                        1024,
                        out var failedMetadata))
                {
                    metadata = failedMetadata;
                }

                var diagnosticCode = result.Diagnostics
                    .FirstOrDefault(diagnostic => diagnostic.EntityId == failedEntity.EntityId)
                    ?.Code ?? "<missing-diagnostic>";
                _output.WriteLine(
                    $"OmittedEntity id={failedEntity.EntityId} "
                    + $"address=0x{failedEntity.EntityAddress.ToInt64():X} "
                    + $"diagnostic={diagnosticCode} metadata={metadata}");
            }
            _output.WriteLine($"Categories={string.Join(", ", categoryCounts)}");
            _output.WriteLine(
                $"EntitiesWithLife={result.Entities.Count(entity => entity.MaximumLife > 0)}");
            _output.WriteLine(
                $"PoiEntityCount={result.Entities.Count(entity => entity.HasMinimapIcon)}");
            _output.WriteLine($"UnknownMetadataCount={unknownMetadata.Length}");
            foreach (var entity in result.Entities.Take(20))
            {
                _output.WriteLine(
                    $"Entity id={entity.EntityId} category={entity.Category} "
                    + $"grid=({entity.GridPosition.X:F2},{entity.GridPosition.Y:F2}) "
                    + $"life={entity.CurrentLife}/{entity.MaximumLife} poi={entity.HasMinimapIcon} "
                    + $"metadata={entity.MetadataPath}");
            }

            foreach (var monster in result.Entities.Where(entity =>
                         entity.Category == Core.Area.AreaEntityCategory.Monster))
            {
                var rawMonster = rawEntities.Single(entity => entity.EntityId == monster.EntityId);
                components.TryResolve(rawMonster, "Render", out var render);
                components.TryResolve(rawMonster, "Life", out var life);
                components.TryResolve(rawMonster, "Positioned", out var positioned);
                components.TryResolve(rawMonster, "ObjectMagicProperties", out var magicProperties);
                _output.WriteLine(
                    $"Monster id={monster.EntityId} name={monster.DisplayName} "
                    + $"address=0x{rawMonster.EntityAddress.ToInt64():X} "
                    + $"components=render:0x{render.ToInt64():X}/life:0x{life.ToInt64():X}"
                    + $"/positioned:0x{positioned.ToInt64():X}/magic:0x{magicProperties.ToInt64():X} "
                    + $"rarity={monster.Rarity} disposition={monster.Disposition} "
                    + $"life={monster.CurrentLife}/{monster.MaximumLife} "
                    + $"poi={monster.HasMinimapIcon}/{monster.IsMinimapIconComplete} "
                    + $"grid=({monster.GridPosition.X:F2},{monster.GridPosition.Y:F2}) "
                    + $"metadata={monster.MetadataPath}");
            }
        }
    }

    [Fact]
    [Trait("Category", "Live")]
    public void SleepingEntityProbe_ReportsUniqueAndMinimapIconEvidence()
    {
        var configuredPid = Environment.GetEnvironmentVariable("FREI_LIVE_PID");
        if (string.IsNullOrWhiteSpace(configuredPid))
        {
            _output.WriteLine("Skipped: FREI_LIVE_PID was not provided; no process was accessed.");
            return;
        }

        Assert.True(
            int.TryParse(configuredPid, out var processId) && processId > 0,
            "FREI_LIVE_PID must contain one positive process ID.");
        Assert.True(
            ProcessAttachment.TryAttach(processId, out var attachment),
            $"Could not attach read-only process memory to specified PID {processId}.");

        using (attachment)
        {
            var memory = attachment!.Memory;
            var profile = Poe2MemoryProfile.Current;
            var session = new GameMemorySession(
                memory,
                profile,
                new GameStateLocator(profile));
            Assert.True(
                session.TryRefresh(out var root, out var area, out var rootDiagnostics),
                FormatDiagnostics(rootDiagnostics));

            var sleepingProfile = profile with
            {
                AreaInstance = profile.AreaInstance with
                {
                    AwakeEntitiesOffset = profile.AreaInstance.SleepingEntitiesOffset
                }
            };
            var tree = new AreaEntityTreeReader(memory, sleepingProfile);
            Assert.True(
                tree.TryRead(root.AreaInstance, out var rawEntities, out var treeDiagnostic),
                treeDiagnostic?.Message ?? "The SleepingEntities tree could not be read.");

            var reader = new AreaEntityReader(
                memory,
                new EntityComponentResolver(memory, sleepingProfile),
                EntityNameCatalog.LoadEmbedded(),
                sleepingProfile);
            var result = reader.Read(area.SessionSequence, rawEntities);
            var evidence = result.Entities
                .Where(entity =>
                    entity.Category == Core.Area.AreaEntityCategory.Monster
                    || entity.Rarity == Core.Area.AreaEntityRarity.Unique
                    || entity.HasMinimapIcon)
                .OrderBy(entity => entity.EntityId)
                .ToArray();

            _output.WriteLine($"ProcessId={processId}");
            _output.WriteLine($"ProfileId={profile.ProfileId}");
            _output.WriteLine($"AreaCode={area.AreaCode}");
            _output.WriteLine($"SleepingRawEntityCount={rawEntities.Count}");
            _output.WriteLine($"SleepingReadableEntityCount={result.Entities.Count}");
            _output.WriteLine($"SleepingEvidenceEntityCount={evidence.Length}");
            foreach (var entity in evidence)
            {
                _output.WriteLine(
                    $"SleepingEntity id={entity.EntityId} name={entity.DisplayName} "
                    + $"metadata={entity.MetadataPath} "
                    + $"grid=({entity.GridPosition.X:F2},{entity.GridPosition.Y:F2}) "
                    + $"life={entity.CurrentLife}/{entity.MaximumLife} "
                    + $"poi={entity.HasMinimapIcon} "
                    + $"completed={entity.IsMinimapIconComplete} "
                    + $"disposition={entity.Disposition} rarity={entity.Rarity}");
            }
        }
    }

    [Fact]
    [Trait("Category", "Live")]
    public void MapViewProbe_TracksCurrentUi()
    {
        var configuredPid = Environment.GetEnvironmentVariable("FREI_LIVE_PID");
        if (string.IsNullOrWhiteSpace(configuredPid))
        {
            _output.WriteLine("Skipped: FREI_LIVE_PID was not provided; no process was accessed.");
            return;
        }

        Assert.True(
            int.TryParse(configuredPid, out var processId) && processId > 0,
            "FREI_LIVE_PID must contain one positive process ID.");
        Assert.True(
            ProcessAttachment.TryAttach(processId, out var attachment),
            $"Could not attach read-only process memory to specified PID {processId}.");

        using (attachment)
        {
            var memory = attachment!.Memory;
            var profile = Poe2MemoryProfile.Current;
            var clientBounds = new GameWindowTracker().Track(processId)?.ClientBounds;
            Assert.True(
                clientBounds is { Width: > 0, Height: > 0 },
                $"Could not read the client viewport for specified PID {processId}.");
            var locator = new GameStateLocator(profile);
            var session = new GameMemorySession(memory, profile, locator);
            Assert.True(
                session.TryRefresh(out var root, out var area, out var diagnostics),
                FormatDiagnostics(diagnostics));

            var probe = new MapUiCandidateProbe(memory, profile);
            var observations = new List<MapUiProbeResult>();
            for (var sample = 0; sample < 3; sample++)
            {
                observations.Add(probe.Probe(
                    root.InGameState,
                    clientBounds!.Value.Width,
                    clientBounds.Value.Height));
                Thread.Sleep(33);
            }

            Assert.Contains(observations, observation => observation.Candidates.Count > 0);
            _output.WriteLine($"ProcessId={processId}");
            _output.WriteLine($"AreaCode={area.AreaCode}");
            _output.WriteLine(
                $"ClientViewport={clientBounds!.Value.Width}x{clientBounds.Value.Height}"
                + $"@{clientBounds.Value.Left},{clientBounds.Value.Top}");
            _output.WriteLine($"MapUiSamples={observations.Count}");
            foreach (var observation in observations)
            {
                _output.WriteLine(
                    $"MapUiCandidates={observation.Candidates.Count} "
                    + $"Truncated={observation.IsTruncated} "
                    + $"Diagnostics={observation.Diagnostics.Count} "
                    + $"Codes={string.Join(",", observation.Diagnostics.Select(item => item.Code))}");
                foreach (var candidate in observation.Candidates)
                {
                    _output.WriteLine(
                        $"MapUi fingerprint={candidate.Fingerprint} visible={candidate.IsVisible} "
                        + $"shift=({candidate.Shift.X:F3},{candidate.Shift.Y:F3}) "
                        + $"zoom={candidate.Zoom:F3} "
                        + $"viewport=({candidate.Viewport.X:F1},{candidate.Viewport.Y:F1},"
                        + $"{candidate.Viewport.Width:F1},{candidate.Viewport.Height:F1}) "
                        + $"rotationRaw={candidate.RotationRaw:F3}");
                }
            }
        }
    }

    [Fact]
    [Trait("Category", "Live")]
    public void MapViewGeometryProbe_ReportsAddressFreeCandidateNeighborhood()
    {
        var configuredPid = Environment.GetEnvironmentVariable("FREI_LIVE_PID");
        if (string.IsNullOrWhiteSpace(configuredPid))
        {
            _output.WriteLine("Skipped: FREI_LIVE_PID was not provided; no process was accessed.");
            return;
        }

        Assert.True(
            int.TryParse(configuredPid, out var processId) && processId > 0,
            "FREI_LIVE_PID must contain one positive process ID.");
        Assert.True(
            ProcessAttachment.TryAttach(processId, out var attachment),
            $"Could not attach read-only process memory to specified PID {processId}.");

        using (attachment)
        {
            var memory = attachment!.Memory;
            var profile = Poe2MemoryProfile.Current;
            var clientBounds = new GameWindowTracker().Track(processId)?.ClientBounds;
            Assert.True(clientBounds is { Width: > 0, Height: > 0 });
            var session = new GameMemorySession(
                memory,
                profile,
                new GameStateLocator(profile));
            Assert.True(
                session.TryRefresh(out var root, out _, out var diagnostics),
                FormatDiagnostics(diagnostics));

            var probe = new MapUiCandidateProbe(memory, profile);
            var result = probe.Probe(
                root.InGameState,
                clientBounds!.Value.Width,
                clientBounds.Value.Height);
            Assert.NotEmpty(result.Candidates);
            var reader = new GameMemoryReader(memory, profile);
            foreach (var candidate in result.Candidates)
            {
                _output.WriteLine(
                    $"Candidate={candidate.Fingerprint};self={FormatViewport(candidate.Viewport)}");
                WriteAncestorGeometry(
                    probe,
                    reader,
                    profile,
                    candidate,
                    clientBounds.Value.Width,
                    clientBounds.Value.Height);
                WriteCornerDescendantGeometry(
                    probe,
                    reader,
                    profile,
                    candidate,
                    clientBounds.Value.Width,
                    clientBounds.Value.Height);
            }
        }
    }

    [Fact]
    [Trait("Category", "Live")]
    public void WorldTerrainContentProbe_ReportsCurrentMapFacts()
    {
        var configuredPid = Environment.GetEnvironmentVariable("FREI_LIVE_PID");
        if (string.IsNullOrWhiteSpace(configuredPid))
        {
            _output.WriteLine("Skipped: FREI_LIVE_PID was not provided; no process was accessed.");
            return;
        }

        Assert.True(
            int.TryParse(configuredPid, out var processId) && processId > 0,
            "FREI_LIVE_PID must contain one positive process ID.");
        Assert.True(
            ProcessAttachment.TryAttach(processId, out var attachment),
            $"Could not attach read-only process memory to specified PID {processId}.");

        using (attachment)
        {
            var memory = attachment!.Memory;
            var profile = Poe2MemoryProfile.Current;
            var locator = new GameStateLocator(profile);
            var session = new GameMemorySession(memory, profile, locator);
            Assert.True(
                session.TryRefresh(out var root, out var area, out var rootDiagnostics),
                FormatDiagnostics(rootDiagnostics));

            var tree = new AreaEntityTreeReader(memory, profile);
            Assert.True(
                tree.TryRead(root.AreaInstance, out var rawEntities, out var treeDiagnostic),
                treeDiagnostic?.Message ?? "The AwakeEntities tree could not be read.");
            var components = new EntityComponentResolver(memory, profile);
            var entities = new AreaEntityReader(
                memory,
                components,
                EntityNameCatalog.LoadEmbedded(),
                profile);
            var world = new AreaWorldReader(memory, components, entities, profile)
                .Read(root, area.SessionSequence, rawEntities);
            Assert.NotNull(world.Area);

            var terrain = new AreaTerrainReader(memory, profile)
                .Read(root.AreaInstance, area.SessionSequence);
            Assert.NotNull(terrain.Terrain);
            var landmarks = AreaLandmarkReader.LoadEmbedded(profile)
                .Read(area.AreaCode, terrain.Tiles);
            var evidenceReader = new ExpeditionStateEvidenceReader(
                memory,
                components,
                profile);
            var entityById = rawEntities.ToDictionary(entity => entity.EntityId);
            var evidenceByEntity = new Dictionary<uint, IReadOnlyList<Core.Area.AreaContentEvidence>>();
            var expeditionDetailsByEntity = new Dictionary<uint, Core.Area.AreaExpeditionDetails>();
            foreach (var entity in world.Entities)
            {
                if (!entityById.TryGetValue(entity.EntityId, out var rawEntity))
                {
                    continue;
                }

                var evidence = evidenceReader.Read(rawEntity, entity);
                if (evidence.Evidence.Count > 0)
                {
                    evidenceByEntity[entity.EntityId] = evidence.Evidence;
                }

                if (evidence.Matched)
                {
                    expeditionDetailsByEntity[entity.EntityId] =
                        new Core.Area.AreaExpeditionDetails(evidence.HoleCount);
                }
            }
            var contents = new AreaContentNormalizer(AreaContentCatalog.LoadEmbedded())
                .Normalize(
                    area,
                    world.Entities,
                    landmarks,
                    evidenceByEntity,
                    expeditionDetailsByEntity);

            Assert.Equal(rawEntities.Count, world.Entities.Count + world.Diagnostics.Count(
                diagnostic => diagnostic.Code is "entity-read-failed"
                    or "entity-unpositioned"
                    or "entity-logical-duplicate"));
            _output.WriteLine($"ProcessId={processId}");
            _output.WriteLine($"AreaCode={area.AreaCode}");
            _output.WriteLine(
                $"Terrain={terrain.Terrain.Width}x{terrain.Terrain.Height} "
                + $"TileCount={terrain.Tiles.Count} LandmarkCount={landmarks.Count}");
            _output.WriteLine(
                $"BossArenaLandmarkCount={landmarks.Count(landmark => landmark.Kind == Core.Area.AreaLandmarkKind.BossArena)}");
            _output.WriteLine(
                $"BossHintLandmarkCount={landmarks.Count(landmark => landmark.Kind == Core.Area.AreaLandmarkKind.BossHint)}");
            foreach (var landmark in landmarks.OrderBy(landmark => landmark.LandmarkId, StringComparer.Ordinal))
            {
                _output.WriteLine(
                    $"Landmark id={landmark.LandmarkId} kind={landmark.Kind} "
                    + $"grid={landmark.GridPosition.X:0.###},{landmark.GridPosition.Y:0.###} "
                    + $"tile={landmark.TilePath}");
            }
            foreach (var tilePath in terrain.Tiles
                         .Select(tile => tile.TilePath)
                         .Where(path => path.Contains("boss", StringComparison.OrdinalIgnoreCase)
                                        || path.Contains("arena", StringComparison.OrdinalIgnoreCase))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Order(StringComparer.OrdinalIgnoreCase)
                         .Take(50))
            {
                _output.WriteLine($"BossTileCandidate={tilePath}");
            }
            _output.WriteLine(
                $"ContentCount={contents.Count} "
                + $"Boss={contents.Count(content => content.Kind == Core.Area.AreaContentKind.Boss)} "
                + $"BossCandidate={contents.Count(content => content.Kind == Core.Area.AreaContentKind.BossCandidate)} "
                + $"Expedition={contents.Count(content => content.Kind == Core.Area.AreaContentKind.Expedition)} "
                + $"Unknown={contents.Count(content => content.Kind == Core.Area.AreaContentKind.Unknown)}");
            _output.WriteLine($"EvidenceEntityCount={evidenceByEntity.Count}");
            foreach (var content in contents.Take(30))
            {
                var sourceMetadata = content.SourceEntityId is { } sourceEntityId
                                     && world.Entities.FirstOrDefault(entity => entity.EntityId == sourceEntityId)
                                         is { } sourceEntity
                    ? sourceEntity.MetadataPath
                    : "none";
                _output.WriteLine(
                    $"Content id={content.InstanceId} kind={content.Kind} phase={content.Phase} "
                    + $"display={content.DisplayName} confidence={content.Confidence:F2} "
                    + $"sourceEntity={content.SourceEntityId?.ToString() ?? "none"} "
                    + $"grid={content.GridPosition.X:0.###},{content.GridPosition.Y:0.###} "
                    + $"metadata={sourceMetadata} "
                    + $"holes={content.ExpeditionDetails?.HoleCount?.ToString() ?? "?"} "
                    + $"evidence={string.Join(",", content.Evidence.Select(item => $"{item.Key}={item.Value}"))}");
            }
        }
    }

    [Fact]
    [Trait("Category", "Live")]
    public void ProviderProbe_PublishesUnifiedSnapshot()
    {
        var configuredPid = Environment.GetEnvironmentVariable("FREI_LIVE_PID");
        if (string.IsNullOrWhiteSpace(configuredPid))
        {
            _output.WriteLine("Skipped: FREI_LIVE_PID was not provided; no process was accessed.");
            return;
        }

        Assert.True(
            int.TryParse(configuredPid, out var processId) && processId > 0,
            "FREI_LIVE_PID must contain one positive process ID.");
        Assert.True(
            ProcessAttachment.TryAttach(processId, out var attachment),
            $"Could not attach read-only process memory to specified PID {processId}.");

        using (attachment)
        {
            var memory = attachment!.Memory;
            var profile = Poe2MemoryProfile.Current;
            var clientBounds = new GameWindowTracker().Track(processId)?.ClientBounds;
            Assert.True(clientBounds is { Width: > 0, Height: > 0 });
            var provider = new AreaMapProviderService(
                memory,
                new AreaMapProviderOptions
                {
                    ClientViewport = new AreaUiRect(
                        0f,
                        0f,
                        clientBounds!.Value.Width,
                        clientBounds.Value.Height)
                },
                profile: profile);

            provider.SampleWorld();
            provider.SampleRealtime();
            var snapshot = provider.Current;

            Assert.Equal(processId, snapshot.ProcessId);
            Assert.NotEqual(0u, snapshot.Area.AreaHash);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.Area.AreaCode));
            Assert.NotEmpty(snapshot.Entities);
            Assert.NotNull(snapshot.Terrain);
            Assert.NotEqual(
                AreaMapViewAvailability.Unavailable,
                snapshot.MapViews.LargeMap.Availability);
            Assert.NotNull(snapshot.MapViews.MiniMap.Viewport);

            _output.WriteLine($"ProcessId={snapshot.ProcessId}");
            _output.WriteLine($"AreaCode={snapshot.Area.AreaCode}");
            _output.WriteLine($"AreaHash=0x{snapshot.Area.AreaHash:X8}");
            _output.WriteLine($"Status={snapshot.Status}");
            _output.WriteLine($"Entities={snapshot.Entities.Count}");
            _output.WriteLine($"Contents={snapshot.Contents.Count}");
            _output.WriteLine($"Landmarks={snapshot.Landmarks.Count}");
            _output.WriteLine(
                $"Diagnostics={string.Join(",", snapshot.Diagnostics.Select(item => item.Code))}");
            _output.WriteLine(
                $"Terrain={snapshot.Terrain!.Width}x{snapshot.Terrain.Height}");
            _output.WriteLine(
                $"LargeMap={snapshot.MapViews.LargeMap.Availability}/"
                + $"visible:{snapshot.MapViews.LargeMap.IsVisible}");
            _output.WriteLine(
                $"MiniMap={snapshot.MapViews.MiniMap.Availability}/"
                + $"visible:{snapshot.MapViews.MiniMap.IsVisible}/"
                + $"viewport:{FormatViewport(snapshot.MapViews.MiniMap.Viewport)}");
        }
    }

    [Fact]
    [Trait("Category", "Live")]
    public void DirectMapPointerChains_ReportAddressFreeMiniMapGeometry()
    {
        var configuredPid = Environment.GetEnvironmentVariable("FREI_LIVE_PID");
        if (string.IsNullOrWhiteSpace(configuredPid))
        {
            _output.WriteLine("Skipped: FREI_LIVE_PID was not provided; no process was accessed.");
            return;
        }

        Assert.True(
            int.TryParse(configuredPid, out var processId) && processId > 0,
            "FREI_LIVE_PID must contain one positive process ID.");
        Assert.True(
            ProcessAttachment.TryAttach(processId, out var attachment),
            $"Could not attach read-only process memory to specified PID {processId}.");

        using (attachment)
        {
            var memory = attachment!.Memory;
            var profile = Poe2MemoryProfile.Current;
            var reader = new GameMemoryReader(memory, profile);
            var session = new GameMemorySession(
                memory,
                profile,
                new GameStateLocator(profile));
            var rootResolved = session.TryRefresh(
                out var root,
                out _,
                out var diagnostics);
            var clientBounds = new GameWindowTracker().Track(processId)?.ClientBounds;
            var clientWidth = clientBounds?.Width ?? 0;
            var clientHeight = clientBounds?.Height ?? 0;
            var viewportProbe = new MapUiCandidateProbe(memory, profile);
            var chains = new[]
            {
                ReadDirectMapChain(
                    "GameHelper2Main",
                    rootResolved ? root.InGameState : 0,
                    0x7C8,
                    0x28,
                    0x30,
                    clientWidth,
                    clientHeight,
                    memory,
                    reader,
                    profile,
                    viewportProbe),
                ReadDirectMapChain(
                    "Legacy",
                    rootResolved ? root.InGameState : 0,
                    0x738,
                    0x50,
                    0x58,
                    clientWidth,
                    clientHeight,
                    memory,
                    reader,
                    profile,
                    viewportProbe)
            };

            _output.WriteLine($"AttachedPid={processId}");
            _output.WriteLine($"RootResolved={rootResolved}");
            _output.WriteLine(
                $"RootDiagnosticCodes={string.Join(",", diagnostics.Select(item => item.Code))}");
            _output.WriteLine(
                $"ClientSize={(clientWidth > 0 && clientHeight > 0 ? $"{clientWidth}x{clientHeight}" : "unavailable")}");
            foreach (var chain in chains)
            {
                WriteDirectMapChainEvidence(chain);
            }

            var currentChain = chains.Single(chain => chain.Label == "GameHelper2Main");
            Assert.True(
                currentChain.UiManagerNonZero
                && currentChain.MapParentNonZero
                && IsUpperRightDirectMiniMap(
                    currentChain.MiniMap,
                    clientWidth,
                    clientHeight),
                "The current direct MiniMap chain did not expose readable map fields and a valid upper-right viewport.");
        }
    }

    private static DirectMapChainEvidence ReadDirectMapChain(
        string label,
        nint inGameState,
        int mapParentOffset,
        int largeMapOffset,
        int miniMapOffset,
        int clientWidth,
        int clientHeight,
        IProcessMemory memory,
        GameMemoryReader reader,
        Poe2MemoryProfile profile,
        MapUiCandidateProbe viewportProbe)
    {
        nint uiManager = 0;
        var uiManagerNonZero = inGameState != 0
                               && reader.TryReadPointer(inGameState + 0x2F0, out uiManager);
        nint mapParent = 0;
        var mapParentNonZero = uiManagerNonZero
                               && reader.TryReadPointer(uiManager + mapParentOffset, out mapParent);
        return new DirectMapChainEvidence(
            label,
            uiManagerNonZero,
            mapParentNonZero,
            ReadDirectMapElement(
                mapParent,
                mapParentNonZero,
                largeMapOffset,
                clientWidth,
                clientHeight,
                memory,
                reader,
                profile,
                viewportProbe),
            ReadDirectMapElement(
                mapParent,
                mapParentNonZero,
                miniMapOffset,
                clientWidth,
                clientHeight,
                memory,
                reader,
                profile,
                viewportProbe));
    }

    private static DirectMapElementEvidence ReadDirectMapElement(
        nint mapParent,
        bool mapParentNonZero,
        int elementOffset,
        int clientWidth,
        int clientHeight,
        IProcessMemory memory,
        GameMemoryReader reader,
        Poe2MemoryProfile profile,
        MapUiCandidateProbe viewportProbe)
    {
        nint element = 0;
        var pointerNonZero = mapParentNonZero
                             && reader.TryReadPointer(mapParent + elementOffset, out element);
        nint self = 0;
        var selfValid = pointerNonZero
                        && reader.TryReadPointer(
                            element + profile.UiElement.SelfOffset,
                            out self)
                        && self == element;
        var defaultShift = default(Vector2);
        var defaultShiftReadable = pointerNonZero
                                   && reader.TryReadVector2(
                                       element + profile.MapUi.DefaultShiftOffset,
                                       out defaultShift);
        var defaultShiftMatches = defaultShiftReadable
                                  && MathF.Abs(defaultShift.X) <= 0.001f
                                  && MathF.Abs(defaultShift.Y + 20f) <= 0.001f;
        var shift = default(Vector2);
        var shiftReadable = pointerNonZero
                            && reader.TryReadVector2(
                                element + profile.MapUi.ShiftOffset,
                                out shift);
        var zoom = 0f;
        var zoomReadable = pointerNonZero
                           && memory.TryReadFloat(
                               element + profile.MapUi.ZoomOffset,
                               out zoom)
                           && float.IsFinite(zoom);
        var viewport = pointerNonZero && clientWidth > 0 && clientHeight > 0
            ? ReadViewportForResearch(viewportProbe, element, clientWidth, clientHeight)
            : null;
        var visibleFlags = 0u;
        var visibleFlagReadable = pointerNonZero
                                  && reader.TryReadUInt32(
                                      element + profile.UiElement.FlagsOffset,
                                      out visibleFlags);
        var visible = visibleFlagReadable
                      && (visibleFlags & (1u << profile.UiElement.VisibleBit)) != 0;

        return new DirectMapElementEvidence(
            pointerNonZero,
            selfValid,
            defaultShiftReadable,
            defaultShiftMatches,
            defaultShift,
            shiftReadable,
            shift,
            zoomReadable,
            zoom,
            viewport,
            visibleFlagReadable,
            visible);
    }

    private void WriteDirectMapChainEvidence(DirectMapChainEvidence chain)
    {
        _output.WriteLine(
            $"DirectMapChain label={chain.Label} "
            + $"uiManagerNonZero={chain.UiManagerNonZero} "
            + $"mapParentNonZero={chain.MapParentNonZero}");
        WriteDirectMapElementEvidence(chain.Label, "Large", chain.LargeMap);
        WriteDirectMapElementEvidence(chain.Label, "Mini", chain.MiniMap);
    }

    private void WriteDirectMapElementEvidence(
        string chainLabel,
        string kind,
        DirectMapElementEvidence element)
        => _output.WriteLine(
            $"DirectMapElement chain={chainLabel} kind={kind} "
            + $"pointerNonZero={element.PointerNonZero} "
            + $"selfValid={element.SelfValid} "
            + $"signature={element.SignatureMatches} "
            + $"defaultShiftReadable={element.DefaultShiftReadable} "
            + $"defaultShift={FormatVector(element.DefaultShiftReadable, element.DefaultShift)} "
            + $"shiftReadable={element.ShiftReadable} "
            + $"shift={FormatVector(element.ShiftReadable, element.Shift)} "
            + $"zoomReadable={element.ZoomReadable} "
            + $"zoom={(element.ZoomReadable ? $"{element.Zoom:F3}" : "none")} "
            + $"rawViewport={FormatViewport(element.Viewport)} "
            + $"visibleFlagReadable={element.VisibleFlagReadable} "
            + $"visible={element.Visible}");

    private static bool IsUpperRightDirectMiniMap(
        DirectMapElementEvidence miniMap,
        int clientWidth,
        int clientHeight)
        => miniMap.PointerNonZero
           && miniMap.SelfValid
           && miniMap.DefaultShiftReadable
           && miniMap.ShiftReadable
           && miniMap.ZoomReadable
           && miniMap.Zoom is > 0.05f and < 8f
           && miniMap.VisibleFlagReadable
           && miniMap.Visible
           && miniMap.Viewport is { } viewport
           && viewport.Width > 1f
           && viewport.Height > 1f
           && viewport.X + viewport.Width >= clientWidth * 0.8f
           && viewport.Y <= clientHeight * 0.2f;

    private static string FormatVector(bool readable, Vector2 value)
        => readable ? $"({value.X:F3},{value.Y:F3})" : "none";

    private static string FormatViewport(AreaUiRect? viewport)
        => viewport is { } rect
            ? $"({rect.X:F1},{rect.Y:F1},{rect.Width:F1},{rect.Height:F1})"
            : "none";

    private void WriteAncestorGeometry(
        MapUiCandidateProbe probe,
        GameMemoryReader reader,
        Poe2MemoryProfile profile,
        MapUiCandidate candidate,
        float width,
        float height)
    {
        var current = candidate.Address;
        for (var depth = 1; depth <= 12; depth++)
        {
            if (!reader.TryReadPointer(
                    current + profile.UiElement.ParentOffset,
                    out var parent))
            {
                break;
            }

            var viewport = ReadViewportForResearch(probe, parent, width, height);
            _output.WriteLine(
                $"Candidate={candidate.Fingerprint};ancestor={depth};rect={FormatViewport(viewport)}");
            current = parent;
        }
    }

    private void WriteCornerDescendantGeometry(
        MapUiCandidateProbe probe,
        GameMemoryReader reader,
        Poe2MemoryProfile profile,
        MapUiCandidate candidate,
        float width,
        float height)
    {
        var queue = new Queue<(nint Address, string Path, int Depth)>();
        queue.Enqueue((candidate.Address, "self", 0));
        var visited = new HashSet<nint>();
        while (queue.Count > 0 && visited.Count < 512)
        {
            var item = queue.Dequeue();
            if (!visited.Add(item.Address) || item.Depth >= 3)
            {
                continue;
            }

            if (!reader.TryReadStdVector(
                    item.Address + profile.UiElement.ChildrenOffset,
                    IntPtr.Size,
                    512,
                    out var children))
            {
                continue;
            }

            for (var index = 0; index < children.Count; index++)
            {
                if (!reader.TryReadPointer(
                        children.First + (index * IntPtr.Size),
                        out var child))
                {
                    continue;
                }

                var path = $"{item.Path}/{index}";
                var viewport = ReadViewportForResearch(probe, child, width, height);
                if (viewport is { } rect
                    && rect.X + rect.Width >= width * 0.7f
                    && rect.Y <= height * 0.4f
                    && rect.Width >= 20f
                    && rect.Height >= 20f)
                {
                    _output.WriteLine(
                        $"Candidate={candidate.Fingerprint};cornerDescendant={path};rect={FormatViewport(rect)}");
                }

                queue.Enqueue((child, path, item.Depth + 1));
            }
        }
    }

    private static AreaUiRect? ReadViewportForResearch(
        MapUiCandidateProbe probe,
        nint address,
        float width,
        float height)
    {
        var method = typeof(MapUiCandidateProbe)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate =>
                candidate.Name == "TryReadViewport"
                && candidate.GetParameters().Length == 4);
        object?[] arguments =
        [
            address,
            width,
            height,
            new AreaUiRect(0f, 0f, 0f, 0f)
        ];
        return method.Invoke(probe, arguments) is true
            ? (AreaUiRect)arguments[3]!
            : null;
    }

    private static string FormatDiagnostics(
        IReadOnlyList<Core.Area.AreaReadDiagnostic> diagnostics)
        => diagnostics.Count == 0
            ? "The specified process did not expose a valid in-game root chain."
            : string.Join(
                Environment.NewLine,
                diagnostics.Select(item => $"{item.Code}: {item.Message}"));

    private void WriteRootEvidence(
        IProcessMemory memory,
        Poe2MemoryProfile profile,
        GameStateLocator locator)
    {
        if (memory is not IProcessMemoryLayout layout)
        {
            _output.WriteLine("RootEvidence: process memory does not expose module layout.");
            return;
        }

        var method = typeof(GameStateLocator).GetMethod(
            "FindGameStateSlots",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (method?.Invoke(locator, [memory, layout]) is not IEnumerable<nint> slots)
        {
            _output.WriteLine("RootEvidence: GameState slot scanner was unavailable.");
            return;
        }

        var reader = new GameMemoryReader(memory, profile);
        var slotIndex = 0;
        foreach (var slot in slots)
        {
            slotIndex++;
            if (!reader.TryReadPointer(slot, out var gameState))
            {
                _output.WriteLine($"RootEvidence[{slotIndex}]: slot=0x{slot.ToInt64():X}, stage=GameStateFailed");
                continue;
            }

            _output.WriteLine(
                $"RootEvidence[{slotIndex}]: slot=0x{slot.ToInt64():X}, gameState=0x{gameState.ToInt64():X}");
            var candidates = ReadStateCandidates(reader, gameState, profile);
            foreach (var candidate in candidates)
            {
                WriteCandidateEvidence(memory, reader, locator, profile, candidate);
            }
        }

        if (slotIndex == 0)
        {
            _output.WriteLine("RootEvidence: no GameState AOB slot matched.");
        }
    }

    private static IReadOnlyList<(string Source, nint Address)> ReadStateCandidates(
        GameMemoryReader reader,
        nint gameState,
        Poe2MemoryProfile profile)
    {
        var candidates = new List<(string Source, nint Address)>();
        if (reader.TryReadPointer(
                gameState + profile.GameState.CurrentStateVectorOffset,
                out var currentVector)
            && reader.TryReadPointer(currentVector, out var current))
        {
            candidates.Add(("Current", current));
        }

        for (var index = 0; index < profile.GameState.StateSlotCount; index++)
        {
            if (reader.TryReadPointer(
                    gameState
                    + profile.GameState.StateSlotsOffset
                    + (index * profile.GameState.StateSlotStride),
                    out var candidate))
            {
                candidates.Add(($"Fallback[{index}]", candidate));
            }
        }

        return candidates;
    }

    private void WriteCandidateEvidence(
        IProcessMemory memory,
        GameMemoryReader reader,
        GameStateLocator locator,
        Poe2MemoryProfile profile,
        (string Source, nint Address) candidate)
    {
        if (!reader.TryReadPointer(
                candidate.Address + profile.InGameState.AreaInstanceOffset,
                out var areaInstance))
        {
            WriteEvidence(candidate, "AreaInstanceFailed");
            return;
        }

        if (!reader.TryReadPointer(
                areaInstance + profile.AreaInstance.LocalPlayerOffset,
                out var localPlayer))
        {
            WriteEvidence(candidate, "LocalPlayerFailed", areaInstance);
            return;
        }

        if (!locator.TryReadMetadata(memory, localPlayer, out var metadata)
            || !metadata.StartsWith("Metadata/", StringComparison.Ordinal))
        {
            WriteEvidence(
                candidate,
                $"MetadataFailed value='{metadata}'",
                areaInstance,
                localPlayer);
            return;
        }

        if (!locator.TryResolveComponent(memory, localPlayer, "Render", out var render))
        {
            WriteEvidence(
                candidate,
                $"RenderComponentFailed metadata='{metadata}'",
                areaInstance,
                localPlayer);
            return;
        }

        if (!reader.TryReadVector3(
                render + profile.Render.WorldPositionOffset,
                out var worldPosition))
        {
            WriteEvidence(
                candidate,
                $"RenderPositionFailed render=0x{render.ToInt64():X}",
                areaInstance,
                localPlayer);
            return;
        }

        WriteEvidence(
            candidate,
            $"Valid metadata='{metadata}' render=0x{render.ToInt64():X} world=({worldPosition.X:F3},{worldPosition.Y:F3},{worldPosition.Z:F3})",
            areaInstance,
            localPlayer);
    }

    private void WriteEvidence(
        (string Source, nint Address) candidate,
        string stage,
        nint areaInstance = 0,
        nint localPlayer = 0)
        => _output.WriteLine(
            $"RootEvidence: source={candidate.Source}, inGameState=0x{candidate.Address.ToInt64():X}, "
            + $"areaInstance=0x{areaInstance.ToInt64():X}, localPlayer=0x{localPlayer.ToInt64():X}, stage={stage}");

    private sealed record DirectMapChainEvidence(
        string Label,
        bool UiManagerNonZero,
        bool MapParentNonZero,
        DirectMapElementEvidence LargeMap,
        DirectMapElementEvidence MiniMap);

    private sealed record DirectMapElementEvidence(
        bool PointerNonZero,
        bool SelfValid,
        bool DefaultShiftReadable,
        bool DefaultShiftMatches,
        Vector2 DefaultShift,
        bool ShiftReadable,
        Vector2 Shift,
        bool ZoomReadable,
        float Zoom,
        AreaUiRect? Viewport,
        bool VisibleFlagReadable,
        bool Visible)
    {
        public bool SignatureMatches
            => DefaultShiftMatches && ShiftReadable && ZoomReadable;
    }
}
