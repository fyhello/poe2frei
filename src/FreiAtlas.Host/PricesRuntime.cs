using System.Collections.Immutable;
using System.Text.Json;
using FreiAtlas.Core.Settings;
using FreiAtlas.Expedition;

namespace FreiAtlas.Host;

public sealed record PricesRuntimeSnapshot(ImmutableArray<PriceLeague> Leagues,
    bool IsUpdatingCatalog, string? CatalogError, PriceServiceStatus Prices);

public sealed class PricesRuntime : IDisposable, IAsyncDisposable
{
    private readonly HttpClient _client;
    private readonly ExpeditionPriceService _service;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private ImmutableArray<PriceLeague> _leagues = PriceLeagueCatalog.Defaults;
    private Task _catalogTask = Task.CompletedTask;
    private Task _refreshTask = Task.CompletedTask;
    private bool _updatingCatalog;
    private string? _catalogError;
    private bool _disposed;

    public PricesRuntime(PriceSettings settings, string? cachePath = null, HttpClient? client = null)
    {
        settings.Validate();
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("FreiAtlas/1.0");
        _service = new ExpeditionPriceService(new ExpeditionPriceServiceOptions(
            cachePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FreiAtlas", "Cache", "expedition-prices-v3.json"), leagueId: settings.LeagueId), BuildSources);
    }

    public ExpeditionPriceSnapshot CurrentBook => _service.Current;

    public PricesRuntimeSnapshot Current
    {
        get { lock (_gate) return new(_leagues, _updatingCatalog, _catalogError, _service.Status); }
    }

    private IEnumerable<IExpeditionPriceSource> BuildSources(string leagueId)
    {
        PriceLeague? league;
        lock (_gate) league = _leagues.FirstOrDefault(item => item.Id == leagueId);
        var sources = new List<IExpeditionPriceSource> { new PoeNinjaPriceSource(_client, leagueId) };
        if (!string.IsNullOrWhiteSpace(league?.ScoutShortName))
            sources.Add(new Poe2ScoutPriceSource(_client, leagueShortName: league.ScoutShortName, leagueId: leagueId));
        return sources;
    }

    public void Configure(PriceSettings settings)
    {
        settings.Validate();
        _service.SelectLeague(settings.LeagueId);
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _catalogTask = RefreshCatalogAsync();
        }
        _ = _service.StartAsync(_shutdown.Token);
    }

    public Task RefreshAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_refreshTask.IsCompleted) return _refreshTask;
            _refreshTask = RefreshCoreAsync();
            return _refreshTask;
        }
    }

    private async Task RefreshCoreAsync()
    {
        await _catalogTask.ConfigureAwait(false);
        await RefreshCatalogAsync().ConfigureAwait(false);
        if (!_shutdown.IsCancellationRequested) await _service.RefreshAsync(true, _shutdown.Token).ConfigureAwait(false);
    }

    private async Task RefreshCatalogAsync()
    {
        lock (_gate) _updatingCatalog = true;
        try
        {
            var leagues = await PriceLeagueCatalog.FetchAsync(_client, _shutdown.Token).ConfigureAwait(false);
            lock (_gate) { _leagues = leagues; _catalogError = null; }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            or OperationCanceledException or ArgumentException)
        {
            lock (_gate) _catalogError = "赛季列表更新失败，保留已知列表：" + exception.GetType().Name;
        }
        finally { lock (_gate) _updatingCatalog = false; }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
        }
        try { await Task.WhenAll(_catalogTask, _refreshTask).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        await _service.DisposeAsync().ConfigureAwait(false);
        _client.Dispose();
        _shutdown.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
