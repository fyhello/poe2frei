using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 补丁并发闸门的单元测试。
///
/// 背景：多个补丁服务会改写同一批数据表，各自「读当前 → 改 → 写回」。
/// 并发执行时后写入者会覆盖前者的改动，表现为某个补丁莫名失效。
/// </summary>
public sealed class PatchConcurrencyGateTests
{
    [Fact]
    public void TryEnter_首次调用成功()
    {
        var gate = new PatchConcurrencyGate();

        using var lease = gate.TryEnter();

        Assert.NotNull(lease);
        Assert.True(gate.IsBusy);
    }

    [Fact]
    public void TryEnter_持有时第二次被拒绝()
    {
        var gate = new PatchConcurrencyGate();

        using var first = gate.TryEnter();
        Assert.NotNull(first);

        var second = gate.TryEnter();

        Assert.Null(second);
    }

    [Fact]
    public void Dispose_释放后可以再次占用()
    {
        var gate = new PatchConcurrencyGate();

        var first = gate.TryEnter();
        Assert.NotNull(first);
        first!.Dispose();

        Assert.False(gate.IsBusy);

        using var second = gate.TryEnter();
        Assert.NotNull(second);
    }

    [Fact]
    public void Dispose_重复调用不会重复释放()
    {
        var gate = new PatchConcurrencyGate();

        var lease = gate.TryEnter();
        Assert.NotNull(lease);
        lease!.Dispose();
        lease.Dispose();

        // 若重复释放导致信号量计数膨胀，这里会同时拿到两个凭据
        using var first = gate.TryEnter();
        var second = gate.TryEnter();

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public void 并发抢占_只有一个成功()
    {
        const int attempts = 16;
        var gate = new PatchConcurrencyGate();
        var acquired = 0;
        var leases = new System.Collections.Concurrent.ConcurrentBag<IDisposable>();

        Parallel.For(0, attempts, _ =>
        {
            var lease = gate.TryEnter();
            if (lease is null) return;
            Interlocked.Increment(ref acquired);
            leases.Add(lease);
        });

        Assert.Equal(1, acquired);
        foreach (var lease in leases) lease.Dispose();
        Assert.False(gate.IsBusy);
    }

    /// <summary>
    /// 真并发下只放行一个：模拟用户连点两个补丁按钮。
    /// 这是本次接线的核心保障 —— 接线前闸门从未被调用，并发完全裸奔。
    /// </summary>
    [Fact]
    public async Task TryEnter_并发调用只放行一个()
    {
        var gate = new PatchConcurrencyGate();
        int granted = 0;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            await ready.Task;
            var lease = gate.TryEnter();
            if (lease is not null)
            {
                Interlocked.Increment(ref granted);
                // 持有到全部并发尝试结束，才能验证「同时只有一个」
                await Task.Delay(50);
                lease.Dispose();
            }
        })).ToArray();

        ready.SetResult();
        await Task.WhenAll(tasks);

        Assert.Equal(1, granted);
        Assert.False(gate.IsBusy);
    }
}