using FreiAtlas.Core.Contracts;
using FreiAtlas.Core.Player;
using FreiAtlas.Core.Recovery;

namespace FreiAtlas.QuickAssist;

public sealed record QuickAssistSnapshot(PlayerVitalsSnapshot Vitals, string HealthStatus, string ManaStatus);

public sealed class QuickAssistService
{
    private readonly IPlayerVitalsSource _source;
    private readonly IRecoveryInput _input;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private QuickAssistSettings _settings = QuickAssistSettings.Default;
    private QuickAssistSnapshot _current;
    private readonly RuleState _health = new();
    private readonly RuleState _mana = new();
    private (int ProcessId, long Sequence) _session;
    private DateTimeOffset _readyAfter;

    public QuickAssistService(IPlayerVitalsSource source, IRecoveryInput input, TimeProvider? clock = null)
    {
        _source = source;
        _input = input;
        _clock = clock ?? TimeProvider.System;
        _current = new(source.Current, "未启用", "未启用");
    }

    public QuickAssistSnapshot Current => Volatile.Read(ref _current);

    public void Configure(QuickAssistSettings settings)
    {
        settings.Validate();
        lock (_gate)
        {
            if (_settings.Health != settings.Health) _health.Error = null;
            if (_settings.Mana != settings.Mana) _mana.Error = null;
            _settings = settings;
        }
    }

    public QuickAssistSnapshot Tick()
    {
        lock (_gate)
        {
            var snapshot = _source.Current;
            var now = _clock.GetUtcNow();
            string? paused = null;
            if (!snapshot.IsValid || now - snapshot.CapturedAt > TimeSpan.FromMilliseconds(250) || snapshot.CapturedAt > now)
            {
                paused = snapshot.Error ?? "角色读数已过期";
                _session = default;
            }
            else if (!snapshot.IsAlive) { paused = "角色已死亡"; _session = default; }
            else if (!_input.IsTargetForeground(snapshot.ProcessId)) paused = "游戏不在前台";

            if (paused is null)
            {
                var session = (snapshot.ProcessId, snapshot.SessionSequence);
                if (_session != session)
                {
                    _session = session;
                    _readyAfter = now.AddMilliseconds(150);
                }
                if (now < _readyAfter || snapshot.CapturedAt < _readyAfter) paused = "等待连续有效读数";
            }
            var result = new QuickAssistSnapshot(snapshot,
                Evaluate(_settings.Health, snapshot.Health, _health, snapshot, now, paused),
                Evaluate(_settings.Mana, snapshot.Mana, _mana, snapshot, now, paused));
            Volatile.Write(ref _current, result);
            return result;
        }
    }

    private string Evaluate(RecoveryRule rule, VitalPool? pool, RuleState state, PlayerVitalsSnapshot snapshot, DateTimeOffset now, string? paused)
    {
        if (!rule.Enabled) return "未启用";
        if (state.Error is not null) return state.Error;
        if (paused is not null) return "已暂停：" + paused;
        if (pool is null) return "已暂停：该资源读数无效";
        if (pool.AvailableMaximum <= 0) return "已暂停：无可恢复容量";
        if (rule.Mode == RecoveryThresholdMode.Fixed && rule.FixedValue > pool.AvailableMaximum)
            return "已暂停：固定阈值超过可恢复上限";
        var below = rule.Mode == RecoveryThresholdMode.Percentage
            ? 100L * pool.Current < (long)pool.AvailableMaximum * rule.Percentage
            : pool.Current < rule.FixedValue;
        if (!below || pool.Current >= pool.AvailableMaximum) return "监测中：未低于阈值";
        if (state.LastSent is { } last && _clock.GetElapsedTime(last) < TimeSpan.FromMilliseconds(rule.IntervalMilliseconds))
            return "等待触发间隔";
        if (!ReferenceEquals(_source.Current, snapshot)) return "等待最新读数";
        if (!RecoveryKey.TryParse(rule.Key, out var key)) return "恢复按键无效";
        var result = _input.Send(snapshot.ProcessId, key!, snapshot.CapturedAt);
        if (result.State == RecoveryInputState.Sent) state.LastSent = _clock.GetTimestamp();
        else if (result.State == RecoveryInputState.Failed) state.Error = "按键失败，修改设置或重新启用后重试：" + result.Message;
        return state.Error ?? result.Message;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50), _clock);
        try
        {
            do
            {
                try { Tick(); }
                catch (Exception exception)
                {
                    lock (_gate)
                    {
                        _health.Error = _mana.Error = "恢复模块异常，重新启用后重试：" + exception.Message;
                        Volatile.Write(ref _current, new(_source.Current, _health.Error, _mana.Error));
                    }
                }
            } while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private sealed class RuleState
    {
        public long? LastSent;
        public string? Error;
    }
}
