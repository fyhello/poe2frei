using FreiAtlas.Core.Contracts;
using FreiAtlas.Core.PortalSqueeze;

namespace FreiAtlas.QuickAssist;

public sealed class PortalSqueezeService : IAsyncDisposable
{
    private readonly Func<int, IPortalCandidateSource?> _sourceFactory;
    private readonly IPortalInteraction _interaction;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly object _gate = new();
    private PortalSqueezeSettings _settings = new();
    private PortalSqueezeSnapshot _current = PortalSqueezeSnapshot.Idle;
    private Task? _running;
    private bool _disposed;

    public PortalSqueezeService(
        Func<int, IPortalCandidateSource?> sourceFactory,
        IPortalInteraction interaction,
        TimeProvider? clock = null)
    {
        _sourceFactory = sourceFactory ?? throw new ArgumentNullException(nameof(sourceFactory));
        _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        _clock = clock ?? TimeProvider.System;
    }

    public PortalSqueezeSnapshot Current => Volatile.Read(ref _current);

    public void Configure(PortalSqueezeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _settings = settings;
        }
    }

    public Task TriggerAsync(int processId, CancellationToken cancellationToken = default)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running is { IsCompleted: false })
            {
                Publish(PortalSqueezeState.Paused, "已有挤门任务正在执行", null, 0);
                return _running;
            }

            var settings = _settings;
            if (!settings.Enabled)
            {
                Publish(PortalSqueezeState.Paused, "挤门功能未启用", null, 0);
                return Task.CompletedTask;
            }

            var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _disposeCancellation.Token);
            _running = Task.Run(async () =>
            {
                try
                {
                    await RunAsync(processId, settings, linked.Token).ConfigureAwait(false);
                }
                finally
                {
                    linked.Dispose();
                }
            });
            return _running;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? running;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _disposeCancellation.Cancel();
            running = _running;
        }

        if (running is not null)
        {
            try
            {
                await running.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _disposeCancellation.Dispose();
    }

    private async Task RunAsync(
        int processId,
        PortalSqueezeSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            using var source = _sourceFactory(processId);
            if (source is null)
            {
                Publish(PortalSqueezeState.Failed, "无法打开游戏只读内存", null, 0);
                return;
            }

            PortalCandidate? target = null;
            for (var attempt = 1; attempt <= settings.MaxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Publish(PortalSqueezeState.Scanning, "正在扫描附近传送门", null, attempt - 1);
                var scan = source.Scan(settings.MaxDistanceGrid);
                if (!scan.IsValid)
                {
                    Publish(PortalSqueezeState.Failed, scan.Error ?? "传送门扫描失败", null, attempt);
                    return;
                }

                target = scan.Candidates
                    .OrderBy(candidate => candidate.DistanceToPlayer)
                    .ThenBy(candidate => candidate.EntityId)
                    .FirstOrDefault();
                if (target is null)
                {
                    Publish(PortalSqueezeState.Failed, "附近没有可挤的传送门", null, attempt);
                    return;
                }

                Publish(PortalSqueezeState.Interacting,
                    $"正在挤门：{target.MetadataPath}，距离 {target.DistanceToPlayer:0.##}",
                    target,
                    attempt);
                var interaction = _interaction.Interact(
                    scan,
                    target,
                    TimeSpan.FromMilliseconds(Math.Min(settings.ConfirmTimeoutMilliseconds, 3_000)));
                if (!interaction.Started)
                {
                    if (attempt < settings.MaxAttempts)
                    {
                        await Task.Delay(350, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    Publish(PortalSqueezeState.Failed, interaction.Message, target, attempt);
                    return;
                }

                Publish(PortalSqueezeState.WaitingAreaChange,
                    interaction.Message,
                    target,
                    attempt);
                var deadline = _clock.GetUtcNow().AddMilliseconds(settings.ConfirmTimeoutMilliseconds);
                while (_clock.GetUtcNow() < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                    var after = source.Scan(settings.MaxDistanceGrid);
                    if (after.IsValid
                        && (after.AreaInstance != scan.AreaInstance
                            || after.AreaHash != scan.AreaHash))
                    {
                        Publish(PortalSqueezeState.Completed, "区域已变化，挤门完成", target, attempt);
                        return;
                    }
                }

                Publish(
                    PortalSqueezeState.Failed,
                    "交互线程已启动，但在超时时间内未确认区域变化；未重复启动远程线程",
                    target,
                    attempt);
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Publish(PortalSqueezeState.Paused, "挤门任务已取消", null, 0);
        }
        catch (Exception exception)
        {
            Publish(PortalSqueezeState.Failed, "挤门模块异常：" + exception.Message, null, 0);
        }
    }

    private void Publish(
        PortalSqueezeState state,
        string message,
        PortalCandidate? target,
        int attempts)
        => Volatile.Write(
            ref _current,
            new PortalSqueezeSnapshot(
                state,
                message,
                target,
                attempts,
                _clock.GetUtcNow()));
}
