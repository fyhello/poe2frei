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
using FreiAtlas.Patcher.EffectPatch.MudMan;
using FreiAtlas.Patcher.IconPatch;
using FreiAtlas.Patcher.Translation;
using System.Windows.Threading;
using FreiAtlas.Settings.Bridge;
using FreiAtlas.Settings.Hotkeys;
using FreiAtlas.Settings.Windowing;
using Microsoft.Web.WebView2.Core;
using FreiAtlas.Patcher.Core;
using FreiAtlas.Patcher.Modules.ColorMod;
using FreiAtlas.Patcher.Modules.Price;
using FreiAtlas.Patcher.Modules.Font;
using FreiAtlas.Patcher.Modules.Name;
using FreiAtlas.Patcher.Modules.Rumour;
using FreiAtlas.Patcher.Modules.MapBeam;

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

    /// <summary>
    /// 补丁写入互斥闸。
    ///
    /// 多个补丁服务会改写同一批数据表（baseitemtypes、endgamemaps、uisettings 等），
    /// 各自「读取当前内容 → 修改 → 写回」。并发执行时后写入的一方会覆盖前一方，
    /// 表现为某个补丁莫名其妙失效。
    ///
    /// 采用拒绝而非排队：排队会让用户连点多次后重复执行多次全量重写，耗时且无意义。
    /// </summary>
    private readonly PatchConcurrencyGate _patchGate = new();

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
        StartBackupVersionCheck();
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

    /// <summary>
    /// 游戏版本不匹配的自动快照，保留 2 天后再清理。
    ///
    /// 该参数只在「快照的游戏版本 ≠ 当前客户端版本」时才参与判断；
    /// 同版本的快照无论多旧都不会被清理。留 2 天是给用户足够的反悔窗口：
    /// 游戏刚更新时可能想临时切回旧客户端验证，一过性重跑不必立刻丢历史快照。
    /// </summary>
    private static readonly TimeSpan StaleSnapshotRetention = TimeSpan.FromDays(2);

    /// <summary>
    /// 后台校验纯净底包是否对应当前游戏版本。
    ///
    /// 游戏更新后旧底包会把客户端还原成旧版本资源，直接导致无法启动，
    /// 因此每次启动都要确认底包仍匹配。该校验同时清理过期的自动快照。
    ///
    /// 放后台执行，不阻塞窗口显示；失败也不会中断启动（该方法内部不抛异常）。
    /// </summary>
    private void StartBackupVersionCheck()
    {
        if (_patcher is null || !_patcher.IsBackupAvailable) return;

        var patcher = _patcher;
        _ = Task.Run(() =>
        {
            try
            {
                var check = patcher.VerifyBackupVersion(StaleSnapshotRetention);
                if (string.IsNullOrWhiteSpace(check.Message)) return;

                Dispatcher.Invoke(() => SendPatcherSnapshot(message: check.Message));
            }
            catch (Exception exception)
            {
                // 版本校验失败不影响使用，但要留痕：它关系到底包能否安全还原。
                Dispatcher.Invoke(() =>
                    SendPatcherSnapshot(message: $"启动版本校验失败：{exception.Message}"));
            }
        });
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

        // 补丁写入互斥：同一时刻只允许一个写补丁任务。
        // 前端虽然会禁用按钮，但那只是即时反馈，真正的互斥必须在这里做 ——
        // 消息是异步派发的，连点两次会进来两个 Task。
        IDisposable? gateLease = null;
        if (IsWriteCommand(command))
        {
            gateLease = _patchGate.TryEnter();
            if (gateLease is null)
            {
                SendPatcherSnapshot(error: "另一个补丁正在写入客户端，请等待其完成后再操作。");
                return;
            }
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
                case ApplyRumourPatchWebCommand applyRumour:
                    await HandleApplyRumourPatchAsync(applyRumour);
                    break;
                case ApplyChestPatchWebCommand applyChest:
                    await HandleApplyChestPatchAsync(applyChest);
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
                case GetMtxDatabaseWebCommand:
                    HandleGetMtxDatabase();
                    break;
                case ApplyMtxPatchWebCommand applyMtx:
                    await HandleApplyMtxPatchAsync(applyMtx);
                    break;
                case RestoreMtxPatchWebCommand:
                    await HandleRestoreMtxPatchAsync();
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
                case ScanIconPatchWebCommand:
                    await HandleScanIconPatchAsync();
                    break;
                case ApplyIconPatchWebCommand applyIcon:
                    await HandleApplyIconPatchAsync(applyIcon);
                    break;
                case ApplyTranslationPatchWebCommand:
                    await HandleApplyTranslationPatchAsync();
                    break;
                case ApplyEffectPatchWebCommand applyEffect:
                    await HandleApplyEffectPatchAsync(applyEffect);
                    break;
                case RestoreEffectPatchWebCommand:
                    await HandleRestoreEffectPatchAsync();
                    break;
                case ApplyMudManTierWebCommand applyMudMan:
                    await HandleApplyMudManTierAsync(applyMudMan);
                    break;
                case RestoreMudManWebCommand:
                    await HandleRestoreMudManAsync();
                    break;
                case ApplyHealthBarWebCommand:
                    await HandleApplyHealthBarAsync();
                    break;
                case RestoreHealthBarWebCommand:
                    await HandleRestoreHealthBarAsync();
                    break;
                case ApplySpellBlockWebCommand applySpell:
                    await HandleApplySpellBlockAsync(applySpell);
                    break;
                case RestoreSpellBlockWebCommand restoreSpell:
                    await HandleRestoreSpellBlockAsync(restoreSpell);
                    break;
                case ApplyAllEffectsWebCommand applyAll:
                    await HandleApplyAllEffectsAsync(applyAll);
                    break;                case ResetPageWebCommand reset:
                    await ResetPageAsync(reset.Page);
                    break;
                case ResetAllWebCommand:
                    _settingsStore!.UpdatePrices(PriceSettings.Default);
                    SendPricesSnapshot(force: true);
                    UpdateQuickAssistSettings(QuickAssistSettings.Default);
                    SendQuickAssistSnapshot();
                    // 补丁设置复位，但游戏目录属于环境信息，保留用户已选路径。
                    ResetClientPatchSettingsKeepingDirectory();
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
            if (command is SetPatcherGameDirWebCommand or ApplyFontPatchWebCommand or ApplyRumourPatchWebCommand or ApplyChestPatchWebCommand or ApplyColorModPatchWebCommand or ApplyMtxPatchWebCommand or RestoreMtxPatchWebCommand or RestoreAllPatchesWebCommand or CreatePatcherSnapshotWebCommand or RestorePatcherSnapshotWebCommand or DeletePatcherSnapshotWebCommand)
                SendPatcherSnapshot(error: exception.Message);
            if (_runtime is not null)
            {
                SendSnapshot(_runtime.Current);
            }
        }
        finally
        {
            // 无论成功、失败还是取消，都必须释放闸门，否则后续补丁全部被拒。
            if (gateLease is not null)
            {
                gateLease.Dispose();

                // 闸门释放后必须立刻把 patchBusy=false 推给前端。
                //
                // 写入型 handler 内部最后一条快照是在 try 块里发的，那一刻闸门
                // 尚未释放，所以前端收到的 patchBusy 必然是 true。前端据此把
                // 18 个补丁按钮全部 disabled 且不关闭操作遮罩；若此处不补发，
                // 前端会永久停在「另一个补丁正在写入」的状态，除非重启程序。
                //
                // 这里走轻量通道而不是 SendPatcherSnapshot()：后者会重新执行
                // GetGameInfo / HasBackup / GetSnapshots，各触发一次完整客户端
                // 探测，而本次只需要同步一个布尔值。
                SendPatchBusyState();
            }
        }
    }

    /// <summary>
    /// 该命令是否会写入客户端文件。
    ///
    /// 只有写入型命令才需要占用补丁互斥闸；扫描、查询、切换进程等只读命令不占用，
    /// 否则用户在看行情时点不了任何按钮。
    ///
    /// 清单覆盖全部补丁模块的「应用」与「还原」入口，以及快照的增删还原 ——
    /// 快照操作会改写 _.index.bin，同样属于写入。
    /// </summary>
    private static bool IsWriteCommand(WebCommand command) => command is
        ApplyFontPatchWebCommand
        or ApplyRumourPatchWebCommand
        or ApplyChestPatchWebCommand
        or ApplyColorModPatchWebCommand
        or ApplyMapBeamPatchWebCommand
        or ApplyPricePatchWebCommand
        or ApplyCurrentPricesPatchWebCommand
        or ConfirmCurrentPricesPatchWebCommand
        or CleanPricePatchWebCommand
        or RestoreAllPatchesWebCommand
        or ApplyTranslationPatchWebCommand
        or ApplyIconPatchWebCommand
        or ApplyEffectPatchWebCommand
        or RestoreEffectPatchWebCommand
        or ApplySpellBlockWebCommand
        or RestoreSpellBlockWebCommand
        or ApplyAllEffectsWebCommand
        or ApplyMudManTierWebCommand
        or RestoreMudManWebCommand
        or ApplyHealthBarWebCommand
        or RestoreHealthBarWebCommand
        or ApplyMtxPatchWebCommand
        or RestoreMtxPatchWebCommand
        or CreatePatcherSnapshotWebCommand
        or RestorePatcherSnapshotWebCommand
        or DeletePatcherSnapshotWebCommand;

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
            // 只重置补丁配置，保留游戏目录 —— 清空路径会迫使非默认安装位置的
            // 用户重新选择一次客户端，而路径本身不属于「补丁设置」。
            ResetClientPatchSettingsKeepingDirectory();
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
                Labels = defaults.Labels,
                AreaMap = current.AreaMap with
                {
                    OverlayScale = defaults.AreaMap.OverlayScale,
                    ExpeditionPanel = current.AreaMap.ExpeditionPanel with
                    {
                        Scale = defaults.AreaMap.ExpeditionPanel.Scale
                    }
                }
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
            PricePatchSeparator = cmd.Separator,
            PricePatchMinExalted = minExalted,
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
            PricePatchSeparator = cmd.Separator,
            PricePatchMinExalted = minExalted,
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

    /// <summary>
    /// 校验前端传入的物价 CSV 路径。
    ///
    /// 该路径最终会交给 <c>PricePatchService.LoadPriceRowsFromCsv</c> 直接读取，
    /// 中间没有任何目录限制，因此只允许用户目录或临时目录下的 .csv 文件，
    /// 避免被指到系统文件或客户端本体上。
    /// </summary>
    private static bool IsAllowedPriceCsvPath(string csvPath)
    {
        if (string.IsNullOrWhiteSpace(csvPath)) return false;

        string full;
        try
        {
            full = Path.GetFullPath(csvPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!string.Equals(Path.GetExtension(full), ".csv", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 只在用户可写目录（文档 / 下载 / 桌面 / 临时）下读取，
        // 防止被指向系统目录或游戏客户端内部文件。
        string[] allowedRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Path.GetTempPath()
        ];

        foreach (var root in allowedRoots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            string normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
            PricePatchSeparator = cmd.Separator,
        });

        SendPatcherSnapshot(message: "正在解析物价数据并准备注入客户端...");

        List<PriceRow> priceRows = [];
        if (!string.IsNullOrWhiteSpace(cmd.CsvPath) && File.Exists(cmd.CsvPath))
        {
            // CSV 路径由前端传入，必须过一遍白名单校验。
            // WebMessageRouter 的字段防护只挡 address/offset 之类的关键词，
            // 不挡路径，所以任何本地文件都可能被指定读取。
            if (!IsAllowedPriceCsvPath(cmd.CsvPath))
            {
                SendPatcherSnapshot(error: "物价 CSV 只允许读取 .csv 文件，该路径已被拒绝。");
                return;
            }
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
            // 索引已回滚为纯净官方版本，磁盘上的模块状态与逐文件备份必须同步清理。
            // 只重置开关是不够的：泥人/血条的 state.json 仍记着 applied=true，
            // 会让「再次应用同一档位」被判定为无需写入而永久跳过。
            _patcher.ClearTransientPatchStates();

            // 恢复默认设置时必须保留用户手动指定的游戏目录，
            // 否则非默认安装路径的用户会被要求重新选择一次客户端。
            ResetClientPatchSettingsKeepingDirectory();

            SendPatcherSnapshot(message: result.Message);
        }
        else
        {
            SendPatcherSnapshot(error: result.Message);
        }
    }

    /// <summary>
    /// 把补丁设置整体复位为默认值，但保留用户手动指定的游戏目录。
    ///
    /// <see cref="ClientPatchSettings.Default"/> 的 <c>CustomGameDirectory</c> 是 null，
    /// 直接整体覆盖会让非默认安装路径的用户丢失已选目录，被迫重新选择客户端。
    /// 该目录属于「环境信息」而非「补丁配置」，不应被补丁状态重置波及。
    /// </summary>
    private void ResetClientPatchSettingsKeepingDirectory()
    {
        if (_settingsStore is null) return;

        string? currentDir = _settingsStore.ClientPatch.CustomGameDirectory;
        _settingsStore.UpdateClientPatch(
            ClientPatchSettings.Default with { CustomGameDirectory = currentDir });
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
                catch
                {
                    // 快照里的设置 JSON 可能来自旧版本、字段已不兼容。
                    // 客户端索引此时已经还原成功，不能因为设置反序列化失败就把还原判为失败；
                    // 失败时保持当前设置不变。
                }
            }
            else
            {
                // 快照未记录设置（官方底包或早期快照）：索引已回滚为纯净版本，
                // 开关与磁盘状态必须一起复位，否则泥人/血条会因为 state.json
                // 仍记着 applied=true 而拒绝再次写入。
                _patcher.ClearTransientPatchStates();
                ResetClientPatchSettingsKeepingDirectory();
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
                patchBusy = _patchGate.IsBusy,
                hasBackup = false,
                snapshots = Array.Empty<PatchSnapshotInfo>(),
                settings = _settingsStore.ClientPatch,
                installedFonts = Array.Empty<FontItem>(),
                clientRumours = (IReadOnlyList<EndgameMapRumourItem>?)null,
                clientChests = (IReadOnlyList<NamePatchItem>?)null,
                fontSettings = (FontSettingsInfo?)null,
                clientBaseItems = (IReadOnlyList<PricePatchService.BaseItemName>?)null,
                defaultRumours = Array.Empty<string>(),
                defaultMapBeams = Array.Empty<object>(),
                scannedEntries = (IReadOnlyList<ColorModEntry>?)null,
                message = (string?)null,
                error = reason
            }
        }, JsonOptions);

        SettingsWebView.CoreWebView2.PostWebMessageAsJson(json);
    }

    /// <summary>
    /// 只下发「补丁闸门是否占用」这一项，不重新探测客户端。
    ///
    /// 用于补丁命令结束、闸门释放之后：此时必须让前端知道可以解锁了，
    /// 但没必要再跑一遍 GetGameInfo / HasBackup / GetSnapshots ——
    /// 那三者各自会触发一次完整的客户端探测（枚举进程 + 读注册表 + 扫目录）。
    ///
    /// 前端只关心 patchBusy 这一个字段，其余字段沿用上一次快照即可。
    /// </summary>
    private void SendPatchBusyState()
    {
        if (SettingsWebView.CoreWebView2 is null) return;

        var json = JsonSerializer.Serialize(
            new
            {
                version = 1,
                type = "patcherBusyState",
                payload = new
                {
                    patchBusy = _patchGate.IsBusy
                }
            },
            JsonOptions);

        try
        {
            SettingsWebView.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (InvalidOperationException)
        {
            // WebView 正在关闭，忽略
        }
    }

    private void SendPatcherSnapshot(
        string? message = null,
        string? error = null,
        IReadOnlyList<ColorModEntry>? scannedEntries = null,
        IReadOnlyList<EndgameMapRumourItem>? clientRumours = null,
        IReadOnlyList<NamePatchItem>? clientChests = null,
        FontSettingsInfo? fontSettings = null,
        IReadOnlyList<PricePatchService.BaseItemName>? clientBaseItems = null,
        PricePatchService.PricePatchPreview? pricePreview = null,
        IReadOnlyList<object>? iconPatchEntries = null,
        int? iconPatchSkipped = null,
        int? iconSpriteCell = null,
        int? iconSpriteColumns = null)
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

        // 探测一次客户端，后续备份查询与快照列举都复用它。
        // GameInstallDetector.Detect 会枚举进程、读注册表、扫十多条候选路径，
        // 而快照推送在补丁写入期间会被反复触发（每条进度消息一次），
        // 每次重探三遍会让 UI 线程明显卡顿。
        var gameInfo = _patcher.GetGameInfo(_settingsStore.ClientPatch.CustomGameDirectory);
        var isRunning = PatcherFacade.IsGameRunning();
        var hasBackup = _patcher.HasBackup(gameInfo);
        var snapshots = _patcher.GetSnapshots(gameInfo)
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

        // 泥人还原点状态：前端据此告诉用户「还原会回到哪一份索引」，
        // 版本不符时必须显式警告 —— 否则用户会回滚到旧索引导致客户端起不来。
        var mudManRestorePoint = _patcher.GetMudManRestorePoint(
            _settingsStore.ClientPatch.CustomGameDirectory);

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
                installedFonts = _cachedInstalledFonts,
                clientRumours = clientRumours,
                clientChests = clientChests,
                fontSettings = fontSettings,
                clientBaseItems = clientBaseItems,
                defaultRumours = gameInfo.IsChina ? RumourPatchService.DefaultModifiedRumourTextsSimplified : RumourPatchService.DefaultModifiedRumourTextsTraditional,
                defaultMapBeams = MapBeamPatchService.DefaultTargets,
                scannedEntries = scannedEntries,
                message = _patcherMessage,
                error = _patcherError,
                pricePreview = pricePreview,
                iconPatchEntries = iconPatchEntries,
                iconPatchSkipped = iconPatchSkipped,
                iconSpriteCell = iconSpriteCell,
                iconSpriteColumns = iconSpriteColumns,
                // 前端据此禁用写入型按钮。以前这个字段从不发送，
                // 导致 patcher.js 里的 busy 恒为 false、按钮从不禁用。
                patchBusy = _patchGate.IsBusy,
                // 国服客户端原生即简体中文，汉化卡据此禁用并给出说明。
                isChina = gameInfo.IsChina,
                mudMan = new
                {
                    available = _patcher.MudManTablesAvailable,
                    error = _patcher.MudManTablesError,
                    customCount = _patcher.MudManCustomCount,
                    listCount = _patcher.MudManListCount,
                    applied = _patcher.IsMudManApplied(),
                    patchedFiles = _patcher.MudManPatchedFileCount(),
                    restorePoint = new
                    {
                        exists = mudManRestorePoint.Exists,
                        gameVersion = mudManRestorePoint.GameVersion,
                        matchesCurrent = mudManRestorePoint.MatchesCurrent
                    }
                },
                healthBar = new
                {
                    // 以磁盘上的 state.json 为准判断是否真的写入过客户端；
                    // 设置里的开关只用于跨会话记住用户意图。
                    applied = _patcher.IsHealthBarApplied(),
                    patchedFiles = _patcher.HealthBarPatchedFileCount(),
                    enabled = _settingsStore.ClientPatch.HealthBarEnabled
                },
                iconPatch = new
                {
                    applied = _patcher.IsIconPatchApplied(),
                    backedUpFiles = _patcher.IconPatchedFileCount()
                },
                mtx = new
                {
                    applied = _patcher.IsMtxPatchApplied(),
                    patchedFiles = _patcher.MtxPatchedFileCount(),
                    totalCount = FreiAtlas.Patcher.MtxPatch.MtxDatabase.Instance.AllEntries.Count
                }
            }
        }, JsonOptions);

        SettingsWebView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private void HandleGetMtxDatabase()
    {
        if (SettingsWebView.CoreWebView2 is null) return;
        var db = FreiAtlas.Patcher.MtxPatch.MtxDatabase.Instance;
        var list = db.AllEntries.Select(e => new
        {
            name = e.Name,
            displayNameZh = e.DisplayNameZh,
            category = e.Category.ToString(),
            group = e.Group,
            displayGroup = e.DisplayGroup,
            iconDdsPath = e.IconDdsPath,
            previewUrl = e.PreviewUrl,
            slotAttach = e.SlotAttach,
            slotAttachLabel = e.SlotAttachLabel,
            armourSetFolder = e.ArmourSetFolder,
            armourSetFilePrefix = e.ArmourSetFilePrefix,
            isArmourSetGlobal = e.IsArmourSetGlobal,
            isConstructionEffectPack = e.IsConstructionEffectPack,
            internalPaths = e.InternalPaths,
            subtitle = e.Subtitle
        }).ToList();

        var json = JsonSerializer.Serialize(new
        {
            version = 1,
            type = "mtxDatabase",
            payload = new
            {
                entries = list,
                supportedClasses = FreiAtlas.Patcher.MtxPatch.MtxDatabase.SupportedClasses
            }
        }, JsonOptions);
        SettingsWebView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private async Task HandleApplyMtxPatchAsync(ApplyMtxPatchWebCommand cmd)
    {
        if (_patcher is null || _settingsStore is null) return;
        if (PatcherFacade.IsGameRunning())
        {
            SendPatcherSnapshot(error: "检测到游戏正在运行中，游戏占用了资源文件。请先完全关闭游戏客户端，然后再应用外观特效。");
            return;
        }

        _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
        {
            MtxSelectedNames = cmd.SelectedNames,
            MtxSelectedArmourSet = cmd.SelectedArmourSetName,
            MtxSelectedPortal = cmd.SelectedPortalName,
            MtxSelectedPet = cmd.SelectedPetName,
            MtxSelectedClass = cmd.SelectedClassCode
        });

        SendPatcherSnapshot(message: "正在准备应用外观商城特效替换，请稍候...");
        string? progressLog = null;
        RecordAutoSnapshot("商城外观特效替换");

        var options = new FreiAtlas.Patcher.MtxPatch.MtxPatchOptions
        {
            SelectedNames = new HashSet<string>(cmd.SelectedNames, StringComparer.OrdinalIgnoreCase),
            SelectedArmourSetName = cmd.SelectedArmourSetName,
            SelectedPortalName = cmd.SelectedPortalName,
            SelectedPetName = cmd.SelectedPetName,
            SelectedClassCode = cmd.SelectedClassCode
        };

        var (ok, message) = await Task.Run(() =>
        {
            var r = _patcher.ApplyMtxPatch(
                _settingsStore.ClientPatch.CustomGameDirectory,
                options,
                msg =>
                {
                    progressLog = msg;
                    Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
                },
                out var m);
            return (r, m);
        });

        if (ok)
        {
            SendPatcherSnapshot(message: !string.IsNullOrWhiteSpace(message) ? message : "外观商城特效替换已成功应用！");
        }
        else
        {
            SendPatcherSnapshot(error: !string.IsNullOrWhiteSpace(message) ? message : (progressLog ?? "外观特效替换应用失败。"));
        }
    }

    private async Task HandleRestoreMtxPatchAsync()
    {
        if (_patcher is null || _settingsStore is null) return;
        if (PatcherFacade.IsGameRunning())
        {
            SendPatcherSnapshot(error: "检测到游戏正在运行中，游戏占用了资源文件。请先完全关闭游戏客户端，然后再还原外观特效。");
            return;
        }

        SendPatcherSnapshot(message: "正在还原外观特效至纯净原版，请稍候...");
        string? progressLog = null;
        RecordAutoSnapshot("还原商城外观特效");

        var (ok, message) = await Task.Run(() =>
        {
            var r = _patcher.RestoreMtxPatch(
                _settingsStore.ClientPatch.CustomGameDirectory,
                msg =>
                {
                    progressLog = msg;
                    Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
                },
                out var m);
            return (r, m);
        });

        if (ok)
        {
            _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
            {
                MtxSelectedNames = null,
                MtxSelectedArmourSet = null,
                MtxSelectedPortal = null,
                MtxSelectedPet = null
            });
            SendPatcherSnapshot(message: !string.IsNullOrWhiteSpace(message) ? message : "外观特效已全部成功恢复纯净原版！");
        }
        else
        {
            SendPatcherSnapshot(error: !string.IsNullOrWhiteSpace(message) ? message : (progressLog ?? "外观特效还原失败。"));
        }
    }

        private async Task HandleScanIconPatchAsync()
        {
            if (_patcher == null || _settingsStore == null)
            {
                return;
            }
            GameModeInfo gameInfo = _patcher.GetGameInfo(_settingsStore.ClientPatch.CustomGameDirectory);
            if (!gameInfo.IsValid)
            {
                SendPatcherSnapshot(null, gameInfo.ErrorMessage ?? "未检测到有效的游戏客户端。");
                return;
            }
            IconPatchService service = _patcher.IconPatchService;
            IconPatchScanResult iconPatchScanResult = await Task.Run(() => service.Scan(gameInfo));
            if (iconPatchScanResult.Error != null)
            {
                SendPatcherSnapshot(null, iconPatchScanResult.Error);
                return;
            }
            IconSpriteInfo spriteInfo = IconPatchService.SpriteInfo;
            IReadOnlyList<string> enabledPaths = _settingsStore.ClientPatch.IconPatchSelectedPaths ?? Array.Empty<string>();
            SendPatcherSnapshot(iconPatchEntries: iconPatchScanResult.Entries.Select((IconPatchEntry e) => new
            {
                path = e.VirtualPath,
                fileName = e.FileName,
                category = e.Category,
                groupIndex = e.GroupIndex,
                originalSize = e.OriginalSize,
                replacementSize = e.ReplacementSize,
                spriteX = e.SpriteX,
                spriteY = e.SpriteY,
                enabled = enabledPaths.Contains(e.VirtualPath, StringComparer.OrdinalIgnoreCase)
            }).ToList(), iconPatchSkipped: iconPatchScanResult.SkippedCount, iconSpriteCell: spriteInfo.CellSize, iconSpriteColumns: spriteInfo.Columns, message: (iconPatchScanResult.SkippedCount > 0) ? $"客户端可用 {iconPatchScanResult.Entries.Count} 个图标，{iconPatchScanResult.SkippedCount} 个不适用于当前版本已隐藏。" : $"客户端可用 {iconPatchScanResult.Entries.Count} 个图标。");
        }

        private async Task HandleApplyIconPatchAsync(ApplyIconPatchWebCommand cmd)
        {
            if (_patcher == null || _settingsStore == null)
            {
                return;
            }
            if (PatcherFacade.IsGameRunning())
            {
                SendPatcherSnapshot(null, "检测到游戏正在运行中，游戏占用了资源文件。请先完全关闭游戏客户端，然后再应用补丁。");
                return;
            }
            IReadOnlyList<string> paths = cmd.Paths ?? Array.Empty<string>();
            _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
            {
                IconPatchEnabled = cmd.Enabled,
                IconPatchSelectedPaths = paths
            });

            // 关闭开关或清空选择时必须真正把官方图标写回客户端。
            // 只改内存开关会让界面显示「已关闭」而磁盘仍是替换后的版本。
            if (!cmd.Enabled || paths.Count == 0)
            {
                if (!_patcher.IsIconPatchApplied())
                {
                    SendPatcherSnapshot(cmd.Enabled
                        ? "未选择任何图标。"
                        : "已关闭图标替换。");
                    return;
                }

                SendPatcherSnapshot("正在还原官方图标，请稍候...");
                string? restoreLog = null;
                IconPatchRestoreResult restored = await Task.Run(() => _patcher.RestoreIconPatch(
                    _settingsStore.ClientPatch.CustomGameDirectory,
                    msg =>
                    {
                        restoreLog = msg;
                        Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
                    }));

                if (restored.Success)
                {
                    SendPatcherSnapshot(restoreLog ?? restored.Message);
                }
                else
                {
                    SendPatcherSnapshot(null, restored.Message);
                }
                return;
            }
            GameModeInfo gameInfo = _patcher.GetGameInfo(_settingsStore.ClientPatch.CustomGameDirectory);
            if (!gameInfo.IsValid)
            {
                SendPatcherSnapshot(null, gameInfo.ErrorMessage ?? "未检测到有效的游戏客户端。");
                return;
            }
            SendPatcherSnapshot("正在准备写入图标，请稍候...");
            string? progressLog = null;
            RecordAutoSnapshot("图标替换写入前");
            IconPatchService service = _patcher.IconPatchService;
            string message = string.Empty;
            if (await Task.Run(() => service.Apply(gameInfo, paths, (string msg) =>
            {
                progressLog = msg;
                Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
            }, out message)))
            {
                SendPatcherSnapshot(message);
            }
            else
            {
                SendPatcherSnapshot(null, message);
            }
        }

        private async Task HandleApplyTranslationPatchAsync()
        {
            if (_patcher == null || _settingsStore == null)
            {
                return;
            }
            SendPatcherSnapshot("正在准备应用官方纯净简体中文汉化补丁，请稍候...");
            RecordAutoSnapshot("汉化补丁写入");
            string? progressLog = null;
            TranslationPatchResult translationPatchResult = await Task.Run(() => _patcher.ApplyTranslation(_settingsStore.ClientPatch.CustomGameDirectory, (string msg) =>
            {
                progressLog = msg;
                Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
            }));
            if (translationPatchResult.Success)
            {
                SendPatcherSnapshot($"官方简中汉化补丁全量注入完成！共改写 {translationPatchResult.CsdCount} 个 CSD 文件，注入 {translationPatchResult.BaseItemsCount} 个基础道具，全量汉化 {translationPatchResult.PatchedDatTablesCount} 个核心数据表（共 {translationPatchResult.PatchedDatCellsCount} 个文本单元格）。正在刷新客户端数据...");
                try
                {
                    SendPatcherSnapshot(null, null, await Task.Run(() => _patcher.ScanClientDictionary(_settingsStore.ClientPatch.CustomGameDirectory)));
                    return;
                }
                catch
                {
                    return;
                }
            }
            SendPatcherSnapshot(null, translationPatchResult.ErrorMessage);
        }

        private async Task HandleApplyEffectPatchAsync(ApplyEffectPatchWebCommand cmd)
        {
            if (_patcher == null || _settingsStore == null)
            {
                return;
            }
            SendPatcherSnapshot("正在应用画面与环境特效优化，请稍候（写入期间请勿关闭窗口）...");
            string? progressLog = null;
            var (flag, text) = await Task.Run(() => (r: _patcher.ApplyEffectRules(cmd.RuleIds, _settingsStore.ClientPatch.CustomGameDirectory, (string msg) =>
            {
                progressLog = msg;
                Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
            }, out string message), m: message));
            if (flag)
            {
                _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
                {
                    EffectPatchRuleIds = cmd.RuleIds,
                    ViewDistanceRatio = cmd.ViewDistanceRatio
                });
                SendPatcherSnapshot(text);
            }
            else
            {
                SendPatcherSnapshot(null, text);
            }
        }

        private async Task HandleRestoreEffectPatchAsync()
        {
            if (_patcher == null || _settingsStore == null)
            {
                return;
            }
            SendPatcherSnapshot("正在还原画面与环境特效优化，请稍候...");
            var (flag, text) = await Task.Run(() => (r: _patcher.RestoreEffectRules(_settingsStore.ClientPatch.CustomGameDirectory, (string msg) =>
            {
                Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
            }, out string message), m: message));
            if (flag)
            {
                _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
                {
                    EffectPatchRuleIds = Array.Empty<string>()
                });
                SendPatcherSnapshot(text);
            }
            else
            {
                SendPatcherSnapshot(null, text);
            }
        }

        private async Task HandleApplyMudManTierAsync(ApplyMudManTierWebCommand cmd)
        {
            if (_patcher == null || _settingsStore == null)
            {
                return;
            }
            if (!Enum.IsDefined(typeof(MudManTier), cmd.Tier))
            {
                SendPatcherSnapshot(null, $"未知的泥人档位：{cmd.Tier}");
                return;
            }
            MudManTier tier = (MudManTier)cmd.Tier;
            SendPatcherSnapshot("正在应用泥人：" + tier.DisplayName() + "，请稍候（写入期间请勿关闭窗口）...");
            var (flag, text) = await Task.Run(() => (r: _patcher.ApplyMudManTier(tier, _settingsStore.ClientPatch.CustomGameDirectory, (string msg) =>
            {
                Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
            }, out string message), m: message));
            if (flag)
            {
                _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
                {
                    MudManTier = cmd.Tier
                });
                SendPatcherSnapshot(text);
            }
            else
            {
                SendPatcherSnapshot(null, text);
            }
        }

        private async Task HandleRestoreMudManAsync()
        {
            if (_patcher == null || _settingsStore == null)
            {
                return;
            }
            SendPatcherSnapshot("正在还原泥人改动，请稍候...");
            var (flag, text) = await Task.Run(() => (r: _patcher.RestoreMudMan(_settingsStore.ClientPatch.CustomGameDirectory, (string msg) =>
            {
                Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
            }, out string message), m: message));
            if (flag)
            {
                _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
                {
                    MudManTier = -1
                });
                SendPatcherSnapshot(text);
            }
            else
            {
                SendPatcherSnapshot(null, text);
            }
        }

        private async Task HandleApplyHealthBarAsync()
        {
            if (_patcher == null || _settingsStore == null)
            {
                return;
            }
            SendPatcherSnapshot("正在应用头顶血条补丁，请稍候（写入期间请勿关闭窗口）...");
            var (flag, text) = await Task.Run(() => (r: _patcher.ApplyHealthBar(_settingsStore.ClientPatch.CustomGameDirectory, (string msg) =>
            {
                Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
            }, out string message), m: message));
            if (flag)
            {
                _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
                {
                    HealthBarEnabled = true
                });
                SendPatcherSnapshot(text);
            }
            else
            {
                SendPatcherSnapshot(null, text);
            }
        }

        private async Task HandleRestoreHealthBarAsync()
        {
            if (_patcher == null || _settingsStore == null)
            {
                return;
            }
            SendPatcherSnapshot("正在还原头顶血条补丁，请稍候...");
            var (flag, text) = await Task.Run(() => (r: _patcher.RestoreHealthBar(_settingsStore.ClientPatch.CustomGameDirectory, (string msg) =>
            {
                Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
            }, out string message), m: message));
            if (flag)
            {
                _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
                {
                    HealthBarEnabled = false
                });
                SendPatcherSnapshot(text);
            }
            else
            {
                SendPatcherSnapshot(null, text);
            }
        }

        private async Task HandleApplySpellBlockAsync(ApplySpellBlockWebCommand cmd)
        {
            if (_patcher == null || _settingsStore == null)
            {
                return;
            }
            SendPatcherSnapshot("正在应用技能特效屏蔽，请稍候（写入期间请勿关闭窗口）...");
            var (flag, text) = await Task.Run(() => (r: _patcher.ApplySpellBlocks(cmd.SpellIds, cmd.IncludeAudio, _settingsStore.ClientPatch.CustomGameDirectory, (string msg) =>
            {
                Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
            }, out string message), m: message));
            if (flag)
            {
                _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
                {
                    BlockedSpellIds = cmd.SpellIds,
                    SpellBlockIncludeAudio = cmd.IncludeAudio
                });
                SendPatcherSnapshot(text);
            }
            else
            {
                SendPatcherSnapshot(null, text);
            }
        }

        private async Task HandleRestoreSpellBlockAsync(RestoreSpellBlockWebCommand cmd)
        {
            if (_patcher == null || _settingsStore == null)
            {
                return;
            }
            SendPatcherSnapshot("正在还原技能特效，请稍候...");
            var (flag, text) = await Task.Run(() => (r: _patcher.RestoreSpellBlocks(cmd.SpellIds, _settingsStore.ClientPatch.CustomGameDirectory, (string msg) =>
            {
                Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
            }, out string message), m: message));
            if (flag)
            {
                List<string> blockedSpellIds = (_settingsStore.ClientPatch.BlockedSpellIds ?? Array.Empty<string>()).Where((string id) => !cmd.SpellIds.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
                _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
                {
                    BlockedSpellIds = blockedSpellIds
                });
                SendPatcherSnapshot(text);
            }
            else
            {
                SendPatcherSnapshot(null, text);
            }
        }

        private async Task HandleApplyAllEffectsAsync(ApplyAllEffectsWebCommand cmd)
        {
            if (_patcher == null || _settingsStore == null)
            {
                return;
            }
            SendPatcherSnapshot("正在保存并应用特效优化，请稍候（写入期间请勿关闭窗口）...");
            (bool, bool, bool, List<string>, List<string>) tuple = await Task.Run(() =>
            {
                List<string> list = new List<string>();
                List<string> list2 = new List<string>();
                bool flag = true;
                bool flag2 = true;
                bool flag3 = true;
                IReadOnlyList<string> readOnlyList = _settingsStore.ClientPatch.EffectPatchRuleIds ?? Array.Empty<string>();
                if (cmd.RuleIds.Count > 0)
                {
                    flag = _patcher.ApplyEffectRules(cmd.RuleIds, _settingsStore.ClientPatch.CustomGameDirectory, (string msg) =>
                    {
                Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
                    }, out string message2);
                    if (flag)
                    {
                        list.Add(message2);
                    }
                    else
                    {
                        list2.Add("画面优化写入失败：" + message2);
                    }
                }
                else if (readOnlyList.Count > 0)
                {
                    flag = _patcher.RestoreEffectRules(_settingsStore.ClientPatch.CustomGameDirectory, (string msg) =>
                    {
                Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
                    }, out string message3);
                    if (flag)
                    {
                        list.Add(message3);
                    }
                    else
                    {
                        list2.Add("画面优化还原失败：" + message3);
                    }
                }
                IReadOnlyList<string> readOnlyList2 = _settingsStore.ClientPatch.BlockedSpellIds ?? Array.Empty<string>();
                if (cmd.SpellIds.Count > 0)
                {
                    flag2 = _patcher.ApplySpellBlocks(cmd.SpellIds, cmd.IncludeAudio, _settingsStore.ClientPatch.CustomGameDirectory, (string msg) =>
                    {
                Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
                    }, out string message4);
                    if (flag2)
                    {
                        list.Add(message4);
                    }
                    else
                    {
                        list2.Add("技能屏蔽写入失败：" + message4);
                    }
                }
                else if (readOnlyList2.Count > 0)
                {
                    flag2 = _patcher.RestoreSpellBlocks(readOnlyList2, _settingsStore.ClientPatch.CustomGameDirectory, (string msg) =>
                    {
                Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
                    }, out string message5);
                    if (flag2)
                    {
                        list.Add(message5);
                    }
                    else
                    {
                        list2.Add("技能特效还原失败：" + message5);
                    }
                }
                int mudManTier = _settingsStore.ClientPatch.MudManTier;
                bool flag4 = _patcher.IsMudManApplied();
                if (cmd.MudManTier >= 0 && Enum.IsDefined(typeof(MudManTier), cmd.MudManTier))
                {
                    MudManTier mudManTier2 = (MudManTier)cmd.MudManTier;
                    if (!flag4 || mudManTier != cmd.MudManTier)
                    {
                        flag3 = _patcher.ApplyMudManTier(mudManTier2, _settingsStore.ClientPatch.CustomGameDirectory, (string msg) =>
                        {
                Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
                        }, out string message6);
                        if (flag3)
                        {
                            list.Add(message6);
                        }
                        else
                        {
                            list2.Add("泥人应用失败：" + message6);
                        }
                    }
                }
                else if (cmd.MudManTier < 0 && (flag4 || mudManTier >= 0))
                {
                    flag3 = _patcher.RestoreMudMan(_settingsStore.ClientPatch.CustomGameDirectory, (string msg) =>
                    {
                Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
                    }, out string message7);
                    if (flag3)
                    {
                        list.Add(message7);
                    }
                    else
                    {
                        list2.Add("泥人还原失败：" + message7);
                    }
                }
                return (ruleOk: flag, spellOk: flag2, mudManOk: flag3, msgs: list, errs: list2);
            });
            bool item = tuple.Item1;
            bool item2 = tuple.Item2;
            bool item3 = tuple.Item3;
            List<string> item4 = tuple.Item4;
            List<string> item5 = tuple.Item5;
            ClientPatchSettings clientPatch = _settingsStore.ClientPatch;
            ClientPatchSettings clientPatchSettings = clientPatch;
            if (item)
            {
                clientPatchSettings = clientPatchSettings with
                {
                    EffectPatchRuleIds = cmd.RuleIds,
                    ViewDistanceRatio = cmd.ViewDistanceRatio
                };
            }
            if (item2)
            {
                clientPatchSettings = clientPatchSettings with
                {
                    BlockedSpellIds = cmd.SpellIds,
                    SpellBlockIncludeAudio = cmd.IncludeAudio
                };
            }
            if (item3)
            {
                clientPatchSettings = clientPatchSettings with
                {
                    MudManTier = cmd.MudManTier
                };
            }
            if (clientPatchSettings != clientPatch)
            {
                _settingsStore.UpdateClientPatch(clientPatchSettings);
            }
            if (item5.Count == 0)
            {
                string message = ((item4.Count > 0) ? string.Join("；", item4) : "未作任何变更。");
                SendPatcherSnapshot(message);
                return;
            }
            string text = string.Join("；", item5);
            if (item4.Count > 0)
            {
                text = text + "（部分已生效：" + string.Join("；", item4) + "）";
            }
            SendPatcherSnapshot(null, text);
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
