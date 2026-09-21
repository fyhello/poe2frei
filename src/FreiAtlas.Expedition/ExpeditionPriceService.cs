using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using FreiAtlas.Core.Settings;

namespace FreiAtlas.Expedition;

public sealed record ExpeditionPriceServiceOptions
{
    public ExpeditionPriceServiceOptions(string cachePath, TimeSpan? refreshInterval = null,
        TimeSpan? cacheLifetime = null, string leagueId = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cachePath);
        CachePath = cachePath;
        RefreshInterval = refreshInterval ?? TimeSpan.FromHours(1);
        CacheLifetime = cacheLifetime ?? TimeSpan.FromHours(24);
        LeagueId = leagueId;
        if (leagueId.Length > 0) new PriceSettings(leagueId).Validate();
        if (RefreshInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(refreshInterval));
        if (CacheLifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(cacheLifetime));
    }

    public string CachePath { get; }
    public TimeSpan RefreshInterval { get; }
    public TimeSpan CacheLifetime { get; }
    public string LeagueId { get; }
}

public sealed record PriceSourceStatus(string Name, int ItemCount, string Message);

public sealed record PriceServiceStatus(string LeagueId, bool IsRefreshing, int ItemCount,
    DateTimeOffset? FetchedAtUtc, decimal? ExaltedPerDivine, string Message,
    ImmutableArray<PriceSourceStatus> Sources);

public sealed class ExpeditionPriceService : IDisposable, IAsyncDisposable
{
    private readonly ExpeditionPriceServiceOptions _options;
    private readonly Func<string, IEnumerable<IExpeditionPriceSource>> _sourceFactory;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HashSet<Task> _operations = [];
    private readonly List<CancellationTokenSource> _retired = [];
    private Market _market;
    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;
    private bool _disposed;

    public ExpeditionPriceService(ExpeditionPriceServiceOptions options,
        IEnumerable<IExpeditionPriceSource> sources, TimeProvider? timeProvider = null)
        : this(options, CreateFactory(sources), timeProvider) { }

    public ExpeditionPriceService(ExpeditionPriceServiceOptions options,
        Func<string, IEnumerable<IExpeditionPriceSource>> sourceFactory, TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _sourceFactory = sourceFactory ?? throw new ArgumentNullException(nameof(sourceFactory));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _market = CreateMarket(options.LeagueId);
    }

    private static Func<string, IEnumerable<IExpeditionPriceSource>> CreateFactory(IEnumerable<IExpeditionPriceSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var stable = sources.ToArray();
        return _ => stable;
    }

    public ExpeditionPriceSnapshot Current
    {
        get { lock (_gate) return CurrentNoLock(); }
    }

    private ExpeditionPriceSnapshot CurrentNoLock() =>
        _market.Snapshot.IsExpired(_timeProvider.GetUtcNow(), _options.CacheLifetime)
            ? ExpeditionPriceSnapshot.Empty : _market.Snapshot;

    public PriceServiceStatus Status
    {
        get
        {
            lock (_gate)
            {
                var current = CurrentNoLock();
                return new(_market.LeagueId, _market.IsRefreshing, current.ItemCount,
                    current.ItemCount > 0 ? current.FetchedAtUtc : null,
                    current.ItemCount > 0 ? current.ExaltedPerDivine : null,
                    current.ItemCount == 0 && !_market.IsRefreshing && _market.Message.Length == 0
                        ? "暂无该赛季价格，请刷新。" : _market.Message, _market.Sources);
            }
        }
    }

    public bool IsRunning { get { lock (_gate) return _loopTask is { IsCompleted: false }; } }

    public void SelectLeague(string leagueId)
    {
        new PriceSettings(leagueId).Validate();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_market.LeagueId == leagueId) return;
            var next = CreateMarket(leagueId);
            var previous = _market.Cancellation;
            _retired.Add(previous);
            _market = next;
            previous.Cancel();
            // 快照先切换；旧请求即使忽略取消，也不能重新发布到当前市场。
            _ = RefreshAsync(force: true);
        }
    }

    public Task RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var task = RefreshCoreAsync(_market, force, cancellationToken);
            _operations.Add(task);
            _ = task.ContinueWith(completed => { lock (_gate) _operations.Remove(completed); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    private async Task RefreshCoreAsync(Market market, bool force, CancellationToken callerToken)
    {
        await Task.Yield();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            callerToken, market.Cancellation.Token, _shutdown.Token);
        var token = linked.Token;
        var entered = false;
        try
        {
            await _refreshGate.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            lock (_gate)
            {
                if (!ReferenceEquals(_market, market) || (!force
                    && !market.Snapshot.IsExpired(_timeProvider.GetUtcNow(), _options.RefreshInterval))) return;
                market.IsRefreshing = true;
                market.Message = CurrentNoLock().ItemCount > 0 ? "正在更新，暂用该赛季缓存。" : "正在获取该赛季价格…";
            }
            var results = new List<ExpeditionPriceSourceResult>();
            var statuses = ImmutableArray.CreateBuilder<PriceSourceStatus>();
            foreach (var source in _sourceFactory(market.LeagueId))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var result = await source.FetchAsync(token).WaitAsync(token).ConfigureAwait(false);
                    if (market.LeagueId.Length > 0 && result.LeagueId != market.LeagueId)
                    {
                        statuses.Add(new(source.Name, 0, "赛季身份不匹配，已拒绝该来源。"));
                        continue;
                    }
                    results.Add(result);
                    statuses.Add(new(source.Name, result.Entries.IsDefault ? 0 : result.Entries.Length,
                        result.Diagnostics.IsDefaultOrEmpty
                            ? (result.Entries.IsDefaultOrEmpty ? "未取得价格" : "已取得价格")
                            : string.Join("；", result.Diagnostics)));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    statuses.Add(new(source.Name, 0, $"获取失败：{exception.GetType().Name}"));
                }
            }
            token.ThrowIfCancellationRequested();
            var hasRate = results.Any(result => result.ExaltedPerDivine > 0m);
            var snapshot = ExpeditionPriceSnapshot.Merge(_timeProvider.GetUtcNow(), results, market.LeagueId);
            lock (_gate)
            {
                if (!ReferenceEquals(_market, market) || token.IsCancellationRequested) return;
                market.Sources = statuses.ToImmutable();
                if (snapshot.ItemCount == 0 || !hasRate)
                {
                    market.Message = CurrentNoLock().ItemCount > 0
                        ? "更新失败，继续使用该赛季有效缓存。" : "暂无该赛季价格或有效汇率，请稍后刷新。";
                    return;
                }
                market.Snapshot = snapshot;
                market.Message = statuses.Any(status => status.ItemCount == 0 || status.Message != "已取得价格")
                    ? "部分来源不可用，已更新取得的价格。" : "价格已更新。";
                try { snapshot.Save(market.CachePath); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { market.Message = "价格已更新，但缓存保存失败：" + exception.GetType().Name; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (callerToken.IsCancellationRequested) throw;
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_market, market)) market.Message = "价格更新失败：" + exception.GetType().Name;
            }
        }
        finally
        {
            if (entered) { lock (_gate) market.IsRefreshing = false; }
            if (entered) _refreshGate.Release();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loopTask is { IsCompleted: false }) return Task.CompletedTask;
            _loopCancellation?.Dispose();
            _loopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            var token = _loopCancellation.Token;
            _loopTask = Task.Run(() => RunLoopAsync(token));
            return Task.CompletedTask;
        }
    }

    public async Task StopAsync()
    {
        Task? loop;
        lock (_gate) { _loopCancellation?.Cancel(); loop = _loopTask; }
        if (loop is not null) await loop.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Task[] operations;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
            operations = _operations.ToArray();
        }
        await StopAsync().ConfigureAwait(false);
        try { await Task.WhenAll(operations).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _loopCancellation?.Dispose();
        _market.Cancellation.Dispose();
        foreach (var retired in _retired) retired.Dispose();
        _shutdown.Dispose();
        _refreshGate.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async Task RunLoopAsync(CancellationToken token)
    {
        try
        {
            await RefreshAsync(true, token).ConfigureAwait(false);
            using var timer = new PeriodicTimer(_options.RefreshInterval, _timeProvider);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                await RefreshAsync(true, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
    }

    public string GetCachePath(string leagueId)
    {
        if (leagueId.Length == 0) return _options.CachePath;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(leagueId)));
        return Path.Combine(Path.GetDirectoryName(_options.CachePath) ?? string.Empty,
            Path.GetFileNameWithoutExtension(_options.CachePath) + "." + hash + ".json");
    }

    private Market CreateMarket(string leagueId)
    {
        var market = new Market(leagueId, GetCachePath(leagueId));
        try
        {
            if (ExpeditionPriceSnapshot.TryLoad(market.CachePath, _timeProvider.GetUtcNow(),
                _options.CacheLifetime, out var cached, leagueId)) market.Snapshot = cached;
        }
        catch (UnauthorizedAccessException) { market.Message = "无法读取该赛季缓存，将尝试联网获取。"; }
        return market;
    }

    private sealed class Market(string leagueId, string cachePath)
    {
        public string LeagueId { get; } = leagueId;
        public string CachePath { get; } = cachePath;
        public CancellationTokenSource Cancellation { get; } = new();
        public ExpeditionPriceSnapshot Snapshot { get; set; } = ExpeditionPriceSnapshot.Empty;
        public bool IsRefreshing { get; set; }
        public string Message { get; set; } = string.Empty;
        public ImmutableArray<PriceSourceStatus> Sources { get; set; } = [];
    }
}
