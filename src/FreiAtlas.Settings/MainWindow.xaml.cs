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
using FreiAtlas.Patcher;
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
    private PatcherFacade? _patcher;
    private string? _patcherMessage;
    private string? _patcherError;
    private PendingPricePatch? _pendingPricePatch;

    private sealed record PendingPricePatch(
        PricePatchService.PricePatchPreview Preview,
        List<PriceRow> Rows,
        string Mode,
        string Separator,
        bool KeepExistingPrice,
        string? GameDirectory,
        DateTimeOffset PriceFetchedAtUtc);

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
        try
        {
            _patcher = new PatcherFacade();
            // 备份目录受限时不再让整个设置窗口退出，仅把原因带到补丁页展示。
            _patcherError = _patcher.IsBackupAvailable ? null : _patcher.BackupUnavailableReason;
        }
        catch (Exception exception)
        {
            // 补丁模块是辅助功能，初始化失败也不应阻断设置窗口和其它模块。
            _patcher = null;
            _patcherError = $"补丁模块初始化失败：{exception.Message}";
        }
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
                    SendPatcherSnapshot();
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
                case GetPatcherStatusWebCommand:
                    SendPatcherSnapshot();
                    break;
                case ScanClientDictionaryWebCommand:
                    await HandleScanClientDictionaryAsync();
                    break;
                case ScanClientRumoursWebCommand:
                    await HandleScanClientRumoursAsync();
                    break;
                case ScanClientChestsWebCommand:
                    await HandleScanClientChestsAsync();
                    break;
                case ScanClientFontSettingsWebCommand:
                    await HandleScanClientFontSettingsAsync();
                    break;
                case ScanClientBaseItemsWebCommand:
                    await HandleScanClientBaseItemsAsync();
                    break;
                case SetPatcherGameDirWebCommand setDir:
                    HandleSetPatcherGameDir(setDir.Path);
                    break;
                case ApplyFontPatchWebCommand applyFont:
                    await HandleApplyFontPatchAsync(applyFont);
                    break;
                case ApplyUiColorPatchWebCommand applyUiColor:
                    await HandleApplyUiColorPatchAsync(applyUiColor);
                    break;
                case ApplyRumourPatchWebCommand applyRumour:
                    await HandleApplyRumourPatchAsync(applyRumour);
                    break;
                case ApplyChestPatchWebCommand applyChest:
                    await HandleApplyChestPatchAsync(applyChest);
                    break;
                case ApplyArchnemesisPatchWebCommand applyArchnemesis:
                    await HandleApplyArchnemesisPatchAsync(applyArchnemesis);
                    break;
                case ApplyColorModPatchWebCommand applyColorMod:
                    await HandleApplyColorModPatchAsync(applyColorMod);
                    break;
                case ApplyMapBeamPatchWebCommand applyMapBeam:
                    await HandleApplyMapBeamPatchAsync(applyMapBeam);
                    break;
                case ApplyPricePatchWebCommand applyPrice:
                    await HandleApplyPricePatchAsync(applyPrice);
                    break;
                case ApplyCurrentPricesPatchWebCommand applyCurrentPrices:
                    await HandleApplyCurrentPricesPatchAsync(applyCurrentPrices);
                    break;
                case PreviewCurrentPricesPatchWebCommand previewCurrentPrices:
                    await HandlePreviewCurrentPricesPatchAsync(previewCurrentPrices);
                    break;
                case ConfirmCurrentPricesPatchWebCommand confirmCurrentPrices:
                    await HandleConfirmCurrentPricesPatchAsync(confirmCurrentPrices);
                    break;
                case CleanPricePatchWebCommand cleanPrice:
                    await HandleCleanPricePatchAsync(cleanPrice);
                    break;
                case RestoreAllPatchesWebCommand:
                    await HandleRestoreAllPatchesAsync();
                    break;
                case GetPatcherSnapshotsWebCommand:
                    SendPatcherSnapshot();
                    break;
                case CreatePatcherSnapshotWebCommand createSnap:
                    await HandleCreatePatcherSnapshotAsync(createSnap);
                    break;
                case RestorePatcherSnapshotWebCommand restoreSnap:
                    await HandleRestorePatcherSnapshotAsync(restoreSnap);
                    break;
                case DeletePatcherSnapshotWebCommand deleteSnap:
                    await HandleDeletePatcherSnapshotAsync(deleteSnap);
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
                    _settingsStore!.UpdateClientPatch(ClientPatchSettings.Default);
                    SendPatcherSnapshot();
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
            if (command is SetPatcherGameDirWebCommand or ApplyFontPatchWebCommand or ApplyUiColorPatchWebCommand or ApplyRumourPatchWebCommand or ApplyChestPatchWebCommand or ApplyArchnemesisPatchWebCommand or ApplyColorModPatchWebCommand or RestoreAllPatchesWebCommand or CreatePatcherSnapshotWebCommand or RestorePatcherSnapshotWebCommand or DeletePatcherSnapshotWebCommand)
                SendPatcherSnapshot(error: exception.Message);
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
        if (page == "clientPatch")
        {
            _settingsStore!.UpdateClientPatch(ClientPatchSettings.Default);
            SendPatcherSnapshot(message: "客户端补丁设置已恢复默认。若需恢复纯净游戏文件，请点击【一键恢复纯净原版】。");
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
            payload = new
            {
                settings = _settingsStore.Prices,
                state = _prices.Current,
                items = _prices.CurrentBook.Entries.Select(entry => new
                {
                    itemId = entry.ItemId,
                    name = entry.DisplayName,
                    aliases = entry.Aliases,
                    category = entry.Category,
                    source = entry.Source,
                    divineValue = entry.DivineValue,
                    exaltedValue = decimal.Round(
                        entry.DivineValue * (_prices.CurrentBook.ExaltedPerDivine > 0m
                            ? _prices.CurrentBook.ExaltedPerDivine
                            : 400m),
                        4)
                }).ToArray(),
                error = _pricesError
            }
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
                    portalSqueeze = ToPortalSqueezeState(_quickAssist.PortalSqueeze),
                    pickup = new
                    {
                        message = _quickAssist.Pickup.Message,
                        eligibleTarget = _quickAssist.Pickup.EligibleTarget,
                        entityId = _quickAssist.Pickup.EntityId,
                        itemName = _quickAssist.Pickup.ItemName,
                        distanceToPlayer = _quickAssist.Pickup.DistanceToPlayer,
                        updatedAt = _quickAssist.Pickup.UpdatedAt
                    }
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

    private void HandleSetPatcherGameDir(string path)
    {
        if (_settingsStore is null) return;
        var dir = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with { CustomGameDirectory = dir });
        SendPatcherSnapshot(message: "游戏目录已更新");
    }

    private async Task HandleScanClientDictionaryAsync()
    {
        if (_patcher is null || _settingsStore is null) return;
        SendPatcherSnapshot(message: "正在扫描客户端词库，请稍候...");
        string? log = null;
        try
        {
            var list = await Task.Run(() => _patcher.ScanClientDictionary(
                _settingsStore.ClientPatch.CustomGameDirectory,
                msg =>
                {
                    log = msg;
                    Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
                }));
            SendPatcherSnapshot(
                message: $"词库同步完成！已成功提取 {list.Count} 条真实词缀。",
                scannedEntries: list);
        }
        catch (Exception ex)
        {
            SendPatcherSnapshot(error: "扫描词库失败：" + ex.Message);
        }
    }

    private async Task HandleApplyFontPatchAsync(ApplyFontPatchWebCommand cmd)
    {
        if (_patcher is null || _settingsStore is null) return;
        if (PatcherFacade.IsGameRunning())
        {
            SendPatcherSnapshot(error: "检测到游戏正在运行中，游戏占用了资源文件。请先完全关闭游戏客户端，然后再应用补丁。");
            return;
        }

        _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
        {
            FontFamily = cmd.FontFamily,
            FontSizeDelta = cmd.FontSizeDelta,
            FontBold = cmd.IsBold,
            FontPresetId = cmd.PresetId,
            FontDropItemSizeDelta = cmd.DropItemSizeDelta,
            FontGeneralTextSizeDelta = cmd.GeneralTextSizeDelta,
            FontTitleSizeDelta = cmd.TitleSizeDelta,
            FontTinyBadgeSizeDelta = cmd.TinyBadgeSizeDelta,
            FontCustomPresets = cmd.CustomPresets ?? _settingsStore.ClientPatch.FontCustomPresets
        });

        SendPatcherSnapshot(message: "正在准备应用字体补丁，请稍候...");
        string? progressLog = null;
        RecordAutoSnapshot("字体补丁写入");
        bool success = await Task.Run(() => _patcher.ApplyFont(
            cmd.FontFamily,
            cmd.FontSizeDelta,
            _settingsStore.ClientPatch.CustomGameDirectory,
            cmd.ApplyPresetScales,
            cmd.IsBold,
            cmd.DropItemSizeDelta,
            cmd.GeneralTextSizeDelta,
            cmd.TitleSizeDelta,
            cmd.TinyBadgeSizeDelta,
            msg =>
            {
                progressLog = msg;
                Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
            }));

        if (success)
        {
            SendPatcherSnapshot(message: progressLog ?? "字体补丁已成功应用！");
        }
        else
        {
            SendPatcherSnapshot(error: progressLog ?? "字体补丁应用失败。");
        }
    }

    private async Task HandleApplyUiColorPatchAsync(ApplyUiColorPatchWebCommand cmd)
    {
        if (_patcher is null || _settingsStore is null) return;
        if (PatcherFacade.IsGameRunning())
        {
            SendPatcherSnapshot(error: "检测到游戏正在运行中，游戏占用了资源文件。请先完全关闭游戏客户端，然后再应用补丁。");
            return;
        }

        _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
        {
            CustomColors = cmd.Colors
        });

        var entries = cmd.Colors.Select(c =>
        {
            var entry = new UiColorEntry
            {
                ColourKey = c.Key,
                DisplayName = c.DisplayName,
                Enabled = c.Enabled
            };
            entry.SetFromColorString(c.Value);
            return entry;
        });

        SendPatcherSnapshot(message: "正在准备应用 UI 配色补丁，请稍候...");
        string? progressLog = null;
        RecordAutoSnapshot("UI配色写入");
        bool success = await Task.Run(() => _patcher.ApplyUiColors(
            entries,
            _settingsStore.ClientPatch.CustomGameDirectory,
            msg =>
            {
                progressLog = msg;
                Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
            }));

        if (success)
        {
            SendPatcherSnapshot(message: progressLog ?? "UI 颜色补丁已成功应用！");
        }
        else
        {
            SendPatcherSnapshot(error: progressLog ?? "UI 颜色补丁应用失败。");
        }
    }

    private async Task HandleApplyRumourPatchAsync(ApplyRumourPatchWebCommand cmd)
    {
        if (_patcher is null || _settingsStore is null) return;
        if (PatcherFacade.IsGameRunning())
        {
            SendPatcherSnapshot(error: "检测到游戏正在运行中，游戏占用了资源文件。请先完全关闭游戏客户端，然后再应用补丁。");
            return;
        }

        _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
        {
            RumourEnhanced = cmd.Enabled,
            RumourItems = cmd.Items
        });

        if (cmd.Enabled)
        {
            SendPatcherSnapshot(message: "正在准备应用传言增强补丁，请稍候...");
            string? progressLog = null;
            RecordAutoSnapshot("传言强化写入");
            var customItems = cmd.Items?.Select(r => new EndgameMapRumourItem
            {
                RowIndex = r.RowIndex,
                MapName = r.MapName,
                OriginalRumourText = r.OriginalText,
                RumourText = r.ModifiedText,
                Category = r.Category,
                Color = r.Color,
                Enabled = r.Enabled
            }).ToList();

            bool success = await Task.Run(() => _patcher.ApplyRumours(
                _settingsStore.ClientPatch.CustomGameDirectory,
                customItems: customItems,
                progress: msg =>
                {
                    progressLog = msg;
                    Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
                }));

            if (success)
            {
                SendPatcherSnapshot(message: progressLog ?? "异界传言增强补丁已成功应用！");
            }
            else
            {
                SendPatcherSnapshot(error: progressLog ?? "异界传言增强补丁应用失败。");
            }
        }
        else
        {
            SendPatcherSnapshot(message: "已保存传言设置。注意：若需彻底恢复原版传言文本，请点击下方【一键恢复纯净原版】。");
        }
    }

    private async Task HandleApplyChestPatchAsync(ApplyChestPatchWebCommand cmd)
    {
        if (_patcher is null || _settingsStore is null) return;
        if (PatcherFacade.IsGameRunning())
        {
            SendPatcherSnapshot(error: "检测到游戏正在运行中，游戏占用了资源文件。请先完全关闭游戏客户端，然后再应用补丁。");
            return;
        }

        _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
        {
            ChestEnhanced = cmd.Enabled,
            ChestItems = cmd.Items
        });

        if (cmd.Enabled)
        {
            SendPatcherSnapshot(message: "正在准备应用保险箱掉落标注补丁，请稍候...");
            string? progressLog = null;
            RecordAutoSnapshot("宝箱标注写入");
            Dictionary<string, string>? customMappings = cmd.Items?
                .Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.OriginalName))
                .ToDictionary(c => c.OriginalName, c => c.ModifiedName);

            bool success = await Task.Run(() => _patcher.ApplyNamePatch(
                NamePatchKind.Chests,
                _settingsStore.ClientPatch.CustomGameDirectory,
                customMappings: customMappings,
                progress: msg =>
                {
                    progressLog = msg;
                    Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
                }));

            if (success)
            {
                SendPatcherSnapshot(message: progressLog ?? "保险箱掉落类型标注补丁已成功应用！");
            }
            else
            {
                SendPatcherSnapshot(error: progressLog ?? "保险箱标注补丁应用失败。");
            }
        }
        else
        {
            SendPatcherSnapshot(message: "已保存保险箱设置。注意：若需彻底恢复原版文本，请点击下方【一键恢复纯净原版】。");
        }
    }

    private async Task HandleApplyArchnemesisPatchAsync(ApplyArchnemesisPatchWebCommand cmd)
    {
        if (_patcher is null || _settingsStore is null) return;
        if (PatcherFacade.IsGameRunning())
        {
            SendPatcherSnapshot(error: "检测到游戏正在运行中，游戏占用了资源文件。请先完全关闭游戏客户端，然后再应用补丁。");
            return;
        }

        _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
        {
            ArchnemesisEnhanced = cmd.Enabled
        });

        if (cmd.Enabled)
        {
            SendPatcherSnapshot(message: "正在准备应用高价值宿敌怪高亮补丁，请稍候...");
            string? progressLog = null;
            RecordAutoSnapshot("特殊名称补丁写入");
            bool success = await Task.Run(() => _patcher.ApplyNamePatch(
                NamePatchKind.ArchnemesisMods,
                _settingsStore.ClientPatch.CustomGameDirectory,
                customMappings: null,
                progress: msg =>
                {
                    progressLog = msg;
                    Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
                }));

            if (success)
            {
                SendPatcherSnapshot(message: progressLog ?? "宿敌高价值怪物词缀高亮补丁已成功应用！");
            }
            else
            {
                SendPatcherSnapshot(error: progressLog ?? "宿敌词缀高亮补丁应用失败。");
            }
        }
        else
        {
            SendPatcherSnapshot(message: "已保存宿敌高亮设置。注意：若需彻底恢复原版文本，请点击下方【一键恢复纯净原版】。");
        }
    }

    private async Task HandleApplyColorModPatchAsync(ApplyColorModPatchWebCommand cmd)
    {
        if (_patcher is null || _settingsStore is null) return;
        if (PatcherFacade.IsGameRunning())
        {
            SendPatcherSnapshot(error: "检测到游戏正在运行中，游戏占用了资源文件。请先完全关闭游戏客户端，然后再应用补丁。");
            return;
        }

        var styleEnum = Enum.TryParse<ColorModStyle>(cmd.Style, ignoreCase: true, out var s) ? s : ColorModStyle.Whole;

        _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
        {
            ColorModEnabled = cmd.Enabled,
            ColorModStyle = cmd.Style,
            ColorModRules = cmd.Rules
        });

        if (cmd.Enabled)
        {
            SendPatcherSnapshot(message: "正在准备应用装备与地图词缀多色彩染色补丁，请稍候...");
            string? progressLog = null;
            RecordAutoSnapshot("词缀变色写入");
            var entries = cmd.Rules?.Select(ColorModEntry.FromRuleItem).ToList();

            bool success = await Task.Run(() => _patcher.ApplyColorMods(
                styleEnum,
                _settingsStore.ClientPatch.CustomGameDirectory,
                entries: entries,
                progress: msg =>
                {
                    progressLog = msg;
                    Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
                }));

            if (success)
            {
                SendPatcherSnapshot(message: progressLog ?? "词缀变色补丁已成功应用！");
            }
            else
            {
                SendPatcherSnapshot(error: progressLog ?? "词缀变色补丁应用失败。");
            }
        }
        else
        {
            SendPatcherSnapshot(message: "已保存词缀变色设置。注意：若需彻底恢复原版词缀显示，请点击下方【一键恢复纯净原版】。");
        }
    }

    private async Task HandleApplyMapBeamPatchAsync(ApplyMapBeamPatchWebCommand cmd)
    {
        if (_patcher is null || _settingsStore is null) return;
        if (PatcherFacade.IsGameRunning())
        {
            SendPatcherSnapshot(error: "检测到游戏正在运行中，游戏占用了资源文件。请先完全关闭游戏客户端，然后再应用补丁。");
            return;
        }

        _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
        {
            MapBeamEnabled = cmd.Enabled,
            MapBeamTargets = cmd.Targets
        });

        if (cmd.Enabled)
        {
            SendPatcherSnapshot(message: "正在准备生成异界神图醒目光柱，请稍候...");
            string? progressLog = null;
            RecordAutoSnapshot("神图光柱写入");
            var targets = cmd.Targets?.Select(t => new MapBeamTarget(t.MapName, t.DisplayName, t.Color, t.Enabled)).ToList();

            bool success = await Task.Run(() => _patcher.ApplyMapBeams(
                targets: targets,
                preferredDirectory: _settingsStore.ClientPatch.CustomGameDirectory,
                progress: msg =>
                {
                    progressLog = msg;
                    Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
                }));

            if (success)
            {
                SendPatcherSnapshot(message: progressLog ?? "神图醒目光柱补丁已成功应用！");
            }
            else
            {
                SendPatcherSnapshot(error: progressLog ?? "神图光柱补丁应用失败。");
            }
        }
        else
        {
            SendPatcherSnapshot(message: "已保存神图光柱设置。注意：若需彻底恢复原版地图光效，请点击下方【一键恢复纯净原版】。");
        }
    }

    private async Task HandleApplyCurrentPricesPatchAsync(ApplyCurrentPricesPatchWebCommand cmd)
    {
        if (_patcher is null || _settingsStore is null) return;
        if (PatcherFacade.IsGameRunning())
        {
            SendPatcherSnapshot(error: "检测到游戏正在运行中，游戏占用了资源文件。请先完全关闭游戏客户端，然后再应用补丁。");
            return;
        }

        decimal minExalted = cmd.MinExalted > 0m ? cmd.MinExalted : 1m;
        string unitMode = string.IsNullOrWhiteSpace(cmd.UnitMode) ? "auto" : cmd.UnitMode.Trim().ToLowerInvariant();
        bool stripExisting = cmd.StripExisting;

        _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
        {
            PricePatchEnabled = true,
            PricePatchMode = cmd.Mode,
            PricePatchSeparator = cmd.Separator,
            PricePatchMinExalted = minExalted,
            PricePatchUnitMode = unitMode,
            PricePatchStripExisting = stripExisting,
            PricePatchCategories = cmd.Categories
        });

        // 直接注入会绕过预览计划，作废旧预览，避免之后误确认旧结果。
        _pendingPricePatch = null;

        var (priceRows, _, error) = BuildCurrentPriceRows(minExalted, unitMode, cmd.Categories);
        if (error is not null)
        {
            SendPatcherSnapshot(error: error);
            return;
        }

        RecordAutoSnapshot("物价补丁写入");

        SendPatcherSnapshot(message: $"已生成 {priceRows.Count} 条换算物价，正在读取客户端并注入地面掉落名称...");

        string? progressLog = null;
        var res = await Task.Run(() => _patcher.ApplyPricePatch(
            priceRows,
            cmd.Mode,
            cmd.Separator,
            _settingsStore.ClientPatch.CustomGameDirectory,
            msg =>
            {
                progressLog = msg;
                Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
            },
            keepExistingPrice: !stripExisting));

        if (res.Success)
        {
            SendPatcherSnapshot(message: progressLog ?? $"实时物价成功注入客户端！（已注入 {res.PatchedNames} 个物品）");
        }
        else
        {
            SendPatcherSnapshot(error: res.ErrorMessage.Length > 0 ? res.ErrorMessage : "实时物价注入客户端失败。");
        }
    }

    private (List<PriceRow> Rows, DateTimeOffset FetchedAtUtc, string? Error) BuildCurrentPriceRows(
        decimal minExalted,
        string unitMode,
        IReadOnlyList<string>? categories)
    {
        var book = _prices?.CurrentBook;
        if (book == null || book.ItemCount == 0)
        {
            return ([], default, "当前尚未拉取到物价快照，请先在【物价设置】页面点击刷新物价或稍后重试。");
        }

        var rows = PriceAnnotationBuilder.BuildRows(
            book.Entries,
            book.ExaltedPerDivine,
            minExalted,
            unitMode,
            categories);
        return rows.Count == 0
            ? ([], book.FetchedAtUtc, $"未筛选出达到 {minExalted:0.##}E 的物品物价，无需注入。")
            : (rows, book.FetchedAtUtc, null);
    }

    private async Task HandlePreviewCurrentPricesPatchAsync(PreviewCurrentPricesPatchWebCommand cmd)
    {
        if (_patcher is null || _settingsStore is null) return;
        if (PatcherFacade.IsGameRunning())
        {
            SendPatcherSnapshot(error: "检测到游戏正在运行中，游戏占用了资源文件。请先完全关闭游戏客户端，然后再预览。");
            return;
        }

        decimal minExalted = cmd.MinExalted > 0m ? cmd.MinExalted : 1m;
        string unitMode = string.IsNullOrWhiteSpace(cmd.UnitMode) ? "auto" : cmd.UnitMode.Trim().ToLowerInvariant();
        bool stripExisting = cmd.StripExisting;

        _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
        {
            PricePatchEnabled = true,
            PricePatchMode = cmd.Mode,
            PricePatchSeparator = cmd.Separator,
            PricePatchMinExalted = minExalted,
            PricePatchUnitMode = unitMode,
            PricePatchStripExisting = stripExisting,
            PricePatchCategories = cmd.Categories
        });

        var (rows, fetchedAtUtc, error) = BuildCurrentPriceRows(minExalted, unitMode, cmd.Categories);
        if (error is not null)
        {
            _pendingPricePatch = null;
            SendPatcherSnapshot(error: error);
            return;
        }

        SendPatcherSnapshot(message: "正在读取客户端实际数据表并匹配物价...");
        string? progressLog = null;
        var preview = await Task.Run(() => _patcher.PreviewPricePatch(
            rows,
            cmd.Mode,
            cmd.Separator,
            _settingsStore.ClientPatch.CustomGameDirectory,
            msg =>
            {
                progressLog = msg;
                Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
            },
            keepExistingPrice: !stripExisting));

        if (!preview.Success)
        {
            _pendingPricePatch = null;
            SendPatcherSnapshot(error: string.IsNullOrWhiteSpace(preview.ErrorMessage)
                ? "读取客户端并预览失败。"
                : preview.ErrorMessage);
            return;
        }

        _pendingPricePatch = new PendingPricePatch(
            preview,
            rows,
            cmd.Mode,
            cmd.Separator,
            !stripExisting,
            _settingsStore.ClientPatch.CustomGameDirectory,
            fetchedAtUtc);

        SendPatcherSnapshot(
            message: progressLog ?? $"预览完成：客户端可匹配 {preview.MatchedCount} 个物品，请确认后再注入。",
            pricePreview: preview);
    }

    private async Task HandleConfirmCurrentPricesPatchAsync(ConfirmCurrentPricesPatchWebCommand cmd)
    {
        if (_patcher is null || _settingsStore is null) return;
        var pending = _pendingPricePatch;
        if (pending is null || !string.Equals(pending.Preview.Token, cmd.Token, StringComparison.Ordinal))
        {
            SendPatcherSnapshot(error: "预览已失效，请重新点击预览后再确认注入。");
            return;
        }

        if (DateTimeOffset.UtcNow - pending.Preview.CreatedAtUtc > TimeSpan.FromMinutes(10))
        {
            _pendingPricePatch = null;
            SendPatcherSnapshot(error: "预览已超过 10 分钟，请重新预览。");
            return;
        }

        if (PatcherFacade.IsGameRunning())
        {
            SendPatcherSnapshot(error: "检测到游戏正在运行中，请先完全关闭游戏客户端后再确认注入。");
            return;
        }

        var book = _prices?.CurrentBook;
        if (book is null || book.FetchedAtUtc != pending.PriceFetchedAtUtc)
        {
            _pendingPricePatch = null;
            SendPatcherSnapshot(error: "物价数据已刷新，预览结果已失效，请重新预览。");
            return;
        }

        SendPatcherSnapshot(message: "正在复核客户端文件与预览结果...");
        var recheck = await Task.Run(() => _patcher.PreviewPricePatch(
            pending.Rows,
            pending.Mode,
            pending.Separator,
            pending.GameDirectory,
            progress: null,
            keepExistingPrice: pending.KeepExistingPrice));

        if (!recheck.Success
            || !string.Equals(recheck.ClientSha256, pending.Preview.ClientSha256, StringComparison.Ordinal)
            || !string.Equals(recheck.MetadataSignature, pending.Preview.MetadataSignature, StringComparison.Ordinal)
            || !string.Equals(recheck.PriceRowsHash, pending.Preview.PriceRowsHash, StringComparison.Ordinal)
            || recheck.MatchedCount != pending.Preview.MatchedCount)
        {
            _pendingPricePatch = null;
            SendPatcherSnapshot(error: "客户端文件或价格数据已变化，预览结果已失效，请重新预览。");
            return;
        }

        RecordAutoSnapshot("物价补丁写入");

        string? progressLog = null;
        var result = await Task.Run(() => _patcher.ApplyPricePatch(
            pending.Rows,
            pending.Mode,
            pending.Separator,
            pending.GameDirectory,
            msg =>
            {
                progressLog = msg;
                Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
            },
            keepExistingPrice: pending.KeepExistingPrice));

        _pendingPricePatch = null;
        if (result.Success)
        {
            _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with { PricePatchEnabled = true });
            SendPatcherSnapshot(message: progressLog ?? $"已确认注入 {result.PatchedNames} 个客户端物品物价。");
        }
        else
        {
            SendPatcherSnapshot(error: string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? "确认注入失败。"
                : result.ErrorMessage);
        }
    }

    private async Task HandleApplyPricePatchAsync(ApplyPricePatchWebCommand cmd)
    {
        if (_patcher is null || _settingsStore is null) return;
        if (PatcherFacade.IsGameRunning())
        {
            SendPatcherSnapshot(error: "检测到游戏正在运行中，游戏占用了资源文件。请先完全关闭游戏客户端，然后再应用补丁。");
            return;
        }

        _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
        {
            PricePatchEnabled = true,
            PricePatchMode = cmd.Mode,
            PricePatchSeparator = cmd.Separator,
            PricePatchCsvPath = cmd.CsvPath
        });

        SendPatcherSnapshot(message: "正在解析物价数据并准备注入客户端...");

        List<PriceRow> priceRows = [];
        if (!string.IsNullOrWhiteSpace(cmd.CsvPath) && File.Exists(cmd.CsvPath))
        {
            priceRows = PricePatchService.LoadPriceRowsFromCsv(cmd.CsvPath);
        }

        if (priceRows.Count == 0)
        {
            // 自动回退到实时物价
            await HandleApplyCurrentPricesPatchAsync(new ApplyCurrentPricesPatchWebCommand(cmd.Mode, cmd.Separator));
            return;
        }

        RecordAutoSnapshot("物价补丁写入");

        string? progressLog = null;
        var res = await Task.Run(() => _patcher.ApplyPricePatch(
            priceRows,
            cmd.Mode,
            cmd.Separator,
            _settingsStore.ClientPatch.CustomGameDirectory,
            msg =>
            {
                progressLog = msg;
                Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
            }));

        if (res.Success)
        {
            _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with { PricePatchEnabled = true });
            SendPatcherSnapshot(message: progressLog ?? $"客户端原生掉落物价注入成功！（已注入 {res.PatchedNames} 个物品）");
        }
        else
        {
            SendPatcherSnapshot(error: res.ErrorMessage.Length > 0 ? res.ErrorMessage : "客户端物价注入失败。");
        }
    }

    private async Task HandleCleanPricePatchAsync(CleanPricePatchWebCommand cmd)
    {
        if (_patcher is null || _settingsStore is null) return;
        if (PatcherFacade.IsGameRunning())
        {
            SendPatcherSnapshot(error: "检测到游戏正在运行中，游戏占用了资源文件。请先完全关闭游戏客户端，然后再执行清洗。");
            return;
        }

        RecordAutoSnapshot("物价清洗");

        SendPatcherSnapshot(message: "正在清洗数据表中的物价后缀...");
        string? progressLog = null;
        var res = await Task.Run(() => _patcher.CleanPricePatch(
            cmd.Separator,
            _settingsStore.ClientPatch.CustomGameDirectory,
            msg =>
            {
                progressLog = msg;
                Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
            }));

        if (res.Success)
        {
            _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with { PricePatchEnabled = false });
            SendPatcherSnapshot(message: progressLog ?? $"物价后缀清洗成功！（已清洗 {res.PatchedNames} 个物品）");
        }
        else
        {
            SendPatcherSnapshot(error: res.ErrorMessage.Length > 0 ? res.ErrorMessage : "清洗物价后缀失败。");
        }
    }

    private async Task HandleRestoreAllPatchesAsync()
    {
        if (_patcher is null || _settingsStore is null) return;

        SendPatcherSnapshot(message: "正在准备恢复纯净客户端，请稍候...");
        string? progressLog = null;
        var result = await Task.Run(() => _patcher.RestoreAllDetailed(
            _settingsStore.ClientPatch.CustomGameDirectory,
            msg =>
            {
                progressLog = msg;
                Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
            }));

        if (result.Success)
        {
            // 还原成功后，将后端的全部补丁启用开关重置为关闭状态
            _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
            {
                ColorModEnabled = false,
                MapBeamEnabled = false,
                ChestEnhanced = false,
                RumourEnhanced = false,
                PricePatchEnabled = false
            });

            SendPatcherSnapshot(message: result.Message);
        }
        else
        {
            SendPatcherSnapshot(error: result.Message);
        }
    }

    private BackupRestoreResult? RecordAutoSnapshot(string? patchActionName = null)
    {
        if (_patcher is null || _settingsStore is null) return null;
        var info = _patcher.GetGameInfo(_settingsStore.ClientPatch.CustomGameDirectory);
        if (!info.IsValid) return null;

        var patch = _settingsStore.ClientPatch;
        var activeModules = new List<string>();
        if (patch.ColorModEnabled) activeModules.Add("词缀变色");
        if (patch.MapBeamEnabled) activeModules.Add("神图光柱");
        if (patch.ChestEnhanced) activeModules.Add("宝箱标注");
        if (patch.RumourEnhanced) activeModules.Add("传言强化");
        if (patch.PricePatchEnabled) activeModules.Add("物价标签");
        if (!string.IsNullOrWhiteSpace(patch.FontFamily)) activeModules.Add("自定义字体");

        string settingsJson = JsonSerializer.Serialize(patch, JsonOptions);
        return _patcher.AutoSnapshotBeforePatch(info, patchActionName, settingsJson, activeModules);
    }

    private async Task HandleCreatePatcherSnapshotAsync(CreatePatcherSnapshotWebCommand cmd)
    {
        if (_patcher is null || _settingsStore is null) return;
        if (PatcherFacade.IsGameRunning())
        {
            SendPatcherSnapshot(error: "检测到游戏正在运行中，游戏占用了资源文件。请先退出游戏后再执行备份。");
            return;
        }

        var patch = _settingsStore.ClientPatch;
        var activeModules = new List<string>();
        if (patch.ColorModEnabled) activeModules.Add("词缀变色");
        if (patch.MapBeamEnabled) activeModules.Add("神图光柱");
        if (patch.ChestEnhanced) activeModules.Add("宝箱标注");
        if (patch.RumourEnhanced) activeModules.Add("传言强化");
        if (patch.PricePatchEnabled) activeModules.Add("物价标签");
        if (!string.IsNullOrWhiteSpace(patch.FontFamily)) activeModules.Add("自定义字体");

        string settingsJson = JsonSerializer.Serialize(patch, JsonOptions);
        string name = string.IsNullOrWhiteSpace(cmd.Name) ? $"手动备份 ({DateTime.Now:MM-dd HH:mm})" : cmd.Name.Trim();
        string desc = string.IsNullOrWhiteSpace(cmd.Description) ? "由用户手动创建的版本快照" : cmd.Description.Trim();

        var result = await Task.Run(() => _patcher.CreateSnapshot(
            PatchSnapshotType.Manual,
            name,
            desc,
            settingsJson,
            activeModules,
            _settingsStore.ClientPatch.CustomGameDirectory));

        if (result.Success)
        {
            SendPatcherSnapshot(message: $"版本快照「{name}」已成功创建并保存！");
        }
        else
        {
            SendPatcherSnapshot(error: $"创建快照失败：{result.Message}");
        }
    }

    private async Task HandleRestorePatcherSnapshotAsync(RestorePatcherSnapshotWebCommand cmd)
    {
        if (_patcher is null || _settingsStore is null) return;
        if (PatcherFacade.IsGameRunning())
        {
            SendPatcherSnapshot(error: "检测到游戏正在运行中，游戏占用了资源文件。请先退出游戏后再执行还原。");
            return;
        }

        SendPatcherSnapshot(message: "正在还原选定的版本快照，请稍候...");
        string? progressLog = null;
        string? restoredSettingsJson = null;

        var result = await Task.Run(() => _patcher.RestoreSnapshot(
            cmd.SnapshotId,
            out restoredSettingsJson,
            _settingsStore.ClientPatch.CustomGameDirectory,
            msg =>
            {
                progressLog = msg;
                Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
            }));

        if (result.Success)
        {
            // 同步回滚配置（双向对齐）
            if (!string.IsNullOrWhiteSpace(restoredSettingsJson))
            {
                try
                {
                    var restored = JsonSerializer.Deserialize<ClientPatchSettings>(restoredSettingsJson, JsonOptions);
                    if (restored is not null)
                    {
                        string? currentDir = _settingsStore.ClientPatch.CustomGameDirectory;
                        _settingsStore.UpdateClientPatch(restored with { CustomGameDirectory = currentDir });
                    }
                }
                catch { }
            }
            else if (cmd.SnapshotId == PatchBackupManager.OfficialSnapshotId)
            {
                _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
                {
                    ColorModEnabled = false,
                    MapBeamEnabled = false,
                    ChestEnhanced = false,
                    RumourEnhanced = false,
                    PricePatchEnabled = false
                });
            }

            SendPatcherSnapshot(message: progressLog ?? result.Message);
        }
        else
        {
            SendPatcherSnapshot(error: result.Message);
        }
    }

    private async Task HandleDeletePatcherSnapshotAsync(DeletePatcherSnapshotWebCommand cmd)
    {
        if (_patcher is null || _settingsStore is null) return;
        string msg = "";
        bool success = await Task.Run(() => _patcher.DeleteSnapshot(
            cmd.SnapshotId,
            out msg,
            _settingsStore.ClientPatch.CustomGameDirectory));

        if (success)
        {
            SendPatcherSnapshot(message: msg);
        }
        else
        {
            SendPatcherSnapshot(error: msg);
        }
    }

    private IReadOnlyList<FontItem>? _cachedInstalledFonts;

    private async Task HandleScanClientRumoursAsync()
    {
        if (_patcher is null || _settingsStore is null) return;
        SendPatcherSnapshot(message: "正在读取客户端地图传言数据（GGPK/Bundle）...");
        try
        {
            var rumours = await Task.Run(() => _patcher.ScanRumours(_settingsStore.ClientPatch.CustomGameDirectory));
            SendPatcherSnapshot(message: $"客户端传言读取完成！已成功识别 {rumours.Count} 条真实传言。", clientRumours: rumours);
        }
        catch (Exception ex)
        {
            SendPatcherSnapshot(error: "读取客户端传言失败：" + ex.Message);
        }
    }

    private async Task HandleScanClientChestsAsync()
    {
        if (_patcher is null || _settingsStore is null) return;
        SendPatcherSnapshot(message: "正在读取客户端宝箱数据（chests.datc64）...");
        try
        {
            var chests = await Task.Run(() => _patcher.ScanChests(_settingsStore.ClientPatch.CustomGameDirectory));
            SendPatcherSnapshot(message: $"客户端宝箱读取完成！已基于语义规则识别 {chests.Count} 类宝箱。", clientChests: chests);
        }
        catch (Exception ex)
        {
            SendPatcherSnapshot(error: "读取客户端宝箱失败：" + ex.Message);
        }
    }

    private async Task HandleScanClientFontSettingsAsync()
    {
        if (_patcher is null || _settingsStore is null) return;
        SendPatcherSnapshot(message: "正在读取客户端实际字体模板（uisettings*.xml）...");
        try
        {
            var fontSettings = await Task.Run(() => _patcher.ScanFontSettings(_settingsStore.ClientPatch.CustomGameDirectory));
            SendPatcherSnapshot(message: fontSettings != null ? $"已成功读取客户端生效字体配置：{Path.GetFileName(fontSettings.FilePath)}" : "未在客户端中找到生效的字体配置", fontSettings: fontSettings);
        }
        catch (Exception ex)
        {
            SendPatcherSnapshot(error: "读取客户端字体配置失败：" + ex.Message);
        }
    }

    private async Task HandleScanClientBaseItemsAsync()
    {
        if (_patcher is null || _settingsStore is null) return;
        SendPatcherSnapshot(message: "正在读取客户端基础物品数据表（baseitemtypes.datc64）...");
        try
        {
            var items = await Task.Run(() => _patcher.ScanBaseItems(_settingsStore.ClientPatch.CustomGameDirectory));
            SendPatcherSnapshot(message: $"已读取客户端 {items.Count} 个基础物品条目。", clientBaseItems: items);
        }
        catch (Exception ex)
        {
            SendPatcherSnapshot(error: "读取基础物品数据表失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 补丁模块整体不可用时，仍向补丁页推送一份最小快照，让前端展示失败原因而不是空白。
    /// </summary>
    private void SendPatcherUnavailableSnapshot(string? error)
    {
        if (SettingsWebView.CoreWebView2 is null || _settingsStore is null) return;

        string reason = error ?? "补丁模块不可用";
        var json = JsonSerializer.Serialize(new
        {
            version = 1,
            type = "patcherSnapshot",
            payload = new
            {
                gameDir = _settingsStore.ClientPatch.CustomGameDirectory ?? string.Empty,
                gameMode = "未知",
                isValid = false,
                errorMessage = reason,
                isGameRunning = PatcherFacade.IsGameRunning(),
                hasBackup = false,
                snapshots = Array.Empty<PatchSnapshotInfo>(),
                settings = _settingsStore.ClientPatch,
                builtinColors = UiColorService.BuiltinEntries,
                installedFonts = Array.Empty<FontItem>(),
                clientRumours = (IReadOnlyList<EndgameMapRumourItem>?)null,
                clientChests = (IReadOnlyList<NamePatchItem>?)null,
                fontSettings = (FontSettingsInfo?)null,
                clientBaseItems = (IReadOnlyList<PricePatchService.BaseItemName>?)null,
                defaultRumours = Array.Empty<string>(),
                defaultChests = Array.Empty<string>(),
                defaultMapBeams = Array.Empty<object>(),
                initialColorMods = ColorModService.GetBuiltinEntries(),
                scannedEntries = (IReadOnlyList<ColorModEntry>?)null,
                message = (string?)null,
                error = reason
            }
        }, JsonOptions);

        SettingsWebView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private void SendPatcherSnapshot(
        string? message = null,
        string? error = null,
        IReadOnlyList<ColorModEntry>? scannedEntries = null,
        IReadOnlyList<EndgameMapRumourItem>? clientRumours = null,
        IReadOnlyList<NamePatchItem>? clientChests = null,
        FontSettingsInfo? fontSettings = null,
        IReadOnlyList<PricePatchService.BaseItemName>? clientBaseItems = null,
        PricePatchService.PricePatchPreview? pricePreview = null)
    {
        if (SettingsWebView.CoreWebView2 is null || _settingsStore is null) return;
        if (_patcher is null)
        {
            SendPatcherUnavailableSnapshot(error ?? _patcherError);
            return;
        }
        if (error is not null)
        {
            _patcherError = error;
            _patcherMessage = null;
        }
        else if (message is not null)
        {
            _patcherMessage = message;
            _patcherError = null;
        }

        var gameInfo = _patcher.GetGameInfo(_settingsStore.ClientPatch.CustomGameDirectory);
        var isRunning = PatcherFacade.IsGameRunning();
        var hasBackup = _patcher.HasBackup(_settingsStore.ClientPatch.CustomGameDirectory);
        var snapshots = _patcher.GetSnapshots(_settingsStore.ClientPatch.CustomGameDirectory)
            .Select(s => new
            {
                id = s.Id,
                type = s.Type,
                name = s.Name,
                description = s.Description,
                createdAt = s.CreatedAt,
                sizeBytes = s.SizeBytes,
                enabledModules = s.EnabledModules
            })
            .ToList();

        _cachedInstalledFonts ??= _patcher.GetInstalledFonts();

        var json = JsonSerializer.Serialize(new
        {
            version = 1,
            type = "patcherSnapshot",
            payload = new
            {
                gameDir = gameInfo.GameDirectory,
                gameMode = gameInfo.Mode.ToString(),
                isValid = gameInfo.IsValid,
                errorMessage = gameInfo.ErrorMessage,
                isGameRunning = isRunning,
                hasBackup = hasBackup,
                snapshots = snapshots,
                settings = _settingsStore.ClientPatch,
                builtinColors = UiColorService.BuiltinEntries,
                installedFonts = _cachedInstalledFonts,
                clientRumours = clientRumours,
                clientChests = clientChests,
                fontSettings = fontSettings,
                clientBaseItems = clientBaseItems,
                defaultRumours = gameInfo.IsChina ? RumourPatchService.DefaultModifiedRumourTextsSimplified : RumourPatchService.DefaultModifiedRumourTextsTraditional,
                defaultChests = gameInfo.IsChina ? NamePatchService.DefaultChestTextsSimplified : NamePatchService.DefaultChestTextsTraditional,
                defaultMapBeams = MapBeamPatchService.DefaultTargets,
                initialColorMods = ColorModService.GetBuiltinEntries(),
                scannedEntries = scannedEntries,
                message = _patcherMessage,
                error = _patcherError,
                pricePreview = pricePreview
            }
        }, JsonOptions);

        SettingsWebView.CoreWebView2.PostWebMessageAsJson(json);
    }

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
