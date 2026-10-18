using System.Diagnostics;

namespace FreiAtlas.Patcher;

public sealed class PatcherFacade
{
    private readonly PatchBackupManager _backupManager;
    private readonly PatchEngine _engine;
    private readonly FontPatchService _fontService;
    private readonly UiColorService _uiColorService;
    private readonly RumourPatchService _rumourService;
    private readonly NamePatchService _nameService;
    private readonly ColorModService _colorModService;
    private readonly PricePatchService _priceService;
    private readonly MapBeamPatchService _mapBeamService;

    public PatchBackupManager BackupManager => _backupManager;

    /// <summary>补丁备份目录是否可用；不可用时补丁页应展示原因而不是让整个设置窗口退出。</summary>
    public bool IsBackupAvailable => _backupManager.IsAvailable;

    /// <summary>补丁备份目录不可用的原因（可用时为 null）。</summary>
    public string? BackupUnavailableReason => _backupManager.UnavailableReason;

    public PatchEngine Engine => _engine;
    public FontPatchService FontService => _fontService;
    public UiColorService UiColorService => _uiColorService;
    public RumourPatchService RumourService => _rumourService;
    public NamePatchService NameService => _nameService;
    public ColorModService ColorModService => _colorModService;
    public PricePatchService PriceService => _priceService;
    public MapBeamPatchService MapBeamService => _mapBeamService;

    public PatcherFacade(string? customBackupDir = null)
    {
        _backupManager = new PatchBackupManager(customBackupDir);
        _engine = new PatchEngine(_backupManager);
        _fontService = new FontPatchService(_engine);
        _uiColorService = new UiColorService(_engine);
        _rumourService = new RumourPatchService(_engine);
        _nameService = new NamePatchService(_engine);
        _colorModService = new ColorModService(_engine);
        _priceService = new PricePatchService(_engine);
        _mapBeamService = new MapBeamPatchService(_engine);
    }

    public static bool IsGameRunning()
    {
        var procNames = new[] { "PathOfExile", "PathOfExile_x64", "Client", "PathOfExileSteam" };
        foreach (var name in procNames)
        {
            if (Process.GetProcessesByName(name).Length > 0)
            {
                return true;
            }
        }
        return false;
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
        var res = _backupManager.Restore(info, progress);
        progress?.Invoke(res.Message);
        return res;
    }

    public IReadOnlyList<PatchSnapshotInfo> GetSnapshots(string? preferredDirectory = null)
    {
        var info = GetGameInfo(preferredDirectory);
        if (!info.IsValid) return [];
        return _backupManager.GetSnapshots(info);
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
    public void AutoSnapshotBeforePatch(GameModeInfo info, string? settingsJson = null, List<string>? activeModules = null)
    {
        try
        {
            // 确保官方底包已优先固化
            _backupManager.EnsureBackup(info);
            // 记录当前自动快照
            string timeStr = DateTime.Now.ToString("MM-dd HH:mm");
            _backupManager.CreateSnapshot(
                info,
                PatchSnapshotType.Auto,
                $"[自动] 应用补丁前 ({timeStr})",
                "每次写入补丁前自动生成的还原点 (系统自动保留最近 5 版)",
                settingsJson,
                activeModules);
        }
        catch { }
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

    public bool ApplyUiColors(
        IEnumerable<UiColorEntry> entries,
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

        return _uiColorService.ApplyColors(info, entries, progress);
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


    public PricePatchService.PricePatchResult ApplyPricePatch(
        List<PriceRow> priceRows,
        string mode = "append",
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

        return _priceService.ApplyPatch(info, priceRows, mode, separator, keepExistingPrice: false, patchSameNameDuplicates: true, progress);
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

        return _nameService.ApplyNamePatch(info, kind, customMappings, progress);
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
}

