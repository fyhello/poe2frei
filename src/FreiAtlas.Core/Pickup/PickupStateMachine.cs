namespace FreiAtlas.Core.Pickup;

public enum PickupState
{
    Idle,
    AwaitingDisappearance
}

public sealed record PickupAction(
    GroundItemPickupTarget Target,
    bool IsRetry,
    int AttemptIndex);

public sealed class PickupStateMachine
{
    private readonly Dictionary<uint, DateTime> _cooldowns = new();
    private readonly TimeSpan _cooldownDuration;

    public PickupStateMachine(TimeSpan? cooldownDuration = null)
    {
        _cooldownDuration = cooldownDuration ?? TimeSpan.FromSeconds(5);
    }

    public PickupState State { get; private set; } = PickupState.Idle;
    public GroundItemPickupTarget? CurrentTarget { get; private set; }
    public DateTime LastActionTime { get; private set; }
    public int CurrentAttempt { get; private set; }

    public IReadOnlySet<uint> ActiveCooldownEntityIds(DateTime now)
    {
        PruneExpiredCooldowns(now);
        return _cooldowns.Keys.ToHashSet();
    }

    public PickupAction? Update(
        PickupSettings settings,
        IReadOnlyList<GroundItemPickupTarget> availableTargets,
        DateTime now,
        bool modifierKeyPressed = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(availableTargets);

        PruneExpiredCooldowns(now);

        if (!settings.Enabled)
        {
            Reset();
            return null;
        }

        // 门禁：按键协同
        if (settings.ModifierKeyEnabled && !modifierKeyPressed)
        {
            if (State == PickupState.Idle) return null;
        }

        switch (State)
        {
            case PickupState.Idle:
                var nextTarget = availableTargets.FirstOrDefault(t => !_cooldowns.ContainsKey(t.EntityId));
                if (nextTarget is null) return null;

                CurrentTarget = nextTarget;
                CurrentAttempt = 1;
                LastActionTime = now;
                State = PickupState.AwaitingDisappearance;
                return new PickupAction(nextTarget, IsRetry: false, AttemptIndex: 1);

            case PickupState.AwaitingDisappearance:
                if (CurrentTarget is null)
                {
                    Reset();
                    return null;
                }

                // 核心判定：前一个目标在当前最新一帧是否依然存在
                var currentTargetId = CurrentTarget.EntityId;
                var currentLabelAddr = CurrentTarget.LabelAddress;
                var latestTarget = availableTargets.FirstOrDefault(t => t.EntityId == currentTargetId
                                                                        || (currentLabelAddr != 0 && t.LabelAddress == currentLabelAddr));
                if (latestTarget is null)
                {
                    // 已消失（或角色移动过快导致物品已离开拾取范围/视野）！闭环完成/及时止损，回到待机允许捡下一个
                    Reset();
                    return null;
                }

                // 关键优化：实时同步目标当前帧最新滚屏屏幕坐标，确保重试追击分毫不差
                CurrentTarget = latestTarget;

                var elapsedMs = (now - LastActionTime).TotalMilliseconds;
                if (elapsedMs < settings.DisappearTimeoutMilliseconds)
                {
                    // 尚未超时，耐心等待
                    return null;
                }

                // 超时未消失，触发重试或放弃
                if (CurrentAttempt < settings.MaxAttempts)
                {
                    CurrentAttempt++;
                    LastActionTime = now;
                    return new PickupAction(latestTarget, IsRetry: true, AttemptIndex: CurrentAttempt);
                }
                else
                {
                    // 达到最大重试次数仍未消失，标记临时冷却放弃并换下一个
                    _cooldowns[currentTargetId] = now.Add(_cooldownDuration);
                    Reset();
                    return null;
                }

            default:
                Reset();
                return null;
        }
    }

    public void Reset()
    {
        State = PickupState.Idle;
        CurrentTarget = null;
        CurrentAttempt = 0;
        LastActionTime = DateTime.MinValue;
    }

    private void PruneExpiredCooldowns(DateTime now)
    {
        var expired = _cooldowns.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList();
        foreach (var key in expired) _cooldowns.Remove(key);
    }
}
