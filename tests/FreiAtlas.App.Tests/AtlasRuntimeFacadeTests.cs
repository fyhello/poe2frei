using FreiAtlas.App.Content;
using FreiAtlas.App.Processes;
using FreiAtlas.App.Runtime;
using FreiAtlas.App.Settings;
using FreiAtlas.Core.Atlas;

namespace FreiAtlas.App.Tests;

public sealed class AtlasRuntimeFacadeTests
{
    [Fact]
    public void SettingsAppSnapshot_ExposesOnlyApprovedProperties()
    {
        var properties = typeof(AtlasSettingsAppSnapshot)
            .GetProperties()
            .Select(property => property.Name)
            .Order()
            .ToArray();

        Assert.Equal(
            new[]
            {
                "HotkeyRegistrationError",
                "IsOverlayRunning",
                "IsOverlayStopping",
                "LoadStatus",
                "NavigationCatalog",
                "NodeCounts",
                "OverlayStatusMessage",
                "OverlayToggleHotkey",
                "PersistenceStatus",
                "Processes",
                "SelectedProcessId",
                "Settings"
            },
            properties);
    }

    [Fact]
    public async Task NavigationCatalog_InitiallyWaitsForAtlasData()
    {
        await using var fixture = await RuntimeFixture.CreateAsync([]);

        Assert.Equal(
            AtlasNavigationCatalogStatus.Waiting,
            fixture.Facade.Current.NavigationCatalog.Status);
        Assert.Null(fixture.Facade.Current.NavigationCatalog.LastReadAt);
        Assert.Empty(fixture.Facade.Current.NavigationCatalog.Entries);
    }

    [Fact]
    public async Task NavigationCatalog_RetainsLastStableDirectoryAcrossClosedSnapshot()
    {
        await using var fixture = await RuntimeFixture.CreateAsync([]);
        var capturedAt = DateTimeOffset.Parse("2026-08-05T12:00:00Z");
        fixture.Session.Publish(SnapshotWithNames(
            capturedAt,
            "live",
            (new AtlasGridPos(2, 0), " Dunes "),
            (new AtlasGridPos(1, 0), "dunes"),
            (new AtlasGridPos(3, 0), "Mesa")));

        var live = fixture.Facade.Current.NavigationCatalog;
        Assert.Equal(AtlasNavigationCatalogStatus.Live, live.Status);
        Assert.Equal(capturedAt, live.LastReadAt);
        Assert.Equal(2, live.Entries.Count);
        Assert.Equal(new AtlasNavigationCatalogEntry("dunes", 2, 2), live.Entries[0]);

        fixture.Session.Publish(new AtlasSnapshot(
            capturedAt.AddSeconds(1),
            AtlasSnapshotStatus.Loading,
            0,
            0,
            [],
            [],
            null,
            AtlasProjection.Identity,
            "closed",
            false));

        var cached = fixture.Facade.Current.NavigationCatalog;
        Assert.Equal(AtlasNavigationCatalogStatus.Cached, cached.Status);
        Assert.Equal(live.LastReadAt, cached.LastReadAt);
        Assert.Equal(live.Entries, cached.Entries);
        Assert.Equal(live.Signature, cached.Signature);
    }

    [Fact]
    public async Task NavigationCatalog_PublishesWhenNamesChangeWithoutCountChange()
    {
        await using var fixture = await RuntimeFixture.CreateAsync([]);
        var published = 0;
        fixture.Facade.SnapshotChanged += _ => published++;
        fixture.Session.Publish(SnapshotWithNames(
            DateTimeOffset.UnixEpoch,
            "first",
            (new AtlasGridPos(0, 0), "Dunes")));
        fixture.Session.Publish(SnapshotWithNames(
            DateTimeOffset.UnixEpoch.AddSeconds(1),
            "second",
            (new AtlasGridPos(0, 0), "Mesa")));

        Assert.Equal(2, published);
        Assert.Equal(
            "Mesa",
            Assert.Single(fixture.Facade.Current.NavigationCatalog.Entries).DisplayName);
    }

    [Fact]
    public async Task NavigationCatalog_DoesNotRepublishIdenticalLiveSamples()
    {
        await using var fixture = await RuntimeFixture.CreateAsync([]);
        var published = 0;
        fixture.Facade.SnapshotChanged += _ => published++;
        var first = SnapshotWithNames(
            DateTimeOffset.UnixEpoch,
            "first",
            (new AtlasGridPos(0, 0), "Dunes"));

        fixture.Session.Publish(first);
        fixture.Session.Publish(first with
        {
            CapturedAt = DateTimeOffset.UnixEpoch.AddSeconds(1),
            Signature = "unrelated-change"
        });

        Assert.Equal(1, published);
        Assert.Equal(
            DateTimeOffset.UnixEpoch,
            fixture.Facade.Current.NavigationCatalog.LastReadAt);
    }

    [Fact]
    public async Task NavigationCatalog_ReopeningSameDirectoryPublishesLiveWithNewTime()
    {
        await using var fixture = await RuntimeFixture.CreateAsync([]);
        var first = SnapshotWithNames(
            DateTimeOffset.UnixEpoch,
            "first",
            (new AtlasGridPos(0, 0), "Dunes"));
        fixture.Session.Publish(first);
        fixture.Session.Publish(first with
        {
            Status = AtlasSnapshotStatus.Loading,
            IsAtlasOpen = false
        });
        var reopenedAt = DateTimeOffset.UnixEpoch.AddMinutes(1);

        fixture.Session.Publish(first with
        {
            CapturedAt = reopenedAt,
            Signature = "reopened"
        });

        Assert.Equal(
            AtlasNavigationCatalogStatus.Live,
            fixture.Facade.Current.NavigationCatalog.Status);
        Assert.Equal(reopenedAt, fixture.Facade.Current.NavigationCatalog.LastReadAt);
    }

    [Fact]
    public async Task NavigationCatalog_SessionEndMarksLiveDirectoryCached()
    {
        await using var fixture = await RuntimeFixture.CreateAsync([]);
        fixture.Session.Publish(SnapshotWithNames(
            DateTimeOffset.UnixEpoch,
            "first",
            (new AtlasGridPos(0, 0), "Dunes")));

        fixture.Session.End(null);

        Assert.Equal(
            AtlasNavigationCatalogStatus.Cached,
            fixture.Facade.Current.NavigationCatalog.Status);
        Assert.Single(fixture.Facade.Current.NavigationCatalog.Entries);
    }

    [Fact]
    public async Task ToggleOverlayAsync_WithoutPid_PublishesActionableStatus()
    {
        await using var fixture = await RuntimeFixture.CreateAsync([]);
        await fixture.Facade.StartAsync();

        Assert.False(await fixture.Facade.ToggleOverlayAsync());

        Assert.Equal(
            "请先选择游戏进程",
            fixture.Facade.Current.OverlayStatusMessage);
        Assert.Equal(0, fixture.Session.StartCount);
    }

    [Fact]
    public async Task UpdateHotkeyAsync_PublishesCurrentHotkey()
    {
        await using var fixture = await RuntimeFixture.CreateAsync([]);
        var hotkey = AtlasHotkey.Parse("Alt+R");

        await fixture.Facade.UpdateHotkeyAsync(hotkey);

        Assert.Equal(hotkey, fixture.Facade.Current.OverlayToggleHotkey);
        fixture.Facade.SetHotkeyRegistrationError("快捷键已被其他程序占用");
        Assert.Equal(
            "快捷键已被其他程序占用",
            fixture.Facade.Current.HotkeyRegistrationError);
    }

    [Fact]
    public async Task StopOverlayAsync_PublishesStoppingBeforeCleanupCompletes()
    {
        await using var fixture = await RuntimeFixture.CreateAsync(
            [Process(123, "逐风者")]);
        await fixture.Facade.StartAsync();
        Assert.True(await fixture.Facade.StartOverlayAsync());
        fixture.Session.DelayStopCompletion = true;

        var stop = fixture.Facade.StopOverlayAsync();
        await fixture.Session.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, fixture.Session.RequestStopCount);
        Assert.False(stop.IsCompleted);
        Assert.True(fixture.Facade.Current.IsOverlayRunning);
        Assert.True(fixture.Facade.Current.IsOverlayStopping);

        fixture.Session.CompleteStop();
        await stop;

        Assert.False(fixture.Facade.Current.IsOverlayRunning);
        Assert.False(fixture.Facade.Current.IsOverlayStopping);
    }

    [Fact]
    public async Task StopOverlayAsync_RequestsStopWhileProcessRefreshIsBlocked()
    {
        await using var fixture = await RuntimeFixture.CreateAsync(
            [Process(123, "逐风者")]);
        await fixture.Facade.StartAsync();
        Assert.True(await fixture.Facade.StartOverlayAsync());
        fixture.Session.DelayStopCompletion = true;
        fixture.Catalog.BlockReads = true;
        var refresh = fixture.Facade.RefreshProcessesAsync();
        await fixture.Catalog.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stop = fixture.Facade.StopOverlayAsync();

        await fixture.Session.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(fixture.Facade.Current.IsOverlayStopping);
        fixture.Catalog.ContinueReads.TrySetResult(null);
        fixture.Session.CompleteStop();
        await Task.WhenAll(refresh, stop);
    }

    [Fact]
    public async Task StartOverlayAsync_WhileStopping_ReturnsFalseWithoutRestarting()
    {
        await using var fixture = await RuntimeFixture.CreateAsync(
            [Process(123, "逐风者")]);
        await fixture.Facade.StartAsync();
        Assert.True(await fixture.Facade.StartOverlayAsync());
        fixture.Session.DelayStopCompletion = true;
        var stop = fixture.Facade.StopOverlayAsync();
        await fixture.Session.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var restarted = await fixture.Facade.StartOverlayAsync();

        Assert.False(restarted);
        Assert.Equal(1, fixture.Session.StartCount);
        fixture.Session.CompleteStop();
        await stop;
    }

    [Fact]
    public async Task StopOverlayAsync_WhileAlreadyStopping_DoesNotRequestAgain()
    {
        await using var fixture = await RuntimeFixture.CreateAsync(
            [Process(123, "逐风者")]);
        await fixture.Facade.StartAsync();
        Assert.True(await fixture.Facade.StartOverlayAsync());
        fixture.Session.DelayStopCompletion = true;
        var firstStop = fixture.Facade.StopOverlayAsync();
        await fixture.Session.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var secondStop = fixture.Facade.StopOverlayAsync();

        Assert.Equal(1, fixture.Session.RequestStopCount);
        Assert.False(secondStop.IsCompleted);
        fixture.Session.CompleteStop();
        await Task.WhenAll(firstStop, secondStop);
        Assert.Equal(1, fixture.Session.RequestStopCount);
    }

    [Fact]
    public async Task OverlayFailureWhileStopping_RemainsVisibleAfterCleanup()
    {
        await using var fixture = await RuntimeFixture.CreateAsync(
            [Process(123, "逐风者")]);
        await fixture.Facade.StartAsync();
        Assert.True(await fixture.Facade.StartOverlayAsync());
        fixture.Session.DelayStopCompletion = true;
        var stop = fixture.Facade.StopOverlayAsync();
        await fixture.Session.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));

        fixture.Session.End(new InvalidOperationException("render failed"));

        Assert.True(fixture.Facade.Current.IsOverlayStopping);
        Assert.Equal(
            "覆盖层运行失败",
            fixture.Facade.Current.OverlayStatusMessage);
        fixture.Session.CompleteStop();
        await stop;
        Assert.False(fixture.Facade.Current.IsOverlayStopping);
        Assert.Equal(
            "覆盖层运行失败",
            fixture.Facade.Current.OverlayStatusMessage);
    }

    [Fact]
    public async Task StartAsync_PerformsImmediateAndPeriodicRefresh()
    {
        await using var fixture = await RuntimeFixture.CreateAsync(
            [Process(123, "逐风者")],
            TimeSpan.FromMilliseconds(20));

        await fixture.Facade.StartAsync();
        await WaitUntilAsync(() => fixture.Catalog.ReadCount >= 3);

        Assert.Equal(123, fixture.Facade.Current.SelectedProcessId);
        Assert.False(fixture.Facade.Current.IsOverlayRunning);
    }

    [Fact]
    public async Task RefreshProcessesAsync_RefreshesImmediately()
    {
        await using var fixture = await RuntimeFixture.CreateAsync([]);
        await fixture.Facade.StartAsync();
        var before = fixture.Catalog.ReadCount;

        await fixture.Facade.RefreshProcessesAsync();

        Assert.Equal(before + 1, fixture.Catalog.ReadCount);
    }

    [Fact]
    public async Task ConcurrentRefreshes_SerializeProcessCatalogReads()
    {
        await using var fixture = await RuntimeFixture.CreateAsync([]);
        fixture.Catalog.ReadDelay = TimeSpan.FromMilliseconds(50);

        await Task.WhenAll(
            fixture.Facade.RefreshProcessesAsync(),
            fixture.Facade.RefreshProcessesAsync());

        Assert.Equal(1, fixture.Catalog.MaximumConcurrentReads);
    }

    [Fact]
    public async Task UpdateSettingsAsync_WaitsForRefreshAndKeepsUpdate()
    {
        await using var fixture = await RuntimeFixture.CreateAsync([]);
        fixture.Catalog.BlockReads = true;
        var refresh = fixture.Facade.RefreshProcessesAsync();
        await fixture.Catalog.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var updated = fixture.Store.Current with
        {
            Theme = FreiAtlas.Core.Settings.AtlasTheme.Light
        };

        var update = fixture.Facade.UpdateSettingsAsync(updated);
        try
        {
            await Task.Delay(50);
            Assert.False(update.IsCompleted);
            Assert.Equal(
                FreiAtlas.Core.Settings.AtlasTheme.Dark,
                fixture.Store.Current.Theme);
        }
        finally
        {
            fixture.Catalog.ContinueReads.TrySetResult(null);
        }

        await Task.WhenAll(refresh, update);

        Assert.Equal(
            FreiAtlas.Core.Settings.AtlasTheme.Light,
            fixture.Store.Current.Theme);
        Assert.Equal(
            FreiAtlas.Core.Settings.AtlasTheme.Light,
            fixture.Facade.Current.Settings.Theme);
    }

    [Fact]
    public async Task UpdateSettingsAsync_TransformationReadsLatestQueuedSettings()
    {
        await using var fixture = await RuntimeFixture.CreateAsync([]);
        fixture.Catalog.BlockReads = true;
        var refresh = fixture.Facade.RefreshProcessesAsync();
        await fixture.Catalog.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var first = fixture.Facade.UpdateSettingsAsync(
            fixture.Store.Current with
            {
                Theme = FreiAtlas.Core.Settings.AtlasTheme.Light
            });
        var transformed = fixture.Facade.UpdateSettingsAsync(current =>
            current with
            {
                Labels = current.Labels with { FontSize = 14f }
            });

        fixture.Catalog.ContinueReads.TrySetResult(null);
        await Task.WhenAll(refresh, first, transformed);

        Assert.Equal(
            FreiAtlas.Core.Settings.AtlasTheme.Light,
            fixture.Store.Current.Theme);
        Assert.Equal(14f, fixture.Store.Current.Labels.FontSize);
    }

    [Fact]
    public async Task UpdateSettingsAsync_WhileOverlayRuns_AppliesWithoutStopping()
    {
        await using var fixture = await RuntimeFixture.CreateAsync(
            [Process(123, "逐风者")]);
        await fixture.Facade.StartAsync();
        Assert.True(await fixture.Facade.StartOverlayAsync());

        await fixture.Facade.UpdateSettingsAsync(current => current with
        {
            Edges = current.Edges with { Width = 3f },
            Labels = current.Labels with { FontSize = 18f }
        });

        Assert.True(fixture.Facade.Current.IsOverlayRunning);
        Assert.Equal(0, fixture.Session.StopCount);
        Assert.Equal(0, fixture.Session.RequestStopCount);
        Assert.Equal(3f, fixture.Store.Current.Edges.Width);
        Assert.Equal(18f, fixture.Store.Current.Labels.FontSize);
    }

    [Fact]
    public async Task MultipleProcesses_WaitForManualSelectionBeforeStart()
    {
        await using var fixture = await RuntimeFixture.CreateAsync(
            [Process(123, "逐风者"), Process(456, "锻铁者")]);
        await fixture.Facade.StartAsync();

        Assert.Null(fixture.Facade.Current.SelectedProcessId);
        Assert.False(await fixture.Facade.StartOverlayAsync());
        Assert.False(fixture.Session.IsRunning);
        Assert.True(await fixture.Facade.SelectProcessAsync(456));
        Assert.True(await fixture.Facade.StartOverlayAsync());
        Assert.Equal(456, fixture.Session.ProcessId);
    }

    [Fact]
    public async Task SelectedProcessExit_StopsSessionAndDoesNotRebind()
    {
        await using var fixture = await RuntimeFixture.CreateAsync(
            [Process(123, "逐风者")]);
        await fixture.Facade.StartAsync();
        Assert.True(await fixture.Facade.StartOverlayAsync());
        fixture.Catalog.Processes = [Process(456, "锻铁者")];

        await fixture.Facade.RefreshProcessesAsync();

        Assert.Equal(123, fixture.Facade.Current.SelectedProcessId);
        Assert.False(fixture.Facade.Current.IsOverlayRunning);
        Assert.False(fixture.Session.IsRunning);
        Assert.Equal(1, fixture.Session.StopCount);
        Assert.Equal(1, fixture.Session.RequestStopCount);
        Assert.Equal(
            "进程已退出",
            fixture.Facade.Current.OverlayStatusMessage);
        Assert.Contains(
            fixture.Facade.Current.Processes,
            process => process.ProcessId == 123
                       && process.State == GameProcessState.Exited);
    }

    [Fact]
    public async Task SettingsAndAtlasUpdatesPublishSanitizedSnapshotImmediately()
    {
        await using var fixture = await RuntimeFixture.CreateAsync(
            [Process(123, "逐风者")]);
        await fixture.Facade.StartAsync();
        var published = new TaskCompletionSource<AtlasSettingsAppSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Facade.SnapshotChanged += snapshot =>
        {
            if (snapshot.Settings.Theme == FreiAtlas.Core.Settings.AtlasTheme.Light
                && snapshot.NodeCounts.Total == 3)
            {
                published.TrySetResult(snapshot);
            }
        };
        await fixture.Facade.UpdateSettingsAsync(fixture.Store.Current with
        {
            Theme = FreiAtlas.Core.Settings.AtlasTheme.Light
        });
        Assert.True(await fixture.Facade.StartOverlayAsync());
        fixture.Session.Publish(SnapshotWithCategories());

        var snapshot = await published.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(3, snapshot.NodeCounts.Total);
        Assert.Equal(1, snapshot.NodeCounts.Completed);
        Assert.Equal(1, snapshot.NodeCounts.Unlocked);
        Assert.Equal(1, snapshot.NodeCounts.Locked);
        Assert.DoesNotContain(
            typeof(AtlasSnapshot),
            typeof(AtlasSettingsAppSnapshot).GetProperties()
                .Select(property => property.PropertyType));
        Assert.DoesNotContain(
            typeof(AtlasSettingsAppSnapshot).GetProperties(),
            property => ContainsForbiddenName(property.Name));
    }

    [Fact]
    public async Task RepeatedAtlasSnapshots_WithUnchangedCounts_PublishOnlyOnce()
    {
        await using var fixture = await RuntimeFixture.CreateAsync(
            [Process(123, "逐风者")]);
        await fixture.Facade.StartAsync();
        Assert.True(await fixture.Facade.StartOverlayAsync());
        var published = 0;
        fixture.Facade.SnapshotChanged += snapshot =>
        {
            if (snapshot.NodeCounts.Total == 3)
            {
                published++;
            }
        };
        var atlasSnapshot = SnapshotWithCategories();

        for (var index = 0; index < 100; index++)
        {
            fixture.Session.Publish(atlasSnapshot);
        }

        Assert.Equal(1, published);
    }

    [Fact]
    public async Task StartOverlayAsync_WhenSessionEndsImmediately_DoesNotReportRunning()
    {
        await using var fixture = await RuntimeFixture.CreateAsync(
            [Process(123, "逐风者")]);
        await fixture.Facade.StartAsync();
        fixture.Session.EndImmediatelyOnStart = true;

        var started = await fixture.Facade.StartOverlayAsync();

        Assert.False(started);
        Assert.False(fixture.Facade.Current.IsOverlayRunning);
        Assert.Equal("覆盖层启动失败", fixture.Facade.Current.OverlayStatusMessage);
    }

    private static bool ContainsForbiddenName(string value)
        => new[] { "Address", "Offset", "Pointer", "Handle", "Memory" }
            .Any(forbidden => value.Contains(
                forbidden,
                StringComparison.OrdinalIgnoreCase));

    private static GameProcessSnapshot Process(int processId, string name)
        => new(processId, name, GameProcessState.InGame, null);

    private static AtlasSnapshot SnapshotWithCategories()
    {
        var nodes = new[]
        {
            Node(new AtlasGridPos(0, 0), accessible: true, completed: true),
            Node(new AtlasGridPos(1, 0), accessible: true, completed: false),
            Node(new AtlasGridPos(2, 0), accessible: false, completed: false)
        };
        return new AtlasSnapshot(
            DateTimeOffset.UtcNow,
            AtlasSnapshotStatus.Stable,
            nodes.Length,
            0,
            nodes,
            [],
            null,
            AtlasProjection.Identity,
            "runtime");
    }

    private static AtlasSnapshot SnapshotWithNames(
        DateTimeOffset capturedAt,
        string signature,
        params (AtlasGridPos Grid, string Name)[] nodes)
        => new(
            capturedAt,
            AtlasSnapshotStatus.Stable,
            nodes.Length,
            0,
            nodes.Select(node => new AtlasNodeSnapshot(
                node.Grid,
                null,
                node.Name,
                0,
                0,
                true,
                true,
                false,
                false,
                [],
                [])).ToArray(),
            [],
            null,
            AtlasProjection.Identity,
            signature,
            true);

    private static AtlasNodeSnapshot Node(
        AtlasGridPos grid,
        bool accessible,
        bool completed)
        => new(
            grid,
            null,
            grid.ToString(),
            0,
            0,
            true,
            accessible,
            completed,
            false,
            [],
            []);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory;

        private RuntimeFixture(
            TemporaryDirectory directory,
            FakeProcessCatalog catalog,
            FakeOverlaySession session,
            AtlasSettingsStore store,
            AtlasRuntimeFacade facade)
        {
            _directory = directory;
            Catalog = catalog;
            Session = session;
            Store = store;
            Facade = facade;
        }

        public FakeProcessCatalog Catalog { get; }

        public FakeOverlaySession Session { get; }

        public AtlasSettingsStore Store { get; }

        public AtlasRuntimeFacade Facade { get; }

        public static async Task<RuntimeFixture> CreateAsync(
            IReadOnlyList<GameProcessSnapshot> processes,
            TimeSpan? refreshInterval = null)
        {
            var directory = new TemporaryDirectory();
            var store = await AtlasSettingsStore.CreateAsync(
                directory.File("settings.json"),
                AtlasContentCatalog.Embedded.ContentIds,
                TimeSpan.FromMilliseconds(10));
            var catalog = new FakeProcessCatalog { Processes = processes };
            var session = new FakeOverlaySession();
            var facade = new AtlasRuntimeFacade(
                catalog,
                session,
                store,
                refreshInterval ?? TimeSpan.FromHours(1));
            return new RuntimeFixture(
                directory,
                catalog,
                session,
                store,
                facade);
        }

        public async ValueTask DisposeAsync()
        {
            await Facade.DisposeAsync();
            await Store.DisposeAsync();
            _directory.Dispose();
        }
    }

    private sealed class FakeProcessCatalog : IGameProcessCatalog
    {
        public IReadOnlyList<GameProcessSnapshot> Processes { get; set; } = [];

        public int ReadCount { get; private set; }

        public int MaximumConcurrentReads { get; private set; }

        public TimeSpan ReadDelay { get; set; }

        public bool BlockReads { get; set; }

        public TaskCompletionSource<object?> ReadEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<object?> ContinueReads { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private int _activeReads;

        public async Task<IReadOnlyList<GameProcessSnapshot>> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            var active = Interlocked.Increment(ref _activeReads);
            MaximumConcurrentReads = Math.Max(MaximumConcurrentReads, active);
            try
            {
                if (BlockReads)
                {
                    ReadEntered.TrySetResult(null);
                    await ContinueReads.Task.WaitAsync(cancellationToken);
                }

                if (ReadDelay > TimeSpan.Zero)
                {
                    await Task.Delay(ReadDelay, cancellationToken);
                }

                return Processes;
            }
            finally
            {
                Interlocked.Decrement(ref _activeReads);
            }
        }
    }

    private sealed class FakeOverlaySession : IOverlaySession
    {
        public bool IsRunning { get; private set; }

        public int? ProcessId { get; private set; }

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public int RequestStopCount { get; private set; }

        public bool DelayStopCompletion { get; set; }

        public TaskCompletionSource<object?> StopRequested { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource<object?> StopCompletion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool EndImmediatelyOnStart { get; set; }

        public event Action<AtlasSnapshot>? SnapshotReceived;

        public event Action<Exception?>? Ended;

        public void Start(
            int processId,
            FreiAtlas.Core.Settings.IAtlasSettingsSource settingsSource)
        {
            Assert.False(IsRunning);
            IsRunning = true;
            ProcessId = processId;
            StartCount++;
            if (EndImmediatelyOnStart)
            {
                IsRunning = false;
                Ended?.Invoke(null);
            }
        }

        public void RequestStop()
        {
            RequestStopCount++;
            StopRequested.TrySetResult(null);
        }

        public async Task StopAsync()
        {
            if (IsRunning)
            {
                StopCount++;
            }

            if (DelayStopCompletion)
            {
                await StopCompletion.Task;
            }

            IsRunning = false;
        }

        public void CompleteStop() => StopCompletion.TrySetResult(null);

        public void Publish(AtlasSnapshot snapshot)
            => SnapshotReceived?.Invoke(snapshot);

        public void End(Exception? failure)
        {
            IsRunning = false;
            Ended?.Invoke(failure);
        }

        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"FreiAtlas.Runtime.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
