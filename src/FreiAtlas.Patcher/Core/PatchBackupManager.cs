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

    /// <summary>
    /// 建立该基线时客户端中已存在的补丁 bundle 前缀（自身与第三方）。
    ///
    /// 用户确认「以当前状态建立基线」时，基线索引会引用这些 bundle。
    /// 恢复前必须校验对应目录仍然存在：若第三方工具已被卸载，
    /// 恢复后的索引会指向缺失资源，客户端可能无法启动。
    /// </summary>
    public List<string> ThirdPartyPrefixes { get; set; } = [];
}

/// <summary>
/// 官方纯净基线的可用状态。
/// </summary>
public enum BaselineStatus
{
    /// <summary>基线存在且与当前客户端版本一致。</summary>
    Ready,

    /// <summary>基线存在，但版本与当前客户端不一致，不能用于还原。</summary>
    VersionMismatch,

    /// <summary>尚无基线。</summary>
    Missing
}

/// <summary>
/// 客户端索引纯净备份与多版本快照管理器 (双轨制：手动永久 + 自动轮转 5 版 + 官方底包锁定)
/// </summary>
public sealed class PatchBackupManager
{
    public const string OfficialSnapshotId = "clean_official";
    private readonly string _backupDir;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>
    /// 清单是否已被判定为损坏。
    ///
    /// 一旦置位，本实例拒绝任何 SaveManifest 写入 —— 否则后续的「官方底包
    /// 初始化」会用一条新记录覆盖掉损坏的清单，把用户全部手动/自动快照永久抹掉。
    /// 只有用户手动处理掉损坏文件、重启程序后才会恢复。
    /// </summary>
    private bool _manifestCorrupt;

    // 本工具自身写入的补丁 bundle 目录前缀。
    //
    // 恢复纯净时只允许删除这些目录：它们由本工具创建，删除不会影响其他软件。
    private static readonly string[] FreiAtlasManagedPrefixes =
    [
        "FreiAtlas",
        "TinyPoe2Smoother",
        "Poe2Price"
    ];

    // 第三方工具（易刷 E-farm / exilence-next-tx-app）的补丁 bundle 前缀。
    //
    // 只参与「客户端是否被改动」的检测，绝不参与删除：
    // 用户可能同时安装多个工具，删掉别人的补丁属于越权破坏。
    private static readonly string[] ThirdPartyPatchPrefixes =
    [
        "EniBundle",
        "TinyBundle"
    ];

    // GGPK 模式下 LibBundle3 写补丁时使用的输出目录（Bundles2/LibGGPK3/）。
    //
    // 干净客户端里没有这个目录（已实测：142 GB 的 Content.ggpk 中 Bundles2/ 下
    // 只有 Streaming/ 和 Content/ 两类子目录）。它的存在等价于「客户端已被写入补丁」。
    //
    // 这一项必须参与检测：GGPK 下补丁 bundle 全部落在这里，路径里不会出现
    // FreiAtlas / EniBundle 这些前缀，漏掉它会导致打满补丁的客户端被判定为
    // 「纯净」，进而把脏索引存成官方底包 —— 2026-10-28 的客户端损坏事故即由此而来。
    // 只检测，不删除。
    private static readonly string[] GgpkPatchBundlePrefixes =
    [
        "LibGGPK3"
    ];

    // 检测用的全量清单 = 自身 + 第三方 + GGPK 补丁目录。
    //
    // 这里只能放工具专有名，不能用 patch_、EF_ 之类通用片段：官方资源里存在
    // jackolanternpatch_01.ao、innocence_floor_relief_v01 这类路径，会被子串匹配
    // 误判成补丁，导致纯净客户端被拒绝创建底包。
    private static readonly string[] KnownPatchPrefixes =
        [.. FreiAtlasManagedPrefixes, .. ThirdPartyPatchPrefixes, .. GgpkPatchBundlePrefixes];

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
        if (IsBackupFileUsable(isolatedPath)) return true;

        // 兜底检查旧版共用备份
        string legacyPath = Path.Combine(_backupDir, info.Mode == GameMode.GGPK ? "ggpk_index.bin.bak" : "bundles2_index.bin.bak");
        return IsBackupFileUsable(legacyPath);
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
        catch (IOException)
        {
            // 独占打开失败有两种可能：
            //   1. 外部进程独占（Steam 更新 / 杀软扫描）—— 真占用；
            //   2. 本进程自己以 FileShare.ReadWrite 持有索引句柄 —— 不是占用。
            // 用共享模式再探一次即可区分：能打开说明没有外部独占者。
            // 不能只看「本引擎是否持有索引」就跳过检测 —— 那会让批量期间
            // 外部占用完全检不出来，只能等写盘时抛 IOException。
            try
            {
                using var shared = new FileStream(
                    targetFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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
            catch (Exception ex)
            {
                // 清单损坏时绝不能再走下方的 official 初始化：
                // 那会构造一条官方记录并 SaveManifest，用一条新记录覆盖掉
                // 损坏的清单，把用户全部手动/自动快照永久抹掉。
                // 宁可本次界面无快照，也不能销毁历史记录。
                _manifestCorrupt = true;
                LastManifestError =
                    $"读取快照清单失败（{manifestPath}）：{ex.Message}。" +
                    "为保护历史快照，本次不写入任何清单，也不重建官方底包。" +
                    "请先备份并手动检查该文件。";
                PreserveCorruptManifest(manifestPath);
                return [];
            }
        }

        // 确保官方底包 clean_official 存在并初始化
        var official = list.FirstOrDefault(s => s.Id == OfficialSnapshotId || s.Type == PatchSnapshotType.Official);
        string officialBinPath = Path.Combine(GetSnapshotsDirectory(info), $"{OfficialSnapshotId}.bin");

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
                        Id = OfficialSnapshotId,
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
    /// 原子写文件：先写同目录临时文件，再改名覆盖目标。
    ///
    /// 快照 .bin 与纯净底包都是上百 MB 的索引，直接 WriteAllBytes 一旦中途失败
    ///（磁盘满、进程被杀、杀软拦截），磁盘上会留下截断文件，而还原时读到的
    /// 正是这份残缺数据，等于把坏索引写回客户端。先写 .tmp 再改名，
    /// 目标文件要么是旧的完整内容，要么是新的完整内容。
    /// </summary>
    private static void WriteFileAtomic(string path, byte[] bytes)
    {
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
        WriteBackupMetadata(path, bytes);
    }

    /// <summary>
    /// 为备份文件写一份长度/摘要元数据。
    ///
    /// 备份文件是一次性整份写入的，但 File.Copy / WriteAllBytes 中途失败会留下
    /// 截断文件，而还原流程只看「文件存在」。元数据让读取方能校验完整性；
    /// 早期版本创建的备份没有元数据，退回只判存在。
    /// </summary>
    private static void WriteBackupMetadata(string path, byte[] bytes)
    {
        try
        {
            string metaPath = path + ".meta.json";
            var meta = new BackupFileMetadata
            {
                Length = bytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes))
            };
            string json = JsonSerializer.Serialize(meta, JsonOpts);
            string tmp = metaPath + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, metaPath, overwrite: true);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            // 元数据写失败不影响备份本体，下次校验退回「只判存在」。
        }
    }

    /// <summary>
    /// 备份文件是否可用于还原。
    ///
    /// 只看 File.Exists 不够：拷贝或写入被中断会留下截断文件，
    /// 还原时会被当成完整索引写回客户端。有元数据时按长度校验；
    /// 早期备份没有元数据，只能退回「存在且非空」。
    /// </summary>
    private bool IsBackupFileUsable(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0) return false;

        string metaPath = path + ".meta.json";
        if (!File.Exists(metaPath)) return true;

        try
        {
            var meta = JsonSerializer.Deserialize<BackupFileMetadata>(
                File.ReadAllText(metaPath, Encoding.UTF8));
            if (meta is null || meta.Length <= 0) return true;
            return file.Length == meta.Length;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            // 元数据损坏不应让备份不可用：无从证明它坏了，按可用处理。
            return true;
        }
    }

    private sealed class BackupFileMetadata
    {
        public long Length { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }

    /// <summary>
    /// 把损坏的清单另存一份取证副本。只复制不移动：
    /// 原文件必须留在原处，否则下一次调用会把它当成「从未有过清单」而重新初始化。
    /// </summary>
    private static void PreserveCorruptManifest(string manifestPath)
    {
        try
        {
            string copy = manifestPath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            if (!File.Exists(copy)) File.Copy(manifestPath, copy, overwrite: false);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            // 取证副本写不出来不影响主判定：原文件仍在原处，写回依然被禁止。
        }
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
        // 清单一旦判定损坏，禁止任何写回：继续写只会把剩余记录一起毁掉。
        if (_manifestCorrupt)
        {
            LastManifestError =
                $"快照清单已损坏并被保护，拒绝写入以免覆盖历史快照（{GetManifestPath(info)}）。" +
                "请先备份并手动检查该文件后重启程序。";
            return false;
        }

        try
        {
            var manifestPath = GetManifestPath(info);
            var json = JsonSerializer.Serialize(list, JsonOpts);
            // 原子写：清单是全部快照的索引，截断会让历史快照"消失"。
            // .tmp 内容在 File.WriteAllText 返回后才改名，中间失败不会破坏原文件。
            string tmp = manifestPath + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, manifestPath, overwrite: true);
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
        // Bundles2 直接读索引文件：这里只需要原始字节，解析索引既慢又会在
        // 客户端/测试夹具的索引不完整时抛异常。GGPK 必须从包内节点取，走统一会话。
        if (info.Mode == GameMode.Bundles2)
        {
            var indexPath = PatchEngine.GetBundlesIndexPath(info);
            if (!File.Exists(indexPath)) return null;
            return File.ReadAllBytes(indexPath);
        }

        using var session = ClientIndexSession.Open(info);
        return session?.ReadIndexBytes();
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
        byte[]? indexBytes = null,
        IReadOnlyList<string>? thirdPartyPrefixes = null)
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
                PatchSnapshotType.Official => OfficialSnapshotId,
                PatchSnapshotType.Auto => $"snap_auto_{timestamp}",
                _ => $"snap_manual_{timestamp}"
            };

            string binPath = Path.Combine(snapDir, $"{snapId}.bin");
            // 原子写入：先写 .tmp 再改名。直接 WriteAllBytes 若中途失败
            //（磁盘满 / 进程被杀），会在磁盘上留下截断的 .bin，
            // 而还原时读到的正是这份残缺数据 —— 等于把坏索引写回客户端。
            WriteFileAtomic(binPath, indexBytes);

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
                GameVersion = ReadGameVersion(info),
                ThirdPartyPrefixes = thirdPartyPrefixes?.ToList() ?? []
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

        // 版本硬拦截：用旧版本的 _.index.bin 覆盖新客户端，索引与资源布局会错位，
        // 客户端直接起不来（实测 2026-10-22 一次误还原导致 12,481 个文件读不出）。
        // 前端虽然会置灰按钮，但按钮状态可能因缓存或直接调用而被绕过，这里必须自己挡。
        //
        // 只在两边的版本都读得到时才比较：读不到版本说明客户端损坏或快照来自旧格式，
        // 此时放行由调用方按指纹判断，避免把「未知」当成「不匹配」而误拒。
        string currentVersion = ReadGameVersion(info);

        // 版本缺失一律拒绝。
        //
        // 旧版本创建的底包没有记录版本号，无法证明它与当前客户端兼容；
        // 一旦把旧构建的索引写进新客户端，索引与 bundle 布局错位，客户端直接起不来
        // （2026-10-28 实测：120,749,196 字节的底包写进 115,161,316 字节的客户端，
        //   Content.ggpk 报废，只能用官方平台重下）。
        // 这里宁可拒绝，也不做「读不到就当匹配」的放行。
        if (string.IsNullOrEmpty(target.GameVersion))
        {
            return new BackupRestoreResult(false,
                $"快照【{target.Name}】没有记录游戏版本，无法确认它与当前客户端兼容。\n\n" +
                "用未知版本的索引还原可能让客户端无法启动，已拒绝。\n" +
                "请改用当前版本创建的备份，或通过官方平台验证游戏文件。");
        }

        if (string.IsNullOrEmpty(currentVersion)
            || !string.Equals(target.GameVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
        {
            return new BackupRestoreResult(false,
                $"快照【{target.Name}】创建于 {target.GameVersion} 版本，当前客户端是 {(string.IsNullOrEmpty(currentVersion) ? "未知" : currentVersion)}。\n" +
                "跨版本还原会让索引与资源布局错位，客户端将无法启动，已拒绝。\n" +
                "如需回退，请用「恢复纯净客户端」重建当前版本的底包，再按需重新打补丁。");
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

            // 长度校验：快照创建时记录了 SizeBytes，这里必须核对读到的字节数。
            // 若上次创建被中断（磁盘满 / 进程被杀），磁盘上会留下截断的 .bin；
            // 不校验就把它当完整索引写回客户端，客户端将无法启动。
            if (target.SizeBytes > 0 && snapshotBytes.Length != target.SizeBytes)
            {
                return new BackupRestoreResult(false,
                    $"快照【{target.Name}】数据不完整：记录 {target.SizeBytes} 字节，实际读取 {snapshotBytes.Length} 字节。\n" +
                    "该快照创建时可能被中断，已拒绝还原以避免损坏客户端索引。" +
                    "请删除该快照后重新创建，或用「恢复纯净客户端」重建。");
            }

            if (info.Mode == GameMode.GGPK)
            {
                var ggpkPath = PatchEngine.GetGgpkPath(info);
                if (!File.Exists(ggpkPath)) return new BackupRestoreResult(false, $"未找到 Content.ggpk 文件：{ggpkPath}");

                return WriteIndexIntoGgpk(
                    ggpkPath, snapshotBytes,
                    $"已成功恢复到版本【{target.Name}】！",
                    logger);
            }
            else if (info.Mode == GameMode.Bundles2)
            {
                var indexPath = PatchEngine.GetBundlesIndexPath(info);
                var tmpPath = indexPath + ".tmp";

                // 基线若是在「客户端带第三方补丁」的状态下建立的，索引会引用那些 bundle。
                // 恢复前必须确认它们仍在：缺失时写回去等于让客户端引用不存在的资源。
                if (target.Type == PatchSnapshotType.Official && target.ThirdPartyPrefixes.Count > 0)
                {
                    var missing = target.ThirdPartyPrefixes
                        .Where(p => !Directory.Exists(Path.Combine(info.GameDirectory, "Bundles2", p)))
                        .ToList();

                    if (missing.Count > 0)
                    {
                        return new BackupRestoreResult(false,
                            $"该基线建立时客户端包含第三方补丁（{string.Join("、", missing)}），" +
                            "但这些目录现在已经不存在。\n\n" +
                            "用这份基线还原会让索引指向缺失的资源，客户端可能无法启动。\n" +
                            "请通过官方平台验证游戏完整性后，重新建立基线。");
                    }
                }

                File.WriteAllBytes(tmpPath, snapshotBytes);
                // 原子覆盖：不要先 Delete 再 Move。
                // Windows 下 Delete 成功后若 Move 因句柄占用失败，
                // _.index.bin 会永久丢失，客户端无法启动。
                File.Move(tmpPath, indexPath, overwrite: true);

                // 如果恢复的是纯净底包，顺带清理补丁文件夹
                if (target.Type == PatchSnapshotType.Official)
                {
                    foreach (var prefix in FreiAtlasManagedPrefixes)
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
        if (snapshotId == OfficialSnapshotId)
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

        // 先删磁盘文件、再改清单：反过来做的话，删除失败会留下
        // 「清单里没有、文件仍占盘」的孤儿，且没有任何入口能重试。
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
                $"快照【{target.Name}】的 {undeleted.Count} 个磁盘文件未能删除（仍占用磁盘空间）：" +
                $"{string.Join("；", undeleted)}。\n" +
                "清单条目已保留，关闭占用该文件的程序后可再次删除。";
            return false;
        }

        list.Remove(target);
        if (!SaveManifest(info, list))
        {
            message = LastManifestError ?? "写入快照清单失败，删除未生效（文件已删除，可重试）。";
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
    public bool EnsureBackup(
        GameModeInfo info,
        Action<string>? logger = null,
        bool allowDirtyBaseline = false,
        bool replaceExisting = false)
    {
        if (!IsAvailable)
        {
            logger?.Invoke(UnavailableReason ?? "补丁备份目录不可用，无法生成备份。");
            return false;
        }

        var backupPath = GetBackupFilePath(info);
        if (IsBackupFileUsable(backupPath) && !replaceExisting)
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
            // 版本号读不到时建出来的底包永远无法使用：
            // CheckBaseline 会一直返回 VersionMismatch，RestoreSnapshot 也会拒绝，
            // 用户会陷入「每次批量都重建底包、但底包永远不可用」的死循环。
            // 与其留下一个不可用的底包，不如在这里挡下并给出可执行的引导。
            if (string.IsNullOrEmpty(ReadGameVersion(info)))
            {
                logger?.Invoke(
                    "无法读取当前游戏版本（logs/LatestClient.txt 缺失或其中没有版本号）。\n" +
                    "请先启动一次游戏进入主菜单，让客户端生成日志，再回来打补丁。");
                return false;
            }

            if (info.Mode == GameMode.GGPK)
            {
                var ggpkPath = PatchEngine.GetGgpkPath(info);
                if (!File.Exists(ggpkPath))
                {
                    logger?.Invoke($"未找到文件：{ggpkPath}");
                    return false;
                }

                // 复用统一的只读会话：索引与包内节点都由 ClientIndexSession 负责，
                // 备份逻辑不再各自 new BundledGGPK。
                using var session = ClientIndexSession.Open(info);
                if (session?.Index is null)
                {
                    logger?.Invoke("无法打开客户端索引。");
                    return false;
                }

                var dirtyPrefixes = DetectPatchPrefixesInIndex(session.Index);
                if (dirtyPrefixes.Count > 0)
                {
                    if (!allowDirtyBaseline)
                    {
                        logger?.Invoke("检测到客户端 index 中已包含补丁 Bundle 记录，判定为非纯净客户端，拒绝将其存为纯净备份。");
                        return false;
                    }

                    logger?.Invoke($"已按用户确认，以当前状态建立基线。检测到的补丁前缀：{string.Join("、", dirtyPrefixes)}");
                }

                var indexBytes = session.ReadIndexBytes();
                if (indexBytes is null || indexBytes.Length == 0)
                {
                    logger?.Invoke("无法从客户端中读取 Bundles2/_.index.bin。");
                    return false;
                }

                WriteFileAtomic(backupPath, indexBytes);
                logger?.Invoke($"已成功生成纯净索引备份（{indexBytes.Length / (1024.0 * 1024.0):F2} MB）");

                // 同步固化为 official 快照。复用已读到的索引：会话仍持有 GGPK 句柄，
                // 让 CreateSnapshot 自行打开同一文件会失败。
                var snapResult = CreateSnapshot(
                    info, PatchSnapshotType.Official,
                    "🌟 官方出厂纯净底包",
                    "首次未打补丁时的官方纯净原始索引，永久锁定不可覆盖",
                    null, [], logger, indexBytes, dirtyPrefixes);
                if (!snapResult.Success)
                {
                    logger?.Invoke($"底包文件已生成，但快照登记失败：{snapResult.Message}");
                    return false;
                }
                return true;
            }
            else if (info.Mode == GameMode.Bundles2)
            {
                var indexPath = PatchEngine.GetBundlesIndexPath(info);
                if (!File.Exists(indexPath)) return false;

                using var session = ClientIndexSession.Open(info);
                if (session?.Index is null)
                {
                    logger?.Invoke("无法打开客户端索引。");
                    return false;
                }

                var dirtyPrefixes = DetectPatchPrefixesInIndex(session.Index);
                if (dirtyPrefixes.Count > 0)
                {
                    if (!allowDirtyBaseline)
                    {
                        logger?.Invoke("检测到客户端 index 中已包含补丁 Bundle 记录，判定为非纯净客户端，拒绝将其存为纯净备份。");
                        return false;
                    }

                    logger?.Invoke($"已按用户确认，以当前状态建立基线。检测到的补丁前缀：{string.Join("、", dirtyPrefixes)}");
                }

                // 一次性读入并原子写盘。File.Copy 中途失败会留下截断的 .bak，
                // 而 HasBackup 只看文件是否存在，后续还原会读到坏索引。
                var snapBytes = File.ReadAllBytes(indexPath);
                WriteFileAtomic(backupPath, snapBytes);
                logger?.Invoke($"已成功生成纯净索引备份（{snapBytes.Length / (1024.0 * 1024.0):F2} MB）");

                var snapResult = CreateSnapshot(
                    info, PatchSnapshotType.Official,
                    "🌟 官方出厂纯净底包",
                    "首次未打补丁时的官方纯净原始索引，永久锁定不可覆盖",
                    null, [], logger, snapBytes, dirtyPrefixes);
                if (!snapResult.Success)
                {
                    logger?.Invoke($"底包文件已生成，但快照登记失败：{snapResult.Message}");
                    return false;
                }
                return true;
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
            return DetectPatchPrefixesInIndex(index).Count == 0;
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
    /// 扫描已打开的索引，返回命中的补丁 bundle 前缀（自身与第三方）。
    /// 供已经持有索引句柄的调用方使用，避免重复打开同一文件。
    /// </summary>
    private static List<string> DetectPatchPrefixesInIndex(LibBundle3.Index index)
    {
        var hits = new List<string>();
        var bundles = index.Bundles.Span;
        for (int i = 0; i < bundles.Length; i++)
        {
            string bPath = bundles[i].Path;
            if (string.IsNullOrEmpty(bPath)) continue;

            // 检测必须用全量清单：只认自身前缀会把第三方补丁当成「纯净」。
            foreach (var prefix in KnownPatchPrefixes)
            {
                if (bPath.Contains(prefix, StringComparison.OrdinalIgnoreCase)
                    && !hits.Contains(prefix, StringComparer.OrdinalIgnoreCase))
                {
                    hits.Add(prefix);
                }
            }
        }
        return hits;
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

    /// <summary>
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
    /// 判断客户端当前索引是否与官方基线不同，即是否已经打过补丁。
    ///
    /// GGPK 模式下补丁 bundle 落在 Bundles2/LibGGPK3/ 下，与原始 bundle 共用同一个
    /// 命名空间，靠路径前缀完全判断不出来（FreiAtlas / EniBundle 这类前缀一个都匹配不上）。
    /// 只有比对索引指纹才是模式无关的可靠判据。
    ///
    /// 返回 null 表示无法判定（读不到当前索引，或基线没有记录指纹），调用方应保守处理。
    /// </summary>
    public bool? IsClientPatched(GameModeInfo info)
    {
        return CheckSnapshotStaleness(info, OfficialSnapshotId) switch
        {
            SnapshotStaleness.Fresh => false,
            SnapshotStaleness.Stale => true,
            _ => null
        };
    }

    private static readonly object _fingerprintCacheLock = new();
    private static string _fingerprintCacheKey = string.Empty;
    private static string _fingerprintCacheValue = string.Empty;

    /// <summary>
    /// 当前客户端索引指纹。
    ///
    /// 索引上百 MB，每次快照推送都重算 SHA256 会把 UI 线程卡住十几秒。
    /// 这里按「路径 + 长度 + 最后写入时间」缓存：索引没变就直接复用，
    /// 只有补丁真正写入后才会重算一次。
    /// </summary>
    public string GetCurrentFingerprint(GameModeInfo info)
    {
        try
        {
            string indexPath = PatchEngine.GetClientIndexPath(info);
            var file = new FileInfo(indexPath);
            if (!file.Exists) return string.Empty;

            string cacheKey = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            lock (_fingerprintCacheLock)
            {
                if (string.Equals(cacheKey, _fingerprintCacheKey, StringComparison.Ordinal))
                {
                    return _fingerprintCacheValue;
                }
            }

            var bytes = ExtractCurrentClientIndexBytes(info);
            string value = bytes is null ? string.Empty : ComputeFingerprint(bytes);

            lock (_fingerprintCacheLock)
            {
                _fingerprintCacheKey = cacheKey;
                _fingerprintCacheValue = value;
            }

            return value;
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
            if (!File.Exists(PatchEngine.GetClientIndexPath(info))) return false;

            // 与 DetectPatchPrefixes 共用同一条只读会话，不再各自 new BundledGGPK/Index。
            using var session = ClientIndexSession.Open(info);
            return session?.Index is not null && IsCleanIndex(session.Index);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 查询官方纯净基线的可用状态。
    ///
    /// 调用方据此决定：直接重建、自动重建，还是先询问用户。
    /// </summary>
    public BaselineStatus CheckBaseline(
        GameModeInfo info,
        out string? officialVersion,
        out string? currentVersion)
    {
        officialVersion = null;
        currentVersion = null;

        if (!IsAvailable || !info.IsValid) return BaselineStatus.Missing;

        currentVersion = ReadGameVersion(info);

        var official = GetSnapshots(info).FirstOrDefault(s => s.Type == PatchSnapshotType.Official);
        if (official is null) return BaselineStatus.Missing;

        officialVersion = official.GameVersion;

        // 版本号缺失时无法证明基线匹配，一律按不匹配处理：
        // 用旧索引还原新客户端会让资源布局错位。
        if (string.IsNullOrEmpty(currentVersion) || string.IsNullOrEmpty(officialVersion))
        {
            return BaselineStatus.VersionMismatch;
        }

        return string.Equals(officialVersion, currentVersion, StringComparison.OrdinalIgnoreCase)
            ? BaselineStatus.Ready
            : BaselineStatus.VersionMismatch;
    }

    /// <summary>
    /// 检测当前客户端索引中已存在的补丁 bundle 前缀（自身与第三方）。
    /// 用于向用户说明「是谁改动了客户端」，不能只给一个布尔值。
    ///
    /// 返回 null 表示「无法判定」（索引缺失或读取失败），与「确认没有补丁」的
    /// 空列表严格区分 —— 旧实现把两者都返回 []，调用方会把读取失败当成客户端纯净。
    /// </summary>
    public IReadOnlyList<string>? DetectPatchPrefixes(GameModeInfo info)
    {
        if (!IsAvailable || !info.IsValid) return null;

        try
        {
            if (!File.Exists(PatchEngine.GetClientIndexPath(info))) return null;

            // 统一走 ClientIndexSession，与 CanBuildCleanBackup 共用同一条只读路径。
            using var session = ClientIndexSession.Open(info);
            if (session?.Index is null) return null;
            return DetectPatchPrefixesInIndex(session.Index);
        }
        catch
        {
            // 探测失败必须表达为「不可判定」而不是「纯净」：
            // 误判为纯净会把来源不明的索引固化成官方底包。
            return null;
        }
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
            }

            // 先重建、成功后才替换：旧底包在新底包落盘并校验通过之前保持原样。
            // 反过来做（先删后建）一旦重建失败（版本读不到 / 磁盘满 / 文件被占用），
            // 用户会彻底失去唯一的还原点。
            // EnsureBackup 内部会以 Official 快照 id 覆盖登记，无需另行删除旧条目。
            return EnsureBackup(info, logger, allowDirtyBaseline: false, replaceExisting: true);
        }
        catch (Exception ex)
        {
            logger?.Invoke($"检查游戏版本时发生异常：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 丢弃现有基线，并以「当前客户端状态」重建一份。
    ///
    /// 只在用户明确确认后调用：当前客户端可能带着第三方补丁，重建出的基线会把
    /// 那些补丁的 bundle 记录一并固化进去。检测到的前缀会写入快照元数据，
    /// 恢复前据此校验对应目录是否仍然存在。
    ///
    /// 新基线落盘并校验通过后才会覆盖旧条目，重建失败时旧基线保持原样。
    /// </summary>
    public bool RebuildBaselineAsIs(GameModeInfo info, Action<string>? logger = null)
    {
        if (!IsAvailable || !info.IsValid) return false;

        try
        {
            return EnsureBackup(info, logger, allowDirtyBaseline: true, replaceExisting: true);
        }
        catch (Exception ex)
        {
            logger?.Invoke($"以当前状态重建基线时发生异常：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 把索引写回 Content.ggpk。
    ///
    /// 不能用 FileRecord.Write：它按「原地改节点字节」的方式工作。索引长度与当前节点
    /// 不一致时需要把差额交还 GGPK 空闲链表，这一步簿记不完整会破坏 GGPK 内部结构
    /// —— 实测表现为恢复后客户端启动报 bad allocation、后续读取找不到 bundle。
    ///
    /// 这里改用库提供的节点替换接口 GGPK.Replace，它和打补丁走同一条路径，
    /// 长度变化、空闲链表、根哈希都由库维护。
    /// </summary>
    private static BackupRestoreResult WriteIndexIntoGgpk(
        string ggpkPath,
        byte[] indexBytes,
        string successMessage,
        Action<string>? logger)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"freiatlas_index_{Guid.NewGuid():N}.bin");

        try
        {
            File.WriteAllBytes(tempFile, indexBytes);

            using (var ggpk = new LibGGPK3.GGPK(ggpkPath))
            {
                if (!ggpk.Root.TryFindNode("Bundles2/_.index.bin", out var node)
                    || node is not LibGGPK3.Records.FileRecord indexRecord)
                {
                    return new BackupRestoreResult(false, "在 Content.ggpk 中未能寻址到 Bundles2/_.index.bin 节点。");
                }

                logger?.Invoke($"正在将索引写入 Content.ggpk（{indexBytes.Length / (1024.0 * 1024.0):F2} MB）...");

                int replaced = LibGGPK3.GGPK.Replace(indexRecord, tempFile, callback: null);
                if (replaced <= 0)
                {
                    return new BackupRestoreResult(false, "写入 Content.ggpk 索引失败：库未替换任何条目。");
                }

                // 索引内容变了，根哈希必须重算，否则游戏校验会拒绝这份 GGPK。
                ggpk.RenewHashes();
                ggpk.Flush();
            }

            // 写后自检：重新打开读回索引并逐字节比对。
            //
            // 这是安全网：如果节点替换在长度变化时出了问题，必须在这里暴露，
            // 而不是等用户启动游戏才发现客户端已经起不来。
            using (var check = new LibGGPK3.GGPK(ggpkPath))
            {
                if (!check.Root.TryFindNode("Bundles2/_.index.bin", out var verifyNode)
                    || verifyNode is not LibGGPK3.Records.FileRecord verifyRecord)
                {
                    return new BackupRestoreResult(false,
                        "写入后自检失败：重新打开 Content.ggpk 找不到索引节点。客户端可能已损坏，请用官方平台验证游戏文件完整性。");
                }

                var actual = verifyRecord.Read().ToArray();
                if (actual.Length != indexBytes.Length || !actual.AsSpan().SequenceEqual(indexBytes))
                {
                    return new BackupRestoreResult(false,
                        $"写入后自检失败：读回 {actual.Length:N0} 字节，期望 {indexBytes.Length:N0} 字节，内容不一致。" +
                        "客户端可能已损坏，请用官方平台验证游戏文件完整性。");
                }
            }

            logger?.Invoke("写入完成，读回校验一致。");
            return new BackupRestoreResult(true, successMessage);
        }
        catch (Exception ex)
        {
            return new BackupRestoreResult(false, $"写入 Content.ggpk 索引失败：{ex.Message}", ex);
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
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
        var res = RestoreSnapshot(info, OfficialSnapshotId, out _, logger);
        if (res.Success) return res;

        // 兜底走历史单文件还原
        var backupPath = GetBackupFilePath(info);
        if (!IsBackupFileUsable(backupPath))
        {
            string legacyPath = Path.Combine(_backupDir, info.Mode == GameMode.GGPK ? "ggpk_index.bin.bak" : "bundles2_index.bin.bak");
            if (IsBackupFileUsable(legacyPath))
            {
                backupPath = legacyPath;
            }
            else
            {
                return new BackupRestoreResult(false, $"找不到纯净备份文件（目标位置: {backupPath}）。若备份已损坏或从未在纯净状态下启动过本软件，请先通过官方平台验证游戏文件。");
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

                return WriteIndexIntoGgpk(
                    ggpkPath, cleanBytes,
                    "已成功还原为纯净原版客户端！",
                    logger);
            }
            else if (info.Mode == GameMode.Bundles2)
            {
                var indexPath = PatchEngine.GetBundlesIndexPath(info);
                var tmpPath = indexPath + ".tmp";
                File.WriteAllBytes(tmpPath, cleanBytes);
                // 同还原快照：原子覆盖，避免 Delete 成功而 Move 失败导致索引丢失。
                File.Move(tmpPath, indexPath, overwrite: true);

                foreach (var prefix in FreiAtlasManagedPrefixes)
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
