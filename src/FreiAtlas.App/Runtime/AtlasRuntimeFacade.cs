using FreiAtlas.App.Processes;
using FreiAtlas.App.Settings;
using FreiAtlas.Core.Atlas;
using FreiAtlas.Core.Settings;

namespace FreiAtlas.App.Runtime;

public sealed record AtlasNodeCounts(
    int Total,
    int Completed,
    int Unlocked,
    int Locked)
{
    public static AtlasNodeCounts Empty { get; } = new(0, 0, 0, 0);
}

public enum AtlasNavigationCatalogStatus
{
    Waiting,
    Live,
    Cached
}

public sealed record AtlasNavigationCatalogAppSnapshot(
    AtlasNavigationCatalogStatus Status,
    DateTimeOffset? LastReadAt,
    IReadOnlyList<AtlasNavigationCatalogEntry> Entries,
    string Signature)
{
    public static AtlasNavigationCatalogAppSnapshot Waiting { get; } = new(
        AtlasNavigationCatalogStatus.Waiting,
        null,
        Array.Empty<AtlasNavigationCatalogEntry>(),
        string.Empty);
}

public sealed record AtlasSettingsAppSnapshot(
    IReadOnlyList<GameProcessSnapshot> Processes,
    int? SelectedProcessId,
    bool IsOverlayRunning,
    bool IsOverlayStopping,
    AtlasHotkey OverlayToggleHotkey,
    string? HotkeyRegistrationError,
    AtlasNodeCounts NodeCounts,
    AtlasNavigationCatalogAppSnapshot NavigationCatalog,
    AtlasDisplaySettings Settings,
    AtlasSettingsLoadStatus LoadStatus,
    AtlasSettingsPersistenceStatus PersistenceStatus,
    string? OverlayStatusMessage);

public sealed class AtlasRuntimeFacade : IAsyncDisposable
{
    private readonly IGameProcessCatalog _processCatalog;
    private readonly IOverlaySession _overlaySession;
    private readonly AtlasSettingsStore _settingsStore;
    private readonly TimeSpan _refreshInterval;
    private readonly ProcessSelectionController _selection = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly object _stateGate = new();
    private CancellationTokenSource? _refreshCancellation;
    private Task? _refreshLoop;
    private Task? _overlayStopTask;
    private AtlasNodeCounts _nodeCounts = AtlasNodeCounts.Empty;
    private AtlasNavigationCatalogAppSnapshot _navigationCatalog =
        AtlasNavigationCatalogAppSnapshot.Waiting;
    private AtlasSettingsPersistenceStatus _persistenceStatus =
        AtlasSettingsPersistenceStatus.Saved;
    private string? _overlayStatusMessage;
    private string? _overlayStopStatusMessage;
    private string? _hotkeyRegistrationError;
    private AtlasSettingsAppSnapshot _current;
    private bool _isOverlayStopping;
    private bool _disposed;

    public AtlasRuntimeFacade(
        IGameProcessCatalog processCatalog,
        IOverlaySession overlaySession,
        AtlasSettingsStore settingsStore,
        TimeSpan? refreshInterval = null)
    {
        _processCatalog = processCatalog
            ?? throw new ArgumentNullException(nameof(processCatalog));
        _overlaySession = overlaySession
            ?? throw new ArgumentNullException(nameof(overlaySession));
        _settingsStore = settingsStore
            ?? throw new ArgumentNullException(nameof(settingsStore));
        _refreshInterval = refreshInterval ?? TimeSpan.FromSeconds(2);
        if (_refreshInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(refreshInterval));
        }

        _current = CreateSnapshot();
        _settingsStore.SettingsChanged += OnSettingsChanged;
        _settingsStore.HotkeyChanged += OnHotkeyChanged;
        _settingsStore.PersistenceChanged += OnPersistenceChanged;
        _overlaySession.SnapshotReceived += OnAtlasSnapshot;
        _overlaySession.Ended += OnOverlayEnded;
    }

    public AtlasSettingsAppSnapshot Current => Volatile.Read(ref _current);

    public event Action<AtlasSettingsAppSnapshot>? SnapshotChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            if (_refreshLoop is null)
            {
                _refreshCancellation = new CancellationTokenSource();
                _refreshLoop = RefreshLoopAsync(_refreshCancellation.Token);
            }
        }
        finally
        {
            _commandGate.Release();
        }

        await RefreshProcessesAsync(cancellationToken);
    }

    public async Task RefreshProcessesAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _commandGate.WaitAsync(cancellationToken);
        Task? stopTask = null;
        try
        {
            var processes = await _processCatalog.ReadAsync(cancellationToken);
            bool shouldStop;
            lock (_stateGate)
            {
                var wasRunning = _selection.IsOverlayRunning;
                _selection.ApplyScan(processes);
                shouldStop = wasRunning && !_selection.IsOverlayRunning;
                if (shouldStop)
                {
                    _nodeCounts = AtlasNodeCounts.Empty;
                    MarkNavigationCatalogCachedNoLock();
                    _overlayStatusMessage = "进程已退出";
                }
            }

            if (shouldStop)
            {
                stopTask = BeginOverlayStop("进程已退出");
            }
            else
            {
                Publish();
            }
        }
        finally
        {
            _commandGate.Release();
        }

        if (stopTask is not null)
        {
            await stopTask.ConfigureAwait(false);
        }
    }

    public async Task<bool> SelectProcessAsync(
        int processId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            bool selected;
            lock (_stateGate)
            {
                selected = _selection.TrySelect(processId);
                if (selected)
                {
                    _overlayStatusMessage = null;
                }
            }

            Publish();
            return selected;
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public async Task<bool> StartOverlayAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            int? processId;
            bool invalidProcess;
            lock (_stateGate)
            {
                processId = _selection.SelectedProcessId;
                invalidProcess = processId is null
                    || _selection.Processes.All(process =>
                        process.ProcessId != processId
                        || process.State == GameProcessState.Exited);
                if (_isOverlayStopping
                    || _overlayStopTask is { IsCompleted: false }
                    || _overlaySession.IsRunning)
                {
                    return false;
                }

                if (invalidProcess)
                {
                    _overlayStatusMessage = "请先选择游戏进程";
                }
            }

            if (invalidProcess)
            {
                Publish();
                return false;
            }

            try
            {
                lock (_stateGate)
                {
                    _selection.MarkOverlayStarted();
                    _overlayStatusMessage = null;
                }

                _overlaySession.Start(processId.GetValueOrDefault(), _settingsStore);
                if (!_overlaySession.IsRunning)
                {
                    lock (_stateGate)
                    {
                        _selection.MarkOverlayStopped();
                        _overlayStatusMessage = "覆盖层启动失败";
                    }

                    Publish();
                    return false;
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                or ArgumentException)
            {
                lock (_stateGate)
                {
                    _selection.MarkOverlayStopped();
                    _overlayStatusMessage = "覆盖层启动失败";
                }

                Publish();
                return false;
            }

            Publish();
            return true;
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public async Task StopOverlayAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var stopTask = BeginOverlayStop(statusMessage: null);
        await stopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task UpdateSettingsAsync(
        AtlasDisplaySettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return UpdateSettingsAsync(_ => settings, cancellationToken);
    }

    public async Task UpdateHotkeyAsync(
        AtlasHotkey hotkey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hotkey);
        ThrowIfDisposed();
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            _settingsStore.UpdateHotkey(hotkey);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public void SetHotkeyRegistrationError(string? message)
    {
        ThrowIfDisposed();
        lock (_stateGate)
        {
            _hotkeyRegistrationError = message;
        }

        Publish();
    }

    public async Task<bool> ToggleOverlayAsync(
        CancellationToken cancellationToken = default)
    {
        var snapshot = Current;
        if (snapshot.IsOverlayStopping)
        {
            return false;
        }

        if (snapshot.IsOverlayRunning)
        {
            await StopOverlayAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        return await StartOverlayAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateSettingsAsync(
        Func<AtlasDisplaySettings, AtlasDisplaySettings> transform,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transform);
        ThrowIfDisposed();
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            _settingsStore.Update(transform(_settingsStore.Current));
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshCancellation?.Cancel();
        if (_refreshLoop is not null)
        {
            try
            {
                await _refreshLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _settingsStore.SettingsChanged -= OnSettingsChanged;
        _settingsStore.HotkeyChanged -= OnHotkeyChanged;
        _settingsStore.PersistenceChanged -= OnPersistenceChanged;
        _overlaySession.SnapshotReceived -= OnAtlasSnapshot;
        _overlaySession.Ended -= OnOverlayEnded;
        await _overlaySession.DisposeAsync();
        _refreshCancellation?.Dispose();
        _commandGate.Dispose();
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_refreshInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                await RefreshProcessesAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private void OnSettingsChanged(AtlasDisplaySettings _) => Publish();

    private void OnHotkeyChanged(AtlasHotkey _) => Publish();

    private void OnPersistenceChanged(AtlasSettingsPersistenceStatus status)
    {
        lock (_stateGate)
        {
            _persistenceStatus = status;
        }

        Publish();
    }

    private void OnAtlasSnapshot(AtlasSnapshot snapshot)
    {
        var completed = snapshot.Nodes.Count(node => node.IsCompleted);
        var unlocked = snapshot.Nodes.Count(node =>
            !node.IsCompleted && node.IsAccessible);
        var nodeCounts = new AtlasNodeCounts(
            snapshot.NodeCount,
            completed,
            unlocked,
            snapshot.NodeCount - completed - unlocked);
        lock (_stateGate)
        {
            var navigationCatalog = NextNavigationCatalogNoLock(snapshot);
            if (_nodeCounts == nodeCounts
                && _navigationCatalog == navigationCatalog)
            {
                return;
            }

            _nodeCounts = nodeCounts;
            _navigationCatalog = navigationCatalog;
        }

        Publish();
    }

    private void OnOverlayEnded(Exception? failure)
    {
        lock (_stateGate)
        {
            _selection.MarkOverlayStopped();
            MarkNavigationCatalogCachedNoLock();
            if (failure is not null)
            {
                _overlayStatusMessage = "覆盖层运行失败";
            }
            else if (_overlayStopStatusMessage is not null)
            {
                _overlayStatusMessage = _overlayStopStatusMessage;
            }
        }

        Publish();
    }

    private void Publish()
    {
        var snapshot = CreateSnapshot();
        Volatile.Write(ref _current, snapshot);
        SnapshotChanged?.Invoke(snapshot);
    }

    private AtlasSettingsAppSnapshot CreateSnapshot()
    {
        lock (_stateGate)
        {
            return new AtlasSettingsAppSnapshot(
                _selection.Processes.ToArray(),
                _selection.SelectedProcessId,
                _selection.IsOverlayRunning,
                _isOverlayStopping,
                _settingsStore.CurrentHotkey,
                _hotkeyRegistrationError,
                _nodeCounts,
                _navigationCatalog,
                _settingsStore.Current,
                _settingsStore.LoadStatus,
                _persistenceStatus,
                _overlayStatusMessage);
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    private Task BeginOverlayStop(string? statusMessage)
    {
        TaskCompletionSource<object?>? completion = null;
        Task stopTask;
        lock (_stateGate)
        {
            MarkNavigationCatalogCachedNoLock();
            if (statusMessage is not null)
            {
                _overlayStopStatusMessage = statusMessage;
                _overlayStatusMessage = statusMessage;
            }

            if (_overlayStopTask is { IsCompleted: false } currentStop)
            {
                stopTask = currentStop;
            }
            else if (!_selection.IsOverlayRunning && !_overlaySession.IsRunning)
            {
                _isOverlayStopping = false;
                stopTask = Task.CompletedTask;
            }
            else
            {
                _isOverlayStopping = true;
                _overlayStopStatusMessage = statusMessage;
                completion = new TaskCompletionSource<object?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                stopTask = completion.Task;
                _overlayStopTask = stopTask;
            }
        }

        if (completion is not null)
        {
            try
            {
                _overlaySession.RequestStop();
            }
            catch (Exception exception)
            {
                lock (_stateGate)
                {
                    _isOverlayStopping = false;
                    _overlayStatusMessage = "覆盖层运行失败";
                }

                Publish();
                completion.TrySetException(exception);
                return stopTask;
            }
        }

        Publish();
        if (completion is not null)
        {
            _ = CompleteOverlayStopAsync(completion);
        }

        return stopTask;
    }

    private AtlasNavigationCatalogAppSnapshot NextNavigationCatalogNoLock(
        AtlasSnapshot snapshot)
    {
        if (snapshot.IsAtlasOpen
            && snapshot.Status == AtlasSnapshotStatus.Stable
            && snapshot.Nodes.Count > 0)
        {
            var catalog = AtlasNavigationCatalogBuilder.Build(snapshot.Nodes);
            if (_navigationCatalog.Status == AtlasNavigationCatalogStatus.Live
                && string.Equals(
                    _navigationCatalog.Signature,
                    catalog.Signature,
                    StringComparison.Ordinal))
            {
                return _navigationCatalog;
            }

            return new AtlasNavigationCatalogAppSnapshot(
                AtlasNavigationCatalogStatus.Live,
                snapshot.CapturedAt,
                catalog.Entries,
                catalog.Signature);
        }

        return _navigationCatalog.Status == AtlasNavigationCatalogStatus.Live
            ? _navigationCatalog with
            {
                Status = AtlasNavigationCatalogStatus.Cached
            }
            : _navigationCatalog;
    }

    private void MarkNavigationCatalogCachedNoLock()
    {
        if (_navigationCatalog.Status == AtlasNavigationCatalogStatus.Live)
        {
            _navigationCatalog = _navigationCatalog with
            {
                Status = AtlasNavigationCatalogStatus.Cached
            };
        }
    }

    private async Task CompleteOverlayStopAsync(
        TaskCompletionSource<object?> completion)
    {
        Exception? failure = null;
        try
        {
            await _overlaySession.StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        lock (_stateGate)
        {
            if (failure is null || !_overlaySession.IsRunning)
            {
                _selection.MarkOverlayStopped();
            }

            _isOverlayStopping = false;
            _overlayStatusMessage = failure is not null
                                    || _overlayStatusMessage == "覆盖层运行失败"
                ? "覆盖层运行失败"
                : _overlayStopStatusMessage;
            _overlayStopStatusMessage = null;
        }

        Publish();
        if (failure is null)
        {
            completion.TrySetResult(null);
        }
        else
        {
            completion.TrySetException(failure);
        }
    }
}
