using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LibBundle3;
using LibBundle3.Records;
using LibBundledGGPK3;
using LibGGPK3.Records;

namespace FreiAtlas.Patcher.Core;

/// <summary>
/// 客户端还原结果结构
/// </summary>
public sealed record BackupRestoreResult(bool Success, string Message, Exception? Exception = null);

/// <summary>
/// 版本快照类型
/// </summary>
public enum PatchSnapshotType
{
    Official = 0, // 官方出厂纯净底包（不可删除、不可覆盖、永久锁定）
    Manual = 1,   // 手动备份版本（用户自主命名，永久保留，仅支持手动删除）
    Auto = 2      // 自动备份版本（打补丁前自动记录，保留最近 5 版，超额自动淘汰）
}

/// <summary>
/// 快照相对于当前客户端的新鲜度判定结果。
/// </summary>
public enum SnapshotStaleness
{
    /// <summary>无法判定：读不到当前索引，或快照没有记录指纹。</summary>
    Unknown = 0,

    /// <summary>新鲜：当前索引指纹与快照一致，回滚不会影响其他改动。</summary>
    Fresh = 1,

    /// <summary>已过期：快照之后索引被改写过，回滚会连带抹掉这些改动。</summary>
    Stale = 2
}

/// <summary>
/// 版本快照元数据信息
/// </summary>
public sealed class PatchSnapshotInfo
{
    public string Id { get; set; } = string.Empty;
    public PatchSnapshotType Type { get; set; } = PatchSnapshotType.Manual;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public long SizeBytes { get; set; }
    public List<string> EnabledModules { get; set; } = [];
    public string? SettingsJson { get; set; }

    /// <summary>
    /// 生成该快照时的客户端索引指纹。用来判断快照是否仍适用于当前游戏版本：
    /// 恢复一份旧版本的索引到新版本客户端，会导致客户端与资源不匹配而无法启动。
    /// </summary>
    public string IndexFingerprint { get; set; } = string.Empty;

    /// <summary>生成该快照时的游戏版本号，仅用于界面展示。</summary>
    public string GameVersion { get; set; } = string.Empty;
}

/// <summary>
/// 客户端索引纯净备份与多版本快照管理器 (双轨制：手动永久 + 自动轮转 5 版 + 官方底包锁定)
/// </summary>
public sealed class PatchBackupManager
{
    public const string OfficialSnapshotId = "clean_official";
    private readonly string _backupDir;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // 已知第三方及自身写入的补丁 bundle 路径特征。
    //
    // 这里只能放工具专有名，不能用 patch_、EF_ 之类通用片段：官方资源里存在
    // jackolanternpatch_01.ao、innocence_floor_relief_v01 这类路径，会被子串匹配
    // 误判成补丁，导致纯净客户端被拒绝创建底包。
    private static readonly string[] KnownPatchPrefixes =
    [
        "FreiAtlas",
        "TinyPoe2Smoother",
        "Poe2Price",
        // 易刷 E-farm（exilence-next-tx-app）的补丁 bundle 前缀。
        // 用户同时安装两个工具时，缺这两项会把对方的补丁误判成「来源不明」，
        // 导致纯净底包无法创建。
        "EniBundle",
        "TinyBundle"
    ];

    /// <summary>
    /// 当前实例的备份目录是否已成功初始化并可写。
    /// 初始化失败时不会抛出异常，而是置为 false，并给出 <see cref="UnavailableReason"/>。
    /// </summary>
    public bool IsAvailable { get; }

    /// <summary>
    /// 备份目录不可用时的原因说明（可用时为空）。
    /// </summary>
    public string? UnavailableReason { get; }

    /// <summary>
    /// 最近一次写入快照清单的失败原因（成功或未写过时为 null）。
    ///
    /// 供无法返回失败的查询类方法（<see cref="GetSnapshots"/>、<see cref="PurgeStaleSnapshots"/>）
    /// 之外的调用方诊断用。
    /// </summary>
    public string? LastManifestError { get; private set; }

    public PatchBackupManager(string? customBackupDir = null)
    {
        _backupDir = customBackupDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FreiAtlas",
            "patch_backup");

        try
        {
            // 目录已存在时 CreateDirectory 不会报错；这里额外做一次写入探针，
            // 以便发现“目录存在但当前进程无权写入”的情况（如低完整性/沙箱/ACL 受限）。
            Directory.CreateDirectory(_backupDir);
            string probePath = Path.Combine(_backupDir, ".frei_write_probe_" + Guid.NewGuid().ToString("N"));
            using (var probe = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                probe.WriteByte(0);
            }

            // 探测结论已由上方的写入成败决定，这里只是清理探测文件。
            // 删不掉（被杀软占用等）不影响「目录是否可写」的判定，可以静默。
            try { File.Delete(probePath); } catch { }
            IsAvailable = true;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            IsAvailable = false;
            UnavailableReason =
                $"补丁备份目录不可用：{_backupDir}。{ex.Message} " +
                "请检查目录权限、磁盘空间，或安全软件/沙箱对该目录的拦截。";
        }
    }

    private static bool IsFileSystemFailure(Exception ex) =>
        ex is UnauthorizedAccessException
            or IOException
            or System.Security.SecurityException
            or NotSupportedException
            or ArgumentException;

    private BackupRestoreResult UnavailableResult() =>
        new(false, UnavailableReason ?? "补丁备份目录不可用，备份/还原功能已禁用。");

    /// <summary>
    /// 获取当前游戏目录专用的备份路径（结合目录哈希隔离，防止多客户端相互覆盖）
    /// </summary>
    public string GetBackupFilePath(GameModeInfo info)
    {
        string dirHash = ComputeDirectoryHash(info.GameDirectory);
        string basePrefix = info.Mode == GameMode.GGPK ? "ggpk_index" : "bundles2_index";
        string isolatedPath = Path.Combine(_backupDir, $"{basePrefix}_{dirHash}.bin.bak");

        // 兼容性继承：如果隔离路径不存在，但历史默认备份存在且有效，直接继承它
        if (!File.Exists(isolatedPath))
        {
            string legacyPath = Path.Combine(_backupDir, $"{basePrefix}.bin.bak");
            if (File.Exists(legacyPath))
            {
                try
                {
                    File.Copy(legacyPath, isolatedPath, overwrite: false);
                }
                catch
                {
                    return legacyPath;
                }
            }
        }

        return isolatedPath;
    }

    private static string ComputeDirectoryHash(string gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir)) return "default";
        string normalized = Path.GetFullPath(gameDir).TrimEnd('\\', '/').ToLowerInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    public bool HasBackup(GameModeInfo info)
    {
        if (!IsAvailable) return false;

        string isolatedPath = GetBackupFilePath(info);
        if (File.Exists(isolatedPath)) return true;

        // 兜底检查旧版共用备份
        string legacyPath = Path.Combine(_backupDir, info.Mode == GameMode.GGPK ? "ggpk_index.bin.bak" : "bundles2_index.bin.bak");
        return File.Exists(legacyPath);
    }

    /// <summary>
    /// 检查资源文件是否被其他进程（如 Steam、游戏下载更新器、后台进程）独占锁定
    /// </summary>
    public static (bool IsLocked, string? LockReason) CheckResourceLock(GameModeInfo info)
    {
        if (!info.IsValid) return (false, null);

        string targetFile = info.Mode == GameMode.GGPK
            ? PatchEngine.GetGgpkPath(info)
            : PatchEngine.GetBundlesIndexPath(info);

        if (!File.Exists(targetFile)) return (false, null);

        try
        {
            using var stream = new FileStream(targetFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return (false, null);
        }
        catch (IOException ex)
        {
            return (true, $"资源文件正被其他进程占用：{ex.Message}（请先完全退出游戏、Steam 或后台下载更新器）");
        }
        catch (UnauthorizedAccessException ex)
        {
            return (true, $"无读写权限访问资源文件：{ex.Message}（请尝试以管理员身份运行）");
        }
        catch (Exception ex)
        {
            return (true, $"无法访问资源文件：{ex.Message}");
        }
    }

    /// <summary>
    /// 获取当前游戏目录专用的版本快照存储文件夹
    /// </summary>
    public string GetSnapshotsDirectory(GameModeInfo info)
    {
        string dirHash = ComputeDirectoryHash(info.GameDirectory);
        string subDir = Path.Combine(_backupDir, $"snapshots_{dirHash}");
        if (IsAvailable && !Directory.Exists(subDir))
        {
            try
            {
                Directory.CreateDirectory(subDir);
            }
            catch (Exception ex) when (IsFileSystemFailure(ex))
            {
                // 留给后续文件操作按“不可用”处理，这里不抛异常打断 UI。
            }
        }
        return subDir;
    }

    private string GetManifestPath(GameModeInfo info)
    {
        return Path.Combine(GetSnapshotsDirectory(info), "manifest.json");
    }

    /// <summary>
    /// 获取所有可用的版本快照列表（官方底包永久置顶，手动按时间倒序，自动按时间倒序）
    /// </summary>
    public IReadOnlyList<PatchSnapshotInfo> GetSnapshots(GameModeInfo info)
    {
        if (!IsAvailable) return [];

        var manifestPath = GetManifestPath(info);
        var list = new List<PatchSnapshotInfo>();

        if (File.Exists(manifestPath))
        {
            try
            {
                var json = File.ReadAllText(manifestPath, Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<List<PatchSnapshotInfo>>(json);
                if (loaded != null)
                {
                    bool needsClean = false;
                    string snapDir = GetSnapshotsDirectory(info);
                    foreach (var s in loaded)
                    {
                        if (!string.IsNullOrWhiteSpace(s.SettingsJson))
                        {
                            try
                            {
                                string sp = Path.Combine(snapDir, $"{s.Id}.settings.json");
                                if (!File.Exists(sp))
                                {
                                    File.WriteAllText(sp, s.SettingsJson, Encoding.UTF8);
                                }
                            }
                            catch
                            {
                                // 这是旧版本数据格式的兼容迁移：清单里内嵌了 settings 字符串，
                                // 这里把它拆到独立文件。写失败不影响快照本身可用，
                                // 下次启动会重试迁移，因此可以静默。
                            }
                            s.SettingsJson = null;
                            needsClean = true;
                        }
                    }
                    list = loaded;
                    if (needsClean && !SaveManifest(info, list))
                    {
                        // 迁移后的清单写失败：快照本身仍可读，只是下次启动会再迁移一次。
                    }
                }
            }
            catch
            {
                // 清单损坏或不可读时返回空列表，由下方的官方底包初始化逻辑重建。
                // 不抛异常是为了让界面仍能进入补丁页（否则整个模块会打不开）。
            }
        }

        // 确保官方底包 clean_official 存在并初始化
        var official = list.FirstOrDefault(s => s.Id == "clean_official" || s.Type == PatchSnapshotType.Official);
        string officialBinPath = Path.Combine(GetSnapshotsDirectory(info), "clean_official.bin");

        if (official == null)
        {
            // 从历史备份文件导入
            string legacyBak = GetBackupFilePath(info);
            if (File.Exists(legacyBak))
            {
                try
                {
                    if (!File.Exists(officialBinPath)) File.Copy(legacyBak, officialBinPath, overwrite: false);
                    official = new PatchSnapshotInfo
                    {
                        Id = "clean_official",
                        Type = PatchSnapshotType.Official,
                        Name = "🌟 官方出厂纯净底包",
                        Description = "首次未打补丁时的官方纯净原始索引，永久锁定不可覆盖",
                        CreatedAt = File.GetCreationTime(legacyBak),
                        SizeBytes = new FileInfo(officialBinPath).Length,
                        EnabledModules = []
                    };
                    list.Insert(0, official);
                    if (!SaveManifest(info, list))
                    {
                        // 底包已能读到（上面刚构造），只是没写进清单，下次调用会再补写。
                    }
                }
                catch
                {
                    // 从历史备份文件导入底包失败：历史文件可能已损坏或被占用。
                    // 保持原列表不变，不影响已有快照的读取；下次启动会重试导入。
                }
            }
        }

        // 排序规则：Official 永远第 1 位；Manual 倒序紧随其后；Auto 倒序排在最后
        return list
            .OrderBy(s => s.Type switch
            {
                PatchSnapshotType.Official => 0,
                PatchSnapshotType.Manual => 1,
                PatchSnapshotType.Auto => 2,
                _ => 3
            })
            .ThenByDescending(s => s.CreatedAt)
            .ToList();
    }

    /// <summary>
    /// 写入快照清单。
    ///
    /// 清单是管理全部快照的唯一索引，写失败会让「界面上有快照、磁盘上没登记」
    /// 或反之，因此不能静默吞掉。返回值供能上报失败的调用方使用；
    /// 返回列表或计数的查询类调用方不阻断主流程，但可从 <see cref="LastManifestError"/> 读到原因。
    /// </summary>
    /// <returns>写入成功返回 true。</returns>
    private bool SaveManifest(GameModeInfo info, List<PatchSnapshotInfo> list)
    {
        try
        {
            var manifestPath = GetManifestPath(info);
            var json = JsonSerializer.Serialize(list, JsonOpts);
            File.WriteAllText(manifestPath, json, Encoding.UTF8);
            LastManifestError = null;
            return true;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            LastManifestError = $"写入快照清单失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 从当前客户端读取 _.index.bin 字节内容
    /// </summary>
    private static byte[]? ExtractCurrentClientIndexBytes(GameModeInfo info)
    {
        if (info.Mode == GameMode.GGPK)
        {
            var ggpkPath = PatchEngine.GetGgpkPath(info);
            if (!File.Exists(ggpkPath)) return null;

            using var ggpk = new BundledGGPK(ggpkPath, parsePathsInIndex: false);
            if (ggpk.Root.TryFindNode("Bundles2/_.index.bin", out var node) && node is LibGGPK3.Records.FileRecord record)
            {
                return record.Read();
            }
            return null;
        }
        else if (info.Mode == GameMode.Bundles2)
        {
            var indexPath = PatchEngine.GetBundlesIndexPath(info);
            if (!File.Exists(indexPath)) return null;
            return File.ReadAllBytes(indexPath);
        }
        return null;
    }

    /// <summary>
    /// 创建版本快照（支持手动备份与补丁前自动备份）
    /// </summary>
    public BackupRestoreResult CreateSnapshot(
        GameModeInfo info,
        PatchSnapshotType type,
        string name,
        string description,
        string? settingsJson = null,
        List<string>? activeModules = null,
        Action<string>? logger = null,
        byte[]? indexBytes = null)
    {
        if (!IsAvailable) return UnavailableResult();

        // 调用方已持有 GGPK 句柄时无法再次打开同一文件，因此允许直接传入读好的索引。
        // 未传入时才自行读取，并做一次资源锁检查。
        if (indexBytes is null)
        {
            var (isLocked, lockReason) = CheckResourceLock(info);
            if (isLocked)
            {
                return new BackupRestoreResult(false, lockReason ?? "客户端资源被独占，无法创建版本快照。");
            }
        }

        try
        {
            indexBytes ??= ExtractCurrentClientIndexBytes(info);
            if (indexBytes is null || indexBytes.Length == 0)
            {
                return new BackupRestoreResult(false, "无法从游戏客户端中读取核心索引文件（Bundles2/_.index.bin）。");
            }

            string snapDir = GetSnapshotsDirectory(info);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N")[..4];
            string snapId = type switch
            {
                PatchSnapshotType.Official => "clean_official",
                PatchSnapshotType.Auto => $"snap_auto_{timestamp}",
                _ => $"snap_manual_{timestamp}"
            };

            string binPath = Path.Combine(snapDir, $"{snapId}.bin");
            File.WriteAllBytes(binPath, indexBytes);

            // 将设置独立保存为小文件，避免 manifest.json 和 IPC 传输暴增到数十兆
            if (!string.IsNullOrWhiteSpace(settingsJson))
            {
                try
                {
                    string settingsPath = Path.Combine(snapDir, $"{snapId}.settings.json");
                    File.WriteAllText(settingsPath, settingsJson, Encoding.UTF8);
                }
                catch
                {
                    // settings.json 是快照的辅助信息（记录当时的补丁配置），
                    // 主数据 .bin 索引已在上方写入成功。这里失败不影响快照可用与还原，
                    // 只是还原后补丁勾选状态会回落到默认值。
                }
            }

            var list = GetSnapshots(info).ToList();
            list.RemoveAll(s => s.Id == snapId);

            var snap = new PatchSnapshotInfo
            {
                Id = snapId,
                Type = type,
                Name = string.IsNullOrWhiteSpace(name) ? (type == PatchSnapshotType.Auto ? $"[自动] 补丁前快照 ({DateTime.Now:MM-dd HH:mm})" : $"手动备份 ({DateTime.Now:MM-dd HH:mm})") : name.Trim(),
                Description = description ?? "",
                CreatedAt = DateTime.Now,
                SizeBytes = indexBytes.Length,
                EnabledModules = activeModules ?? [],
                SettingsJson = null,
                IndexFingerprint = ComputeFingerprint(indexBytes),
                GameVersion = ReadGameVersion(info)
            };

            list.Add(snap);

            // 如果是自动快照，严格执行保留最近 5 版的轮转淘汰策略
            if (type == PatchSnapshotType.Auto)
            {
                RotateAutoSnapshotsInternal(info, list, maxAutoKeep: 5, logger);
            }

            if (!SaveManifest(info, list))
            {
                return new BackupRestoreResult(false, LastManifestError ?? "写入快照清单失败，快照未登记。");
            }

            logger?.Invoke($"已成功创建版本快照【{snap.Name}】（{snap.SizeBytes / (1024.0 * 1024.0):F2} MB）");
            return new BackupRestoreResult(true, $"已成功创建版本快照【{snap.Name}】！");
        }
        catch (Exception ex)
        {
            logger?.Invoke($"创建快照失败：{ex.Message}");
            return new BackupRestoreResult(false, $"创建版本快照失败：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 自动快照轮转：仅保留最近 maxAutoKeep 个（默认 5 个），超出自动淘汰最早的版本
    /// </summary>
    private void RotateAutoSnapshotsInternal(GameModeInfo info, List<PatchSnapshotInfo> list, int maxAutoKeep, Action<string>? logger)
    {
        var autoList = list.Where(s => s.Type == PatchSnapshotType.Auto).OrderByDescending(s => s.CreatedAt).ToList();
        if (autoList.Count <= maxAutoKeep) return;

        var toRemove = autoList.Skip(maxAutoKeep).ToList();
        string snapDir = GetSnapshotsDirectory(info);

        foreach (var oldSnap in toRemove)
        {
            list.Remove(oldSnap);
            try
            {
                string p = Path.Combine(snapDir, $"{oldSnap.Id}.bin");
                if (File.Exists(p)) File.Delete(p);
                string sp = Path.Combine(snapDir, $"{oldSnap.Id}.settings.json");
                if (File.Exists(sp)) File.Delete(sp);
                logger?.Invoke($"自动轮转淘汰早期自动快照：{oldSnap.Name}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 自动轮转在补丁写入流程内执行，删除失败不能中断补丁本身。
                // 但必须让用户知道磁盘没释放，否则会疑惑空间为何没减少。
                logger?.Invoke($"淘汰快照【{oldSnap.Name}】时文件删除失败（清单已移除，磁盘可能残留）：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 将客户端与设置精确还原到指定的版本快照
    /// </summary>
    public BackupRestoreResult RestoreSnapshot(
        GameModeInfo info,
        string snapshotId,
        out string? restoredSettingsJson,
        Action<string>? logger = null)
    {
        restoredSettingsJson = null;

        if (!IsAvailable) return UnavailableResult();

        var snapshots = GetSnapshots(info);
        var target = snapshots.FirstOrDefault(s => s.Id == snapshotId);
        if (target == null)
        {
            return new BackupRestoreResult(false, $"未找到指定的版本快照（ID: {snapshotId}）。");
        }

        string binPath = Path.Combine(GetSnapshotsDirectory(info), $"{target.Id}.bin");
        if (!File.Exists(binPath))
        {
            return new BackupRestoreResult(false, $"快照索引数据文件丢失（目标位置: {binPath}）。");
        }

        string settingsPath = Path.Combine(GetSnapshotsDirectory(info), $"{target.Id}.settings.json");
        if (File.Exists(settingsPath))
        {
            try
            {
                restoredSettingsJson = File.ReadAllText(settingsPath, Encoding.UTF8);
            }
            catch
            {
                // 读不到 settings.json 时回落到下方清单里内嵌的 SettingsJson（旧格式），
                // 两者都没有就保持 null 由调用方用默认设置。索引本身已经还原成功，
                // 不能因为辅助配置缺失就把整次还原判为失败。
            }
        }
        restoredSettingsJson ??= target.SettingsJson;

        var (isLocked, lockReason) = CheckResourceLock(info);
        if (isLocked)
        {
            return new BackupRestoreResult(false, lockReason ?? "客户端资源正被占用，无法写入还原。");
        }

        try
        {
            logger?.Invoke($"正在读取快照【{target.Name}】数据（{target.SizeBytes / (1024.0 * 1024.0):F2} MB）...");
            byte[] snapshotBytes = File.ReadAllBytes(binPath);

            if (info.Mode == GameMode.GGPK)
            {
                var ggpkPath = PatchEngine.GetGgpkPath(info);
                if (!File.Exists(ggpkPath)) return new BackupRestoreResult(false, $"未找到 Content.ggpk 文件：{ggpkPath}");

                using var ggpk = new BundledGGPK(ggpkPath, parsePathsInIndex: false);
                if (ggpk.Root.TryFindNode("Bundles2/_.index.bin", out var node) && node is LibGGPK3.Records.FileRecord record)
                {
                    logger?.Invoke($"正在将快照【{target.Name}】索引写回 Content.ggpk...");
                    record.Write(snapshotBytes);
                    logger?.Invoke("写入完成！客户端状态已恢复至该版本。");
                    return new BackupRestoreResult(true, $"已成功恢复到版本【{target.Name}】！");
                }
                return new BackupRestoreResult(false, "在 Content.ggpk 中未能寻址到 Bundles2/_.index.bin 节点。");
            }
            else if (info.Mode == GameMode.Bundles2)
            {
                var indexPath = PatchEngine.GetBundlesIndexPath(info);
                var tmpPath = indexPath + ".tmp";
                File.WriteAllBytes(tmpPath, snapshotBytes);
                if (File.Exists(indexPath)) File.Delete(indexPath);
                File.Move(tmpPath, indexPath);

                // 如果恢复的是纯净底包，顺带清理补丁文件夹
                if (target.Type == PatchSnapshotType.Official)
                {
                    foreach (var prefix in KnownPatchPrefixes)
                    {
                        var customDir = Path.Combine(info.GameDirectory, "Bundles2", prefix);
                        if (Directory.Exists(customDir))
                        {
                    // 自定义 bundle 目录是补丁写入产物，删除失败不阻断还原流程：
                    // 索引已回滚，残留目录不会被客户端引用，下次写入会被覆盖。
                    try { Directory.Delete(customDir, recursive: true); } catch { }
                        }
                    }
                }

                logger?.Invoke("写入完成！Bundles2 索引已恢复至该版本。");
                return new BackupRestoreResult(true, $"已成功恢复到版本【{target.Name}】！");
            }
        }
        catch (IOException ioEx)
        {
            return new BackupRestoreResult(false, $"还原时文件访问受阻：{ioEx.Message}（游戏、Steam 或后台下载更新器正在占用文件，请退出后重试）", ioEx);
        }
        catch (Exception ex)
        {
            return new BackupRestoreResult(false, $"还原到快照失败：{ex.Message}", ex);
        }

        return new BackupRestoreResult(false, "未知还原失败。");
    }

    /// <summary>
    /// 删除指定快照（官方底包受永久保护，禁止删除）
    /// </summary>
    public bool DeleteSnapshot(GameModeInfo info, string snapshotId, out string message)
    {
        if (snapshotId == "clean_official")
        {
            message = "🌟 官方出厂纯净底包受永久保护，禁止删除！";
            return false;
        }

        if (!IsAvailable)
        {
            message = UnavailableReason ?? "补丁备份目录不可用，无法删除快照。";
            return false;
        }

        var list = GetSnapshots(info).ToList();
        var target = list.FirstOrDefault(s => s.Id == snapshotId);
        if (target == null)
        {
            message = "未找到指定的版本快照。";
            return false;
        }

        if (target.Type == PatchSnapshotType.Official)
        {
            message = "🌟 官方出厂纯净底包受永久保护，禁止删除！";
            return false;
        }

        list.Remove(target);
        if (!SaveManifest(info, list))
        {
            message = LastManifestError ?? "写入快照清单失败，删除未生效。";
            return false;
        }

        // 清单条目已移除，但磁盘文件可能因占用 / 权限 / 磁盘错误删不掉。
        // 早期这里吞掉异常后照样报「已删除」，用户以为空间已释放、快照已消失，
        // 实际 .bin（每份约 115 MB）仍留在磁盘上。改为如实报告。
        var undeleted = new List<string>();
        try
        {
            string binPath = Path.Combine(GetSnapshotsDirectory(info), $"{target.Id}.bin");
            if (File.Exists(binPath)) File.Delete(binPath);
            string sp = Path.Combine(GetSnapshotsDirectory(info), $"{target.Id}.settings.json");
            if (File.Exists(sp)) File.Delete(sp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            string binPath = Path.Combine(GetSnapshotsDirectory(info), $"{target.Id}.bin");
            string sp = Path.Combine(GetSnapshotsDirectory(info), $"{target.Id}.settings.json");
            if (File.Exists(binPath)) undeleted.Add(binPath);
            if (File.Exists(sp)) undeleted.Add(sp);
        }

        if (undeleted.Count > 0)
        {
            message =
                $"快照【{target.Name}】的清单条目已删除，但 {undeleted.Count} 个磁盘文件未能删除，" +
                $"仍占用磁盘空间：{string.Join("；", undeleted)}。\n" +
                "请关闭可能占用该文件的程序后手动删除。";
            return false;
        }

        message = $"已删除快照【{target.Name}】。";
        return true;
    }

    /// <summary>
    /// 删除快照文件与清单条目，跳过官方底包的删除保护。
    /// 专供「游戏版本变化后重建底包」使用：旧底包对当前版本已无用且有害，
    /// 必须允许替换，但用户主动删除的入口仍然受保护。
    /// </summary>
    private bool DeleteSnapshotInternal(GameModeInfo info, string snapshotId, out string message)
    {
        if (!IsAvailable)
        {
            message = UnavailableReason ?? "补丁备份目录不可用。";
            return false;
        }

        var list = GetSnapshots(info).ToList();
        var target = list.FirstOrDefault(s => s.Id == snapshotId);
        if (target is null)
        {
            message = "未找到指定的版本快照。";
            return false;
        }

        list.Remove(target);

        // 与 DeleteSnapshot 同理：清单已移除但磁盘文件可能删不掉，
        // 不能吞掉异常后照样报成功。
        var undeleted = new List<string>();
        try
        {
            string dir = GetSnapshotsDirectory(info);
            string binPath = Path.Combine(dir, $"{target.Id}.bin");
            if (File.Exists(binPath)) File.Delete(binPath);
            string settingsPath = Path.Combine(dir, $"{target.Id}.settings.json");
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            string dir = GetSnapshotsDirectory(info);
            string binPath = Path.Combine(dir, $"{target.Id}.bin");
            string settingsPath = Path.Combine(dir, $"{target.Id}.settings.json");
            if (File.Exists(binPath)) undeleted.Add(binPath);
            if (File.Exists(settingsPath)) undeleted.Add(settingsPath);
        }

        if (undeleted.Count > 0)
        {
            message =
                $"快照【{target.Name}】的清单条目已删除，但 {undeleted.Count} 个磁盘文件未能删除：{string.Join("；", undeleted)}。";
            return false;
        }

        message = $"已删除快照【{target.Name}】。";
        return true;
    }

    /// <summary>
    /// 确保存在纯净官方原版备份（具备纯净性防污染检测）
    /// </summary>
    public bool EnsureBackup(GameModeInfo info, Action<string>? logger = null)
    {
        if (!IsAvailable)
        {
            logger?.Invoke(UnavailableReason ?? "补丁备份目录不可用，无法生成备份。");
            return false;
        }

        var backupPath = GetBackupFilePath(info);
        if (File.Exists(backupPath))
        {
            // 备份文件存在即视为可用。版本号只用于「提示」，不作为删除依据：
            //   1. 早期版本创建的底包没有记录版本号，无法判断新旧；
            //   2. 客户端一旦打过补丁就不再纯净，此时删掉旧备份也无法重建新的，
            //      删除只会让用户失去唯一的还原点。
            // 因此这里绝不主动删除既有备份。版本不匹配只记录日志，交由启动时的
            // EnsureOfficialBackupForCurrentVersion 在客户端纯净时择机重建。
            string officialVersion = GetSnapshots(info)
                .FirstOrDefault(s => s.Type == PatchSnapshotType.Official)?.GameVersion ?? string.Empty;
            string currentVersion = ReadGameVersion(info);

            if (!string.IsNullOrEmpty(officialVersion)
                && !string.IsNullOrEmpty(currentVersion)
                && !string.Equals(officialVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
            {
                logger?.Invoke($"注意：已有备份来自版本 {officialVersion}，当前客户端为 {currentVersion}。保留该备份，不做删除。");
            }
            return true;
        }

        try
        {
            if (info.Mode == GameMode.GGPK)
            {
                var ggpkPath = PatchEngine.GetGgpkPath(info);
                if (!File.Exists(ggpkPath))
                {
                    logger?.Invoke($"未找到文件：{ggpkPath}");
                    return false;
                }

                using var ggpk = new BundledGGPK(ggpkPath, parsePathsInIndex: false);
                if (ggpk.Root.TryFindNode("Bundles2/_.index.bin", out var node) && node is LibGGPK3.Records.FileRecord record)
                {
                    if (!IsCleanIndex(ggpk.Index))
                    {
                        logger?.Invoke("检测到客户端 index 中已包含补丁 Bundle 记录，判定为非纯净客户端，拒绝将其存为纯净备份。");
                        return false;
                    }

                    var indexBytes = record.Read();
                    File.WriteAllBytes(backupPath, indexBytes);
                    logger?.Invoke($"已成功生成纯净索引备份（{indexBytes.Length / (1024.0 * 1024.0):F2} MB）");

                    // 同步固化为 official 快照。必须复用已读到的索引：GGPK 句柄仍由
                    // 上面的 using 持有，让 CreateSnapshot 自行打开同一文件会失败。
                    var snapResult = CreateSnapshot(
                        info, PatchSnapshotType.Official,
                        "🌟 官方出厂纯净底包",
                        "首次未打补丁时的官方纯净原始索引，永久锁定不可覆盖",
                        null, [], logger, indexBytes);
                    if (!snapResult.Success)
                    {
                        logger?.Invoke($"底包文件已生成，但快照登记失败：{snapResult.Message}");
                        return false;
                    }
                    return true;
                }
                return false;
            }
            else if (info.Mode == GameMode.Bundles2)
            {
                var bundlesDir = PatchEngine.GetBundlesDirectory(info);
                var indexPath = PatchEngine.GetBundlesIndexPath(info);
                if (!File.Exists(indexPath)) return false;

                var driveFactory = new DriveBundleFactory(bundlesDir);
                try
                {
                    using var index = new LibBundle3.Index(indexPath, parsePaths: false, driveFactory);
                    if (!IsCleanIndex(index))
                    {
                        logger?.Invoke("检测到客户端 index 中已包含补丁 Bundle 记录，判定为非纯净客户端，拒绝将其存为纯净备份。");
                        return false;
                    }

                    File.Copy(indexPath, backupPath, overwrite: false);
                    logger?.Invoke($"已成功生成纯净索引备份（{new FileInfo(backupPath).Length / (1024.0 * 1024.0):F2} MB）");

                    // 复用已读到的索引，避免在 index 句柄未释放时再次打开同一文件
                    var snapBytes = File.ReadAllBytes(backupPath);
                    var snapResult = CreateSnapshot(
                        info, PatchSnapshotType.Official,
                        "🌟 官方出厂纯净底包",
                        "首次未打补丁时的官方纯净原始索引，永久锁定不可覆盖",
                        null, [], logger, snapBytes);
                    if (!snapResult.Success)
                    {
                        logger?.Invoke($"底包文件已生成，但快照登记失败：{snapResult.Message}");
                        return false;
                    }
                    return true;
                }
                finally
                {
                    (driveFactory as IDisposable)?.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            logger?.Invoke($"生成备份时发生异常：{ex.Message}");
            return false;
        }

        return false;
    }

    /// <summary>
    /// 检查 Index 是否纯净（不含已知补丁 bundle）
    /// </summary>
    private static bool IsCleanIndex(LibBundle3.Index index)
    {
        try
        {
            var bundles = index.Bundles.Span;
            for (int i = 0; i < bundles.Length; i++)
            {
                string bPath = bundles[i].Path;
                if (string.IsNullOrEmpty(bPath)) continue;

                foreach (var prefix in KnownPatchPrefixes)
                {
                    if (bPath.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }
            return true;
        }
        catch
        {
            // 读取索引失败时无法证明客户端纯净。此时必须按「不纯净」处理：
            // 若误判为纯净，会把一份来源不明的索引固化成官方底包，
            // 之后用它还原可能让客户端无法启动。
            return false;
        }
    }

    /// <summary>
    /// 计算索引字节的指纹。游戏更新必然改变索引内容，指纹变化即代表版本变化。
    /// </summary>
    public static string ComputeFingerprint(byte[] indexBytes)
    {
        if (indexBytes is null || indexBytes.Length == 0) return string.Empty;
        return Convert.ToHexString(SHA256.HashData(indexBytes));
    }

    /// <summary>
    /// <summary>
    /// 把快照的索引指纹刷新为当前客户端的指纹。
    ///
    /// 用途：泥人 / 血条这类「还原点固定复用」的模块，在同一版本内反复换档位时
    /// 会不断改写索引。若不刷新还原点指纹，它会在第二次应用后就被判定为
    /// Stale，导致用户无法还原自己刚打的补丁。
    ///
    /// 只在确认本次写入来自该模块自身时调用；其他补丁写入不应调用此方法，
    /// 否则会把「别的补丁改过客户端」这个事实抹掉。
    /// </summary>
    /// <returns>是否成功刷新。</returns>
    public bool RefreshSnapshotFingerprint(GameModeInfo info, string snapshotId)
    {
        if (!IsAvailable || !info.IsValid) return false;

        try
        {
            string current = GetCurrentFingerprint(info);
            if (string.IsNullOrEmpty(current)) return false;

            var list = GetSnapshots(info).ToList();
            var target = list.FirstOrDefault(s => s.Id == snapshotId);
            if (target is null) return false;

            target.IndexFingerprint = current;
            return SaveManifest(info, list);
        }
        catch
        {
            return false;
        }
    }

    /// 判断某个快照之后是否又有其他补丁写入过客户端。
    ///
    /// 依据索引指纹：快照记录创建时刻的索引指纹，当前客户端索引指纹与它不同，
    /// 说明快照之后索引被改写过 —— 可能来自本模块的再次应用，也可能来自
    /// 物价 / 汉化 / MTX 等其他补丁。
    ///
    /// 为什么必须拦：泥人与血条的还原是「回滚全量索引快照」，
    /// 若快照之后打过其他补丁，回滚会把那些补丁一起抹掉。
    ///
    /// 返回值 <see cref="SnapshotStaleness"/>：
    ///   · <c>Fresh</c>      指纹一致，可以安全回滚
    ///   · <c>Stale</c>      指纹不同，回滚会误杀其他改动
    ///   · <c>Unknown</c>    读不到当前索引或快照无指纹，调用方自行决定
    /// </summary>
    public SnapshotStaleness CheckSnapshotStaleness(GameModeInfo info, string snapshotId)
    {
        if (!IsAvailable || !info.IsValid) return SnapshotStaleness.Unknown;

        PatchSnapshotInfo? snap;
        try
        {
            snap = GetSnapshots(info).FirstOrDefault(s => s.Id == snapshotId);
        }
        catch
        {
            return SnapshotStaleness.Unknown;
        }

        if (snap is null || string.IsNullOrEmpty(snap.IndexFingerprint))
        {
            return SnapshotStaleness.Unknown;
        }

        string current = GetCurrentFingerprint(info);
        if (string.IsNullOrEmpty(current)) return SnapshotStaleness.Unknown;

        return string.Equals(current, snap.IndexFingerprint, StringComparison.OrdinalIgnoreCase)
            ? SnapshotStaleness.Fresh
            : SnapshotStaleness.Stale;
    }

    /// <summary>
    /// 计算当前客户端索引指纹。读取失败时返回空串，调用方应视为「无法判定」而非「版本已变」。
    /// </summary>
    public string GetCurrentFingerprint(GameModeInfo info)
    {
        try
        {
            var bytes = ExtractCurrentClientIndexBytes(info);
            return bytes is null ? string.Empty : ComputeFingerprint(bytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 读取客户端版本号（如 0.5.5e）。仅用于界面展示，判定版本变化一律以索引指纹为准。
    /// 日志缺失或格式变化时返回空串，不影响功能。
    /// </summary>
    public static string ReadGameVersion(GameModeInfo info)
    {
        try
        {
            string logPath = Path.Combine(info.GameDirectory, "logs", "LatestClient.txt");
            if (!File.Exists(logPath)) return string.Empty;

            // 只读尾部：日志可能很大
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long readFrom = Math.Max(0, stream.Length - 256 * 1024);
            stream.Seek(readFrom, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string text = reader.ReadToEnd();

            var match = System.Text.RegularExpressions.Regex.Match(text, @"poe2_production/tags/([\w.]+)");
            return match.Success ? match.Groups[1].Value : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 删除与当前客户端版本不匹配、且创建超过 <paramref name="maxAge"/> 的快照。
    ///
    /// 把旧版本的索引恢复进新版本客户端，会让客户端与资源不匹配而无法启动，
    /// 因此这类快照必须限期清理。官方底包不在此列——它失效时由
    /// <see cref="EnsureOfficialBackupForCurrentVersion"/> 直接重建。
    /// </summary>
    /// <returns>实际删除的快照数量。</returns>
    public int PurgeStaleSnapshots(GameModeInfo info, TimeSpan maxAge, Action<string>? logger = null)
    {
        if (!IsAvailable) return 0;

        try
        {
            // 版本判据用日志里的版本号，不能用索引指纹：
            // 索引内容会随补丁写入而变化，同一游戏版本下每次打补丁指纹都不同。
            string current = ReadGameVersion(info);
            if (string.IsNullOrEmpty(current)) return 0;

            var now = DateTime.Now;
            var list = GetSnapshots(info).ToList();
            int removed = 0;

            foreach (var snap in list.ToList())
            {
                // 只清理自动快照。
                //
                // 手动备份是用户主动创建的，可能有意长期保留，任何情况下都不能自动删除——
                // 用户失去它就无法回退到自己确认过的状态。官方底包同理由重建流程处理。
                if (snap.Type != PatchSnapshotType.Auto) continue;

                // 无版本号的是旧版程序创建的快照，无法证明仍适用，按过期处理
                bool stale = string.IsNullOrEmpty(snap.GameVersion)
                             || !string.Equals(snap.GameVersion, current, StringComparison.OrdinalIgnoreCase);
                if (!stale) continue;
                if (now - snap.CreatedAt < maxAge) continue;

                if (DeleteSnapshotInternal(info, snap.Id, out _))
                {
                    list.RemoveAll(s => s.Id == snap.Id);
                    removed++;
                    logger?.Invoke($"已清理过期快照【{snap.Name}】（版本不匹配且超过 {maxAge.TotalHours:F0} 小时）");
                }
            }

            if (removed > 0 && !SaveManifest(info, list))
            {
                // 清理属后台维护，清单写失败不阻断主流程，但要让用户知道磁盘可能没同步。
                logger?.Invoke(LastManifestError ?? "写入快照清单失败，清理结果未持久化。");
            }

            return removed;
        }
        catch (Exception ex)
        {
            logger?.Invoke($"清理过期快照时发生异常：{ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// 判断当前客户端是否纯净到可以生成新的纯净底包。
    ///
    /// 打补丁后索引里会带上补丁 bundle 记录，此时无法生成有效底包。调用方应依据
    /// 这个结果决定「重建」还是「保留现有备份」——删掉旧备份却建不出新的，
    /// 会让用户彻底失去还原点。
    /// </summary>
    public bool CanBuildCleanBackup(GameModeInfo info)
    {
        if (!IsAvailable || !info.IsValid) return false;

        try
        {
            if (info.Mode == GameMode.GGPK)
            {
                var ggpkPath = PatchEngine.GetGgpkPath(info);
                if (!File.Exists(ggpkPath)) return false;
                using var ggpk = new BundledGGPK(ggpkPath, parsePathsInIndex: false);
                return IsCleanIndex(ggpk.Index);
            }
            if (info.Mode == GameMode.Bundles2)
            {
                var bundlesDir = PatchEngine.GetBundlesDirectory(info);
                var indexPath = PatchEngine.GetBundlesIndexPath(info);
                if (!File.Exists(indexPath)) return false;
                var driveFactory = new DriveBundleFactory(bundlesDir);
                try
                {
                    using var index = new LibBundle3.Index(indexPath, parsePaths: false, driveFactory);
                    return IsCleanIndex(index);
                }
                finally
                {
                    (driveFactory as IDisposable)?.Dispose();
                }
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    /// <summary>
    /// 确保官方底包对应当前游戏版本。
    ///
    /// 游戏更新后旧底包会把客户端还原成旧版本资源，导致无法启动。检测到版本变化时：
    /// 删除旧底包与官方快照，再按当前客户端重新生成。若当前客户端已带补丁
    /// （<see cref="EnsureBackup"/> 会拒绝非纯净客户端），则保持「无有效底包」状态，
    /// 等客户端恢复纯净后下次启动再自动补齐。
    /// </summary>
    /// <returns>底包是否对应当前版本。</returns>
    public bool EnsureOfficialBackupForCurrentVersion(GameModeInfo info, Action<string>? logger = null)
    {
        if (!IsAvailable || !info.IsValid) return false;

        try
        {
            // 版本判据用日志版本号。索引指纹会随补丁写入变化，
            // 同一版本下每次打补丁都不同，不能用来判断版本是否更新。
            string current = ReadGameVersion(info);
            if (string.IsNullOrEmpty(current)) return false;

            var official = GetSnapshots(info).FirstOrDefault(s => s.Type == PatchSnapshotType.Official);
            bool matches = official is not null
                           && !string.IsNullOrEmpty(official.GameVersion)
                           && string.Equals(official.GameVersion, current, StringComparison.OrdinalIgnoreCase);

            if (matches) return true;

            // 版本不匹配（或旧底包没有版本号）。此时不能直接删掉旧底包：
            // 客户端若已打补丁，重建必然失败，删除会让用户彻底失去还原点。
            // 只有在确认当前客户端纯净、能够成功重建时，才替换旧底包。
            if (!CanBuildCleanBackup(info))
            {
                logger?.Invoke("当前客户端带有补丁，暂不重建纯净底包；保留既有备份。");
                return false;
            }

            if (official is not null)
            {
                logger?.Invoke($"检测到游戏版本变化（{official.GameVersion} → {current}），旧纯净底包已失效，开始重建。");
                DeleteSnapshotInternal(info, official.Id, out _);
                var remaining = GetSnapshots(info).Where(s => s.Type != PatchSnapshotType.Official).ToList();
                if (!SaveManifest(info, remaining))
                {
                    logger?.Invoke(LastManifestError ?? "写入快照清单失败，旧底包未清理。");
                    return false;
                }
            }

            // 只有确认能重建时才清除旧文件
            string backupPath = GetBackupFilePath(info);
            if (File.Exists(backupPath)) File.Delete(backupPath);

            return EnsureBackup(info, logger);
        }
        catch (Exception ex)
        {
            logger?.Invoke($"检查游戏版本时发生异常：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 判断官方底包是否与当前游戏版本一致。
    ///
    /// 判据用日志里的版本号，不用索引指纹 —— 指纹会随补丁写入变化，
    /// 同一版本下每次打补丁都不同。
    ///
    /// 底包没有版本号（早期版本生成的）也按不匹配处理：
    /// 无法证明它属于当前客户端时，宁可拒绝还原，也不要把客户端写坏。
    /// </summary>
    private bool IsOfficialSnapshotVersionMatched(GameModeInfo info, out string reason)
    {
        reason = string.Empty;

        var official = GetSnapshots(info).FirstOrDefault(s => s.Type == PatchSnapshotType.Official);
        if (official is null)
        {
            // 没有官方快照时交给后续逻辑处理（可能走单文件兜底），这里不拦。
            return true;
        }

        string current = ReadGameVersion(info);
        if (string.IsNullOrEmpty(current))
        {
            reason = "无法读取当前客户端版本，已拒绝还原：无法确认官方底包是否匹配。";
            return false;
        }

        if (string.IsNullOrEmpty(official.GameVersion)
            || !string.Equals(official.GameVersion, current, StringComparison.OrdinalIgnoreCase))
        {
            reason =
                $"官方纯净底包版本与当前客户端不匹配（底包 {official.GameVersion ?? "未知"}，" +
                $"当前 {current}），已拒绝还原。\n\n" +
                "用旧版本底包还原会让索引与资源布局错位，客户端将无法启动。\n" +
                "请改用「备份当前版本」手动创建的备份，或通过官方平台验证游戏文件。";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 执行全量纯净还原（优先还原 official 快照）
    ///
    /// 还原前必须校验官方底包的版本：游戏更新后旧底包会把客户端还原成旧版本资源，
    /// 索引与 bundle 布局错位，表现为大量文件「读取失败 - (uint)offset 超出范围」，
    /// 客户端无法启动。实测 2026-10-22 一次误还原导致 12,481 个文件读不出来。
    /// </summary>
    public BackupRestoreResult Restore(GameModeInfo info, Action<string>? logger = null)
    {
        if (!IsAvailable) return UnavailableResult();

        // 版本不匹配的官方底包绝不能用来还原
        if (!IsOfficialSnapshotVersionMatched(info, out var mismatchReason))
        {
            logger?.Invoke(mismatchReason);
            return new BackupRestoreResult(false, mismatchReason);
        }

        // 优先尝试从快照系统中还原 clean_official
        var res = RestoreSnapshot(info, "clean_official", out _, logger);
        if (res.Success) return res;

        // 兜底走历史单文件还原
        var backupPath = GetBackupFilePath(info);
        if (!File.Exists(backupPath))
        {
            string legacyPath = Path.Combine(_backupDir, info.Mode == GameMode.GGPK ? "ggpk_index.bin.bak" : "bundles2_index.bin.bak");
            if (File.Exists(legacyPath))
            {
                backupPath = legacyPath;
            }
            else
            {
                return new BackupRestoreResult(false, $"找不到纯净备份文件（目标位置: {backupPath}）。若从未在纯净状态下启动过本软件，请先通过官方平台验证游戏文件。");
            }
        }

        var (isLocked, lockReason) = CheckResourceLock(info);
        if (isLocked)
        {
            return new BackupRestoreResult(false, lockReason ?? "游戏文件正在被其他进程独占访问，无法写入还原。");
        }

        try
        {
            logger?.Invoke($"正在读取纯净备份文件（{new FileInfo(backupPath).Length / (1024.0 * 1024.0):F2} MB）...");
            var cleanBytes = File.ReadAllBytes(backupPath);

            if (info.Mode == GameMode.GGPK)
            {
                var ggpkPath = PatchEngine.GetGgpkPath(info);
                if (!File.Exists(ggpkPath)) return new BackupRestoreResult(false, $"未找到 Content.ggpk 文件：{ggpkPath}");

                using var ggpk = new BundledGGPK(ggpkPath, parsePathsInIndex: false);
                if (ggpk.Root.TryFindNode("Bundles2/_.index.bin", out var node) && node is LibGGPK3.Records.FileRecord record)
                {
                    logger?.Invoke("正在将纯净索引写入 Content.ggpk...");
                    record.Write(cleanBytes);
                    logger?.Invoke("写入成功！官方纯净索引已恢复。");
                    return new BackupRestoreResult(true, "已成功还原为纯净原版客户端！");
                }
                return new BackupRestoreResult(false, "在 Content.ggpk 中未能寻址到 Bundles2/_.index.bin 节点。");
            }
            else if (info.Mode == GameMode.Bundles2)
            {
                var indexPath = PatchEngine.GetBundlesIndexPath(info);
                var tmpPath = indexPath + ".tmp";
                File.WriteAllBytes(tmpPath, cleanBytes);
                if (File.Exists(indexPath)) File.Delete(indexPath);
                File.Move(tmpPath, indexPath);

                foreach (var prefix in KnownPatchPrefixes)
                {
                    var customDir = Path.Combine(info.GameDirectory, "Bundles2", prefix);
                    if (Directory.Exists(customDir))
                    {
                    // 自定义 bundle 目录是补丁写入产物，删除失败不阻断还原流程：
                    // 索引已回滚，残留目录不会被客户端引用，下次写入会被覆盖。
                    try { Directory.Delete(customDir, recursive: true); } catch { }
                    }
                }

                logger?.Invoke("写入成功！Bundles2 纯净索引与目录已恢复。");
                return new BackupRestoreResult(true, "已成功还原为纯净原版客户端！");
            }
        }
        catch (IOException ioEx)
        {
            return new BackupRestoreResult(false, $"还原时文件访问受阻：{ioEx.Message}（游戏、Steam 或下载器正在占用该文件，请彻底关闭后重试）", ioEx);
        }
        catch (Exception ex)
        {
            return new BackupRestoreResult(false, $"还原执行失败：{ex.Message}", ex);
        }

        return new BackupRestoreResult(false, "未知还原失败。");
    }
}
