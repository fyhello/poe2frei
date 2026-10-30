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

    /// <summary>
    /// 本轮批量内累积的错误。
    ///
    /// _patcherError 只是「最近一条错误」的展示字段，会被下一条 message 快照清空；
    /// 批量成功与否必须看这份累积列表，否则模块先报错、再推一条进度消息，
    /// 就会被误判为成功。
    /// </summary>
    private readonly List<string> _patcherIssues = [];
    private PendingPricePatch? _pendingPricePatch;

    /// <summary>
    /// 批量应用进行中。为真时 RecordAutoSnapshot 直接返回：
    /// 一次批量只应留下 1 份快照，而不是每个模块各记一份 115 MB。
    /// </summary>
    private bool _batchMode;

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

            // 进度消息已改为轻量通道，不再携带 patchBusy。
            // busy 必须在拿到闸门后显式推一次，否则写入期间前端按钮不会被禁用。
            SendPatchBusyState();
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

                case ApplyBatchWebCommand applyBatch:
                    await HandleApplyBatchAsync(applyBatch);
                    break;

                case SetBatchCategoryWebCommand setCategory:
                    HandleSetBatchCategory(setCategory);
                    break;

                case SetModuleEnabledWebCommand setModule:
                    HandleSetModuleEnabled(setModule);
                    break;
                case RestoreDefaultRulesWebCommand:
                    HandleRestoreDefaultRules();
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
    private static bool IsWriteCommand(WebCommand command) => command switch
    {
        // 「只保存设置」的自动保存不写客户端，不应占用写入闸门。
        // 否则批量写入期间用户改任意参数，自动保存都会被拒绝并弹「另一个补丁正在写入」，
        // 既误导用户，也可能打断正在进行的批量任务。
        ApplyFontPatchWebCommand { PersistOnly: true } => false,
        ApplyRumourPatchWebCommand { PersistOnly: true } => false,
        ApplyChestPatchWebCommand { PersistOnly: true } => false,
        ApplyColorModPatchWebCommand { PersistOnly: true } => false,
        ApplyIconPatchWebCommand { PersistOnly: true } => false,
        ApplyMapBeamPatchWebCommand { PersistOnly: true } => false,
        ApplyCurrentPricesPatchWebCommand { PersistOnly: true } => false,
        ApplyAllEffectsWebCommand { PersistOnly: true } => false,
        ApplyMtxPatchWebCommand { PersistOnly: true } => false,

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
        or ApplyBatchWebCommand
        or CreatePatcherSnapshotWebCommand
        or RestorePatcherSnapshotWebCommand
        or DeletePatcherSnapshotWebCommand => true,

        _ => false
    };

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
                    Dispatcher.Invoke(() => SendPatcherProgress(msg));
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

        // 空值表示「使用内置字体」。内置字体不在系统里时先安装，装不上就中止，
        // 不能把补丁打上去却因字体缺失回落到其它字体。
        string resolvedFontFamily = cmd.FontFamily;
        if (string.IsNullOrWhiteSpace(resolvedFontFamily))
        {
            resolvedFontFamily = FontInstaller.Bundled?.Family ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(resolvedFontFamily))
        {
            SendPatcherSnapshot(error: "未选择字体，且未找到内置字体配置。");
            return;
        }

        if (FontInstaller.IsBundledFamily(resolvedFontFamily))
        {
            if (!FontInstaller.EnsureInstalled(out string installMessage))
            {
                SendPatcherSnapshot(error: installMessage);
                return;
            }

            resolvedFontFamily = FontInstaller.Bundled!.Family;
        }

        _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
        {
            FontFamily = resolvedFontFamily,
            FontSizeDelta = cmd.FontSizeDelta,
            FontBold = cmd.IsBold,
            FontPresetId = cmd.PresetId,
            FontDropItemSizeDelta = cmd.DropItemSizeDelta,
            FontGeneralTextSizeDelta = cmd.GeneralTextSizeDelta,
            FontTitleSizeDelta = cmd.TitleSizeDelta,
            FontTinyBadgeSizeDelta = cmd.TinyBadgeSizeDelta,
            FontCustomPresets = cmd.CustomPresets ?? _settingsStore.ClientPatch.FontCustomPresets
        });

        // 只保存设置：自动保存路径不写客户端。
        if (cmd.PersistOnly) return;

        SendPatcherSnapshot(message: "正在准备应用字体补丁，请稍候...");
        string? progressLog = null;
        await RecordAutoSnapshotAsync("字体补丁写入");
        bool success = await Task.Run(() => _patcher.ApplyFont(
            resolvedFontFamily,
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
                Dispatcher.Invoke(() => SendPatcherProgress(msg));
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

        // 空列表表示「使用默认规则」：后端按区域 id 套用 20 条内置传言规则。
        // 自动保存路径仍要跳过，否则配置页没读到客户端时会把自定义规则清掉。
        bool useDefaultRules = cmd.Items is null || cmd.Items.Count == 0;

        if (useDefaultRules && cmd.PersistOnly)
        {
            SendPatcherSnapshot(error: "尚未读取客户端传言，已跳过本次提交以保留已有配置。");
            return;
        }

        _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
        {
            RumourEnhanced = cmd.Enabled,
            RumourItems = useDefaultRules ? null : cmd.Items
        });

        // 只保存设置：自动保存路径不写客户端。
        if (cmd.PersistOnly) return;

        if (cmd.Enabled)
        {
            SendPatcherSnapshot(message: "正在准备应用传言增强补丁，请稍候...");
            string? progressLog = null;
            await RecordAutoSnapshotAsync("传言强化写入");
            // 无自定义规则时传 null：ApplyPatch 会用 ParseFromBytes 按区域 id 算出的默认 Memo。
            var customItems = useDefaultRules
                ? null
                : cmd.Items!.Select(r => new EndgameMapRumourItem
                {
                    RowIndex = r.RowIndex,
                    WorldAreaIndex = r.WorldAreaIndex,
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
                    Dispatcher.Invoke(() => SendPatcherProgress(msg));
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

    /// <summary>
    /// 清空全部自定义规则，恢复内置默认规则。
    ///
    /// 默认规则在「设置为空」时自动生效，所以这里只清空规则列表，
    /// 不把默认值写回配置 —— 这样以后默认规则更新能自动跟进。
    /// </summary>
    private void HandleRestoreDefaultRules()
    {
        if (_settingsStore is null) return;

        _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
        {
            ColorModRules = null,
            ChestItems = null,
            RumourItems = null,
            IconPatchSelectedPaths = null,
            EffectPatchRuleIds = null,
            MapBeamTargets = null
        });

        SendPatcherSnapshot(message: "已恢复默认规则：词缀染色、宝箱标注、传言标注、图标替换、特效优化、神图光柱的自定义规则已清空，下次应用将使用内置默认规则。");
    }

    private async Task HandleApplyChestPatchAsync(ApplyChestPatchWebCommand cmd)
    {
        if (_patcher is null || _settingsStore is null) return;
        if (PatcherFacade.IsGameRunning())
        {
            SendPatcherSnapshot(error: "检测到游戏正在运行中，游戏占用了资源文件。请先完全关闭游戏客户端，然后再应用补丁。");
            return;
        }

        // 空列表表示「使用默认规则」（按 IdField 族名套用 36 条内置规则）；
        // 自动保存路径仍要跳过，否则配置页没读到客户端时会把自定义规则清掉。
        bool useDefaultRules = cmd.Items is null || cmd.Items.Count == 0;

        if (useDefaultRules && cmd.PersistOnly)
        {
            SendPatcherSnapshot(error: "尚未读取客户端宝箱，已跳过本次提交以保留已有配置。");
            return;
        }

        _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
        {
            ChestEnhanced = cmd.Enabled,
            ChestItems = useDefaultRules ? null : cmd.Items
        });

        // 只保存设置：自动保存路径不写客户端。
        if (cmd.PersistOnly) return;

        if (cmd.Enabled)
        {
            SendPatcherSnapshot(message: "正在准备应用保险箱掉落标注补丁，请稍候...");
            string? progressLog = null;
            await RecordAutoSnapshotAsync("宝箱标注写入");
            // 无自定义规则时传 null：ApplyNamePatch 会按 IdField 族名套用 36 条内置默认规则。
            // 与默认规则同一套键：IdField 族名。同族多个变体只保留一条，
            // 避免 ToDictionary 因重复 key 抛异常。
            Dictionary<string, string>? customMappings = useDefaultRules
                ? null
                : cmd.Items!
                    .Where(c => c.Enabled)
                    .GroupBy(c => string.IsNullOrWhiteSpace(c.FamilyName) ? c.OriginalName : c.FamilyName,
                             StringComparer.OrdinalIgnoreCase)
                    .Where(g => !string.IsNullOrWhiteSpace(g.Key))
                    .ToDictionary(g => g.Key, g => g.First().ModifiedName, StringComparer.OrdinalIgnoreCase);

            // 显式选择集合：未勾选的条目必须被真正禁用。
            // 只传 customMappings 不够 —— 未勾选的条目在字典里查不到，
            // 会落到「有默认 Memo 就启用」的分支，导致取消勾选无效。
            HashSet<string>? selectedChestNames = useDefaultRules
                ? null
                : new HashSet<string>(
                    cmd.Items!.Where(c => c.Enabled)
                        .Select(c => string.IsNullOrWhiteSpace(c.FamilyName) ? c.OriginalName : c.FamilyName),
                    StringComparer.OrdinalIgnoreCase);

            bool success = await Task.Run(() => _patcher.ApplyNamePatch(
                NamePatchKind.Chests,
                _settingsStore.ClientPatch.CustomGameDirectory,
                customMappings: customMappings,
                progress: msg =>
                {
                    progressLog = msg;
                    Dispatcher.Invoke(() => SendPatcherProgress(msg));
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

        bool useDefaultRules = cmd.Rules is null || cmd.Rules.Count == 0;

        // 自动保存路径下，空规则只代表「配置页还没读到客户端」，不能据此清空已有配置。
        // 真正的「使用默认规则」只发生在用户显式点击应用时。
        if (useDefaultRules && cmd.PersistOnly)
        {
            SendPatcherSnapshot(error: "尚未读取客户端词缀数据，已跳过本次提交以保留已有配置。");
            return;
        }

        _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
        {
            ColorModEnabled = cmd.Enabled,
            ColorModStyle = cmd.Style,
            ColorModRules = useDefaultRules ? null : cmd.Rules
        });

        // 只保存设置：自动保存路径不写客户端。
        if (cmd.PersistOnly) return;

        if (cmd.Enabled)
        {
            SendPatcherSnapshot(message: "正在准备应用装备与地图词缀多色彩染色补丁，请稍候...");
            string? progressLog = null;
            await RecordAutoSnapshotAsync("词缀变色写入");
            // 无自定义规则时传 null，由 PatcherFacade 自行 ExtractEntries 并用
            // DefaultColorMap 的 482 条默认规则；传空列表则会绕过这条默认路径。
            var entries = useDefaultRules ? null : cmd.Rules!.Select(ColorModEntry.FromRuleItem).ToList();

            bool success = await Task.Run(() => _patcher.ApplyColorMods(
                styleEnum,
                _settingsStore.ClientPatch.CustomGameDirectory,
                entries: entries,
                progress: msg =>
                {
                    progressLog = msg;
                    Dispatcher.Invoke(() => SendPatcherProgress(msg));
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

        // 只保存设置：自动保存路径不写客户端。
        if (cmd.PersistOnly) return;

        if (cmd.Enabled)
        {
            SendPatcherSnapshot(message: "正在准备生成异界神图醒目光柱，请稍候...");
            string? progressLog = null;
            await RecordAutoSnapshotAsync("神图光柱写入");
            var targets = cmd.Targets?.Select(t => new MapBeamTarget(t.MapName, t.DisplayName, t.Color, t.Enabled)).ToList();

            // 未配置自定义规则时使用默认规则：7 张内置地图。
            // 应用时找不到模型的地图会被跳过并汇总，不会因为客户端缺图而整体失败。
            if (targets is null || targets.Count == 0)
            {
                targets = FreiAtlas.Patcher.Modules.MapBeam.MapBeamPatchService.DefaultTargets.ToList();
            }

            bool success = await Task.Run(() => _patcher.ApplyMapBeams(
                targets: targets,
                preferredDirectory: _settingsStore.ClientPatch.CustomGameDirectory,
                progress: msg =>
                {
                    progressLog = msg;
                    Dispatcher.Invoke(() => SendPatcherProgress(msg));
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

        // 只保存设置：自动保存路径不写客户端。
        if (cmd.PersistOnly) return;

        // 直接注入会绕过预览计划，作废旧预览，避免之后误确认旧结果。
        _pendingPricePatch = null;

        var (priceRows, _, error) = BuildCurrentPriceRows(minExalted, unitMode, cmd.Categories);
        if (error is not null)
        {
            SendPatcherSnapshot(error: error);
            return;
        }

        await RecordAutoSnapshotAsync("物价补丁写入");

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
                Dispatcher.Invoke(() => SendPatcherProgress(msg));
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
                Dispatcher.Invoke(() => SendPatcherProgress(msg));
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

        await RecordAutoSnapshotAsync("物价补丁写入");

        string? progressLog = null;
        var result = await Task.Run(() => _patcher.ApplyPricePatch(
            pending.Rows,
            pending.Mode,
            pending.Separator,
            pending.GameDirectory,
            msg =>
            {
                progressLog = msg;
                Dispatcher.Invoke(() => SendPatcherProgress(msg));
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

        await RecordAutoSnapshotAsync("物价清洗");

        SendPatcherSnapshot(message: "正在清洗数据表中的物价后缀...");
        string? progressLog = null;
        var res = await Task.Run(() => _patcher.CleanPricePatch(
            cmd.Separator,
            _settingsStore.ClientPatch.CustomGameDirectory,
            msg =>
            {
                progressLog = msg;
                Dispatcher.Invoke(() => SendPatcherProgress(msg));
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

    /// <summary>
    /// 恢复纯净客户端，同时保住用户当前配置。
    ///
    /// 两个必须处理的问题：
    /// 1. 共享索引句柄会占用 Content.ggpk，恢复前必须释放，否则报「被其他进程占用」。
    /// 2. 恢复会用备份里的旧设置覆盖 settings.json，把用户刚勾选的功能与调好的参数清空，
    ///    因此恢复后必须把当前配置写回。
    /// </summary>
    private async Task<FreiAtlas.Patcher.Core.BackupRestoreResult> RestoreClientPreservingSettingsAsync(
        Action<string>? progress)
    {
        if (_patcher is null || _settingsStore is null)
        {
            return new FreiAtlas.Patcher.Core.BackupRestoreResult(false, "补丁器未就绪。");
        }

        var keep = _settingsStore.ClientPatch;
        _patcher.Engine.ReleaseClientIndex();

        var result = await Task.Run(() => _patcher.RestoreAllDetailed(keep.CustomGameDirectory, progress));

        // 恢复失败也要写回，避免界面勾选被清空。
        _settingsStore.UpdateClientPatch(keep);
        return result;
    }

    private async Task HandleRestoreAllPatchesAsync()
    {
        if (_patcher is null || _settingsStore is null) return;

        SendPatcherSnapshot(message: "正在准备恢复纯净客户端，请稍候...");
        string? progressLog = null;
        var result = await RestoreClientPreservingSettingsAsync(msg =>
        {
            progressLog = msg;
            Dispatcher.Invoke(() => SendPatcherProgress(msg));
        });

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

    /// <summary>
    /// 打补丁前记录自动快照。索引上百 MB，复制必须放在后台线程，
    /// 否则整个 GUI 会在补丁开始前卡住十几秒（窗口无法拖动/放大）。
    /// </summary>
    private async Task<BackupRestoreResult?> RecordAutoSnapshotAsync(string? patchActionName = null)
    {
        if (_patcher is null || _settingsStore is null) return null;

        // 批量应用在开始前统一记过一次快照，这里不再重复。
        if (_batchMode) return null;
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
        return await Task.Run(() =>
            _patcher.AutoSnapshotBeforePatch(info, patchActionName, settingsJson, activeModules));
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
                Dispatcher.Invoke(() => SendPatcherProgress(msg));
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
            // 红字在界面上一闪而过，必须同时落盘，否则事后无法定位。
            AppendProgressLog("ERROR", error);
            _patcherError = error;
            _patcherIssues.Add(error);
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
        string snapshotCurrentVersion = PatchBackupManager.ReadGameVersion(gameInfo);
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
                // 版本不匹配 = 禁止恢复的唯一依据。
                // 版本号缺失同样按不匹配处理：无法证明兼容就不允许写入。
                versionMismatch = string.IsNullOrEmpty(s.GameVersion)
                                  || string.IsNullOrEmpty(snapshotCurrentVersion)
                                  || !string.Equals(s.GameVersion, snapshotCurrentVersion, StringComparison.OrdinalIgnoreCase),
                // 指纹不一致只表示「快照之后客户端被改过」，是打完补丁的正常状态，
                // 仅用于提示，不参与禁用判断 —— 否则任何补丁之后就再也回滚不了。
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

    /// <summary>
    /// 只推送一条进度文本。
    ///
    /// 不走 SendPatcherSnapshot：那条路径会探测客户端、读取快照清单，并对整份
    /// _.index.bin 计算 SHA256（115~200 MB）。补丁写入期间进度消息可达上百条，
    /// 每条都重算一次会造成数十 GB 的无谓磁盘读取，而且全部压在 UI 线程上。
    /// </summary>
    private void SendPatcherProgress(string message)
    {
        // 模块内部的进度（含「规则命中统计」）也要落盘，否则事后无法复盘。
        AppendProgressLog("INFO", message);

        if (SettingsWebView.CoreWebView2 is null) return;

        _patcherMessage = message;
        // 不能在这里清空 _patcherError：模块刚报的错会被下一条进度消息抹掉，
        // 结果就是界面红字一闪而过、批量汇总却显示「全部成功」。

        var json = JsonSerializer.Serialize(
            new
            {
                version = 1,
                type = "patcherSnapshot",
                payload = new
                {
                    // 前端据此走轻量分支：只更新提示条，不覆盖完整状态。
                    progressOnly = true,
                    message
                }
            },
            JsonOptions);

        try
        {
            SettingsWebView.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (InvalidOperationException)
        {
            // WebView 正在关闭，忽略。
        }
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

        // 只保存设置：自动保存路径不写客户端。
        if (cmd.PersistOnly) return;

        LogBatchProgress($"[诊断] 商城(选择前): 名称={cmd.SelectedNames?.Count ?? -1}, 套装={(string.IsNullOrWhiteSpace(cmd.SelectedArmourSetName) ? "无" : cmd.SelectedArmourSetName)}, 传送门={(string.IsNullOrWhiteSpace(cmd.SelectedPortalName) ? "无" : cmd.SelectedPortalName)}, 宠物={(string.IsNullOrWhiteSpace(cmd.SelectedPetName) ? "无" : cmd.SelectedPetName)}, persistOnly={cmd.PersistOnly}");

        // 商城外观没有默认规则，必须手动选择条目。
        if ((cmd.SelectedNames is null || cmd.SelectedNames.Count == 0)
            && string.IsNullOrWhiteSpace(cmd.SelectedArmourSetName)
            && string.IsNullOrWhiteSpace(cmd.SelectedPortalName)
            && string.IsNullOrWhiteSpace(cmd.SelectedPetName))
        {
            SendPatcherSnapshot(error: "商城外观没有默认规则，请先手动勾选要替换的外观条目。");
            return;
        }

        LogBatchProgress($"[诊断] 商城: 名称={cmd.SelectedNames?.Count ?? -1}, 套装={(string.IsNullOrWhiteSpace(cmd.SelectedArmourSetName) ? "无" : cmd.SelectedArmourSetName)}, 传送门={(string.IsNullOrWhiteSpace(cmd.SelectedPortalName) ? "无" : cmd.SelectedPortalName)}, 宠物={(string.IsNullOrWhiteSpace(cmd.SelectedPetName) ? "无" : cmd.SelectedPetName)}");

        SendPatcherSnapshot(message: "正在准备应用外观商城特效替换，请稍候...");
        string? progressLog = null;
        await RecordAutoSnapshotAsync("商城外观特效替换");

        var options = new FreiAtlas.Patcher.MtxPatch.MtxPatchOptions
        {
            SelectedNames = new HashSet<string>(cmd.SelectedNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase),
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
                    Dispatcher.Invoke(() => SendPatcherProgress(msg));
                },
                out var m);
            return (r, m);
        });

        LogBatchProgress($"[诊断] 商城: ApplyMtxPatch 返回={ok}, 消息={message}");

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
        await RecordAutoSnapshotAsync("还原商城外观特效");

        var (ok, message) = await Task.Run(() =>
        {
            var r = _patcher.RestoreMtxPatch(
                _settingsStore.ClientPatch.CustomGameDirectory,
                msg =>
                {
                    progressLog = msg;
                    Dispatcher.Invoke(() => SendPatcherProgress(msg));
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
            // 未配置自定义规则时使用默认规则：内置图标包全量。
            // 这样用户只勾选「图标替换」、不进配置页，也能直接生效。
            // 设置里仍然保存空列表，保持「空 = 默认规则」的语义。
            // 占用检测已改为「独占失败后用共享模式二次探测」，不会再把自己的
            // 只读索引句柄误判成外部占用，因此这里不需要预先释放会话。
            LogBatchProgress($"[诊断] 图标: enabled={cmd.Enabled}, 传入路径={cmd.Paths?.Count ?? -1}, 内置可用={FreiAtlas.Patcher.IconPatch.IconPatchService.IsAvailable}");

            IReadOnlyList<string> configuredPaths = cmd.Paths ?? Array.Empty<string>();
            IReadOnlyList<string> paths = configuredPaths;
            if (cmd.Enabled && paths.Count == 0)
            {
                paths = FreiAtlas.Patcher.IconPatch.IconPatchService.DefaultPaths;
            }

            LogBatchProgress($"[诊断] 图标: 最终路径数={paths.Count}, 内置可用={FreiAtlas.Patcher.IconPatch.IconPatchService.IsAvailable}, 加载错误={FreiAtlas.Patcher.IconPatch.IconPatchService.LoadError ?? "无"}");

            // 更新设置前先取出上次选中的路径：取消勾选的图标需要写回官方原件，
            // 否则界面上勾选数变少，但客户端里仍是修改版。
            IReadOnlyList<string> previousPaths =
                _settingsStore.ClientPatch.IconPatchSelectedPaths ?? Array.Empty<string>();
            _settingsStore.UpdateClientPatch(_settingsStore.ClientPatch with
            {
                IconPatchEnabled = cmd.Enabled,
                IconPatchSelectedPaths = configuredPaths
            });

            // 只保存设置：自动保存路径不写客户端。
            if (cmd.PersistOnly) return;

            // 关闭开关或清空选择时必须真正把官方图标写回客户端。
            // 只改内存开关会让界面显示「已关闭」而磁盘仍是替换后的版本。
            LogBatchProgress($"[诊断] 图标: 分支判断 enabled={cmd.Enabled}, paths={paths.Count}, 已应用={_patcher.IsIconPatchApplied()}");

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
                        Dispatcher.Invoke(() => SendPatcherProgress(msg));
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
            await RecordAutoSnapshotAsync("图标替换写入前");
            IconPatchService service = _patcher.IconPatchService;
            string message = string.Empty;
            bool iconApplied = await Task.Run(() => service.Apply(gameInfo, paths, previousPaths, (string msg) =>
            {
                progressLog = msg;
                Dispatcher.Invoke(() => SendPatcherProgress(msg));
            }, out message));
            LogBatchProgress($"[诊断] 图标: Apply 返回={iconApplied}, 消息={message}");
            if (iconApplied)
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
            if (PatcherFacade.IsGameRunning())
            {
                SendPatcherSnapshot(error: "检测到游戏正在运行中，游戏占用了资源文件。请先完全关闭游戏客户端，然后再应用补丁。");
                return;
            }
            SendPatcherSnapshot("正在准备应用官方纯净简体中文汉化补丁，请稍候...");
            await RecordAutoSnapshotAsync("汉化补丁写入");
            string? progressLog = null;
            TranslationPatchResult translationPatchResult = await Task.Run(() => _patcher.ApplyTranslation(_settingsStore.ClientPatch.CustomGameDirectory, (string msg) =>
            {
                progressLog = msg;
                Dispatcher.Invoke(() => SendPatcherProgress(msg));
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
                (string msg) => { Dispatcher.Invoke(() => SendPatcherProgress(msg)); },
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
            if (PatcherFacade.IsGameRunning())
            {
                SendPatcherSnapshot(error: "检测到游戏正在运行中，游戏占用了资源文件。请先完全关闭游戏客户端，然后再应用补丁。");
                return;
            }
            SendPatcherSnapshot("正在应用头顶血条补丁，请稍候（写入期间请勿关闭窗口）...");
            var (flag, text) = await Task.Run(() => (r: _patcher.ApplyHealthBar(_settingsStore.ClientPatch.CustomGameDirectory, (string msg) =>
            {
                Dispatcher.Invoke(() => SendPatcherProgress(msg));
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
                Dispatcher.Invoke(() => SendPatcherProgress(msg));
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
            if (PatcherFacade.IsGameRunning())
            {
                SendPatcherSnapshot(error: "检测到游戏正在运行中，游戏占用了资源文件。请先完全关闭游戏客户端，然后再应用补丁。");
                return;
            }
            SendPatcherSnapshot("正在应用技能特效屏蔽，请稍候（写入期间请勿关闭窗口）...");
            var (flag, text) = await Task.Run(() => (r: _patcher.ApplySpellBlocks(cmd.SpellIds, cmd.IncludeAudio, _settingsStore.ClientPatch.CustomGameDirectory, (string msg) =>
            {
                Dispatcher.Invoke(() => SendPatcherProgress(msg));
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
                Dispatcher.Invoke(() => SendPatcherProgress(msg));
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

            // 只保存设置时不应因游戏运行被拦；真正写盘前才检查。
            if (!cmd.PersistOnly && PatcherFacade.IsGameRunning())
            {
                SendPatcherSnapshot(error: "检测到游戏正在运行中，游戏占用了资源文件。请先完全关闭游戏客户端，然后再应用补丁。");
                return;
            }

            if (cmd.PersistOnly)
            {
                // 特效模块的设置在正常流程里是「写入成功后」才落库的（只保存成功的那部分）。
                // 自动保存没有写入环节，这里无条件保存本次下发的参数。
                var saved = _settingsStore.ClientPatch with
                {
                    EffectPatchRuleIds = cmd.RuleIds,
                    ViewDistanceRatio = cmd.ViewDistanceRatio,
                    BlockedSpellIds = cmd.SpellIds,
                    SpellBlockIncludeAudio = cmd.IncludeAudio,
                    MudManTier = cmd.MudManTier
                };

                if (cmd.HealthBarEnabled is { } healthBar) saved = saved with { HealthBarEnabled = healthBar };

                _settingsStore.UpdateClientPatch(saved);
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
                Dispatcher.Invoke(() => SendPatcherProgress(msg));
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
                Dispatcher.Invoke(() => SendPatcherProgress(msg));
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

                // 未配置自定义规则时使用默认规则（33 条，已排除高危与不可用项）。
                // 设置里仍保存空列表，保持「空 = 默认规则」的语义。
                IReadOnlyList<string> effectiveRuleIds = cmd.RuleIds is { Count: > 0 }
                    ? cmd.RuleIds
                    : FreiAtlas.Patcher.EffectPatch.EffectPatchRules.DefaultRuleIds;

                if (effectiveRuleIds.Count > 0)
                {
                    flag = _patcher.ApplyEffectRules(effectiveRuleIds, _settingsStore.ClientPatch.CustomGameDirectory, (string msg) =>
                    {
                Dispatcher.Invoke(() => SendPatcherProgress(msg));
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
                Dispatcher.Invoke(() => SendPatcherProgress(msg));
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
                Dispatcher.Invoke(() => SendPatcherProgress(msg));
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
                Dispatcher.Invoke(() => SendPatcherProgress(msg));
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
                            Dispatcher.Invoke(() => SendPatcherProgress(msg));
                        }, out string hbMsg);
                        if (healthBarOk) list.Add(hbMsg);
                        else list2.Add("头顶血条应用失败：" + hbMsg);
                    }
                    else if (!wantHealthBar && applied)
                    {
                        healthBarOk = _patcher.RestoreHealthBar(_settingsStore.ClientPatch.CustomGameDirectory, (string msg) =>
                        {
                            Dispatcher.Invoke(() => SendPatcherProgress(msg));
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


    // ── 批量应用 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 批量应用的固定顺序。
    ///
    /// 顺序不是随意的：
    /// - 特效模块内部第一步是泥人，而泥人切档会回滚整个索引，必须最先执行；
    /// - 汉化是底稿重写（全部 CSD + 数据表），必须早于染色与物价；
    /// - 外观的防具套装是整份 .ao 替换，必须最后执行，否则会盖掉泥人对 items 目录的改动。
    /// </summary>
    private static readonly string[] BatchModuleOrder =
    [
        "effectpatch", "translation", "fontmod", "price", "colormod",
        "chests", "rumours", "mapbeam", "iconpatch", "mtxpatch"
    ];

    private static string BatchModuleDisplayName(string module) => module switch
    {
        "effectpatch" => "特效与性能优化",
        "translation" => "官方简中汉化",
        "fontmod" => "游戏字体与排版",
        "price" => "原生价格注入",
        "colormod" => "词缀多色彩染色",
        "chests" => "宝箱掉落标注",
        "rumours" => "异界地图传言",
        "mapbeam" => "神图冲天光柱",
        "iconpatch" => "图标替换",
        "mtxpatch" => "商城外观特效",
        _ => module
    };

    private static readonly object _progressLogLock = new();

    /// <summary>
    /// 把每一条进度与错误追加到 patch_progress.log。
    ///
    /// 界面上的红字一闪而过、且后续消息会把 _patcherError 清掉，
    /// 导致汇总里看不到真实失败原因；只有落盘才能事后复盘完整执行链。
    /// </summary>
    private static void AppendProgressLog(string level, string message)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreiAtlas");
            Directory.CreateDirectory(dir);
            lock (_progressLogLock)
            {
                File.AppendAllText(
                    Path.Combine(dir, "patch_progress.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}",
                    System.Text.Encoding.UTF8);
            }
        }
        catch
        {
            // 日志写入失败不影响补丁执行。
        }
    }

    private void LogBatchProgress(string message)
    {
        AppendProgressLog("INFO", message);
        Dispatcher.Invoke(() => SendPatcherProgress(message));
    }

    /// <summary>保存大类门控开关。自动保存路径，不写客户端。</summary>
    private void HandleSetBatchCategory(SetBatchCategoryWebCommand cmd)
    {
        if (_settingsStore is null) return;

        var patch = _settingsStore.ClientPatch;
        bool enabled = cmd.Enabled;

        patch = cmd.Category.ToLowerInvariant() switch
        {
            "translation" => patch with { BatchCategoryTranslation = enabled },
            "text" => patch with { BatchCategoryText = enabled },
            "effect" => patch with { BatchCategoryEffect = enabled },
            "mtx" => patch with { BatchCategoryMtx = enabled },
            _ => patch
        };

        _settingsStore.UpdateClientPatch(patch);
    }

    /// <summary>保存模块启用开关。自动保存路径，不写客户端。</summary>
    private void HandleSetModuleEnabled(SetModuleEnabledWebCommand cmd)
    {
        if (_settingsStore is null) return;

        var patch = _settingsStore.ClientPatch;
        bool enabled = cmd.Enabled;

        patch = cmd.Module.ToLowerInvariant() switch
        {
            "translation" => patch with { TranslationEnabled = enabled },
            "fontmod" => patch with { FontPatchEnabled = enabled },
            "colormod" => patch with { ColorModEnabled = enabled },
            "chests" => patch with { ChestEnhanced = enabled },
            "rumours" => patch with { RumourEnhanced = enabled },
            "price" => patch with { PricePatchEnabled = enabled },
            "mapbeam" => patch with { MapBeamEnabled = enabled },
            "iconpatch" => patch with { IconPatchEnabled = enabled },
            "effectpatch" => patch with { EffectPatchEnabled = enabled },
            "mtxpatch" => patch with { MtxPatchEnabled = enabled },
            _ => patch
        };

        _settingsStore.UpdateClientPatch(patch);
    }

    /// <summary>
    /// 批量应用：先确保基线可用（必要时恢复），再按固定顺序打勾选的模块。
    ///
    /// 为什么批量前要恢复基线：各模块都是增量写入，取消勾选某个模块后如果不先回滚，
    /// 该模块此前的改动会一直留在客户端里，界面显示已关闭而实际仍生效。
    /// </summary>
    private async Task HandleApplyBatchAsync(ApplyBatchWebCommand cmd)
    {
        if (_patcher is null || _settingsStore is null) return;

        var modules = cmd.Modules
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();

        if (modules.Count == 0)
        {
            SendPatcherSnapshot(error: "未勾选任何要应用的功能。");
            return;
        }

        if (PatcherFacade.IsGameRunning())
        {
            SendPatcherSnapshot(error: "检测到游戏正在运行中，游戏占用了资源文件。请先完全关闭游戏客户端，然后再应用补丁。");
            return;
        }

        // 分段计时：每个阶段结束时打印一行，汇总里再按耗时排序输出。
        var batchTimer = System.Diagnostics.Stopwatch.StartNew();
        var stageTimer = System.Diagnostics.Stopwatch.StartNew();
        var timings = new List<(string Name, long Ms)>();

        _patcher.Engine.ModuleWriteCounts.Clear();

        void MarkStage(string name, long? overrideMs = null)
        {
            long ms = overrideMs ?? stageTimer.ElapsedMilliseconds;
            stageTimer.Restart();
            timings.Add((name, ms));
            LogBatchProgress($"⏱ {name}: {ms:N0} ms");
        }

        var info = _patcher.GetGameInfo(_settingsStore.ClientPatch.CustomGameDirectory);
        if (!info.IsValid)
        {
            SendPatcherSnapshot(error: $"未检测到有效的 PoE2 游戏目录：{info.ErrorMessage}");
            return;
        }

        // ── 0. 客户端自检：索引与资源对不上时，任何写入都只会产生连锁错误 ──
        var selfCheckError = _patcher.Engine.VerifyClientReadable(info);
        if (selfCheckError is not null)
        {
            SendPatcherSnapshot(error: selfCheckError);
            return;
        }

        MarkStage("客户端检测与校验");

        // ── 1. 物价前置：数据过期直接中止整批 ──
        if (modules.Contains("price"))
        {
            var book = _prices?.CurrentBook;
            if (book is null || book.ItemCount == 0)
            {
                SendPatcherSnapshot(error: "尚未拉取到物价数据。请先在【物价注入】页刷新物价，或取消勾选物价注入。");
                return;
            }

            if (!PatchPreconditions.IsPriceDataFresh(book.FetchedAtUtc, DateTimeOffset.UtcNow, PatchPreconditions.PriceDataMaxAge))
            {
                var age = DateTimeOffset.UtcNow - book.FetchedAtUtc;
                SendPatcherSnapshot(error:
                    $"物价数据已过期（上次更新 {age.TotalHours:F1} 小时前，上限 {PatchPreconditions.PriceDataMaxAge.TotalHours:0} 小时）。" +
                    "请刷新物价后重试，或取消勾选物价注入。");
                return;
            }
        }

        MarkStage("物价数据检查");

        // ── 2. 基线决策 ──
        var baseline = _patcher.BackupManager.CheckBaseline(info, out var officialVersion, out var currentVersion);
        var detectedPrefixes = _patcher.BackupManager.DetectPatchPrefixes(info);

        // GGPK 模式下补丁 bundle 与原始 bundle 共用 Bundles2/LibGGPK3/ 命名空间，
        // 前缀匹配永远返回空。改用索引指纹判断，两种模式都可靠；指纹不可用时才回退到前缀。
        // 前缀探测返回 null 表示「无法判定」：此时按「可能已打补丁」保守处理，
        // 避免把来源不明的客户端固化成纯净底包。
        bool? patchedByFingerprint = _patcher.BackupManager.IsClientPatched(info);
        bool clientDirty = patchedByFingerprint ?? (detectedPrefixes is null || detectedPrefixes.Count > 0);

        MarkStage("基线检查");

        bool baselineReady;
        bool needRestore;

        if (baseline == BaselineStatus.Ready)
        {
            baselineReady = true;
            needRestore = clientDirty;
        }
        else if (!clientDirty)
        {
            // 客户端本身干净：建/重建基线即可，建完就是当前状态，无需再恢复。
            LogBatchProgress(baseline == BaselineStatus.Missing
                ? "正在为当前客户端建立纯净基线..."
                : "游戏版本已变化，正在重建纯净基线...");

            baselineReady = baseline == BaselineStatus.Missing
                ? _patcher.BackupManager.EnsureBackup(info, LogBatchProgress)
                : _patcher.BackupManager.RebuildBaselineAsIs(info, LogBatchProgress);
            needRestore = false;
        }
        else
        {
            // 客户端带补丁，且基线缺失或版本不符：必须由用户决定。
            string detail = baseline == BaselineStatus.Missing
                ? "尚未建立纯净基线"
                : $"现有基线版本 {officialVersion} 与当前客户端 {currentVersion} 不一致";

            var choice = MessageBox.Show(
                $"检测到客户端已包含补丁记录：{string.Join("、", detectedPrefixes ?? [])}\n"
                + $"{detail}。\n\n"
                + "选择【是】：以当前状态建立基线并继续应用补丁。\n"
                + "  当前已有的补丁会被固化进基线；之后第三方工具更新或卸载，基线可能失效。\n\n"
                + "选择【否】：取消本次操作。建议先在 Steam / 官方平台点一次【验证游戏完整性】。",
                "批量应用 — 需要确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (choice != MessageBoxResult.Yes)
            {
                SendPatcherSnapshot(message: "已取消批量应用，未做任何写入。");
                return;
            }

            baselineReady = _patcher.BackupManager.RebuildBaselineAsIs(info, LogBatchProgress);
            needRestore = false;
        }

        if (!baselineReady)
        {
            SendPatcherSnapshot(error: "无法建立可用的纯净基线，已中止批量应用。");
            return;
        }

        MarkStage("基线准备");

        // ── 3. 恢复基线（仅客户端已带补丁时）──
        if (needRestore)
        {
            LogBatchProgress("正在恢复纯净基线...");
            var restore = await RestoreClientPreservingSettingsAsync(LogBatchProgress);

            if (!restore.Success)
            {
                SendPatcherSnapshot(error: $"恢复纯净基线失败：{restore.Message}");
                return;
            }

            // 索引已整体回滚，各模块的瞬态标记与逐文件备份必须一并清掉，
            // 否则泥人 / 血条会误判「已应用」而跳过写入。
            _patcher.ClearTransientPatchStates();
        }

        MarkStage("恢复纯净底包");

        // ── 4. 整批只记一次快照 ──
        await RecordAutoSnapshotAsync("批量应用补丁");
        MarkStage("自动快照");

        // ── 5. 按固定顺序应用 ──
        //
        // 文本类六个模块（汉化/字体/物价/染色/宝箱/传言）全部是「读文件 → 改字节 → 写文件」，
        // 串行执行会把整份索引重写六次。这里把它们放进一个写盘会话：
        // 每个模块的写入只登记到内存，后一个模块读到的是前一个模块的输出，
        // 六个模块跑完后一次性落盘。
        //
        // 特效与外观不在会话内：泥人切档会回滚整个索引，外观是整份 .ao 替换，
        // 两者都不适合与其它模块叠加。
        var selectedOrder = BatchModuleOrder.Where(modules.Contains).ToList();
        // 会话内模块：共用一次索引落盘。
        // 图标 / 光柱 / 商城会写入大量二进制资源，合并后能省下 2~3 次整索引重写；
        // 读缓存已按索引文件时间戳失效，不会再出现「读到旧快照而静默跳过」。
        // 特效（effectpatch）必须排除 —— 泥人档位会注入 3.2 万张贴图，
        // 塞进 512MB 上限的内存会话会造成内存雪崩，只能独立写。
        var sessionModules = new HashSet<string>(StringComparer.Ordinal)
        {
            "translation", "fontmod", "price", "colormod", "chests", "rumours",
            "mapbeam", "iconpatch", "mtxpatch"
        };
        int firstSession = selectedOrder.FindIndex(m => sessionModules.Contains(m));
        int lastSession = selectedOrder.FindLastIndex(m => sessionModules.Contains(m));

        // 汇总里标注每个模块本次用的是默认规则还是自定义规则，出问题便于判断来源。
        // 必须在循环前算好：各模块处理器在应用过程中会改写设置。
        string DescribeRuleSource(string module)
        {
            var patch = _settingsStore!.ClientPatch;
            return module switch
            {
                "colormod" => patch.ColorModRules is { Count: > 0 } r ? $"自定义 {r.Count} 条" : "默认规则 482 条",
                "chests" => patch.ChestItems is { Count: > 0 } c ? $"自定义 {c.Count} 条" : "默认规则",
                "rumours" => patch.RumourItems is { Count: > 0 } u ? $"自定义 {u.Count} 条" : "默认规则 19 条",
                "iconpatch" => patch.IconPatchSelectedPaths is { Count: > 0 } i ? $"自定义 {i.Count} 个" : "默认全量",
                "effectpatch" => patch.EffectPatchRuleIds is { Count: > 0 } e ? $"自定义 {e.Count} 条" : "默认规则 33 条",
                "mapbeam" => patch.MapBeamTargets is { Count: > 0 } m ? $"自定义 {m.Count} 张" : "默认 7 张",
                "mtxpatch" => "手动选择",
                _ => "内置"
            };
        }

        var ruleSourceTags = selectedOrder.ToDictionary(m => m, DescribeRuleSource);

        var succeeded = new List<string>();
        var failed = new List<string>();
        _patcherIssues.Clear();

        _batchMode = true;
        bool sessionOpen = false;
        // 文本类会话是否因失败被整组回滚。汇总里必须说明，否则用户只看得到失败的那一个模块。
        bool sessionAborted = false;
        try
        {
            for (int index = 0; index < selectedOrder.Count; index++)
            {
                string module = selectedOrder[index];
                _patcherError = null;
                int issueMark = _patcherIssues.Count;
                LogBatchProgress($"正在应用：{BatchModuleDisplayName(module)} ...");

                if (index == firstSession)
                {
                    _patcher.Engine.BeginSession();
                    sessionOpen = true;
                }

                var moduleTimer = System.Diagnostics.Stopwatch.StartNew();
                _patcher.Engine.CurrentModuleTag = BatchModuleDisplayName(module);
                await DispatchBatchModuleAsync(module, cmd);
                int moduleWrites = _patcher.Engine.ModuleWriteCounts.TryGetValue(
                    BatchModuleDisplayName(module), out var wc) ? wc : 0;

                // 以累积列表判定成败：_patcherError 可能已被后续 message 快照清空，
                // 单看它会把「报错后又推了条进度」的模块误判为成功。
                string moduleIssues = string.Join("；", _patcherIssues.Skip(issueMark));
                LogBatchProgress($"[汇总] {BatchModuleDisplayName(module)}: 写入 {moduleWrites} 个文件, 错误={(moduleIssues.Length == 0 ? "无" : moduleIssues)}");
                _patcher.Engine.CurrentModuleTag = null;
                MarkStage(BatchModuleDisplayName(module), moduleTimer.ElapsedMilliseconds);

                if (moduleIssues.Length == 0)
                {
                    succeeded.Add($"{BatchModuleDisplayName(module)}（{ruleSourceTags[module]}）");
                }
                else
                {
                    failed.Add($"{BatchModuleDisplayName(module)}：{moduleIssues}");
                }

                if (index != lastSession) continue;

                if (failed.Count == 0)
                {
                    LogBatchProgress("正在把会话内改动一次性写入客户端 ...");
                    bool committed = _patcher.Engine.CommitSession(info);
                    sessionOpen = false;
                    MarkStage("会话统一写入");

                    if (!committed)
                    {
                        failed.Add($"会话统一写入失败：{_patcher.Engine.LastWriteException?.Message ?? "未知原因"}");
                    }
                }
                else
                {
                    _patcher.Engine.AbortSession();
                    sessionOpen = false;
                    sessionAborted = true;
                    LogBatchProgress("会话内改动已放弃写入（前面有模块失败）。");
                }
            }
        }
        finally
        {
            if (sessionOpen) _patcher.Engine.AbortSession();
            _batchMode = false;
        }

        // ── 6. 汇总 ──
        string summary = failed.Count == 0
            ? $"批量应用完成：{succeeded.Count} 项全部成功（{string.Join("、", succeeded)}）。"
            : $"批量应用结束：成功 {succeeded.Count} 项，失败 {failed.Count} 项。\n失败明细：{string.Join("；", failed)}";

        // 分段耗时：降序输出，并写文件便于事后分析。
        long totalMs = batchTimer.ElapsedMilliseconds;
        var timingText = new System.Text.StringBuilder();
        timingText.AppendLine($"总耗时：{totalMs:N0} ms（{totalMs / 1000.0:F1} 秒）");
        foreach (var (name, ms) in timings.OrderByDescending(t => t.Ms))
        {
            double pct = totalMs > 0 ? ms * 100.0 / totalMs : 0;
            timingText.AppendLine($"  {name,-16} {ms,8:N0} ms  ({pct,5:F1}%)");
        }

        summary += "\n\n阶段耗时（降序）：\n" + timingText.ToString().TrimEnd();

        try
        {
            string timingDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreiAtlas");
            Directory.CreateDirectory(timingDir);
            File.AppendAllText(
                Path.Combine(timingDir, "patch_timing.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {summary}\n{new string('-', 60)}\n",
                System.Text.Encoding.UTF8);
        }
        catch (Exception timingEx)
        {
            // 计时日志写失败不影响补丁结果，但要在进度区留痕。
            LogBatchProgress("计时日志写入失败：" + timingEx.Message);
        }

        if (_patcher.Engine.ModuleWriteCounts.Count > 0)
        {
            var countText = new System.Text.StringBuilder("\n\n写入文件数：");
            foreach (var kv in _patcher.Engine.ModuleWriteCounts.OrderByDescending(k => k.Value))
            {
                countText.Append($"\n  {kv.Key,-16} {kv.Value,6} 个");
            }
            summary += countText.ToString();
        }

        if (sessionAborted)
        {
            summary += "\n注意：会话内模块共用一次写入，任一失败会整组回滚 —— 汉化、字体、物价、染色、宝箱、传言、光柱、图标、商城外观本次均未写入客户端。";
        }

        if (failed.Count == 0) SendPatcherSnapshot(message: summary);
        else SendPatcherSnapshot(error: summary);
    }

    /// <summary>把单个模块交给它自己的处理器，参数由批量命令里嵌套的同形 payload 提供。</summary>
    private async Task DispatchBatchModuleAsync(string module, ApplyBatchWebCommand cmd)
    {
        switch (module)
        {
            case "effectpatch" when cmd.Effects is not null:
                await HandleApplyAllEffectsAsync(cmd.Effects);
                break;

            case "translation":
                await HandleApplyTranslationPatchAsync();
                break;

            case "fontmod" when cmd.Font is not null:
                await HandleApplyFontPatchAsync(cmd.Font);
                break;

            case "price" when cmd.Price is not null:
                await HandleApplyCurrentPricesPatchAsync(cmd.Price);
                break;

            case "colormod" when cmd.ColorMod is not null:
                await HandleApplyColorModPatchAsync(cmd.ColorMod);
                break;

            case "chests" when cmd.Chests is not null:
                await HandleApplyChestPatchAsync(cmd.Chests);
                break;

            case "rumours" when cmd.Rumours is not null:
                await HandleApplyRumourPatchAsync(cmd.Rumours);
                break;

            case "mapbeam" when cmd.MapBeam is not null:
                await HandleApplyMapBeamPatchAsync(cmd.MapBeam);
                break;

            // 批量勾选本身就是「启用」意图：强制 Enabled=true，
            // 否则会被各模块自己的总开关（默认关闭）拦下，表现为「显示成功但没写入」。
            case "iconpatch" when cmd.Icon is not null:
                await HandleApplyIconPatchAsync(cmd.Icon);
                break;

            case "mtxpatch" when cmd.Mtx is not null:
                await HandleApplyMtxPatchAsync(cmd.Mtx);
                break;

            default:
                _patcherError = "缺少该模块的参数，已跳过。";
                break;
        }
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
