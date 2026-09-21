using FreiAtlas.App.Settings;
using FreiAtlas.Core.PortalSqueeze;
using FreiAtlas.Host;
using FreiAtlas.QuickAssist;

namespace FreiAtlas.App.Runtime;

public sealed class QuickAssistSession : IAsyncDisposable
{
    private readonly AtlasSettingsStore _settings;
    private readonly QuickAssistRuntime _runtime = new();

    public QuickAssistSession(AtlasSettingsStore settings)
    {
        _settings = settings;
        _runtime.Configure(settings.QuickAssist);
        settings.QuickAssistChanged += _runtime.Configure;
    }

    public QuickAssistSnapshot Current => _runtime.Current;

    public PortalSqueezeSnapshot PortalSqueeze => _runtime.PortalSqueeze;

    public int SelectedProcessId => _runtime.SelectedProcessId;

    public void SelectProcess(int? processId) => _runtime.SelectProcess(processId);

    public Task TriggerPortalSqueezeAsync(
        int processId,
        CancellationToken cancellationToken = default)
        => _runtime.TriggerPortalSqueezeAsync(processId, cancellationToken);

    public void Start() => _runtime.Start();

    public async ValueTask DisposeAsync()
    {
        _settings.QuickAssistChanged -= _runtime.Configure;
        await _runtime.DisposeAsync().ConfigureAwait(false);
    }
}
