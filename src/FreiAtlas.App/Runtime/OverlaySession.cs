using FreiAtlas.Core.Atlas;
using FreiAtlas.Core.Settings;
using FreiAtlas.Host;

namespace FreiAtlas.App.Runtime;

public interface IOverlaySession : IAsyncDisposable
{
    bool IsRunning { get; }

    event Action<AtlasSnapshot>? SnapshotReceived;

    event Action<Exception?>? Ended;

    void Start(int processId, IAtlasSettingsSource settingsSource);

    void RequestStop();

    Task StopAsync();
}

public sealed class OverlaySession : IOverlaySession
{
    private readonly object _gate = new();
    private readonly AtlasOverlayRunner _runner;
    private CancellationTokenSource? _cancellation;
    private OverlayStopController? _stopController;
    private Task? _task;
    private bool _disposed;

    public OverlaySession()
        : this(new AtlasOverlayRunner())
    {
    }

    internal OverlaySession(AtlasOverlayRunner runner)
    {
        _runner = runner;
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _task is { IsCompleted: false };
            }
        }
    }

    public event Action<AtlasSnapshot>? SnapshotReceived;

    public event Action<Exception?>? Ended;

    public void Start(int processId, IAtlasSettingsSource settingsSource)
    {
        ArgumentNullException.ThrowIfNull(settingsSource);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_task is { IsCompleted: false })
            {
                throw new InvalidOperationException(
                    "The overlay session is already running.");
            }

            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            _stopController = new OverlayStopController();
            var cancellationToken = _cancellation.Token;
            var stopController = _stopController;
            _task = Task.Factory.StartNew(
                () => Run(
                    processId,
                    cancellationToken,
                    settingsSource,
                    stopController),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
    }

    public void RequestStop()
    {
        CancellationTokenSource? cancellation;
        OverlayStopController? stopController;
        lock (_gate)
        {
            cancellation = _cancellation;
            stopController = _stopController;
        }

        RequestStopCore(cancellation, stopController);
    }

    internal static void RequestStopCore(
        CancellationTokenSource? cancellation,
        OverlayStopController? stopController)
    {
        try
        {
            stopController?.RequestStop();
        }
        finally
        {
            cancellation?.Cancel();
        }
    }

    public async Task StopAsync()
    {
        RequestStop();
        Task? task;
        lock (_gate)
        {
            task = _task;
        }

        if (task is not null)
        {
            await task.ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        await StopAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _cancellation?.Dispose();
            _cancellation = null;
            _stopController = null;
            _task = null;
        }
    }

    private void Run(
        int processId,
        CancellationToken cancellationToken,
        IAtlasSettingsSource settingsSource,
        OverlayStopController stopController)
    {
        Exception? failure = null;
        try
        {
            var exitCode = _runner.Run(
                processId,
                cancellationToken,
                settingsSource,
                snapshot => SnapshotReceived?.Invoke(snapshot),
                stopController);
            if (exitCode != 0 && !cancellationToken.IsCancellationRequested)
            {
                failure = new InvalidOperationException(
                    "The overlay runner exited unsuccessfully.");
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            Ended?.Invoke(failure);
        }
    }
}
