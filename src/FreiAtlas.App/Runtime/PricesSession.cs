using FreiAtlas.App.Settings;
using FreiAtlas.Core.Settings;
using FreiAtlas.Host;

namespace FreiAtlas.App.Runtime;

public sealed class PricesSession : IAsyncDisposable
{
    private readonly AtlasSettingsStore _settings;
    private readonly PricesRuntime _runtime;

    public PricesSession(AtlasSettingsStore settings)
    {
        _settings = settings;
        _runtime = new PricesRuntime(settings.Prices);
        settings.PricesChanged += _runtime.Configure;
    }

    public PricesRuntimeSnapshot Current => _runtime.Current;
    public IOverlaySession CreateOverlaySession() => new OverlaySession(new AtlasOverlayRunner(_runtime));
    public void Start() => _runtime.Start();
    public Task RefreshAsync() => _runtime.RefreshAsync();

    public void SelectLeague(string leagueId)
    {
        if (!_runtime.Current.Leagues.Any(league => league.Id == leagueId))
            throw new ArgumentException("该赛季不在当前可选列表中，请先刷新赛季列表。");
        _settings.UpdatePrices(new PriceSettings(leagueId));
    }

    public async ValueTask DisposeAsync()
    {
        _settings.PricesChanged -= _runtime.Configure;
        await _runtime.DisposeAsync().ConfigureAwait(false);
    }
}
