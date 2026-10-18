using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LibBundle3;
using LibBundle3.Records;
using LibBundledGGPK3;
using LibGGPK3.Records;

namespace FreiAtlas.Patcher;

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
}

/// <summary>
/// 客户端索引纯净备份与多版本快照管理器 (双轨制：手动永久 + 自动轮转 5 版 + 官方底包锁定)
/// </summary>
public sealed class PatchBackupManager
{
    public const string OfficialSnapshotId = "clean_official";
    private readonly string _backupDir;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // 已知第三方及自身的补丁 bundle 路径特征
    private static readonly string[] KnownPatchPrefixes =
    [
        "FreiAtlas",
        "TinyPoe2Smoother",
        "Poe2Price",
        "patch_",
        "EF_"
    ];

    public PatchBackupManager(string? customBackupDir = null)
    {
        _backupDir = customBackupDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FreiAtlas",
            "patch_backup");

        if (!Directory.Exists(_backupDir))
        {
            Directory.CreateDirectory(_backupDir);
        }
    }

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
            ? Path.Combine(info.GameDirectory, "Content.ggpk")
            : Path.Combine(info.GameDirectory, "Bundles2", "_.index.bin");

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
        if (!Directory.Exists(subDir))
        {
            Directory.CreateDirectory(subDir);
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
        var manifestPath = GetManifestPath(info);
        var list = new List<PatchSnapshotInfo>();

        if (File.Exists(manifestPath))
        {
            try
            {
                var json = File.ReadAllText(manifestPath, Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<List<PatchSnapshotInfo>>(json);
                if (loaded != null) list = loaded;
            }
            catch { }
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
                    SaveManifest(info, list);
                }
                catch { }
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

    private void SaveManifest(GameModeInfo info, List<PatchSnapshotInfo> list)
    {
        try
        {
            var manifestPath = GetManifestPath(info);
            var json = JsonSerializer.Serialize(list, JsonOpts);
            File.WriteAllText(manifestPath, json, Encoding.UTF8);
        }
        catch { }
    }

    /// <summary>
    /// 从当前客户端读取 _.index.bin 字节内容
    /// </summary>
    private static byte[]? ExtractCurrentClientIndexBytes(GameModeInfo info)
    {
        if (info.Mode == GameMode.GGPK)
        {
            var ggpkPath = Path.Combine(info.GameDirectory, "Content.ggpk");
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
            var indexPath = Path.Combine(info.GameDirectory, "Bundles2", "_.index.bin");
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
        Action<string>? logger = null)
    {
        var (isLocked, lockReason) = CheckResourceLock(info);
        if (isLocked)
        {
            return new BackupRestoreResult(false, lockReason ?? "客户端资源被独占，无法创建版本快照。");
        }

        try
        {
            var indexBytes = ExtractCurrentClientIndexBytes(info);
            if (indexBytes == null || indexBytes.Length == 0)
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
                SettingsJson = settingsJson
            };

            list.Add(snap);

            // 如果是自动快照，严格执行保留最近 5 版的轮转淘汰策略
            if (type == PatchSnapshotType.Auto)
            {
                RotateAutoSnapshotsInternal(info, list, maxAutoKeep: 5, logger);
            }

            SaveManifest(info, list);
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
                logger?.Invoke($"自动轮转淘汰早期自动快照：{oldSnap.Name}");
            }
            catch { }
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
                var ggpkPath = Path.Combine(info.GameDirectory, "Content.ggpk");
                if (!File.Exists(ggpkPath)) return new BackupRestoreResult(false, $"未找到 Content.ggpk 文件：{ggpkPath}");

                using var ggpk = new BundledGGPK(ggpkPath, parsePathsInIndex: false);
                if (ggpk.Root.TryFindNode("Bundles2/_.index.bin", out var node) && node is LibGGPK3.Records.FileRecord record)
                {
                    logger?.Invoke($"正在将快照【{target.Name}】索引写回 Content.ggpk...");
                    record.Write(snapshotBytes);
                    restoredSettingsJson = target.SettingsJson;
                    logger?.Invoke("写入完成！客户端状态已恢复至该版本。");
                    return new BackupRestoreResult(true, $"已成功恢复到版本【{target.Name}】！");
                }
                return new BackupRestoreResult(false, "在 Content.ggpk 中未能寻址到 Bundles2/_.index.bin 节点。");
            }
            else if (info.Mode == GameMode.Bundles2)
            {
                var indexPath = Path.Combine(info.GameDirectory, "Bundles2", "_.index.bin");
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
                            try { Directory.Delete(customDir, recursive: true); } catch { }
                        }
                    }
                }

                restoredSettingsJson = target.SettingsJson;
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
        SaveManifest(info, list);

        try
        {
            string binPath = Path.Combine(GetSnapshotsDirectory(info), $"{target.Id}.bin");
            if (File.Exists(binPath)) File.Delete(binPath);
        }
        catch { }

        message = $"已删除快照【{target.Name}】。";
        return true;
    }

    /// <summary>
    /// 确保存在纯净官方原版备份（具备纯净性防污染检测）
    /// </summary>
    public bool EnsureBackup(GameModeInfo info, Action<string>? logger = null)
    {
        var backupPath = GetBackupFilePath(info);
        if (File.Exists(backupPath))
        {
            return true;
        }

        try
        {
            if (info.Mode == GameMode.GGPK)
            {
                var ggpkPath = Path.Combine(info.GameDirectory, "Content.ggpk");
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

                    // 同步固化为 official 快照
                    CreateSnapshot(info, PatchSnapshotType.Official, "🌟 官方出厂纯净底包", "首次未打补丁时的官方纯净原始索引，永久锁定不可覆盖", null, []);
                    return true;
                }
                return false;
            }
            else if (info.Mode == GameMode.Bundles2)
            {
                var bundlesDir = Path.Combine(info.GameDirectory, "Bundles2");
                var indexPath = Path.Combine(bundlesDir, "_.index.bin");
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

                    CreateSnapshot(info, PatchSnapshotType.Official, "🌟 官方出厂纯净底包", "首次未打补丁时的官方纯净原始索引，永久锁定不可覆盖", null, []);
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
            return true;
        }
    }

    /// <summary>
    /// 执行全量纯净还原（优先还原 official 快照）
    /// </summary>
    public BackupRestoreResult Restore(GameModeInfo info, Action<string>? logger = null)
    {
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
                var ggpkPath = Path.Combine(info.GameDirectory, "Content.ggpk");
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
                var indexPath = Path.Combine(info.GameDirectory, "Bundles2", "_.index.bin");
                var tmpPath = indexPath + ".tmp";
                File.WriteAllBytes(tmpPath, cleanBytes);
                if (File.Exists(indexPath)) File.Delete(indexPath);
                File.Move(tmpPath, indexPath);

                foreach (var prefix in KnownPatchPrefixes)
                {
                    var customDir = Path.Combine(info.GameDirectory, "Bundles2", prefix);
                    if (Directory.Exists(customDir))
                    {
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
