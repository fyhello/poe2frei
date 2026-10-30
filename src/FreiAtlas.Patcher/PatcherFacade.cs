using System.Diagnostics;
using FreiAtlas.Patcher.EffectPatch.HealthBar;
using FreiAtlas.Patcher.EffectPatch.MudMan;
using FreiAtlas.Patcher.Core;
using FreiAtlas.Patcher.Modules.ColorMod;
using FreiAtlas.Patcher.Modules.Price;
using FreiAtlas.Patcher.Modules.Font;
using FreiAtlas.Patcher.Modules.Name;
using FreiAtlas.Patcher.Modules.Rumour;
using FreiAtlas.Patcher.Modules.MapBeam;

namespace FreiAtlas.Patcher;

/// <summary>启动时版本校验的结果。</summary>
public sealed class BackupVersionCheck
{
    /// <summary>当前客户端版本号（如 0.5.5e），读取失败时为空。</summary>
    public string CurrentVersion { get; set; } = string.Empty;

    /// <summary>纯净底包是否已对应当前游戏版本。</summary>
    public bool OfficialBackupReady { get; set; }

    /// <summary>本次清理掉的过期快照数量。</summary>
    public int PurgedSnapshots { get; set; }

    /// <summary>给用户看的说明。</summary>
    public string Message { get; set; } = string.Empty;
}

public sealed class PatcherFacade
{
    private readonly PatchBackupManager _backupManager;
    private readonly PatchEngine _engine;
    private readonly FontPatchService _fontService;
    private readonly RumourPatchService _rumourService;
    private readonly NamePatchService _nameService;
    private readonly ColorModService _colorModService;
    private readonly FreiAtlas.Patcher.IconPatch.IconPatchService _iconPatchService;
    private readonly PricePatchService _priceService;
    private readonly MapBeamPatchService _mapBeamService;
    private readonly FreiAtlas.Patcher.Translation.TranslationPatchService _translationService;
    private readonly FreiAtlas.Patcher.EffectPatch.EffectPatchApplier _effectPatchApplier;
    private readonly FreiAtlas.Patcher.EffectPatch.SpellEffectBlocker _spellEffectBlocker;
    private readonly FreiAtlas.Patcher.EffectPatch.MudMan.MudManApplier _mudManApplier;
    private readonly FreiAtlas.Patcher.EffectPatch.HealthBar.HealthBarApplier _healthBarApplier;
    private readonly FreiAtlas.Patcher.MtxPatch.MtxPatchApplier _mtxPatchApplier;

    public PatchBackupManager BackupManager => _backupManager;

    /// <summary>补丁备份目录是否可用；不可用时补丁页应展示原因而不是让整个设置窗口退出。</summary>
    public bool IsBackupAvailable => _backupManager.IsAvailable;

    /// <summary>补丁备份目录不可用的原因（可用时为 null）。</summary>
    public string? BackupUnavailableReason => _backupManager.UnavailableReason;

    public PatchEngine Engine => _engine;
    public FontPatchService FontService => _fontService;
    public RumourPatchService RumourService => _rumourService;
    public NamePatchService NameService => _nameService;
    public ColorModService ColorModService => _colorModService;
    public FreiAtlas.Patcher.IconPatch.IconPatchService IconPatchService => _iconPatchService;
    public PricePatchService PriceService => _priceService;
    public MapBeamPatchService MapBeamService => _mapBeamService;
    public FreiAtlas.Patcher.Translation.TranslationPatchService TranslationService => _translationService;

    public PatcherFacade(string? customBackupDir = null)
    {
        _backupManager = new PatchBackupManager(customBackupDir);
        _engine = new PatchEngine(_backupManager);
        _fontService = new FontPatchService(_engine);
        _rumourService = new RumourPatchService(_engine);
        _nameService = new NamePatchService(_engine);
        _colorModService = new ColorModService(_engine);
        _iconPatchService = new FreiAtlas.Patcher.IconPatch.IconPatchService(_engine);
        _priceService = new PricePatchService(_engine);
        _mapBeamService = new MapBeamPatchService(_engine);
        _translationService = new FreiAtlas.Patcher.Translation.TranslationPatchService(_engine);
        _effectPatchApplier = new FreiAtlas.Patcher.EffectPatch.EffectPatchApplier(_engine);
        _spellEffectBlocker = new FreiAtlas.Patcher.EffectPatch.SpellEffectBlocker(_engine);
        _mudManApplier = new FreiAtlas.Patcher.EffectPatch.MudMan.MudManApplier(_engine, _backupManager);
        _healthBarApplier = new FreiAtlas.Patcher.EffectPatch.HealthBar.HealthBarApplier(_engine, _backupManager);
        _mtxPatchApplier = new FreiAtlas.Patcher.MtxPatch.MtxPatchApplier(_engine);
    }

    /// <summary>
    /// PoE2 客户端可能使用的进程名。
    ///
    /// 必须覆盖所有发行渠道，否则「游戏正在运行」的判定会漏判，
    /// 导致补丁在游戏占用资源文件时强行写入。
    ///   · PathOfExile / PathOfExile_x64 —— 官方独立客户端
    ///   · PathOfExileSteam —— Steam 版
    ///   · PathOfExile_KG —— 韩服（Kakao Games）
    ///   · PathOfExileEGS —— Epic Games Store
    ///   · Client —— 部分渠道的通用可执行名
    ///
    /// 该列表与 FreiAtlas.Host 的 Poe2ProcessCandidateReader.SupportedProcessNames
    /// 保持同步。两处分别位于不同项目，无法共享常量，改动时需同时更新。
    /// </summary>
    public static readonly IReadOnlyList<string> PoE2ProcessNames =
    [
        "PathOfExile",
        "PathOfExile_x64",
        "PathOfExileSteam",
        "PathOfExile_KG",
        "PathOfExileEGS",
        "Client"
    ];

    private static readonly object GameRunningGate = new();
    private static DateTime _gameRunningCheckedAtUtc = DateTime.MinValue;
    private static bool _gameRunningCached;

    /// <summary>
    /// 检测游戏是否在运行。
    ///
    /// 结果缓存 1 秒：批量与快照推送会连续调用几十次，每次都枚举 6 个进程名
    /// 既慢又会泄漏 Process 句柄。Process 对象必须 Dispose，
    /// 否则单次用户操作就能累积上百个未释放的句柄。
    /// </summary>
    public static bool IsGameRunning()
    {
        lock (GameRunningGate)
        {
            if (DateTime.UtcNow - _gameRunningCheckedAtUtc < TimeSpan.FromSeconds(1))
            {
                return _gameRunningCached;
            }
        }

        bool running = false;
        foreach (var name in PoE2ProcessNames)
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Length > 0)
                {
                    running = true;
                    break;
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        lock (GameRunningGate)
        {
            _gameRunningCached = running;
            _gameRunningCheckedAtUtc = DateTime.UtcNow;
        }
        return running;
    }

    public IReadOnlyList<FontItem> GetInstalledFonts() => FontPatchService.GetInstalledFonts();

    public GameModeInfo GetGameInfo(string? preferredDirectory = null)
    {
        return GameInstallDetector.Detect(preferredDirectory);
    }

    public bool HasBackup(string? preferredDirectory = null)
    {
        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid) return false;
        return _backupManager.HasBackup(info);
    }

    /// <summary>
    /// 用已探测好的客户端信息查询备份状态，避免重复探测。
    ///
    /// GameInstallDetector.Detect 每次会枚举进程、读注册表、扫十多条路径；
    /// 快照类查询需要连续做「是否有备份 / 列出快照 / 探测客户端」三件事，
    /// 复用同一个 GameModeInfo 可把三次探测降为一次。
    /// </summary>
    public bool HasBackup(GameModeInfo info) => info.IsValid && _backupManager.HasBackup(info);

    public BackupRestoreResult RestoreAllDetailed(string? preferredDirectory = null, Action<string>? progress = null)
    {
        if (IsGameRunning())
        {
            const string msg = "检测到游戏正在运行中，游戏占用了资源文件。请先退出游戏后再执行还原。";
            progress?.Invoke(msg);
            return new BackupRestoreResult(false, msg);
        }

        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid)
        {
            string msg = $"未检测到有效的 PoE2 游戏目录：{info.ErrorMessage}";
            progress?.Invoke(msg);
            return new BackupRestoreResult(false, msg);
        }

        progress?.Invoke("正在检查客户端状态并准备恢复纯净原版备份...");
        // 整体替换索引前先让引擎缓存失效，避免后续读取拿到回滚前的旧索引。
        _engine.ReleaseClientIndex();
        var res = _backupManager.Restore(info, progress);
        progress?.Invoke(res.Message);
        return res;
    }

    /// <summary>
    /// 清理各补丁模块的瞬态标记与逐文件备份。
    ///
    /// 客户端索引被整体回滚成纯净版本后，磁盘上仍留着各模块自己的状态文件
    /// （mudman/state.json、healthbar/state.json）和逐文件备份。若不清掉：
    ///
    /// - 泥人：IsMudManApplied() 仍返回 true，再次应用同一档位时被判定为
    ///   「已应用、无需写入」而直接跳过，用户再也打不上泥人；
    /// - 血条 / 图标：界面一直显示已开启，与实际客户端不符；
    /// - 特效 / 外观 / 技能屏蔽：逐文件备份指向已不存在的索引条目，
    ///   残留占用空间，且与「已恢复纯净客户端」的实际状态脱节。
    ///
    /// 必须在索引还原成功之后调用，否则会把「本想保留的还原点」一并删掉。
    /// </summary>
    public void ClearTransientPatchStates()
    {
        FreiAtlas.Patcher.EffectPatch.MudMan.MudManApplier.ClearState();
        FreiAtlas.Patcher.EffectPatch.HealthBar.HealthBarApplier.ClearState();

        PatchFileBackup.Clear(FreiAtlas.Patcher.EffectPatch.EffectPatchApplier.BackupGroup);
        PatchFileBackup.Clear(FreiAtlas.Patcher.MtxPatch.MtxPatchApplier.BackupGroup);
        PatchFileBackup.Clear(FreiAtlas.Patcher.IconPatch.IconPatchService.BackupGroup);
        PatchFileBackup.Clear(FreiAtlas.Patcher.EffectPatch.SpellEffectBlocker.BackupGroup);
        // 旧版本技能屏蔽曾用独立分组存放闪电球特效，现已并入 spell_block_v2。
        // 老用户机器上可能仍有残留，一并清掉。
        PatchFileBackup.Clear(FreiAtlas.Patcher.EffectPatch.SpellEffectBlocker.LegacyLightningSparkGroup);
    }

    /// <summary>是否有可还原的图标备份。</summary>
    public bool IsIconPatchApplied() => FreiAtlas.Patcher.IconPatch.IconPatchService.HasBackup();

    /// <summary>已备份的图标数量。</summary>
    public int IconPatchedFileCount() => FreiAtlas.Patcher.IconPatch.IconPatchService.BackupCount();

    /// <summary>
    /// 把此前备份的官方图标写回客户端。
    ///
    /// 与其它模块的还原一致：先过游戏运行检查，再取游戏信息，
    /// 交给服务层完成实际写入。
    /// </summary>
    public FreiAtlas.Patcher.IconPatch.IconPatchRestoreResult RestoreIconPatch(
        string? preferredDirectory = null,
        Action<string>? progress = null)
    {
        if (IsGameRunning())
        {
            const string msg = "检测到游戏正在运行中，游戏占用了资源文件。请先退出游戏后再执行还原。";
            progress?.Invoke(msg);
            return FreiAtlas.Patcher.IconPatch.IconPatchRestoreResult.Failure(msg);
        }

        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid)
        {
            string msg = $"未检测到有效的 PoE2 游戏目录：{info.ErrorMessage}";
            progress?.Invoke(msg);
            return FreiAtlas.Patcher.IconPatch.IconPatchRestoreResult.Failure(msg);
        }

        return _iconPatchService.Restore(info, progress, out _);
    }

    public FreiAtlas.Patcher.Translation.TranslationPatchResult ApplyTranslation(string? preferredDirectory = null, Action<string>? progress = null)
    {
        if (IsGameRunning())
        {
            return new FreiAtlas.Patcher.Translation.TranslationPatchResult
            {
                Success = false,
                ErrorMessage = "检测到游戏正在运行，游戏占用了资源文件。请先退出游戏后再执行汉化补丁。"
            };
        }

        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid)
        {
            return new FreiAtlas.Patcher.Translation.TranslationPatchResult
            {
                Success = false,
                ErrorMessage = $"未检测到有效的 PoE2 游戏目录：{info.ErrorMessage}"
            };
        }

        return _translationService.ApplyTranslationPatch(info, progress);
    }

    public IReadOnlyList<PatchSnapshotInfo> GetSnapshots(string? preferredDirectory = null)
    {
        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid) return [];
        return _backupManager.GetSnapshots(info);
    }

    /// <summary>用已探测好的客户端信息列出快照，避免重复探测。</summary>
    public IReadOnlyList<PatchSnapshotInfo> GetSnapshots(GameModeInfo info)
        => info.IsValid ? _backupManager.GetSnapshots(info) : [];

    /// <summary>
    /// 启动时的版本校验：确认纯净底包对应当前游戏版本，并清理已过期的历史快照。
    ///
    /// 游戏更新后旧底包会把客户端还原成旧版本资源，直接导致无法启动，因此每次启动
    /// 都要确认底包仍然匹配。这里不抛异常，失败只记录日志，不阻断软件启动。
    /// </summary>
    /// <param name="staleRetention">过期快照的保留时长，超时才删除。</param>
    public BackupVersionCheck VerifyBackupVersion(
        TimeSpan staleRetention,
        string? preferredDirectory = null,
        Action<string>? progress = null)
    {
        var report = new BackupVersionCheck();
        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid || !_backupManager.IsAvailable)
        {
            report.Message = _backupManager.IsAvailable
                ? "未检测到有效的游戏目录，跳过版本校验。"
                : _backupManager.UnavailableReason ?? "补丁备份目录不可用。";
            return report;
        }

        report.CurrentVersion = PatchBackupManager.ReadGameVersion(info);
        report.PurgedSnapshots = _backupManager.PurgeStaleSnapshots(info, staleRetention, progress);

        bool hadOfficial = _backupManager.GetSnapshots(info)
            .Any(s => s.Type == PatchSnapshotType.Official);

        report.OfficialBackupReady = _backupManager.EnsureOfficialBackupForCurrentVersion(info, progress);

        if (report.OfficialBackupReady && !hadOfficial)
        {
            report.Message = $"游戏版本 {report.CurrentVersion} 的纯净底包已自动建立。";
        }
        else if (report.OfficialBackupReady && hadOfficial)
        {
            report.Message = $"纯净底包已对应当前版本 {report.CurrentVersion}。";
        }
        else if (!report.OfficialBackupReady)
        {
            report.Message = "当前客户端带有补丁，暂无法建立纯净底包；恢复纯净后下次启动会自动补齐。";
        }
        return report;
    }

    public BackupRestoreResult CreateSnapshot(
        PatchSnapshotType type,
        string name,
        string description,
        string? settingsJson = null,
        List<string>? activeModules = null,
        string? preferredDirectory = null)
    {
        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid) return new BackupRestoreResult(false, "未检测到有效的 PoE2 游戏目录。");
        return _backupManager.CreateSnapshot(info, type, name, description, settingsJson, activeModules);
    }

    public BackupRestoreResult RestoreSnapshot(
        string snapshotId,
        out string? restoredSettingsJson,
        string? preferredDirectory = null,
        Action<string>? progress = null)
    {
        restoredSettingsJson = null;
        if (IsGameRunning())
        {
            const string msg = "检测到游戏正在运行中，游戏占用了资源文件。请先退出游戏后再执行还原。";
            progress?.Invoke(msg);
            return new BackupRestoreResult(false, msg);
        }

        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid)
        {
            string msg = $"未检测到有效的 PoE2 游戏目录：{info.ErrorMessage}";
            progress?.Invoke(msg);
            return new BackupRestoreResult(false, msg);
        }

        progress?.Invoke("正在检查客户端状态并恢复指定版本快照...");
        // 整体替换索引前先让引擎缓存失效，避免后续读取拿到回滚前的旧索引。
        _engine.ReleaseClientIndex();
        var res = _backupManager.RestoreSnapshot(info, snapshotId, out restoredSettingsJson, progress);
        progress?.Invoke(res.Message);
        return res;
    }

    public bool DeleteSnapshot(string snapshotId, out string message, string? preferredDirectory = null)
    {
        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid)
        {
            message = "未检测到有效的 PoE2 游戏目录。";
            return false;
        }
        return _backupManager.DeleteSnapshot(info, snapshotId, out message);
    }

    /// <summary>
    /// 在每次打补丁写入前自动拍摄快照（自动保留最近 5 版）
    /// </summary>
    public BackupRestoreResult AutoSnapshotBeforePatch(
        GameModeInfo info,
        string? patchActionName = null,
        string? settingsJson = null,
        List<string>? activeModules = null,
        Action<string>? logger = null,
        bool allowDirtyBaseline = false)
    {
        try
        {
            // 确保官方底包已优先固化。
            // allowDirtyBaseline 由调用方在用户明确同意「以当前状态建立基线」后传入。
            _backupManager.EnsureBackup(info, logger, allowDirtyBaseline);
            // 记录当前自动快照
            string timeStr = DateTime.Now.ToString("MM-dd HH:mm");
            string actionText = string.IsNullOrWhiteSpace(patchActionName) ? "应用补丁" : patchActionName.Trim();
            string title = $"[自动] {actionText}前 ({timeStr})";
            string desc = $"写入【{actionText}】前自动生成的还原点 (系统自动保留最近 5 版)";

            return _backupManager.CreateSnapshot(
                info,
                PatchSnapshotType.Auto,
                title,
                desc,
                settingsJson,
                activeModules,
                logger);
        }
        catch (Exception ex)
        {
            logger?.Invoke($"自动创建快照失败：{ex.Message}");
            return new BackupRestoreResult(false, $"自动创建快照失败：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 兼容性重载：在每次打补丁写入前自动拍摄快照
    /// </summary>
    public void AutoSnapshotBeforePatch(GameModeInfo info, string? settingsJson, List<string>? activeModules)
    {
        AutoSnapshotBeforePatch(info, patchActionName: null, settingsJson, activeModules);
    }

    public bool ApplyFont(
        string fontFamily,
        int fontSizeDelta,
        string? preferredDirectory = null,
        bool applyPresetScales = false,
        bool isBold = false,
        int dropItemSizeDelta = 0,
        int generalTextSizeDelta = 0,
        int titleSizeDelta = 0,
        int tinyBadgeSizeDelta = 0,
        Action<string>? progress = null)
    {
        if (IsGameRunning())
        {
            progress?.Invoke("检测到游戏正在运行中，游戏占用了资源文件。请先退出游戏，然后再点击应用补丁。");
            return false;
        }

        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid)
        {
            progress?.Invoke($"未检测到游戏目录：{info.ErrorMessage}");
            return false;
        }

        return _fontService.ApplyFont(
            info,
            fontFamily,
            fontSizeDelta,
            applyPresetScales,
            isBold,
            dropItemSizeDelta,
            generalTextSizeDelta,
            titleSizeDelta,
            tinyBadgeSizeDelta,
            progress);
    }

    public bool ApplyRumours(
        string? preferredDirectory = null,
        List<EndgameMapRumourItem>? customItems = null,
        Action<string>? progress = null)
    {
        if (IsGameRunning())
        {
            progress?.Invoke("检测到游戏正在运行中，游戏占用了资源文件。请先退出游戏，然后再点击应用补丁。");
            return false;
        }

        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid)
        {
            progress?.Invoke($"未检测到游戏目录：{info.ErrorMessage}");
            return false;
        }

        return _rumourService.ApplyPatch(info, customItems, progress);
    }

    public List<EndgameMapRumourItem> ScanRumours(string? preferredDirectory = null)
    {
        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid || string.IsNullOrEmpty(info.EndgameMapsPath))
        {
            return [];
        }

        try
        {
            return _rumourService.ScanClientRumours(info);
        }
        catch
        {
            return [];
        }
    }

    public List<NamePatchItem> ScanChests(string? preferredDirectory = null)
    {
        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid)
        {
            return [];
        }

        try
        {
            return _nameService.ScanClientChests(info);
        }
        catch
        {
            return [];
        }
    }

    public FontSettingsInfo? ScanFontSettings(string? preferredDirectory = null)
    {
        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid)
        {
            return null;
        }

        try
        {
            return _fontService.ScanFontSettings(info);
        }
        catch
        {
            return null;
        }
    }

    public List<PricePatchService.BaseItemName> ScanBaseItems(string? preferredDirectory = null)
    {
        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid)
        {
            return [];
        }

        try
        {
            return _priceService.ScanClientBaseItems(info);
        }
        catch
        {
            return [];
        }
    }


    public PricePatchService.PricePatchPreview PreviewPricePatch(
        List<PriceRow> priceRows,
        string mode = "append",
        string separator = " ",
        string? preferredDirectory = null,
        Action<string>? progress = null,
        bool keepExistingPrice = false)
    {
        if (IsGameRunning())
        {
            progress?.Invoke("检测到游戏正在运行中，请先退出游戏后再预览。");
            return new PricePatchService.PricePatchPreview { ErrorMessage = "游戏运行中" };
        }

        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid)
        {
            progress?.Invoke($"未检测到游戏目录：{info.ErrorMessage}");
            return new PricePatchService.PricePatchPreview { ErrorMessage = info.ErrorMessage };
        }

        return _priceService.PreviewPatch(info, priceRows, mode, separator, keepExistingPrice, patchSameNameDuplicates: true, progress);
    }

    public PricePatchService.PricePatchResult ApplyPricePatch(
        List<PriceRow> priceRows,
        string mode = "append",
        string separator = " ",
        string? preferredDirectory = null,
        Action<string>? progress = null,
        bool keepExistingPrice = false)
    {
        if (IsGameRunning())
        {
            progress?.Invoke("检测到游戏正在运行中，游戏占用了资源文件。请先退出游戏，然后再点击应用补丁。");
            return new PricePatchService.PricePatchResult { ErrorMessage = "游戏运行中" };
        }

        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid)
        {
            progress?.Invoke($"未检测到游戏目录：{info.ErrorMessage}");
            return new PricePatchService.PricePatchResult { ErrorMessage = info.ErrorMessage };
        }

        return _priceService.ApplyPatch(info, priceRows, mode, separator, keepExistingPrice, patchSameNameDuplicates: true, progress);
    }

    public PricePatchService.PricePatchResult CleanPricePatch(
        string separator = " ",
        string? preferredDirectory = null,
        Action<string>? progress = null)
    {
        if (IsGameRunning())
        {
            progress?.Invoke("检测到游戏正在运行中，游戏占用了资源文件。请先退出游戏，然后再点击应用补丁。");
            return new PricePatchService.PricePatchResult { ErrorMessage = "游戏运行中" };
        }

        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid)
        {
            progress?.Invoke($"未检测到游戏目录：{info.ErrorMessage}");
            return new PricePatchService.PricePatchResult { ErrorMessage = info.ErrorMessage };
        }

        return _priceService.CleanPatch(info, separator, progress);
    }

    public bool ApplyNamePatch(
        NamePatchKind kind,
        string? preferredDirectory = null,
        Dictionary<string, string>? customMappings = null,
        Action<string>? progress = null,
        IReadOnlySet<string>? selectedNames = null)
    {
        if (IsGameRunning())
        {
            progress?.Invoke("检测到游戏正在运行中，游戏占用了资源文件。请先退出游戏，然后再点击应用补丁。");
            return false;
        }

        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid)
        {
            progress?.Invoke($"未检测到游戏目录：{info.ErrorMessage}");
            return false;
        }

        return _nameService.ApplyNamePatch(info, kind, customMappings, progress, selectedNames);
    }

    public bool ApplyColorMods(
        ColorModStyle style = ColorModStyle.Whole,
        string? preferredDirectory = null,
        IEnumerable<ColorModEntry>? entries = null,
        Action<string>? progress = null)
    {
        if (IsGameRunning())
        {
            progress?.Invoke("检测到游戏正在运行中，游戏占用了资源文件。请先退出游戏，然后再点击应用补丁。");
            return false;
        }

        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid)
        {
            progress?.Invoke($"未检测到游戏目录：{info.ErrorMessage}");
            return false;
        }

        entries ??= _colorModService.ExtractEntries(info, progress);
        return _colorModService.ApplyColorMods(info, style, entries, progress);
    }

    public bool ApplyMapBeams(
        IEnumerable<MapBeamTarget>? targets = null,
        string? preferredDirectory = null,
        Action<string>? progress = null)
    {
        if (IsGameRunning())
        {
            progress?.Invoke("检测到游戏正在运行中，游戏占用了资源文件。请先退出游戏，然后再点击应用补丁。");
            return false;
        }

        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid)
        {
            progress?.Invoke($"未检测到游戏目录：{info.ErrorMessage}");
            return false;
        }

        return _mapBeamService.ApplyPatch(info, targets, progress);
    }

    public List<ColorModEntry> ScanClientDictionary(
        string? preferredDirectory = null,
        Action<string>? progress = null)
    {
        var info = GetGameInfo(preferredDirectory);
        return _colorModService.ExtractEntries(info, progress);
    }

    public bool ApplyEffectRules(
        IReadOnlyList<string> ruleIds,
        string? preferredDirectory,
        Action<string>? progress,
        out string message)
    {
        message = string.Empty;
        if (IsGameRunning())
        {
            message = "检测到游戏正在运行中，游戏占用了资源文件。请先退出游戏，然后再点击应用补丁。";
            return false;
        }

        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid)
        {
            message = $"未检测到游戏目录：{info.ErrorMessage}";
            return false;
        }

        return _effectPatchApplier.Apply(info, ruleIds, progress, out message);
    }

    public bool RestoreEffectRules(
        string? preferredDirectory,
        Action<string>? progress,
        out string message)
    {
        message = string.Empty;
        if (IsGameRunning())
        {
            message = "检测到游戏正在运行中，游戏占用了资源文件。请先退出游戏，然后再点击还原补丁。";
            return false;
        }

        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid)
        {
            message = $"未检测到游戏目录：{info.ErrorMessage}";
            return false;
        }

        return _effectPatchApplier.Restore(info, progress, out message);
    }

    public bool ApplySpellBlocks(
        IReadOnlyList<string> spellIds,
        bool includeAudio,
        string? preferredDirectory,
        Action<string>? progress,
        out string message)
    {
        message = string.Empty;
        if (IsGameRunning())
        {
            message = "检测到游戏正在运行中，游戏占用了资源文件。请先退出游戏，然后再点击应用技能屏蔽。";
            return false;
        }

        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid)
        {
            message = $"未检测到游戏目录：{info.ErrorMessage}";
            return false;
        }

        return _spellEffectBlocker.ApplyBatch(info, spellIds, includeAudio, progress, out message);
    }

    public bool RestoreSpellBlocks(
        IReadOnlyList<string> spellIds,
        string? preferredDirectory,
        Action<string>? progress,
        out string message)
    {
        message = string.Empty;
        if (IsGameRunning())
        {
            message = "检测到游戏正在运行中，游戏占用了资源文件。请先退出游戏，然后再点击还原技能。";
            return false;
        }

        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid)
        {
            message = $"未检测到游戏目录：{info.ErrorMessage}";
            return false;
        }

        return _spellEffectBlocker.RestoreBatch(info, spellIds, progress, out message);
    }

    // ── 泥人（MudMan）────────────────────────────────────
    //
    // 与上面的「画面与环境优化」完全独立：规则集、备份分组、还原入口都不共用。
    // 泥人按易刷的六档模型工作，用户选一档即应用该档全部规则。

    /// <summary>泥人贴图表是否可用（内嵌资源能否加载）。</summary>
    public bool MudManTablesAvailable => MudManTables.IsAvailable;

    /// <summary>泥人贴图表加载失败原因；可用时为 null。</summary>
    public string? MudManTablesError => MudManTables.LoadError;

    /// <summary>泥人定制表条目数。</summary>
    public int MudManCustomCount => MudManTables.CustomCount;

    /// <summary>泥人名单表条目数。</summary>
    public int MudManListCount => MudManTables.ListCount;

    /// <summary>
    /// 把泥人还原点的索引指纹同步到当前客户端索引。
    ///
    /// 批处理（applyAllEffects）里泥人之后还会写特效/技能/血条，会把索引再改一次。
    /// 若泥人在自己收尾时立刻刷指纹，那次写入会让指纹马上作废，后续「还原泥人」
    /// 就被判定为 Stale 而永远失败。所以批处理会让泥人跳过刷新，等整批写完后
    /// 由调用方在这里统一刷新一次。
    /// </summary>
    public bool RefreshMudManFingerprint(string? preferredDirectory)
    {
        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid) return false;

        var point = FindMudManRestorePoint(info);

        return point is not null
               && _backupManager.RefreshSnapshotFingerprint(info, point.Id);
    }

    /// <summary>
    /// 取最近一次泥人还原点。
    /// 状态查询与指纹刷新必须用同一判据，否则会出现
    /// 「界面说有还原点、刷新时却找不到」的分叉。
    /// </summary>
    private PatchSnapshotInfo? FindMudManRestorePoint(GameModeInfo info) =>
        _backupManager.GetSnapshots(info)
            .Where(s => s.Name.StartsWith(MudManApplier.RestorePointPrefix, StringComparison.Ordinal))
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefault();

    public bool ApplyMudManTier(
        MudManTier tier,
        string? preferredDirectory,
        Action<string>? progress,
        out string message,
        bool refreshFingerprint = true)
    {
        message = string.Empty;
        if (IsGameRunning())
        {
            message = "检测到游戏正在运行中，游戏占用了资源文件。请先退出游戏，然后再点击应用补丁。";
            return false;
        }

        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid)
        {
            message = $"未检测到游戏目录：{info.ErrorMessage}";
            return false;
        }

        return _mudManApplier.Apply(info, tier, progress, out message, refreshFingerprint);
    }

    /// <summary>还原泥人改动的全部文件。</summary>
    public bool RestoreMudMan(
        string? preferredDirectory,
        Action<string>? progress,
        out string message,
        bool force = false)
    {
        message = string.Empty;
        if (IsGameRunning())
        {
            message = "检测到游戏正在运行中，游戏占用了资源文件。请先退出游戏，然后再点击还原补丁。";
            return false;
        }

        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid)
        {
            message = $"未检测到游戏目录：{info.ErrorMessage}";
            return false;
        }

        return _mudManApplier.Restore(info, force, progress, out message);
    }

    /// <summary>当前泥人是否已应用（存在可还原的备份）。</summary>
    public bool IsMudManApplied() => MudManApplier.HasBackup();

    /// <summary>泥人已改动的文件数量。</summary>
    public int MudManPatchedFileCount() => MudManApplier.PatchedFileCount();

    /// <summary>
    /// 泥人还原点状态：是否已建立、属于哪个游戏版本、是否仍适用于当前客户端。
    ///
    /// 界面据此告诉用户「还原会回到哪一份索引」，避免他们在不知情的情况下
    /// 点还原却发现回滚到旧版本、客户端起不来。
    /// </summary>
    public (bool Exists, string? GameVersion, bool MatchesCurrent) GetMudManRestorePoint(
        string? preferredDirectory = null)
    {
        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid) return (false, null, false);

        var point = FindMudManRestorePoint(info);
        if (point is null) return (false, null, false);

        string current = PatchBackupManager.ReadGameVersion(info);
        bool matches = !string.IsNullOrEmpty(current)
                       && string.Equals(point.GameVersion, current, StringComparison.OrdinalIgnoreCase);

        return (true, point.GameVersion, matches);
    }

    // ── 头顶血条 ──────────────────────────────────────────────────────────

    /// <summary>应用头顶血条补丁（把隐藏的头顶血条全部改为显示）。</summary>
    public bool ApplyHealthBar(
        string? preferredDirectory,
        Action<string>? progress,
        out string message)
    {
        message = string.Empty;
        if (IsGameRunning())
        {
            message = "检测到游戏正在运行中，游戏占用了资源文件。请先退出游戏，然后再点击应用补丁。";
            return false;
        }

        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid)
        {
            message = $"未检测到游戏目录：{info.ErrorMessage}";
            return false;
        }

        return _healthBarApplier.Apply(info, progress, out message);
    }

    /// <summary>还原头顶血条补丁。</summary>
    public bool RestoreHealthBar(
        string? preferredDirectory,
        Action<string>? progress,
        out string message)
    {
        message = string.Empty;
        if (IsGameRunning())
        {
            message = "检测到游戏正在运行中，游戏占用了资源文件。请先退出游戏，然后再点击还原补丁。";
            return false;
        }

        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid)
        {
            message = $"未检测到游戏目录：{info.ErrorMessage}";
            return false;
        }

        return _healthBarApplier.Restore(info, progress, out message);
    }

    /// <summary>血条补丁是否已应用。</summary>
    public bool IsHealthBarApplied() => HealthBarApplier.HasBackup();

    /// <summary>血条补丁改动的文件数量。</summary>
    public int HealthBarPatchedFileCount() => HealthBarApplier.PatchedFileCount();

    /// <summary>应用商城外观 (MTX) 特效替换补丁。</summary>
    public bool ApplyMtxPatch(
        string? preferredDirectory,
        FreiAtlas.Patcher.MtxPatch.MtxPatchOptions options,
        Action<string>? progress,
        out string message)
    {
        message = string.Empty;
        if (IsGameRunning())
        {
            message = "检测到游戏正在运行中，游戏占用了资源文件。请先退出游戏，然后再点击应用外观特效。";
            return false;
        }

        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid)
        {
            message = $"未检测到游戏目录：{info.ErrorMessage}";
            return false;
        }

        var res = _mtxPatchApplier.Apply(info, options, progress);
        message = res.Message;
        return res.Success;
    }

    /// <summary>还原商城外观 (MTX) 特效替换补丁。</summary>
    public bool RestoreMtxPatch(
        string? preferredDirectory,
        Action<string>? progress,
        out string message)
    {
        message = string.Empty;
        if (IsGameRunning())
        {
            message = "检测到游戏正在运行中，游戏占用了资源文件。请先退出游戏，然后再点击还原外观特效。";
            return false;
        }

        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid)
        {
            message = $"未检测到游戏目录：{info.ErrorMessage}";
            return false;
        }

        var res = _mtxPatchApplier.Restore(info, progress);
        message = res.Message;
        return res.Success;
    }

    /// <summary>商城外观特效补丁是否已应用。</summary>
    public bool IsMtxPatchApplied() => _mtxPatchApplier.HasAppliedBackup();

    /// <summary>商城外观特效补丁修改的文件数量。</summary>
    public int MtxPatchedFileCount() => _mtxPatchApplier.GetModifiedFileCount();
}

