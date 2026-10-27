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
        DateTimeOffset PriceFetchedAtUtc,
        // 以下三项用于确认注入后持久化用户配置：
        // 预览阶段会写一次设置，但用户可能在预览后又改了参数才确认，
        // 因此确认时必须按「确认这一刻」的值再写一遍。
        decimal MinExalted,
        bool StripExisting,
        IReadOnlyList<string>? Categories);

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
            // 必须与用户操作共用同一道闸门。
            // VerifyBackupVersion 内部会读写 manifest.json（清理过期快照），
            // 若与用户点击「创建快照」「打补丁」并发执行，两方会同时覆写同一文件，
            // 导致快照登记条目丢失或 manifest 半写入损坏。
            //
            // 最多等 60 秒：等不到说明用户正在打补丁，本次跳过即可，
            // 下次启动会再检查。不能无限等，否则后台线程长期占着。
            IDisposable? lease = WaitPatchGate(TimeSpan.FromSeconds(60));
            if (lease is null) return;

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
            finally
            {
                lease.Dispose();
            }
        });
    }

    /// <summary>
    /// 在后台轮询等待补丁闸门，超时返回 null（调用方放弃本次操作）。
    /// </summary>
    private IDisposable? WaitPatchGate(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var lease = _patchGate.TryEnter();
            if (lease is not null) return lease;
            Thread.Sleep(200);
        }
        return null;
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
        catch (Exception exception)
        {
            // 这里必须捕获全部异常，不能只过滤少数几种类型。
            // OnWebMessageReceived 是 async void 事件处理器，未捕获的异常会直接
            // 冒泡到顶层导致进程崩溃 —— 而补丁写入最常见的失败恰恰是
            // IOException（文件被占用）与 UnauthorizedAccessException（权限不足），
            // 二者原先都不在过滤列表里。
            if (command is SelectPriceLeagueWebCommand or RefreshPricesWebCommand)
                SendPricesSnapshot(exception.Message);
            if (command is UpdateQuickAssistWebCommand) SendQuickAssistSnapshot(exception.Message);
            if (command is UpdateHotkeyWebCommand) _runtime.SetHotkeyRegistrationError(exception.Message);

            // 写命令一律回送错误快照。用 IsWriteCommand 而不是手写白名单：
            // 手写清单会随命令增减而腐化，漏掉的命令失败时前端收不到任何提示，
            // 界面会永久停在「处理中」或误显示成功。
            if (IsWriteCommand(command))
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
        or ApplyCurrentPricesPatchWebCommand
        or ConfirmCurrentPricesPatchWebCommand
        or CleanPricePatchWebCommand
        or RestoreAllPatchesWebCommand
        or ApplyTranslationPatchWebCommand
        or ApplyIconPatchWebCommand
        or ApplySpellBlockWebCommand
        or RestoreSpellBlockWebCommand
        or ApplyAllEffectsWebCommand
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

            // 显式选择集合：未勾选的条目必须被真正禁用。
            // 只传 customMappings 不够 —— 未勾选的条目在字典里查不到，
            // 会落到「有默认 Memo 就启用」的分支，导致取消勾选无效。
            HashSet<string> selectedChestNames = new(
                cmd.Items?.Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.OriginalName))
                    .Select(c => c.OriginalName) ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            bool success = await Task.Run(() => _patcher.ApplyNamePatch(
                NamePatchKind.Chests,
                _settingsStore.ClientPatch.CustomGameDirectory,
                customMappings: customMappings,
                progress: msg =>
                {
                    progressLog = msg;
                    Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
                },
                selectedNames: selectedChestNames));
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
            fetchedAtUtc,
            minExalted,
            stripExisting,
            cmd.Categories);

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
            // 确认注入这条主路径同样要持久化全部过滤参数。
            // 原先只写 PricePatchEnabled，用户配置的门槛 / 分隔符 / 去旧价 / 类别
            // 在重启后全部丢失，界面回到默认值而客户端仍是按旧参数打的补丁。
            _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
            {
                PricePatchEnabled = true,
                PricePatchSeparator = pending.Separator,
                PricePatchMinExalted = pending.MinExalted,
                PricePatchStripExisting = pending.StripExisting,
                PricePatchCategories = pending.Categories
            });
            SendPatcherSnapshot(message: progressLog ?? $"已确认注入 {result.PatchedNames} 个客户端物品物价。");
        }
        else
        {
            SendPatcherSnapshot(error: string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? "确认注入失败。"
                : result.ErrorMessage);
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
            // 索引已回滚，磁盘上的 state.json 必须同步清理 —— 无论本快照是否带设置。
            //
            // 原实现只在「无设置 JSON」分支里调用 ClearTransientPatchStates，
            // 带设置的快照走另一条路，导致 mudman/state.json / healthbar/state.json
            // 仍记着 applied=true，而索引其实已经回到干净状态：
            // 用户之后点「应用泥人」会被判定为「已应用、无需写入」而永久跳过。
            //
            // 清状态不影响随后恢复的设置项 —— ClearState 只删状态文件，
            // 不动备份，也不碰 ClientPatchSettings。
            _patcher.ClearTransientPatchStates();

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
                // 开关也要一起复位（state.json 已在上方无条件清理）。
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
        // 快照必须带上版本与失效标记，否则前端无法阻止用户把旧版本索引
        // 还原到新客户端上 —— 那会让客户端直接起不来（实测 12,481 个文件读不出）。
        // PatchSnapshotInfo 本来就记录了 IndexFingerprint 与 GameVersion，此前忘了投影。
        string snapshotCurrentFingerprint = _patcher.BackupManager.GetCurrentFingerprint(gameInfo);
        var snapshots = _patcher.GetSnapshots(gameInfo)
            .Select(s => new
            {
                id = s.Id,
                type = s.Type,
                name = s.Name,
                description = s.Description,
                createdAt = s.CreatedAt,
                sizeBytes = s.SizeBytes,
                enabledModules = s.EnabledModules,
                gameVersion = s.GameVersion,
                // 指纹缺失或读不到当前指纹时判为 false（未知不等于失效），
                // 避免把全部快照一律标成不可用。
                isStale = !string.IsNullOrEmpty(s.IndexFingerprint)
                          && !string.IsNullOrEmpty(snapshotCurrentFingerprint)
                          && !string.Equals(s.IndexFingerprint, snapshotCurrentFingerprint, StringComparison.OrdinalIgnoreCase)
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
                // 前端按数组消费（Array.isArray 判定 + 逐项读 rowIndex/mapName）。
                // 后端原样下发的却是 Dictionary<string,string>，序列化成 JSON 对象，
                // 前端那个分支永远不成立 —— 不扫描客户端时传言表恒为空。
                // 这里投影成数组，字段名与前端读取的一致。
                defaultRumours = (gameInfo.IsChina
                        ? RumourPatchService.DefaultModifiedRumourTextsSimplified
                        : RumourPatchService.DefaultModifiedRumourTextsTraditional)
                    .Select(kv => new
                    {
                        originalText = kv.Key,
                        modifiedText = kv.Value,
                        enabled = true
                    })
                    .ToList(),
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
            SelectedArmourSetFolder = cmd.SelectedArmourSetFolder,
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
            // 更新设置前先取出上次选中的路径：取消勾选的图标需要写回官方原件，
            // 否则界面上勾选数变少，但客户端里仍是修改版。
            IReadOnlyList<string> previousPaths =
                _settingsStore.ClientPatch.IconPatchSelectedPaths ?? Array.Empty<string>();
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
            if (await Task.Run(() => service.Apply(gameInfo, paths, previousPaths, (string msg) =>
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
                // 中间进度：告诉用户还在跑，但这不是终点。
                SendPatcherSnapshot($"汉化已写入客户端，正在刷新词库...（改写 {translationPatchResult.CsdCount} 个 CSD、注入 {translationPatchResult.BaseItemsCount} 个底子、汉化 {translationPatchResult.PatchedDatTablesCount} 个数据表）");

                // 必须发一条【非 null】的 message 作为收尾。
                // SendPatcherSnapshot 在 message 为 null 时会复用 _patcherMessage，
                // 导致前端一直停在「正在刷新词库...」，用户看不到完成。
                try
                {
                    var scanned = await Task.Run(
                        () => _patcher.ScanClientDictionary(_settingsStore.ClientPatch.CustomGameDirectory));

                    SendPatcherSnapshot(
                        $"官方简中汉化补丁全部完成！共改写 {translationPatchResult.CsdCount} 个 CSD 词缀文件，"
                        + $"注入 {translationPatchResult.BaseItemsCount} 个基础道具名称，"
                        + $"全量汉化 {translationPatchResult.PatchedDatTablesCount} 个核心数据表"
                        + $"（{translationPatchResult.PatchedDatCellsCount} 个文本单元格）。客户端词库已刷新。",
                        null,
                        scanned);
                }
                catch (Exception scanEx)
                {
                    // 词库刷新失败不该让用户以为整个补丁失败：索引已经写入成功了。
                    SendPatcherSnapshot(
                        $"汉化补丁已写入并生效（改写 {translationPatchResult.CsdCount} 个 CSD、"
                        + $"汉化 {translationPatchResult.PatchedDatTablesCount} 个数据表），"
                        + $"但刷新词库失败：{scanEx.Message}。可重新进入本页重试刷新，不影响已打的补丁。");
                }
                return;
            }
            SendPatcherSnapshot(null, translationPatchResult.ErrorMessage);
        }

        private async Task HandleRestoreMudManAsync()
        {
            if (_patcher == null || _settingsStore == null)
            {
                return;
            }

            bool force = await TryRestoreMudManAsync(force: false);
            if (!force)
            {
                return;
            }

            // 用户已确认「会抹掉其他补丁」，带 force 重新执行。
            await TryRestoreMudManAsync(force: true);
        }

        /// <summary>
        /// 执行一次泥人还原。返回 true 表示需要用户二次确认后重试。
        ///
        /// 泥人还原是索引级回滚：若还原点之后客户端又被其他补丁改过（指纹已变），
        /// 回滚会把那些补丁一并抹掉。旧实现直接拒绝并永久死锁 —— 同版本换档
        /// 必然改变指纹，导致「还原泥人」再也点不动。
        /// 现在改为把后果讲清楚，由用户决定是否继续。
        /// </summary>
        private async Task<bool> TryRestoreMudManAsync(bool force)
        {
            if (_patcher == null || _settingsStore == null)
            {
                return false;
            }

            SendPatcherSnapshot(force
                ? "正在还原泥人改动（已确认，将重置索引）..."
                : "正在还原泥人改动，请稍候...");

            var (flag, text) = await Task.Run(() => (r: _patcher.RestoreMudMan(
                _settingsStore.ClientPatch.CustomGameDirectory,
                (string msg) => { Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg)); },
                out string message,
                force), m: message));

            if (flag)
            {
                _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with { MudManTier = -1 });
                SendPatcherSnapshot(text);
                return false;
            }

            // 后端要求确认：弹出明确后果的对话框，用户同意则带 force 重试。
            if (!force && text.StartsWith(MudManApplier.StaleRequiresConfirmPrefix, StringComparison.Ordinal))
            {
                string detail = text[MudManApplier.StaleRequiresConfirmPrefix.Length..].Trim();
                var choice = MessageBox.Show(
                    this,
                    detail,
                    "还原泥人 — 需要确认",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);

                return choice == MessageBoxResult.Yes;
            }

            SendPatcherSnapshot(null, text);
            return false;
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
            (bool, bool, bool, bool, List<string>, List<string>) tuple = await Task.Run(() =>
            {
                List<string> list = new List<string>();
                List<string> list2 = new List<string>();
                bool flag = true;
                bool flag2 = true;
                bool flag3 = true;
                bool healthBarOk = true;   // 头顶血条操作结果
                // 泥人换档必须先执行。
                //
                // 泥人切档时会 RestoreSnapshot 回滚整个客户端索引（见 MudManApplier.Apply），
                // 若把它放在画面优化 / 技能屏蔽之后，刚写入的内容会被这次回滚整体抹掉 ——
                // 界面显示全部成功，游戏内却什么都没生效。
                //
                // 先做泥人，让回滚落在「旧状态」上，随后写入的画面优化与技能屏蔽
                // 才是在最终索引之上的有效改动。
                //
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
                        }, out string message6, refreshFingerprint: false);
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

                // 头顶血条随批量一起处理。
                //
                // 原先前端勾选框 change 事件直接发 applyHealthBar/restoreHealthBar，
                // 点一下就跑一次写盘并弹出全屏遮罩，与其余 10 项特效「勾选只改状态、
                // 统一保存」的交互不一致。现在改为在这里比对期望值与实际状态。
                if (cmd.HealthBarEnabled is bool wantHealthBar)
                {
                    bool applied = _patcher.IsHealthBarApplied();
                    if (wantHealthBar && !applied)
                    {
                        healthBarOk = _patcher.ApplyHealthBar(_settingsStore.ClientPatch.CustomGameDirectory, (string msg) =>
                        {
                            Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
                        }, out string hbMsg);
                        if (healthBarOk) list.Add(hbMsg);
                        else list2.Add("头顶血条应用失败：" + hbMsg);
                    }
                    else if (!wantHealthBar && applied)
                    {
                        healthBarOk = _patcher.RestoreHealthBar(_settingsStore.ClientPatch.CustomGameDirectory, (string msg) =>
                        {
                            Dispatcher.Invoke(() => SendPatcherSnapshot(message: msg));
                        }, out string hbMsg2);
                        if (healthBarOk) list.Add(hbMsg2);
                        else list2.Add("头顶血条还原失败：" + hbMsg2);
                    }
                    // 状态已一致：无需写盘。
                }

                // 整批写入结束后统一刷新泥人还原点指纹。
                //
                // 泥人自己收尾时不刷新（上面传了 refreshFingerprint: false），
                // 因为随后特效/技能/血条还会改写索引，提前刷新会让指纹立刻失效，
                // 导致「还原泥人」被误判为 Stale 而永远失败。
                if (cmd.MudManTier >= 0)
                {
                    try { _patcher.RefreshMudManFingerprint(_settingsStore.ClientPatch.CustomGameDirectory); }
                    catch { /* 指纹刷新失败不影响本批写入结果 */ }
                }

                return (ruleOk: flag, spellOk: flag2, mudManOk: flag3, healthBarOk: healthBarOk, msgs: list, errs: list2);
            });
            bool item = tuple.Item1;
            bool item2 = tuple.Item2;
            bool item3 = tuple.Item3;
            bool item3b = tuple.Item4;   // 头顶血条
            List<string> item4 = tuple.Item5;
            List<string> item5 = tuple.Item6;
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
