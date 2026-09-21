using FreiAtlas.Core.Contracts;
using FreiAtlas.Core.Player;
using FreiAtlas.Core.Recovery;

namespace FreiAtlas.QuickAssist.Tests;

public sealed class QuickAssistServiceTests
{
    [Theory]
    [InlineData(1278, 100, false)]
    [InlineData(767, 60, false)]
    [InlineData(766, 60, true)]
    [InlineData(1, 1, true)]
    public void Percentage_UsesUnreservedMaximumAndStrictBoundary(int current, int threshold, bool sent)
    {
        var test = new Harness();
        test.Configure(test.Settings.Health with { Percentage = threshold });
        test.Ready(new VitalPool(current, 1704, 0, 2500));
        Assert.Equal(sent ? 1 : 0, test.Input.Sends.Count);
    }

    [Theory]
    [InlineData(499, 500, true)]
    [InlineData(500, 500, false)]
    [InlineData(1, 1001, false)]
    public void Fixed_RejectsThresholdAboveCapacity(int current, int threshold, bool sent)
    {
        var test = new Harness();
        test.Configure(test.Settings.Health with { Mode = RecoveryThresholdMode.Fixed, FixedValue = threshold });
        var result = test.Ready(new VitalPool(current, 1000, 0, 0));
        Assert.Equal(sent ? 1 : 0, test.Input.Sends.Count);
        if (threshold > 1000) Assert.Contains("超过可恢复上限", result.HealthStatus);
    }

    [Fact]
    public void Cooldown_RetriesOnlyAfterConfiguredInterval()
    {
        var test = new Harness();
        test.Ready();
        test.Clock.Advance(2999);
        test.Publish();
        Assert.Contains("触发间隔", test.Service.Tick().HealthStatus);
        Assert.Single(test.Input.Sends);
        test.Clock.Advance(1);
        test.Publish();
        test.Service.Tick();
        Assert.Equal(2, test.Input.Sends.Count);
    }

    [Fact]
    public void Rules_AreIndependentAndMissingManaDoesNotBlockHealth()
    {
        var test = new Harness();
        test.Configure(test.Settings.Health with { Enabled = false }, test.Settings.Mana with { Enabled = true });
        test.Ready();
        Assert.Equal("2", Assert.Single(test.Input.Sends).Gesture);
        test.Input.Sends.Clear();
        test.Configure(test.Settings.Health with { Enabled = true }, test.Settings.Mana with { Enabled = false });
        test.Publish();
        test.Source.Current = test.Source.Current with { Mana = null };
        test.Service.Tick();
        Assert.Equal("1", Assert.Single(test.Input.Sends).Gesture);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("invalid")]
    [InlineData("dead")]
    [InlineData("background")]
    [InlineData("zero-capacity")]
    public void UnsafeState_NeverSends(string condition)
    {
        var test = new Harness();
        test.Input.Result = RecoveryInputState.Paused;
        test.Ready();
        test.Input.Sends.Clear();
        test.Input.Result = RecoveryInputState.Sent;
        switch (condition)
        {
            case "stale": test.Clock.Advance(251); break;
            case "future": test.Source.Current = test.Source.Current with { CapturedAt = test.Clock.GetUtcNow().AddSeconds(1) }; break;
            case "invalid": test.Source.Current = PlayerVitalsSnapshot.Unavailable(1, test.Clock.GetUtcNow(), "读取失败"); break;
            case "dead": test.Source.Current = test.Source.Current with { Health = new(0, 1000, 0, 0) }; break;
            case "background": test.Input.Foreground = false; break;
            case "zero-capacity": test.Source.Current = test.Source.Current with { Health = new(1, 1000, 1000, 0) }; break;
        }
        Assert.Contains("暂停", test.Service.Tick().HealthStatus);
        Assert.Empty(test.Input.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProcessOrAreaChange_RequiresNewStableFrame(bool processChanged)
    {
        var test = new Harness();
        test.Input.Result = RecoveryInputState.Paused;
        test.Ready();
        test.Input.Sends.Clear();
        test.Input.Result = RecoveryInputState.Sent;
        test.Source.Current = test.Source.Current with { ProcessId = processChanged ? 2 : 1, SessionSequence = processChanged ? 1 : 2 };
        Assert.Contains("连续有效", test.Service.Tick().HealthStatus);
        test.Clock.Advance(150);
        Assert.Contains("连续有效", test.Service.Tick().HealthStatus);
        Assert.Empty(test.Input.Sends);
        test.Source.Current = test.Source.Current with { CapturedAt = test.Clock.GetUtcNow() };
        test.Service.Tick();
        Assert.Single(test.Input.Sends);
    }

    [Fact]
    public void FailedSend_LatchesUntilRuleIsChanged()
    {
        var test = new Harness();
        test.Input.Result = RecoveryInputState.Failed;
        Assert.Contains("按键失败", test.Ready().HealthStatus);
        test.Clock.Advance(5000);
        test.Publish();
        test.Service.Tick();
        Assert.Single(test.Input.Sends);
        test.Configure(test.Settings.Health with { Enabled = false });
        test.Service.Tick();
        test.Configure(test.Settings.Health with { Enabled = true });
        test.Input.Result = RecoveryInputState.Sent;
        test.Service.Tick();
        Assert.Equal(2, test.Input.Sends.Count);
    }

    [Fact]
    public void SnapshotReplacedBeforeSend_IsRejected()
    {
        var test = new Harness();
        test.Input.Result = RecoveryInputState.Paused;
        test.Ready();
        test.Input.Sends.Clear();
        test.Input.OnForegroundCheck = () => test.Source.Current = PlayerVitalsSnapshot.Unavailable(2, test.Clock.GetUtcNow(), "已切换进程");
        Assert.Contains("最新读数", test.Service.Tick().HealthStatus);
        Assert.Empty(test.Input.Sends);
    }

    private sealed class Harness
    {
        public ManualClock Clock { get; } = new();
        public Source Source { get; } = new();
        public Input Input { get; } = new();
        public QuickAssistService Service { get; }
        public QuickAssistSettings Settings { get; private set; } = QuickAssistSettings.Default;
        public Harness()
        {
            Service = new(Source, Input, Clock);
            Configure(Settings.Health with { Enabled = true });
        }
        public void Configure(RecoveryRule health, RecoveryRule? mana = null)
        {
            Settings = Settings with { Health = health, Mana = mana ?? Settings.Mana };
            Service.Configure(Settings);
        }
        public void Publish(VitalPool? health = null) => Source.Current = new(1, 1, Clock.GetUtcNow(), health ?? new(100, 1000, 0, 0), new(100, 1000, 0, 0), new(0, 0, 0, 0), null);
        public QuickAssistSnapshot Ready(VitalPool? health = null)
        {
            Publish(health);
            Service.Tick();
            Clock.Advance(150);
            Publish(health);
            return Service.Tick();
        }
    }

    private sealed class Source : IPlayerVitalsSource
    {
        public PlayerVitalsSnapshot Current { get; set; } = PlayerVitalsSnapshot.Unavailable(0, DateTimeOffset.UtcNow, "尚未读取");
    }

    private sealed class Input : IRecoveryInput
    {
        public bool Foreground { get; set; } = true;
        public Action? OnForegroundCheck { get; set; }
        public RecoveryInputState Result { get; set; } = RecoveryInputState.Sent;
        public List<RecoveryKey> Sends { get; } = [];
        public bool IsTargetForeground(int processId) { OnForegroundCheck?.Invoke(); return Foreground; }
        public RecoveryInputResult Send(int processId, RecoveryKey key, DateTimeOffset capturedAt)
        {
            Sends.Add(key);
            return new(Result, Result == RecoveryInputState.Failed ? "系统拒绝" : "输入结果");
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _milliseconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(_milliseconds);
        public void Advance(int milliseconds) => _milliseconds += milliseconds;
    }
}
