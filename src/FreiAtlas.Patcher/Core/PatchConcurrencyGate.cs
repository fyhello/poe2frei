namespace FreiAtlas.Patcher.Core;

/// <summary>
/// 补丁任务互斥闸。
///
/// 多个补丁服务会改写同一批数据表（baseitemtypes、endgamemaps、archnemesismods 等），
/// 各自「读取当前内容 → 修改 → 写回」。并发执行时，后写入的一方会覆盖前一方刚写的内容，
/// 表现为某个补丁莫名其妙失效。此闸确保同一时刻只有一个补丁任务运行。
///
/// 采用「拒绝」而非「排队」：排队会让用户连点多次后重复执行多次全量重写，耗时且无意义。
/// </summary>
public sealed class PatchConcurrencyGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private int _busy;

    /// <summary>当前是否有任务持有闸门。</summary>
    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    /// <summary>
    /// 尝试占用闸门，不等待。
    /// </summary>
    /// <returns>成功时返回释放凭据；已被占用时返回 null。</returns>
    public IDisposable? TryEnter()
    {
        if (!_semaphore.Wait(0)) return null;
        Interlocked.Exchange(ref _busy, 1);
        return new Lease(this);
    }

    private void Exit()
    {
        Interlocked.Exchange(ref _busy, 0);
        _semaphore.Release();
    }

    /// <summary>释放凭据。重复 Dispose 不会重复释放信号量。</summary>
    private sealed class Lease(PatchConcurrencyGate owner) : IDisposable
    {
        private PatchConcurrencyGate? _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Exit();
        }
    }
}
