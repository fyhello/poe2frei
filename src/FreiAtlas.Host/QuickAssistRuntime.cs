using FreiAtlas.Core.Recovery;
using FreiAtlas.Core.PortalSqueeze;
using FreiAtlas.Game.Memory;
using FreiAtlas.Platform.Windows.Input;
using FreiAtlas.Platform.Windows.Process;
using FreiAtlas.QuickAssist;

namespace FreiAtlas.Host;

public sealed class QuickAssistRuntime : IAsyncDisposable
{
    private readonly PlayerVitalsMemoryService _memory = new(processId =>
        NativeProcessMemory.TryOpen(processId, out var memory)
            ? memory! : throw new InvalidOperationException("无法只读打开选中的游戏进程"));
    private readonly QuickAssistService _assistant;
    private readonly PortalInteractionService _portalInteraction;
    private readonly PortalSqueezeService _portalSqueeze;
    private readonly CancellationTokenSource _cancellation = new();
    private int _selectedProcessId;
    private Task? _task;
    private bool _disposed;

    public QuickAssistRuntime()
    {
        _assistant = new(_memory, new RecoveryInput());
        _portalInteraction = new();
        _portalSqueeze = new(
            processId =>
            {
                if (!NativeProcessMemory.TryOpen(processId, out var memory)
                    || memory is null)
                {
                    return null;
                }

                return new PortalCandidateMemorySource(memory);
            },
            _portalInteraction);
    }

    public QuickAssistSnapshot Current => _assistant.Current;

    public PortalSqueezeSnapshot PortalSqueeze => _portalSqueeze.Current;

    public int SelectedProcessId => Volatile.Read(ref _selectedProcessId);

    public void Configure(QuickAssistSettings settings)
    {
        _assistant.Configure(settings);
        _portalSqueeze.Configure(settings.PortalSqueeze);
    }

    public void SelectProcess(int? processId)
    {
        var selected = processId.GetValueOrDefault();
        Volatile.Write(ref _selectedProcessId, selected);
        _memory.SelectProcess(processId);
    }

    public Task TriggerPortalSqueezeAsync(
        int processId,
        CancellationToken cancellationToken = default)
        => _portalSqueeze.TriggerAsync(processId, cancellationToken);

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _memory.Start();
        _task ??= Task.Run(() => _assistant.RunAsync(_cancellation.Token));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cancellation.Cancel();
        try { if (_task is not null) await _task.ConfigureAwait(false); }
        finally
        {
            try { await _portalSqueeze.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                _portalInteraction.Dispose();
                try { await _memory.DisposeAsync().ConfigureAwait(false); }
                finally { _cancellation.Dispose(); }
            }
        }
    }
}
