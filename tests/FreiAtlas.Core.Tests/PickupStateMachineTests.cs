using System.Numerics;
using FreiAtlas.Core.Pickup;

namespace FreiAtlas.Core.Tests;

public sealed class PickupStateMachineTests
{
    private static readonly PickupSettings DefaultSettings = new()
    {
        Enabled = true,
        Whitelist = ["混沌石", "崇高石"],
        PickupRangeGrid = 8f,
        DisappearTimeoutMilliseconds = 800,
        MaxAttempts = 3
    };

    private static GroundItemPickupTarget MakeTarget(uint id, string name)
        => new(id, name, 4f, new Vector2(1000f, 500f), (nint)(0x1000 + id), (nint)(0x2000 + id));

    [Fact]
    public void StateMachine_InitialIdle_PicksFirstTargetAndEntersAwaiting()
    {
        var sm = new PickupStateMachine();
        var now = new DateTime(2026, 10, 14, 12, 0, 0);
        var target1 = MakeTarget(1, "混沌石");
        var target2 = MakeTarget(2, "崇高石");

        var action = sm.Update(DefaultSettings, [target1, target2], now);

        Assert.NotNull(action);
        Assert.False(action.IsRetry);
        Assert.Equal(1, action.AttemptIndex);
        Assert.Equal(1u, action.Target.EntityId);
        Assert.Equal(PickupState.AwaitingDisappearance, sm.State);
        Assert.Equal(1u, sm.CurrentTarget!.EntityId);
    }

    [Fact]
    public void StateMachine_AwaitingDisappearance_ClearsWhenItemDisappears()
    {
        var sm = new PickupStateMachine();
        var start = new DateTime(2026, 10, 14, 12, 0, 0);
        var target1 = MakeTarget(1, "混沌石");
        var target2 = MakeTarget(2, "崇高石");

        // 第一次 update 发起拾取
        sm.Update(DefaultSettings, [target1, target2], start);

        // 200ms 后，target1 已经不在列表里了（已捡起消失）
        var action = sm.Update(DefaultSettings, [target2], start.AddMilliseconds(200));

        Assert.Null(action);
        Assert.Equal(PickupState.Idle, sm.State);
        Assert.Null(sm.CurrentTarget);
    }

    [Fact]
    public void StateMachine_AwaitingDisappearance_RetriesWhenTimeoutOccurs()
    {
        var sm = new PickupStateMachine();
        var start = new DateTime(2026, 10, 14, 12, 0, 0);
        var target = MakeTarget(1, "混沌石");

        // 第一次尝试
        sm.Update(DefaultSettings, [target], start);

        // 500ms 后未超时，不产生新 action
        var actionMid = sm.Update(DefaultSettings, [target], start.AddMilliseconds(500));
        Assert.Null(actionMid);
        Assert.Equal(1, sm.CurrentAttempt);

        // 850ms 后超时，触发第 2 次重试尝试
        var actionRetry = sm.Update(DefaultSettings, [target], start.AddMilliseconds(850));
        Assert.NotNull(actionRetry);
        Assert.True(actionRetry.IsRetry);
        Assert.Equal(2, actionRetry.AttemptIndex);
        Assert.Equal(2, sm.CurrentAttempt);
        Assert.Equal(PickupState.AwaitingDisappearance, sm.State);
    }

    [Fact]
    public void StateMachine_ReachingMaxAttempts_BlacklistsAndSwitchesToNext()
    {
        var sm = new PickupStateMachine(TimeSpan.FromSeconds(5));
        var time = new DateTime(2026, 10, 14, 12, 0, 0);
        var stuckTarget = MakeTarget(1, "混沌石");
        var nextTarget = MakeTarget(2, "崇高石");

        // 第 1 次尝试 (time=0)
        sm.Update(DefaultSettings, [stuckTarget, nextTarget], time);
        // 第 2 次尝试 (time=900)
        time = time.AddMilliseconds(900);
        sm.Update(DefaultSettings, [stuckTarget, nextTarget], time);
        // 第 3 次尝试 (time=1800)
        time = time.AddMilliseconds(900);
        sm.Update(DefaultSettings, [stuckTarget, nextTarget], time);

        // 第 3 次依然超时未消失 (time=2700) -> 应该放弃，将 stuckTarget(1) 冷却，状态回到 Idle
        time = time.AddMilliseconds(900);
        var actionGiveUp = sm.Update(DefaultSettings, [stuckTarget, nextTarget], time);
        Assert.Null(actionGiveUp);
        Assert.Equal(PickupState.Idle, sm.State);
        Assert.Contains(1u, sm.ActiveCooldownEntityIds(time));

        // 下一次轮询 (time=2750) -> 应该直接选取 nextTarget(2) 进行拾取！
        time = time.AddMilliseconds(50);
        var actionNext = sm.Update(DefaultSettings, [stuckTarget, nextTarget], time);
        Assert.NotNull(actionNext);
        Assert.Equal(2u, actionNext.Target.EntityId);
    }

    [Fact]
    public void StateMachine_ModifierKeyGate_BlocksWhenKeyNotPressed()
    {
        var settingsWithKey = DefaultSettings with { ModifierKeyEnabled = true, ModifierKey = "Shift" };
        var sm = new PickupStateMachine();
        var now = new DateTime(2026, 10, 14, 12, 0, 0);
        var target = MakeTarget(1, "混沌石");

        // 未按键时不触发
        var actionBlocked = sm.Update(settingsWithKey, [target], now, modifierKeyPressed: false);
        Assert.Null(actionBlocked);
        Assert.Equal(PickupState.Idle, sm.State);

        // 按住键时触发
        var actionAllowed = sm.Update(settingsWithKey, [target], now, modifierKeyPressed: true);
        Assert.NotNull(actionAllowed);
        Assert.Equal(1u, actionAllowed.Target.EntityId);
        Assert.Equal(PickupState.AwaitingDisappearance, sm.State);
    }

    [Fact]
    public void StateMachine_AwaitingDisappearance_RefreshesTargetCoordinatesOnRetry()
    {
        var sm = new PickupStateMachine();
        var start = new DateTime(2026, 10, 14, 12, 0, 0);
        var initialTarget = MakeTarget(1, "混沌石"); // 初始坐标 (1000, 500)

        // 第一次尝试拾取
        var firstAction = sm.Update(DefaultSettings, [initialTarget], start);
        Assert.NotNull(firstAction);
        Assert.Equal(new Vector2(1000f, 500f), firstAction.Target.LabelCenter);

        // 角色跑动滚屏后，目标在最新一帧的屏幕坐标移动到 (1250, 620)
        var shiftedTarget = new GroundItemPickupTarget(
            initialTarget.EntityId,
            initialTarget.ItemName,
            initialTarget.DistanceToPlayer,
            new Vector2(1250f, 620f),
            initialTarget.LabelAddress,
            initialTarget.EntityAddress);

        // 超时触发重试，重试动作必须动态使用最新滚屏屏幕坐标！
        var retryAction = sm.Update(DefaultSettings, [shiftedTarget], start.AddMilliseconds(850));
        Assert.NotNull(retryAction);
        Assert.True(retryAction.IsRetry);
        Assert.Equal(new Vector2(1250f, 620f), retryAction.Target.LabelCenter);
        Assert.Equal(new Vector2(1250f, 620f), sm.CurrentTarget!.LabelCenter);
    }

    [Fact]
    public void StateMachine_AwaitingDisappearance_CancelsRetryWhenTargetLeavesRangeOrScreen()
    {
        var sm = new PickupStateMachine();
        var start = new DateTime(2026, 10, 14, 12, 0, 0);
        var target = MakeTarget(1, "混沌石");

        // 第一次尝试拾取
        sm.Update(DefaultSettings, [target], start);
        Assert.Equal(PickupState.AwaitingDisappearance, sm.State);

        // 角色飞速跑开，目标在最新一帧中已脱离可用范围（列表为空）
        var action = sm.Update(DefaultSettings, [], start.AddMilliseconds(200));
        Assert.Null(action);
        Assert.Equal(PickupState.Idle, sm.State);
        Assert.Null(sm.CurrentTarget);
    }
}
