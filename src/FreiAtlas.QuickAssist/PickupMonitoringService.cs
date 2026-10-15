using System.Numerics;
using FreiAtlas.Core.Pickup;

namespace FreiAtlas.QuickAssist;

public sealed record PickupMonitorSnapshot(
    string Message,
    bool EligibleTarget,
    uint? EntityId,
    string? ItemName,
    float? DistanceToPlayer,
    DateTimeOffset UpdatedAt)
{
    public static PickupMonitorSnapshot Disabled { get; } = new(
        "未启用", false, null, null, null, DateTimeOffset.UtcNow);
}

/// <summary>
/// Monitors for whitelisted ground items within pickup range, performs non-blocking click on
/// exact screen label center, and ensures closed-loop disappearance verification.
/// </summary>
public sealed class PickupMonitoringService : IAsyncDisposable
{
    private readonly Func<int, IPickupCandidateSource?> _sourceFactory;
    private readonly Func<int, bool> _isGameForeground;
    private readonly Func<int, Vector2, bool>? _tryClickClientPoint;
    private readonly Func<string?, bool>? _isModifierKeyPressed;
    private readonly Func<int, Vector2?>? _getClientSize;
    private readonly Func<int, bool>? _clickCurrentCursor;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly PickupStateMachine _stateMachine;
    private PickupSettings _settings = new();
    private PickupMonitorSnapshot _current = PickupMonitorSnapshot.Disabled;
    private bool _disposed;

    public PickupMonitoringService(
        Func<int, IPickupCandidateSource?> sourceFactory,
        Func<int, bool> isGameForeground,
        Func<int, Vector2, bool>? tryClickClientPoint = null,
        Func<string?, bool>? isModifierKeyPressed = null,
        Func<int, Vector2?>? getClientSize = null,
        Func<int, bool>? clickCurrentCursor = null,
        TimeProvider? clock = null,
        TimeSpan? cooldownDuration = null)
    {
        _sourceFactory = sourceFactory ?? throw new ArgumentNullException(nameof(sourceFactory));
        _isGameForeground = isGameForeground ?? throw new ArgumentNullException(nameof(isGameForeground));
        _tryClickClientPoint = tryClickClientPoint;
        _isModifierKeyPressed = isModifierKeyPressed;
        _getClientSize = getClientSize;
        _clickCurrentCursor = clickCurrentCursor;
        _clock = clock ?? TimeProvider.System;
        _stateMachine = new PickupStateMachine(cooldownDuration);
    }

    public PickupMonitorSnapshot Current => Volatile.Read(ref _current);

    public void Configure(PickupSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _settings = settings;
            if (!settings.Enabled)
            {
                _stateMachine.Reset();
                Publish("未启用", null, null, null);
            }
        }
    }

    public PickupMonitorSnapshot ScanOnce(int processId)
    {
        if (processId <= 0) return Publish("未选择游戏进程", null, null, null);
        PickupSettings settings;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            settings = _settings;
        }

        if (!settings.Enabled)
        {
            _stateMachine.Reset();
            return Publish("未启用", null, null, null);
        }
        if (settings.Whitelist.Count == 0) return Publish("请先设置拾取白名单", null, null, null);
        if (!_isGameForeground(processId)) return Publish("等待游戏位于前台", null, null, null);

        var modifierPressed = !settings.ModifierKeyEnabled || (_isModifierKeyPressed?.Invoke(settings.ModifierKey) ?? true);
        if (settings.ModifierKeyEnabled && !modifierPressed && _stateMachine.State == PickupState.Idle)
        {
            return Publish($"门禁未满足：请按住 [{settings.ModifierKey}] 触发自动拾取", null, null, null);
        }

        var clientSize = _getClientSize?.Invoke(processId);
        var effectiveClientSize = clientSize is { X: > 0, Y: > 0 }
            ? clientSize.Value
            : new Vector2(2560, 1440);

        try
        {
            using var source = _sourceFactory(processId);
            if (source is null) return Publish("无法只读打开游戏进程", null, null, null);
            var scan = source.Scan();
            if (!scan.IsValid || scan.PlayerGridPosition is null)
                return Publish(scan.Error ?? "读取游戏区域或角色位置失败", null, null, null);

            // 轨道一：鼠标悬停在白名单物品上时直接点击拾取（保底机制）
            if (scan.HoveredGroundItemEntityId is { } hoveredId && hoveredId != 0)
            {
                var hoveredItem = scan.Candidates.FirstOrDefault(c => c is not null && c.EntityId == hoveredId);
                if (hoveredItem is not null
                    && settings.IsWhitelisted(hoveredItem.ItemName, hoveredItem.ArtName)
                    && hoveredItem.DistanceToPlayer <= settings.PickupRangeGrid + 5f)
                {
                    if (_clickCurrentCursor is not null && _clickCurrentCursor(processId))
                    {
                        return Publish($"悬停拾取目标：{hoveredItem.ItemName}（距离 {hoveredItem.DistanceToPlayer:F1} 格）",
                            hoveredItem.EntityId, hoveredItem.ItemName, hoveredItem.DistanceToPlayer);
                    }
                }
            }

            var now = _clock.GetUtcNow().UtcDateTime;
            var blacklisted = _stateMachine.ActiveCooldownEntityIds(now);
            var availableTargets = PickupTargetCorrelator.Correlate(
                settings,
                scan.PlayerGridPosition,
                scan.CameraWorldToScreenMatrix,
                effectiveClientSize,
                scan.Candidates,
                scan.Labels ?? [],
                blacklisted);

            var action = _stateMachine.Update(settings, availableTargets, now, modifierPressed);

            if (action is not null)
            {
                if (!_isGameForeground(processId)) return Publish("游戏失焦，取消拾取", null, null, null);

                var target = action.Target;
                var clicked = _tryClickClientPoint?.Invoke(processId, target.LabelCenter) ?? false;
                if (clicked)
                {
                    var msg = action.IsRetry
                        ? $"白名单目标重试拾取（第 {action.AttemptIndex}/{settings.MaxAttempts} 次）：{target.ItemName}"
                        : $"已顺手拾取目标：{target.ItemName}（距离 {target.DistanceToPlayer:F1} 格）";
                    return Publish(msg, target.EntityId, target.ItemName, target.DistanceToPlayer);
                }
                else
                {
                    return Publish($"尝试拾取 {target.ItemName} 失败：点击未发送", target.EntityId, target.ItemName, target.DistanceToPlayer);
                }
            }

            if (_stateMachine.State == PickupState.AwaitingDisappearance)
            {
                var cur = _stateMachine.CurrentTarget;
                return Publish($"等待物品拾取消失：{cur?.ItemName}（尝试第 {_stateMachine.CurrentAttempt}/{settings.MaxAttempts} 次）",
                    cur?.EntityId, cur?.ItemName, cur?.DistanceToPlayer);
            }

            if (settings.ModifierKeyEnabled && !modifierPressed)
            {
                return Publish($"门禁未满足：请按住 [{settings.ModifierKey}] 触发自动拾取", null, null, null);
            }

            if (blacklisted.Count > 0 && scan.Candidates.Any(c => c.EntityId != 0 && blacklisted.Contains(c.EntityId) && c.DistanceToPlayer <= settings.PickupRangeGrid))
            {
                return Publish("监测中：当前白名单物品已达重试上限，处于临时冷却中", null, null, null);
            }

            return Publish("监测中：拾取范围内没有白名单物品", null, null, null);
        }
        catch (Exception exception)
        {
            return Publish("拾取监测失败：" + exception.Message, null, null, null);
        }
    }

    public async Task RunAsync(Func<int> processIdProvider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processIdProvider);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                bool enabled;
                lock (_gate)
                {
                    if (_disposed) return;
                    enabled = _settings.Enabled;
                }

                var delayMs = 250;
                if (enabled)
                {
                    var snapshot = ScanOnce(processIdProvider());
                    // 自适应动态频率：若在拾取范围内侦测到有效白名单目标、或正处于等待消失/重试确认中，
                    // 将轮询频率提升至 50ms（每秒 20 次快速采样），实现极速跟进与消失感知；
                    // 平时监控中则保持 100ms 敏锐响应，CPU 占用近乎于零。
                    if (snapshot.EligibleTarget || _stateMachine.State == PickupState.AwaitingDisappearance)
                    {
                        delayMs = 50;
                    }
                    else
                    {
                        delayMs = 100;
                    }
                }
                else
                {
                    Publish("未启用", null, null, null);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), _clock, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            _stateMachine.Reset();
        }
        return ValueTask.CompletedTask;
    }

    private PickupMonitorSnapshot Publish(string message, uint? entityId, string? itemName, float? distanceToPlayer)
    {
        var snapshot = new PickupMonitorSnapshot(
            message,
            entityId is not null,
            entityId,
            itemName,
            distanceToPlayer,
            _clock.GetUtcNow());
        Volatile.Write(ref _current, snapshot);
        return snapshot;
    }
}
