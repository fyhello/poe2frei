using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FreiAtlas.Patcher.Core;

/// <summary>
/// 逐文件原始字节备份（技能特效屏蔽与预设效果补丁共用）。
///
/// 存在的理由：索引级快照（<see cref="PatchBackupManager"/> 的 snapshots）只保存索引，
/// 一旦客户端被外部工具重建（PackCheck 修复、第三方补丁器、游戏更新），
/// 索引里的 bundle 偏移就和实际 bundle 布局脱节，快照再也读不出原始文件。
/// 那种情况下唯一可靠的还原来源，是写入前落盘的逐文件字节副本。
///
/// 存放位置：%LOCALAPPDATA%\FreiAtlas\patch_file_backup\{分组标识}\
/// 用 LOCALAPPDATA 而不是 TEMP，因为 TEMP 会被系统清理。
///
/// 同一路径已存在备份时不覆盖——第一次写入前的状态才是"原始"状态，
/// 后续重复写入不应把已屏蔽的内容当成原始内容。
/// </summary>
public static class PatchFileBackup
{
    private const string ManifestName = "manifest.json";

    /// <summary>
    /// 串行化 SaveOriginals / SaveManifest / RemoveOriginals。
    ///
    /// 这些方法都走「读清单 → 改内存 → 写回清单」的流程，并发调用会互相覆盖：
    /// 后写入的一方拿着旧快照，把前一方刚登记的条目抹掉。备份属于低频操作，
    /// 用一把进程内的锁换正确性是划算的。
    /// </summary>
    private static readonly object SaveGate = new();

    /// <summary>最近一次清单读写的失败原因。查询失败时为 null。</summary>
    public static string? LastManifestError { get; private set; }

    /// <summary>最近一次 SaveOriginals 中从孤儿文件补录进清单的条目数（异常恢复观测用）。</summary>
    public static int LastAdoptedCount { get; private set; }

    /// <summary>测试用备份根目录覆盖；生产环境保持为 null。</summary>
    internal static string? RootDirectoryOverride { get; set; }

    /// <summary>备份根目录。</summary>
    public static string GetRootDirectory()
    {
        if (!string.IsNullOrEmpty(RootDirectoryOverride))
        {
            return RootDirectoryOverride;
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var current = Path.Combine(local, "FreiAtlas", "patch_file_backup");
        var legacy = Path.Combine(local, "FreiAtlas", "spell_backup");
        TryMigrateLegacy(legacy, current);
        return current;
    }

    /// <summary>
    /// 将旧版 spell_backup 目录幂等迁移到新版 patch_file_backup 目录。
    /// 保留目标目录中已有文件不覆盖；迁移完成后安全清理旧空目录。
    /// </summary>
    public static void TryMigrateLegacy(string legacyDir, string targetDir)
    {
        try
        {
            if (!Directory.Exists(legacyDir)) return;

            Directory.CreateDirectory(targetDir);
            foreach (var subDir in Directory.GetDirectories(legacyDir))
            {
                var dirName = Path.GetFileName(subDir);
                var destSubDir = Path.Combine(targetDir, dirName);

                if (!Directory.Exists(destSubDir))
                {
                    Directory.Move(subDir, destSubDir);
                }
                else
                {
                    foreach (var file in Directory.GetFiles(subDir))
                    {
                        var destFile = Path.Combine(destSubDir, Path.GetFileName(file));
                        if (!File.Exists(destFile))
                        {
                            File.Move(file, destFile);
                        }
                    }

                    if (Directory.GetFileSystemEntries(subDir).Length == 0)
                    {
                        Directory.Delete(subDir, recursive: false);
                    }
                }
            }

            if (Directory.GetFileSystemEntries(legacyDir).Length == 0)
            {
                Directory.Delete(legacyDir, recursive: false);
            }
        }
        catch
        {
            // 防御性忽略迁移异常，不阻断主流程
        }
    }

    /// <summary>某个技能的备份目录。</summary>
    public static string GetGroupDirectory(string groupId)
    {
        // groupId 会直接参与 Path.Combine，必须挡住 ".." 与绝对路径：
        // 否则一个构造出来的分组标识就能把备份写到根目录之外。
        if (!VirtualPath.IsSafeRelative(groupId))
        {
            throw new ArgumentException($"备份分组标识不是安全的相对名称：{groupId}", nameof(groupId));
        }

        return Path.Combine(GetRootDirectory(), Sanitize(groupId));
    }

    /// <summary>
    /// 保存原始字节。已存在的备份不覆盖。
    /// </summary>
    /// <param name="reconstructedPaths">
    /// 不是从客户端直接读出、而是事后重建的路径。写入备份时会带上标记，
    /// 让还原操作能提示"这份数据不是原始副本"，避免把重建内容当成原件长期信任。
    /// </param>
    /// <returns>本次新写入的备份数量。</returns>
    public static int SaveOriginals(
        string groupId,
        IEnumerable<string> targetPaths,
        IReadOnlyDictionary<string, byte[]> originals,
        ISet<string>? reconstructedPaths = null,
        string? gameVersion = null)
    {
        lock (SaveGate)
        {
            var dir = GetGroupDirectory(groupId);
            Directory.CreateDirectory(dir);

            var targets = new HashSet<string>(targetPaths, StringComparer.OrdinalIgnoreCase);
            var manifest = LoadManifest(dir);
            // 一次目录枚举取代并行循环里的逐文件 File.Exists。
            // 商城首次备份有 13,306 个文件，逐条探测在杀软实时扫描下约 2 ms/次，
            // 单这一项就占掉近 20 秒；目录枚举一次即可得到同样的「已存在」判断。
            var existingFileNames = EnumerateExistingFileNames(dir);
            var written = new System.Collections.Concurrent.ConcurrentDictionary<string, BackupEntry>(
                StringComparer.OrdinalIgnoreCase);
            var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
            int adopted = 0;

            // 首次备份可能涉及上万个小文件（商城套装一次 13,306 个）。
            // 逐个 WriteAllBytes + SHA256 是纯串行 IO，实测占掉近 30 秒；
            // 这里并行写文件与算哈希，manifest 合并仍在主线程完成，格式不变。
            System.Threading.Tasks.Parallel.ForEach(
                originals,
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 8 },
                kv =>
                {
                    if (!targets.Contains(kv.Key)) return;

                    try
                    {
                        var fileName = FileNameFor(kv.Key);
                        var filePath = Path.Combine(dir, fileName);

                        // 清单里已有记录：只有备份文件仍在才算真正完成。
                        // 逐个 File.Exists 在开启杀软实时扫描的机器上约 2ms/次，
                        // 上万条就是几十秒，因此复用一次目录枚举的结果。
                        if (manifest.TryGetValue(kv.Key, out var existing)
                            && !string.IsNullOrEmpty(existing.FileName)
                            && existingFileNames.Contains(existing.FileName))
                        {
                            return;
                        }

                        if (existingFileNames.Contains(fileName))
                        {
                            // 文件在、清单里没有：上一次 SaveOriginals 写完文件后
                            // 未能落盘清单（断电 / 进程被杀 / 磁盘满）留下的孤儿。
                            // 内容来自当时的原始字节，但无法证明来源，因此补录进
                            // 清单并标记为「重建」——否则下一次应用会把它当作
                            // 「已备份」跳过，这份原件就永远不可见了。
                            long length = new FileInfo(filePath).Length;
                            written[kv.Key] = new BackupEntry
                            {
                                FileName = fileName,
                                Length = (int)Math.Min(length, int.MaxValue),
                                Sha256 = ComputeFileSha256(filePath),
                                SavedAt = File.GetLastWriteTimeUtc(filePath),
                                IsReconstructed = true,
                                GameVersion = gameVersion
                            };
                            Interlocked.Increment(ref adopted);
                            return;
                        }

                        WriteBackupFile(filePath, kv.Value);
                        written[kv.Key] = new BackupEntry
                        {
                            FileName = fileName,
                            Length = kv.Value.Length,
                            Sha256 = Convert.ToHexString(SHA256.HashData(kv.Value)),
                            SavedAt = DateTime.UtcNow,
                            IsReconstructed = reconstructedPaths?.Contains(kv.Key) == true,
                            GameVersion = gameVersion
                        };
                    }
                    catch (Exception ex)
                    {
                        // 单个文件失败不能中断整批：一旦整批抛出，已写入的文件
                        // 会留在磁盘上却进不了清单，全部变成下一次被跳过的孤儿。
                        failures.Add($"{kv.Key}（{ex.Message}）");
                    }
                });

            int saved = 0;
            foreach (var (virtualPath, entry) in written)
            {
                manifest[virtualPath] = entry;
                saved++;
            }

            LastAdoptedCount = adopted;
            if (failures.Count > 0)
            {
                LastManifestError =
                    $"有 {failures.Count} 个文件未能写入备份目录：" +
                    string.Join("；", failures.Take(5)) +
                    (failures.Count > 5 ? $"…（共 {failures.Count} 个）" : string.Empty);
            }

            // 有新增/补录才写清单；纯跳过时不产生无谓磁盘写入。
            if (saved > 0 || failures.Count > 0)
            {
                SaveManifest(dir, manifest);
            }

            return saved;
        }
    }

    /// <summary>
    /// 列出某个技能可还原的文件。
    /// </summary>
    public static IReadOnlyList<(string VirtualPath, string FilePath, int Length)> ListOriginals(string groupId)
    {
        var dir = GetGroupDirectory(groupId);

        Dictionary<string, BackupEntry> manifest;
        try
        {
            manifest = LoadManifest(dir);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // 查询类接口不抛：清单损坏按「无备份」处理并保留 LastManifestError，
            // 由写入路径负责强制上报。与 CountOriginals 的契约保持一致，
            // 避免 UI 状态查询因一份坏清单直接崩掉。
            return [];
        }

        var result = new List<(string, string, int)>();
        foreach (var (virtualPath, entry) in manifest)
        {
            var filePath = Path.Combine(dir, entry.FileName);
            if (File.Exists(filePath))
            {
                result.Add((virtualPath, filePath, entry.Length));
            }
        }
        return result;
    }

    /// <summary>
    /// 只统计清单条目数，不读取任何备份文件内容。
    ///
    /// 供界面查询使用：LoadOriginals 会把每个文件全读进内存，
    /// 任一文件损坏或被占用就抛异常，而这些查询位于 UI 快照路径上，
    /// 一次异常就会打断整个界面刷新。
    /// </summary>
    public static int CountOriginals(string groupId)
    {
        var dir = GetGroupDirectory(groupId);
        if (!Directory.Exists(dir)) return 0;

        try
        {
            return LoadManifest(dir).Count;
        }
        catch
        {
            // 查询类接口不抛：清单读不出来按「无备份」处理，
            // 具体错误由写入路径的 LoadManifest 调用方负责上报。
            return 0;
        }
    }

    public static List<(string VirtualPath, byte[] Data)> LoadOriginals(string groupId)
    {
        var result = new List<(string, byte[])>();
        var failures = new List<string>();

        foreach (var (virtualPath, filePath, length) in ListOriginals(groupId))
        {
            // 单个文件读失败不应让整组加载崩掉：备份目录里可能有几十上百个文件，
            // 其中一个被杀软隔离或磁盘坏道，不该导致「全部还原」失败。
            // 失败的条目收集起来，由最后统一上报（不静默吞掉）。
            try
            {
                var bytes = File.ReadAllBytes(filePath);

                // 长度校验：manifest 记录了备份时的字节数。磁盘满、杀软隔离或
                // 进程中断都可能留下截断文件，若当成有效原件写回客户端，
                // 得到的就是一个损坏的资源。宁可拒绝还原并明确报错。
                if (length > 0 && bytes.Length != length)
                {
                    failures.Add(
                        $"{virtualPath}（长度不符：清单 {length} 字节，实际 {bytes.Length} 字节，可能已截断）");
                    continue;
                }

                result.Add((virtualPath, bytes));
            }
            catch (Exception ex)
            {
                failures.Add($"{virtualPath}（{ex.Message}）");
            }
        }

        LastManifestError = failures.Count == 0
            ? null
            : $"有 {failures.Count} 个备份文件读取失败，这些条目将无法还原：" +
              string.Join("；", failures.Take(5)) +
              (failures.Count > 5 ? $"…（共 {failures.Count} 个）" : string.Empty);

        return result;
    }

    /// <summary>
    /// 检查某组备份是否来自其他游戏版本。
    ///
    /// 返回 null 表示可安全还原（版本一致，或备份未记录版本号）。
    /// 返回非 null 时是不可还原的原因，调用方应提示用户而非强行写回。
    /// </summary>
    public static string? CheckVersionMismatch(string groupId, string? currentVersion)
    {
        if (string.IsNullOrWhiteSpace(currentVersion)) return null;

        var dir = GetGroupDirectory(groupId);
        if (!Directory.Exists(dir)) return null;

        Dictionary<string, BackupEntry> manifest;
        try
        {
            manifest = LoadManifest(dir);
        }
        catch
        {
            return null;   // 清单问题由 LoadManifest 的调用方另行上报
        }

        var stale = manifest
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value.GameVersion)
                         && !string.Equals(kv.Value.GameVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Value.GameVersion!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (stale.Count == 0) return null;

        return $"备份来自其他游戏版本（备份：{string.Join("、", stale.Take(3))}，当前：{currentVersion}）。"
             + "游戏更新后资源内容已变化，写回旧版本文件可能导致显示异常或客户端报错。\n"
             + "建议先「恢复纯净客户端」再重新打补丁。";
    }

    /// <summary>
    /// 移除指定路径列表的备份条目（当这些文件已成功写回客户端恢复为原件后调用）。
    /// </summary>
    public static void RemoveOriginals(string groupId, IEnumerable<string> virtualPaths)
    {
        lock (SaveGate)
        {
            var dir = GetGroupDirectory(groupId);
            if (!Directory.Exists(dir)) return;

            var manifest = LoadManifest(dir);
            if (manifest.Count == 0) return;

            bool changed = false;
            foreach (var vp in virtualPaths)
            {
                if (manifest.TryGetValue(vp, out var entry))
                {
                    manifest.Remove(vp);
                    changed = true;
                    try
                    {
                        var filePath = Path.Combine(dir, entry.FileName);
                        if (File.Exists(filePath)) File.Delete(filePath);
                    }
                    catch
                    {
                        // 忽略单个文件删除异常
                    }
                }
            }

            if (changed)
            {
                SaveManifest(dir, manifest);
            }
        }
    }

    /// <summary>
    /// 删除某个分组的全部备份。
    ///
    /// 用于索引级还原之后：索引已回到补丁前状态，逐文件备份不再有意义，
    /// 留着会在下次应用时被当成「原始内容」误用。
    /// </summary>
    public static void Clear(string groupId)
    {
        var dir = GetGroupDirectory(groupId);
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // 清理失败不影响主流程：下次应用时备份已存在的文件会被跳过，
            // 最坏情况只是多占一点磁盘。
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    /// <summary>
    /// 虚拟路径转成扁平文件名。
    ///
    /// 不能用「把 / 换成 _」：a/b_c 与 a_b/c 会撞成同一个名字，两份备份
    /// 互相覆盖，还原时把 A 的内容写进 B 的路径。改用路径的 SHA-256 前 16 位，
    /// 既避免碰撞，也不受 Windows 260 字符路径长度限制。
    /// </summary>
    private static string FileNameFor(string virtualPath)
    {
        var normalized = VirtualPath.Normalize(virtualPath);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant() + ".bin";
    }

    /// <summary>
    /// 写单个备份文件：先写 .tmp 再原子改名，避免中途失败留下截断文件。
    /// </summary>
    private static void WriteBackupFile(string filePath, byte[] bytes)
    {
        string tmp = filePath + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, filePath, overwrite: true);
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// 枚举备份目录里已存在的文件名。
    /// 只做一次目录枚举，避免 SaveOriginals 对每个待备份文件单独调用 File.Exists。
    /// </summary>
    private static HashSet<string> EnumerateExistingFileNames(string directory)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(name))
            {
                names.Add(name);
            }
        }
        return names;
    }

    private static Dictionary<string, BackupEntry> LoadManifest(string dir)
    {
        var path = Path.Combine(dir, ManifestName);
        if (!File.Exists(path)) return new Dictionary<string, BackupEntry>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<Dictionary<string, BackupEntry>>(json);
            if (data is null)
            {
                // 空 JSON 也视为损坏：正常清单至少有若干条记录。
                throw new InvalidDataException("清单内容为空。");
            }
            return new Dictionary<string, BackupEntry>(data, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // 清单损坏绝不能静默降级为空清单。
            //
            // 空清单会让 SaveOriginals 认为「这些文件都没备份过」，于是从
            // 【当前客户端】重新读取内容当作原件保存 —— 而当前客户端可能
            // 已经打过补丁，等于把补丁后的内容冒充原件存下来。用户之后点
            // 「恢复原版」写回的就是补丁内容，原始文件永久丢失。
            //
            // 因此这里必须让错误可见：记录原因并在读取方上报。
            LastManifestError = $"读取备份清单失败（{path}）：{ex.Message}。" +
                "请勿在此状态下重新打补丁 —— 否则会把已修改的内容误存为原件。";
            throw new InvalidDataException(LastManifestError, ex);
        }
    }

    /// <summary>
    /// 写清单：先写 .tmp 再原子改名。
    ///
    /// 清单是全部逐文件备份的唯一索引。上万个备份文件写完后，若清单写到一半
    /// 断电 / 被杀 / 磁盘满，下一次 LoadManifest 会判定「清单损坏」，
    /// 整组备份全部不可见。原子写保证目标文件要么是旧完整内容，
    /// 要么是新完整内容。
    /// </summary>
    private static void SaveManifest(string dir, Dictionary<string, BackupEntry> manifest)
    {
        var path = Path.Combine(dir, ManifestName);
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, json, Encoding.UTF8);
        File.Move(tmp, path, overwrite: true);
    }

    private sealed class BackupEntry
    {
        public string FileName { get; set; } = string.Empty;
        public int Length { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public DateTime SavedAt { get; set; }

        /// <summary>
        /// 该备份不是从客户端直接读出的原始字节，而是事后重建。
        /// 还原时据此提示用户，避免把重建内容误当成官方原件。
        /// </summary>
        public bool IsReconstructed { get; set; }

        /// <summary>
        /// 备份时客户端的版本号。
        ///
        /// 游戏大更新后资源内容会变，此时拿旧版本备份还原会写回过期文件，
        /// 轻则显示异常，重则资源格式不匹配导致客户端报错。
        /// 还原前据此提示用户，而不是默默写回。
        /// 早期版本创建的备份没有该字段（null），按「版本未知」处理。
        /// </summary>
        public string? GameVersion { get; set; }
    }
}
