using FreiAtlas.Core.Contracts;
using FreiAtlas.Core.Memory;
using FreiAtlas.Core.Player;

namespace FreiAtlas.Game.Memory;

public sealed class PlayerVitalsMemoryService : IPlayerVitalsSource, IAsyncDisposable
{
    private readonly Func<int, IProcessMemory> _openMemory;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private PlayerVitalsSnapshot _current;
    private Task? _loop;
    private int _target;
    private long _generation;
    private bool _disposed;

    public PlayerVitalsMemoryService(Func<int, IProcessMemory> openMemory, TimeProvider? clock = null)
    {
        _openMemory = openMemory ?? throw new ArgumentNullException(nameof(openMemory));
        _clock = clock ?? TimeProvider.System;
        _current = PlayerVitalsSnapshot.Unavailable(0, _clock.GetUtcNow(), "请先选择游戏进程");
    }

    public PlayerVitalsSnapshot Current => Volatile.Read(ref _current);

    public void SelectProcess(int? processId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var target = processId.GetValueOrDefault();
            if (target == _target) return;
            _target = target;
            _generation++;
            Volatile.Write(ref _current, PlayerVitalsSnapshot.Unavailable(target, _clock.GetUtcNow(), "等待角色数据"));
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _loop ??= Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        IProcessMemory? memory = null;
        PlayerVitalsReader? reader = null;
        long activeGeneration = -1;
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                int target;
                long generation;
                lock (_gate) { target = _target; generation = _generation; }
                var delay = 50;
                try
                {
                    if (generation != activeGeneration)
                    {
                        memory?.Dispose();
                        memory = null;
                        reader = null;
                        activeGeneration = generation;
                    }
                    if (target > 0)
                    {
                        memory ??= _openMemory(target);
                        reader ??= new PlayerVitalsReader(memory, clock: _clock);
                        var snapshot = reader.Read();
                        Publish(generation, snapshot);
                        if (!snapshot.IsValid) delay = 500;
                    }
                    else delay = 250;
                }
                catch (Exception exception)
                {
                    Publish(generation, PlayerVitalsSnapshot.Unavailable(target, _clock.GetUtcNow(), "内存读取失败：" + exception.Message));
                    memory?.Dispose();
                    memory = null;
                    reader = null;
                    delay = 1000;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(delay), _clock, _cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        finally { memory?.Dispose(); }
    }

    private void Publish(long generation, PlayerVitalsSnapshot snapshot)
    {
        lock (_gate)
        {
            if (!_disposed && _generation == generation) Volatile.Write(ref _current, snapshot);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _cancellation.Cancel();
            loop = _loop;
            Volatile.Write(ref _current, PlayerVitalsSnapshot.Unavailable(0, _clock.GetUtcNow(), "内存服务已停止"));
        }
        if (loop is not null) await loop.ConfigureAwait(false);
        _cancellation.Dispose();
    }
}
