using FreiAtlas.Core.Area;
using FreiAtlas.Core.Contracts;
using FreiAtlas.Core.Memory;
using FreiAtlas.Game.Content;
using FreiAtlas.Game.Entities;
using FreiAtlas.Game.Memory;
using FreiAtlas.Game.Terrain;
using FreiAtlas.Game.Views;
using FreiAtlas.Game.World;

namespace FreiAtlas.Game;

internal interface IAreaMapReadSource
{
    int ProcessId { get; }

    string ProfileId { get; }

    AreaMapReadResult SampleWorld();

    AreaMapReadResult SampleRealtime();

    void ResetAreaCaches();

    void UpdateClientViewport(AreaUiRect clientViewport);
}

internal sealed record AreaMapReadResult(
    AreaIdentity? Area,
    AreaPlayerSnapshot? Player,
    IReadOnlyList<AreaEntitySnapshot> Entities,
    IReadOnlyList<AreaContentSnapshot> Contents,
    IReadOnlyList<AreaLandmarkSnapshot> Landmarks,
    AreaTerrainSnapshot? Terrain,
    AreaMapViewsSnapshot MapViews,
    IReadOnlyList<AreaReadDiagnostic> Diagnostics,
    bool RootChainHealthy,
    bool EntityTreeHealthy,
    bool TerrainHealthy)
{
    public AreaExpeditionRecipePanelSnapshot ExpeditionRecipePanel { get; init; } =
        AreaExpeditionRecipePanelSnapshot.Unavailable;
}

public sealed class AreaMapProviderService : IAreaMapApi
{
    private static readonly AreaReadDiagnostic EntityTreeRetainedDiagnostic = new(
        "entity-tree-last-good-retained",
        "The most recent complete same-area entity-tree world sample is retained temporarily.",
        AreaDiagnosticSeverity.Warning);

    private readonly IAreaMapReadSource _source;
    private readonly AreaMapProviderOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Action<AreaReadDiagnostic>? _diagnosticSink;
    private readonly object _sampleGate = new();
    private readonly object _notificationGate = new();
    private readonly List<AreaReadDiagnostic> _consumerDiagnostics = [];
    private readonly SortedDictionary<long, AreaMapNotification> _pendingNotifications = [];
    private AreaMapReadResult? _worldCache;
    private AreaMapReadResult? _realtimeCache;
    private AreaMapReadResult? _entityTreeFailureCache;
    private AreaUiRect _clientViewport;
    private DateTimeOffset? _lastRealtimeSuccessAt;
    private DateTimeOffset? _lastEntityTreeSuccessAt;
    private AreaMapSnapshot _current = AreaMapSnapshot.Loading(0, string.Empty, 0);
    private long _publicationSequence;
    private long _nextNotificationSequence = 1;
    private bool _isNotifying;
    private int _started;

    public AreaMapProviderService(
        IProcessMemory memory,
        AreaMapProviderOptions? options = null,
        TimeProvider? timeProvider = null,
        Poe2MemoryProfile? profile = null,
        Action<AreaReadDiagnostic>? diagnosticSink = null)
        : this(
            new MemoryAreaMapReadSource(
                memory ?? throw new ArgumentNullException(nameof(memory)),
                profile ?? Poe2MemoryProfile.Current,
                (options ?? new AreaMapProviderOptions()).ClientViewport,
                (options ?? new AreaMapProviderOptions())
                    .IncludeExpeditionResearchEvidence),
            options,
            timeProvider,
            diagnosticSink)
    {
    }

    internal AreaMapProviderService(
        IAreaMapReadSource source,
        AreaMapProviderOptions? options = null,
        TimeProvider? timeProvider = null,
        Action<AreaReadDiagnostic>? diagnosticSink = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _options = options ?? new AreaMapProviderOptions();
        _options.Validate();
        _clientViewport = _options.ClientViewport;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _diagnosticSink = diagnosticSink;
    }

    public AreaMapSnapshot Current => Volatile.Read(ref _current);

    internal AreaUiRect ClientViewport
    {
        get
        {
            lock (_sampleGate)
            {
                return _clientViewport;
            }
        }
    }

    public event Action<AreaMapSnapshot>? SnapshotChanged;

    public bool UpdateClientViewport(AreaUiRect clientViewport)
    {
        ArgumentNullException.ThrowIfNull(clientViewport);
        if (!float.IsFinite(clientViewport.X)
            || !float.IsFinite(clientViewport.Y)
            || !float.IsFinite(clientViewport.Width)
            || !float.IsFinite(clientViewport.Height)
            || clientViewport.Width <= 0f
            || clientViewport.Height <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(clientViewport));
        }

        var notifications = new List<AreaMapNotification>(capacity: 1);
        lock (_sampleGate)
        {
            if (_clientViewport == clientViewport)
            {
                return false;
            }

            _source.UpdateClientViewport(clientViewport);
            _clientViewport = clientViewport;
            _realtimeCache = null;
            _lastRealtimeSuccessAt = null;

            var current = Volatile.Read(ref _current);
            if (current.MapViews != AreaMapViewsSnapshot.Unavailable
                || current.ExpeditionRecipePanel != AreaExpeditionRecipePanelSnapshot.Unavailable)
            {
                var invalidated = current with
                {
                    MapViews = AreaMapViewsSnapshot.Unavailable,
                    ExpeditionRecipePanel = AreaExpeditionRecipePanelSnapshot.Unavailable
                };
                Interlocked.Exchange(ref _current, invalidated);
                QueueNotification(notifications, invalidated);
            }
        }

        NotifyConsumers(notifications);
        return true;
    }

    public AreaMapSnapshot SampleWorld()
        => Sample(_source.SampleWorld, isRealtime: false);

    public AreaMapSnapshot SampleRealtime()
        => Sample(_source.SampleRealtime, isRealtime: true);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The area map provider has already been started.");
        }

        try
        {
            var worldLoop = RunLoop(
                _options.WorldInterval,
                SampleWorld,
                cancellationToken);
            var realtimeLoop = RunLoop(
                _options.RealtimeInterval,
                SampleRealtime,
                cancellationToken);
            await Task.WhenAll(worldLoop, realtimeLoop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            Volatile.Write(ref _started, 0);
        }
    }

    private async Task RunLoop(
        TimeSpan interval,
        Func<AreaMapSnapshot> sample,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    sample();
                }
                catch (Exception exception)
                {
                    RecordDiagnostic(new AreaReadDiagnostic(
                        "sample-failed",
                        exception.Message,
                        AreaDiagnosticSeverity.Error));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private AreaMapSnapshot Sample(
        Func<AreaMapReadResult> read,
        bool isRealtime)
    {
        var notifications = new List<AreaMapNotification>(capacity: 2);
        AreaMapSnapshot result;
        var now = _timeProvider.GetUtcNow();
        var areaChanged = false;

        lock (_sampleGate)
        {
            AreaMapReadResult sample;
            try
            {
                sample = read();
            }
            catch (Exception exception)
            {
                sample = RootFailure(exception);
            }

            if (sample.Area is null)
            {
                if (CanRetainRealtime(now))
                {
                    result = Volatile.Read(ref _current);
                }
                else
                {
                    ClearSampleCachesLocked();

                    result = PublishLoadingLocked(sample, notifications);
                }
            }
            else
            {
                if (Volatile.Read(ref _current).Area.AreaHash == 0)
                {
                    PublishLoadingLocked(sample, notifications);
                }
                else if (HasAreaChanged(sample.Area))
                {
                    areaChanged = true;
                    PublishLoadingLocked(sample, notifications);
                    ClearSampleCachesLocked();
                    _source.ResetAreaCaches();
                }

                if (isRealtime)
                {
                    _realtimeCache = sample;
                    _lastRealtimeSuccessAt = now;
                }
                else
                {
                    UpdateWorldCacheLocked(sample, now);
                }

                result = areaChanged
                    ? Volatile.Read(ref _current)
                    : TryMergeLocked(now);
                if (!ReferenceEquals(result, Volatile.Read(ref _current)))
                {
                    Interlocked.Exchange(ref _current, result);
                    QueueNotification(notifications, result);
                }
            }
        }

        NotifyConsumers(notifications);
        return result;
    }

    private bool HasAreaChanged(AreaIdentity incoming)
    {
        var known = _worldCache?.Area
                    ?? _realtimeCache?.Area
                    ?? (Volatile.Read(ref _current).Area.AreaHash == 0
                        ? null
                        : Volatile.Read(ref _current).Area);
        return known is not null && !SameArea(known, incoming);
    }

    private AreaMapSnapshot TryMergeLocked(DateTimeOffset now)
    {
        ExpireEntityTreeRetentionLocked(now);
        var world = _worldCache;
        var realtime = _realtimeCache;
        if (world?.Area is null)
        {
            return Volatile.Read(ref _current);
        }

        if (_entityTreeFailureCache is { } entityTreeFailure)
        {
            if (realtime?.Area is not null && !SameArea(world.Area, realtime.Area))
            {
                return Volatile.Read(ref _current);
            }

            return CreateRetainedEntityTreeSnapshot(
                now,
                world,
                entityTreeFailure,
                realtime);
        }

        if (realtime?.Area is null)
        {
            var worldDegraded = IsDegraded(world);
            if (!worldDegraded)
            {
                return Volatile.Read(ref _current);
            }

            return CreateSnapshot(
                now,
                world,
                player: world.Player,
                mapViews: world.MapViews,
                expeditionRecipePanel: world.ExpeditionRecipePanel,
                status: AreaMapSnapshotStatus.Degraded,
                diagnostics: world.Diagnostics
                    .Concat(_consumerDiagnostics)
                    .Distinct()
                    .ToArray());
        }

        if (!SameArea(world.Area, realtime.Area))
        {
            return Volatile.Read(ref _current);
        }

        var diagnostics = world.Diagnostics
            .Concat(realtime.Diagnostics)
            .Concat(_consumerDiagnostics)
            .Distinct()
            .ToArray();
        var degraded = IsDegraded(world)
                       || !realtime.RootChainHealthy
                       || diagnostics.Any(item =>
                           item.Severity is AreaDiagnosticSeverity.Warning
                               or AreaDiagnosticSeverity.Error);
        return CreateSnapshot(
            now,
            world,
            realtime.Player ?? world.Player,
            realtime.MapViews,
            realtime.ExpeditionRecipePanel,
            degraded ? AreaMapSnapshotStatus.Degraded : AreaMapSnapshotStatus.Stable,
            diagnostics);
    }

    private AreaMapSnapshot CreateRetainedEntityTreeSnapshot(
        DateTimeOffset capturedAt,
        AreaMapReadResult world,
        AreaMapReadResult failure,
        AreaMapReadResult? realtime)
    {
        var diagnostics = world.Diagnostics
            .Concat(failure.Diagnostics)
            .Concat([EntityTreeRetainedDiagnostic])
            .Concat(realtime?.Diagnostics ?? [])
            .Concat(_consumerDiagnostics)
            .Distinct()
            .ToArray();
        return CreateSnapshot(
            capturedAt,
            world,
            realtime?.Player,
            realtime?.MapViews ?? AreaMapViewsSnapshot.Unavailable,
            realtime?.ExpeditionRecipePanel ?? AreaExpeditionRecipePanelSnapshot.Unavailable,
            AreaMapSnapshotStatus.Degraded,
            diagnostics);
    }

    private void UpdateWorldCacheLocked(AreaMapReadResult sample, DateTimeOffset now)
    {
        if (sample.RootChainHealthy && sample.EntityTreeHealthy)
        {
            _worldCache = sample;
            _lastEntityTreeSuccessAt = now;
            _entityTreeFailureCache = null;
            return;
        }

        if (CanRetainEntityTreeWorld(sample, now))
        {
            _entityTreeFailureCache = sample;
            return;
        }

        _worldCache = sample;
        _lastEntityTreeSuccessAt = null;
        _entityTreeFailureCache = null;
    }

    private bool CanRetainEntityTreeWorld(AreaMapReadResult sample, DateTimeOffset now)
        => sample.Area is { } area
           && sample.RootChainHealthy
           && !sample.EntityTreeHealthy
           && _worldCache is
           {
               Area: { } retainedArea,
               RootChainHealthy: true,
               EntityTreeHealthy: true
           }
           && SameArea(retainedArea, area)
           && _lastEntityTreeSuccessAt is { } last
           && now - last <= _options.SameAreaEntityTreeGracePeriod;

    private void ExpireEntityTreeRetentionLocked(DateTimeOffset now)
    {
        if (_entityTreeFailureCache is null
            || _lastEntityTreeSuccessAt is not { } last
            || now - last <= _options.SameAreaEntityTreeGracePeriod)
        {
            return;
        }

        _worldCache = _entityTreeFailureCache;
        _entityTreeFailureCache = null;
        _lastEntityTreeSuccessAt = null;
    }

    private void ClearSampleCachesLocked()
    {
        _worldCache = null;
        _realtimeCache = null;
        _entityTreeFailureCache = null;
        _lastRealtimeSuccessAt = null;
        _lastEntityTreeSuccessAt = null;
    }

    private AreaMapSnapshot CreateSnapshot(
        DateTimeOffset capturedAt,
        AreaMapReadResult world,
        AreaPlayerSnapshot? player,
        AreaMapViewsSnapshot mapViews,
        AreaExpeditionRecipePanelSnapshot expeditionRecipePanel,
        AreaMapSnapshotStatus status,
        IReadOnlyList<AreaReadDiagnostic>? diagnostics = null)
        => new(
            capturedAt,
            _source.ProcessId,
            _source.ProfileId,
            status,
            world.Area!,
            player,
            world.Entities.ToArray(),
            world.Contents.ToArray(),
            world.Landmarks.ToArray(),
            world.Terrain,
            mapViews,
            diagnostics ?? world.Diagnostics)
        {
            ExpeditionRecipePanel = expeditionRecipePanel
        };

    private bool CanRetainRealtime(DateTimeOffset now)
        => _realtimeCache is not null
           && _lastRealtimeSuccessAt is { } last
           && now - last <= _options.SameAreaRootGracePeriod;

    private bool IsDegraded(AreaMapReadResult sample)
        => !sample.RootChainHealthy
           || !sample.EntityTreeHealthy
           || !sample.TerrainHealthy
           || sample.Terrain is null
           || sample.Diagnostics.Any(item =>
               item.Severity is AreaDiagnosticSeverity.Warning
                   or AreaDiagnosticSeverity.Error);

    private AreaMapSnapshot PublishLoadingLocked(
        AreaMapReadResult sample,
        ICollection<AreaMapNotification>? notifications,
        bool includeDiagnostic = false)
    {
        var diagnostics = includeDiagnostic
            ? sample.Diagnostics.Concat([new AreaReadDiagnostic(
                    "area-identity-mismatch",
                    "World and realtime samples refer to different area identities.",
                    AreaDiagnosticSeverity.Warning)])
                .Distinct()
                .ToArray()
            : sample.Diagnostics.ToArray();
        var sequence = sample.Area?.SessionSequence
                       ?? Volatile.Read(ref _current).Area.SessionSequence;
        var snapshot = new AreaMapSnapshot(
            _timeProvider.GetUtcNow(),
            _source.ProcessId,
            _source.ProfileId,
            AreaMapSnapshotStatus.Loading,
            sample.Area ?? new AreaIdentity(0, string.Empty, 0, sequence),
            null,
            [],
            [],
            [],
            null,
            AreaMapViewsSnapshot.Unavailable,
            diagnostics)
        {
            ExpeditionRecipePanel = AreaExpeditionRecipePanelSnapshot.Unavailable
        };
        Interlocked.Exchange(ref _current, snapshot);
        if (notifications is not null)
        {
            QueueNotification(notifications, snapshot);
        }

        return snapshot;
    }

    private void QueueNotification(
        ICollection<AreaMapNotification> notifications,
        AreaMapSnapshot snapshot)
        => notifications.Add(new AreaMapNotification(
            ++_publicationSequence,
            snapshot));

    private void NotifyConsumers(IReadOnlyList<AreaMapNotification> notifications)
    {
        lock (_notificationGate)
        {
            foreach (var notification in notifications)
            {
                _pendingNotifications.Add(notification.Sequence, notification);
            }

            if (_isNotifying)
            {
                return;
            }

            _isNotifying = true;
        }

        while (true)
        {
            AreaMapNotification notification;
            lock (_notificationGate)
            {
                if (!_pendingNotifications.Remove(
                        _nextNotificationSequence,
                        out notification))
                {
                    _isNotifying = false;
                    return;
                }

                _nextNotificationSequence++;
            }

            NotifyConsumers(notification.Snapshot);
        }
    }

    private void NotifyConsumers(AreaMapSnapshot snapshot)
    {
        var handlers = SnapshotChanged?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers)
        {
            try
            {
                ((Action<AreaMapSnapshot>)handler)(snapshot);
            }
            catch (Exception exception)
            {
                RecordDiagnostic(new AreaReadDiagnostic(
                    "snapshot-consumer-failed",
                    exception.Message,
                    AreaDiagnosticSeverity.Warning));
            }
        }
    }

    private void RecordDiagnostic(AreaReadDiagnostic diagnostic)
    {
        _diagnosticSink?.Invoke(diagnostic);
        lock (_sampleGate)
        {
            if (_consumerDiagnostics.Contains(diagnostic))
            {
                return;
            }

            _consumerDiagnostics.Add(diagnostic);
            while (true)
            {
                var current = Volatile.Read(ref _current);
                var updated = current with
                {
                    Diagnostics = current.Diagnostics.Concat([diagnostic]).ToArray(),
                    Status = current.Status == AreaMapSnapshotStatus.Stable
                        ? AreaMapSnapshotStatus.Degraded
                        : current.Status
                };
                if (ReferenceEquals(
                        Interlocked.CompareExchange(ref _current, updated, current),
                        current))
                {
                    return;
                }
            }
        }
    }

    private static bool SameArea(AreaIdentity? left, AreaIdentity right)
        => left is not null
           && left.AreaHash == right.AreaHash
           && left.SessionSequence == right.SessionSequence;

    private static AreaMapReadResult RootFailure(Exception exception)
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
                exception.Message,
                AreaDiagnosticSeverity.Warning)],
            RootChainHealthy: false,
            EntityTreeHealthy: false,
            TerrainHealthy: false);

    private readonly record struct AreaMapNotification(
        long Sequence,
        AreaMapSnapshot Snapshot);
}

internal sealed record AreaContentReadBatch(
    IReadOnlyDictionary<uint, IReadOnlyList<AreaContentEvidence>> EvidenceByEntity,
    IReadOnlyDictionary<uint, AreaExpeditionDetails> ExpeditionDetailsByEntity);

internal sealed class MemoryAreaMapReadSource : IAreaMapReadSource
{
    private readonly IProcessMemory _memory;
    private readonly Poe2MemoryProfile _profile;
    private readonly GameMemorySession _session;
    private readonly AreaEntityTreeReader _entityTree;
    private readonly EntityComponentResolver _components;
    private readonly AreaEntityReader _entityReader;
    private readonly AreaWorldReader _worldReader;
    private readonly AreaTerrainReader _terrainReader;
    private readonly AreaLandmarkReader _landmarkReader;
    private readonly ExpeditionStateEvidenceReader _evidenceReader;
    private readonly MechanicStateEvidenceReader _mechanicEvidenceReader;
    private readonly AreaContentNormalizer _contentNormalizer;
    private readonly MapUiCandidateProbe _mapUiProbe;
    private readonly AreaMapViewReader _mapViewReader;
    private readonly ExpeditionRecipeUiProbe _recipeUiProbe;
    private readonly ExpeditionRecipePanelMatcher _recipePanelMatcher;
    private AreaUiRect _clientViewport;
    private long _mapUiSessionSequence = long.MinValue;
    private long _recipeUiSessionSequence = long.MinValue;
    private IReadOnlyList<AreaContentSnapshot> _latestWorldContents = [];

    public MemoryAreaMapReadSource(
        IProcessMemory memory,
        Poe2MemoryProfile profile,
        AreaUiRect clientViewport,
        bool includeExpeditionResearchEvidence)
    {
        _memory = memory;
        _profile = profile;
        _clientViewport = clientViewport;
        _session = new GameMemorySession(
            memory,
            profile,
            new GameStateLocator(profile));
        _entityTree = new AreaEntityTreeReader(memory, profile);
        _components = new EntityComponentResolver(memory, profile);
        _entityReader = new AreaEntityReader(
            memory,
            _components,
            EntityNameCatalog.LoadEmbedded(),
            profile);
        _worldReader = new AreaWorldReader(
            memory,
            _components,
            _entityReader,
            profile);
        _terrainReader = new AreaTerrainReader(memory, profile);
        _landmarkReader = AreaLandmarkReader.LoadEmbedded(profile);
        _evidenceReader = new ExpeditionStateEvidenceReader(
            memory,
            _components,
            profile,
            includeExpeditionResearchEvidence);
        _mechanicEvidenceReader = new MechanicStateEvidenceReader(
            memory,
            _components,
            profile);
        _contentNormalizer = new AreaContentNormalizer(
            AreaContentCatalog.LoadEmbedded());
        _mapUiProbe = new MapUiCandidateProbe(memory, profile);
        _mapViewReader = new AreaMapViewReader();
        _recipeUiProbe = new ExpeditionRecipeUiProbe(memory, profile);
        _recipePanelMatcher = new ExpeditionRecipePanelMatcher();
    }

    public int ProcessId => _memory.ProcessId;

    public string ProfileId => _profile.ProfileId;

    public AreaMapReadResult SampleWorld()
        => ReadCore(includeWorld: true);

    public AreaMapReadResult SampleRealtime()
        => ReadCore(includeWorld: false);

    public void ResetAreaCaches()
    {
        _components.Reset();
        _mapUiProbe.Reset();
        _mapViewReader.Reset();
        _recipeUiProbe.Reset();
        _recipePanelMatcher.Reset();
        _latestWorldContents = [];
        _mapUiSessionSequence = long.MinValue;
        _recipeUiSessionSequence = long.MinValue;
    }

    public void UpdateClientViewport(AreaUiRect clientViewport)
    {
        if (_clientViewport == clientViewport)
        {
            return;
        }

        _clientViewport = clientViewport;
        _mapUiProbe.Reset();
        _mapViewReader.Reset();
        _recipeUiProbe.Reset();
        _recipePanelMatcher.Reset();
    }

    private AreaMapReadResult ReadCore(bool includeWorld)
    {
        if (!_session.TryRefresh(out var root, out var area, out var diagnostics))
        {
            return new AreaMapReadResult(
                null,
                null,
                [],
                [],
                [],
                null,
                AreaMapViewsSnapshot.Unavailable,
                diagnostics,
                RootChainHealthy: false,
                EntityTreeHealthy: false,
                TerrainHealthy: false);
        }

        var messages = diagnostics.ToList();
        var views = ReadMapViews(root, area.SessionSequence, messages);
        var recipeContents = includeWorld ? _latestWorldContents : _latestWorldContents;
        if (!includeWorld)
        {
            var player = _worldReader.ReadPlayer(
                root,
                area.SessionSequence,
                out var playerDiagnostic);
            if (playerDiagnostic is not null)
            {
                messages.Add(playerDiagnostic);
            }

            return new AreaMapReadResult(
                area,
                player,
                [],
                [],
                [],
                null,
                views,
                messages,
                RootChainHealthy: true,
                EntityTreeHealthy: true,
                TerrainHealthy: true)
            {
                ExpeditionRecipePanel = ReadRecipePanel(root, area.SessionSequence, recipeContents)
            };
        }

        if (!_entityTree.TryRead(
                root.AreaInstance,
                out var rawEntities,
                out var treeDiagnostic))
        {
            if (treeDiagnostic is not null)
            {
                messages.Add(treeDiagnostic);
            }

            return new AreaMapReadResult(
                area,
                null,
                [],
                [],
                [],
                null,
                views,
                messages,
                RootChainHealthy: true,
                EntityTreeHealthy: false,
                TerrainHealthy: false);
        }

        var world = _worldReader.Read(
            root,
            area.SessionSequence,
            rawEntities);
        messages.AddRange(world.Diagnostics);
        var terrain = _terrainReader.Read(
            root.AreaInstance,
            area.SessionSequence);
        messages.AddRange(terrain.Diagnostics);
        var landmarks = terrain.Terrain is null
            ? []
            : _landmarkReader.Read(area.AreaCode, terrain.Tiles);
        var contentData = ReadContentData(
            rawEntities,
            world.Entities,
            area.SessionSequence,
            area.AreaLevel);
        var contents = _contentNormalizer.Normalize(
            area,
            world.Entities,
            landmarks,
            contentData.EvidenceByEntity,
            contentData.ExpeditionDetailsByEntity,
            player: GetContentNormalizationPlayer(world.Diagnostics, world.Player),
            rawObservedEntityIds: rawEntities
                .Select(entity => entity.EntityId)
                .ToHashSet());
        _latestWorldContents = contents;
        return new AreaMapReadResult(
            area,
            world.Player,
            world.Entities,
            contents,
            landmarks,
            terrain.Terrain,
            views,
            messages,
            RootChainHealthy: true,
            EntityTreeHealthy: true,
            TerrainHealthy: terrain.Terrain is not null)
        {
            ExpeditionRecipePanel = ReadRecipePanel(root, area.SessionSequence, contents)
        };
    }

    private AreaMapViewsSnapshot ReadMapViews(
        GameRootState root,
        long sessionSequence,
        List<AreaReadDiagnostic> diagnostics)
    {
        if (_mapUiSessionSequence != sessionSequence)
        {
            _mapUiSessionSequence = sessionSequence;
            _mapUiProbe.Reset();
        }

        var result = _mapUiProbe.ProbeDirect(
            root.InGameState,
            _clientViewport.Width,
            _clientViewport.Height);
        diagnostics.AddRange(result.Diagnostics);
        return _mapViewReader.Read(
            sessionSequence,
            result.Candidates,
            _clientViewport);
    }

    internal static AreaPlayerSnapshot? GetContentNormalizationPlayer(
        IReadOnlyList<AreaReadDiagnostic> diagnostics,
        AreaPlayerSnapshot? player)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return diagnostics.Any(diagnostic =>
                   diagnostic.Code == "entity-read-failed")
            ? null
            : player;
    }

    private AreaExpeditionRecipePanelSnapshot ReadRecipePanel(
        GameRootState root,
        long sessionSequence,
        IReadOnlyList<AreaContentSnapshot> contents)
    {
        if (_recipeUiSessionSequence != sessionSequence)
        {
            _recipeUiSessionSequence = sessionSequence;
            _recipeUiProbe.Reset();
            _recipePanelMatcher.Reset();
            _latestWorldContents = contents;
        }

        var result = _recipeUiProbe.Probe(root.InGameState, _clientViewport);
        return _recipePanelMatcher.Observe(sessionSequence, result, contents);
    }

    private AreaContentReadBatch ReadContentData(
        IReadOnlyList<RawEntityRef> rawEntities,
        IReadOnlyList<AreaEntitySnapshot> entities,
        long sessionSequence,
        int areaLevel)
    {
        _components.BeginSample(sessionSequence);
        var byId = rawEntities.ToDictionary(entity => entity.EntityId);
        var evidence = new Dictionary<uint, IReadOnlyList<AreaContentEvidence>>();
        var details = new Dictionary<uint, AreaExpeditionDetails>();
        foreach (var entity in entities)
        {
            if (!byId.TryGetValue(entity.EntityId, out var raw))
            {
                continue;
            }

            var result = _evidenceReader.Read(raw, entity, areaLevel);
            var mechanicResult = _mechanicEvidenceReader.Read(raw, entity);
            var entityEvidence = result.Evidence
                .Concat(mechanicResult.Evidence)
                .ToArray();
            if (entityEvidence.Length > 0)
            {
                evidence[entity.EntityId] = entityEvidence;
            }

            if (result.Matched)
            {
                details[entity.EntityId] = new AreaExpeditionDetails(
                    result.HoleCount,
                    result.Recipes.IsDefault ? [] : result.Recipes);
            }
        }

        return new AreaContentReadBatch(evidence, details);
    }
}
