using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using FreiAtlas.App.Content;
using FreiAtlas.App.Runtime;
using FreiAtlas.App.Settings;
using FreiAtlas.Core.Settings;
using FreiAtlas.Core.Recovery;
using FreiAtlas.Core.PortalSqueeze;
using System.Windows.Threading;
using FreiAtlas.Settings.Bridge;
using FreiAtlas.Settings.Hotkeys;
using FreiAtlas.Settings.Windowing;
using Microsoft.Web.WebView2.Core;

namespace FreiAtlas.Settings;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private AtlasSettingsStore? _settingsStore;
    private AtlasRuntimeFacade? _runtime;
    private WebMessageRouter? _router;
    private GlobalHotkeyService? _hotkeyService;
    private SystemTrayService? _systemTray;
    private bool _hotkeyToggleInProgress;
    private bool _shutdownStarted;
    private bool _shutdownCompleted;
    private QuickAssistSession? _quickAssist;
    private PricesSession? _prices;
    private string? _lastPricesJson;
    private string? _pricesError;
    private DispatcherTimer? _quickAssistUiTimer;
    private bool _recordingRecoveryKey;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            _systemTray = new SystemTrayService(this);
            await InitializeAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"设置窗口启动失败：{exception.Message}",
                "FreiAtlas",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            _shutdownStarted = true;
            await ShutdownAndCloseAsync();
        }
    }

    private async Task InitializeAsync()
    {
        var catalog = AtlasContentCatalog.Embedded;
        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FreiAtlas",
            "settings.json");
        _settingsStore = await AtlasSettingsStore.CreateAsync(
            settingsPath,
            catalog.ContentIds);
        _router = new WebMessageRouter(catalog.ContentIds);
        _quickAssist = new QuickAssistSession(_settingsStore);
        _quickAssist.Start();
        _prices = new PricesSession(_settingsStore);
        _prices.Start();
        _runtime = new AtlasRuntimeFacade(
            new Poe2ProcessCatalog(),
            _prices.CreateOverlaySession(),
            _settingsStore);
        _runtime.SnapshotChanged += OnSnapshotChanged;
        _hotkeyService = new GlobalHotkeyService(this);
        _hotkeyService.Pressed += OnHotkeyPressed;
        _hotkeyService.PortalSqueezePressed += OnPortalSqueezePressed;
        if (!_hotkeyService.TryRegister(
                _settingsStore.CurrentHotkey,
                out var hotkeyError))
        {
            _runtime.SetHotkeyRegistrationError(hotkeyError);
        }
        if (!TryRegisterPortalHotkey(
                _settingsStore.QuickAssist,
                out var portalHotkeyError))
        {
            _portalHotkeyError = portalHotkeyError;
        }

        await _runtime.StartAsync();

        await SettingsWebView.EnsureCoreWebView2Async();
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Web");
        SettingsWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "app.freiatlas.local",
            webRoot,
            CoreWebView2HostResourceAccessKind.DenyCors);
        SettingsWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        SettingsWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        SettingsWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        SettingsWebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
        SettingsWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        SettingsWebView.Source = new Uri("https://app.freiatlas.local/index.html");
        _quickAssistUiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _quickAssistUiTimer.Tick += OnQuickAssistUiTick;
        _quickAssistUiTimer.Start();
    }

    private async void OnWebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_router is null || _runtime is null)
        {
            return;
        }

        if (!_router.TryRoute(
                e.WebMessageAsJson,
                out var command,
                out var routeError)
            || command is null)
        {
            SendSnapshot(_runtime.Current);
            SendQuickAssistSnapshot("设置未被接受：" + routeError);
            SendPricesSnapshot("设置未被接受：" + routeError);
            return;
        }

        try
        {
            switch (command)
            {
                case ReadyWebCommand:
                    SendSnapshot(_runtime.Current);
                    SendQuickAssistSnapshot();
                    SendPricesSnapshot(force: true);
                    break;
                case SelectPriceLeagueWebCommand selectLeague:
                    _prices!.SelectLeague(selectLeague.LeagueId);
                    SendPricesSnapshot(force: true);
                    break;
                case RefreshPricesWebCommand:
                    await _prices!.RefreshAsync();
                    SendPricesSnapshot(force: true);
                    break;
                case RefreshProcessesWebCommand:
                    await _runtime.RefreshProcessesAsync();
                    break;
                case SelectProcessWebCommand select:
                    await _runtime.SelectProcessAsync(select.ProcessId);
                    break;
                case StartOverlayWebCommand:
                    await _runtime.StartOverlayAsync();
                    break;
                case StopOverlayWebCommand:
                    await _runtime.StopOverlayAsync();
                    break;
                case UpdateQuickAssistWebCommand update:
                    UpdateQuickAssistSettings(update.Settings);
                    SendQuickAssistSnapshot();
                    break;
                case TriggerPortalSqueezeWebCommand:
                    await TriggerPortalSqueezeAsync();
                    break;
                case SetRecoveryKeyRecordingWebCommand recording:
                    _recordingRecoveryKey = recording.Recording;
                    break;
                case UpdateSettingsWebCommand update:
                    await _runtime.UpdateSettingsAsync(update.Settings);
                    break;
                case UpdateHotkeyWebCommand update:
                    await UpdateHotkeyAsync(update.Hotkey);
                    break;
                case ResetPageWebCommand reset:
                    await ResetPageAsync(reset.Page);
                    break;
                case ResetAllWebCommand:
                    _settingsStore!.UpdatePrices(PriceSettings.Default);
                    SendPricesSnapshot(force: true);
                    UpdateQuickAssistSettings(QuickAssistSettings.Default);
                    SendQuickAssistSnapshot();
                    await _runtime.UpdateSettingsAsync(DefaultSettings());
                    break;
            }
        }
        catch (OperationCanceledException) when (_shutdownStarted)
        {
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or ArgumentException
            or ObjectDisposedException)
        {
            if (command is SelectPriceLeagueWebCommand or RefreshPricesWebCommand)
                SendPricesSnapshot(exception.Message);
            if (command is UpdateQuickAssistWebCommand) SendQuickAssistSnapshot(exception.Message);
            if (command is UpdateHotkeyWebCommand) _runtime.SetHotkeyRegistrationError(exception.Message);
            if (_runtime is not null)
            {
                SendSnapshot(_runtime.Current);
            }
        }
    }

    private async Task ResetPageAsync(string page)
    {
        if (_runtime is null)
        {
            return;
        }

        if (page == "prices")
        {
            _settingsStore!.UpdatePrices(PriceSettings.Default);
            SendPricesSnapshot(force: true);
            return;
        }
        if (page == "quickAssist")
        {
            UpdateQuickAssistSettings(QuickAssistSettings.Default);
            SendQuickAssistSnapshot();
            return;
        }
        var defaults = DefaultSettings();
        await _runtime.UpdateSettingsAsync(current => page switch
        {
            "nodes" => current with { Nodes = defaults.Nodes },
            "contents" => current with
            {
                ContentVisibility = defaults.ContentVisibility
            },
            "atlasNavigation" => current with { Navigation = defaults.Navigation },
            "areaMap" => current with { AreaMap = defaults.AreaMap },
            "visuals" => current with
            {
                Edges = defaults.Edges,
                Highlight = defaults.Highlight,
                Labels = defaults.Labels
            },
            _ => current
        });
    }

    private async Task UpdateHotkeyAsync(AtlasHotkey hotkey)
    {
        if (_runtime is null || _hotkeyService is null)
        {
            return;
        }

        var previous = _runtime.Current.OverlayToggleHotkey;
        if (!_hotkeyService.TryRegister(hotkey, out var error))
        {
            _runtime.SetHotkeyRegistrationError(error);
            return;
        }

        try
        {
            _runtime.SetHotkeyRegistrationError(null);
            await _runtime.UpdateHotkeyAsync(hotkey);
        }
        catch
        {
            _hotkeyService.TryRegister(previous, out _);
            throw;
        }
    }

    private string? _portalHotkeyError;

    private void UpdateQuickAssistSettings(QuickAssistSettings settings)
    {
        ArgumentNullException.ThrowIfNull(_settingsStore);
        var previous = _settingsStore.QuickAssist;
        if (!TryRegisterPortalHotkey(settings, out var registrationError))
        {
            throw new ArgumentException(registrationError ?? "挤门快捷键注册失败。", nameof(settings));
        }

        try
        {
            _settingsStore.UpdateQuickAssist(settings);
            _portalHotkeyError = null;
        }
        catch
        {
            TryRegisterPortalHotkey(previous, out _);
            throw;
        }
    }

    private bool TryRegisterPortalHotkey(
        QuickAssistSettings settings,
        out string? error)
    {
        error = null;
        if (_hotkeyService is null)
        {
            return true;
        }

        if (!settings.PortalSqueeze.Enabled)
        {
            _hotkeyService.ClearPortalSqueeze();
            return true;
        }

        if (!RecoveryKey.TryParse(settings.PortalSqueeze.Hotkey, out var hotkey)
            || hotkey is null)
        {
            error = "挤门快捷键无效。";
            return false;
        }

        return _hotkeyService.TryRegisterPortalSqueeze(hotkey, out error);
    }

    private async void OnHotkeyPressed()
    {
        if (_recordingRecoveryKey)
        {
            _recordingRecoveryKey = false;
            SendQuickAssistSnapshot("该按键已用于切换覆盖层，请选择其他恢复按键。");
            return;
        }
        if (_runtime is null || _hotkeyToggleInProgress)
        {
            return;
        }

        _hotkeyToggleInProgress = true;
        try
        {
            await _runtime.ToggleOverlayAsync();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or ArgumentException
            or ObjectDisposedException)
        {
            if (_runtime is not null)
            {
                SendSnapshot(_runtime.Current);
            }
        }
        finally
        {
            _hotkeyToggleInProgress = false;
        }
    }

    private async void OnPortalSqueezePressed()
    {
        if (_recordingRecoveryKey)
        {
            _recordingRecoveryKey = false;
            SendQuickAssistSnapshot("该按键已用于挤门，请选择其他恢复按键。");
            return;
        }

        await TriggerPortalSqueezeAsync();
    }

    private async Task TriggerPortalSqueezeAsync()
    {
        if (_quickAssist is null)
        {
            return;
        }

        var processId = _quickAssist.SelectedProcessId;
        if (processId <= 0)
        {
            SendQuickAssistSnapshot("请先选择游戏进程。");
            return;
        }

        try
        {
            await _quickAssist.TriggerPortalSqueezeAsync(processId);
            SendQuickAssistSnapshot();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or ArgumentException
            or ObjectDisposedException)
        {
            SendQuickAssistSnapshot(exception.Message);
        }
    }

    private static AtlasDisplaySettings DefaultSettings()
        => AtlasDisplaySettingsValidator.Validate(
            AtlasDisplaySettings.Default,
            AtlasContentCatalog.Embedded.ContentIds);

    private void OnSnapshotChanged(AtlasSettingsAppSnapshot snapshot)
    {
        if (!_shutdownStarted) _quickAssist?.SelectProcess(snapshot.SelectedProcessId);
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => SendSnapshot(snapshot));
            return;
        }

        SendSnapshot(snapshot);
    }

    private void SendSnapshot(AtlasSettingsAppSnapshot snapshot)
    {
        if (SettingsWebView.CoreWebView2 is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(
            new
            {
                version = 1,
                type = "snapshot",
                payload = snapshot
            },
            JsonOptions);
        SettingsWebView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private void OnQuickAssistUiTick(object? sender, EventArgs e)
    {
        SendQuickAssistSnapshot();
        SendPricesSnapshot();
    }

    private void SendPricesSnapshot(string? error = null, bool force = false)
    {
        if (SettingsWebView.CoreWebView2 is null || _prices is null || _settingsStore is null) return;
        if (error is not null) _pricesError = error;
        else if (force) _pricesError = null;
        var json = JsonSerializer.Serialize(new
        {
            version = 1,
            type = "pricesSnapshot",
            payload = new { settings = _settingsStore.Prices, state = _prices.Current, error = _pricesError }
        }, JsonOptions);
        if (!force && json == _lastPricesJson) return;
        _lastPricesJson = json;
        SettingsWebView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private void SendQuickAssistSnapshot(string? error = null)
    {
        if (SettingsWebView.CoreWebView2 is null || _quickAssist is null || _settingsStore is null) return;
        SettingsWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            version = 1,
            type = "quickAssistSnapshot",
            payload = new
            {
                settings = _settingsStore.QuickAssist,
                state = new
                {
                    _quickAssist.Current.Vitals,
                    _quickAssist.Current.HealthStatus,
                    _quickAssist.Current.ManaStatus,
                    portalSqueeze = ToPortalSqueezeState(_quickAssist.PortalSqueeze)
                },
                error = error ?? _portalHotkeyError
            }
        }, JsonOptions));
    }

    private static object ToPortalSqueezeState(PortalSqueezeSnapshot snapshot)
        => new
        {
            state = snapshot.State,
            message = snapshot.Message,
            attempts = snapshot.Attempts,
            updatedAt = snapshot.UpdatedAt,
            target = snapshot.Target is { } target
                ? new
                {
                    entityId = target.EntityId,
                    metadataPath = target.MetadataPath,
                    gridPosition = new { x = target.GridPosition.X, y = target.GridPosition.Y },
                    distanceToPlayer = target.DistanceToPlayer
                }
                : null
        };

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownCompleted)
        {
            return;
        }

        e.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        _ = ShutdownAndCloseAsync();
    }

    private async Task ShutdownAndCloseAsync()
    {
        var errors = new List<Exception>();
        async Task Cleanup(Func<Task> action)
        {
            try { await action(); }
            catch (Exception exception) { errors.Add(exception); }
        }

        try
        {
            await Cleanup(() =>
            {
                _systemTray?.Dispose();
                _systemTray = null;
                return Task.CompletedTask;
            });
            if (_runtime is not null) _runtime.SnapshotChanged -= OnSnapshotChanged;
            _quickAssistUiTimer?.Stop();
            await Cleanup(async () => { if (_quickAssist is not null) await _quickAssist.DisposeAsync(); });
            await Cleanup(() =>
            {
                if (_hotkeyService is not null)
                {
                    _hotkeyService.Pressed -= OnHotkeyPressed;
                    _hotkeyService.PortalSqueezePressed -= OnPortalSqueezePressed;
                    _hotkeyService.Dispose();
                    _hotkeyService = null;
                }
                return Task.CompletedTask;
            });
            await Cleanup(async () => { if (_runtime is not null) await _runtime.StopOverlayAsync(); });
            await Cleanup(async () => { if (_runtime is not null) await _runtime.DisposeAsync(); });
            await Cleanup(async () => { if (_prices is not null) await _prices.DisposeAsync(); });
            await Cleanup(async () => { if (_settingsStore is not null) await _settingsStore.DisposeAsync(); });
            await Cleanup(() =>
            {
                if (SettingsWebView.CoreWebView2 is not null)
                    SettingsWebView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                SettingsWebView.Dispose();
                return Task.CompletedTask;
            });
            if (errors.Count > 0)
                MessageBox.Show(this, "关闭时发生错误：\n" + string.Join("\n", errors.Select(error => error.Message)),
                    "FreiAtlas", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _shutdownCompleted = true;
            Close();
        }
    }
}
