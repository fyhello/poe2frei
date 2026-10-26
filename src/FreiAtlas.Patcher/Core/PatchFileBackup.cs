using System.IO;
using System.Security.Cryptography;
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

    /// <summary>备份根目录。</summary>
    public static string GetRootDirectory()
    {
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
    public static string GetGroupDirectory(string groupId) =>
        Path.Combine(GetRootDirectory(), Sanitize(groupId));

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
        ISet<string>? reconstructedPaths = null)
    {
        var dir = GetGroupDirectory(groupId);
        Directory.CreateDirectory(dir);

        var targets = new HashSet<string>(targetPaths, StringComparer.OrdinalIgnoreCase);
        var manifest = LoadManifest(dir);
        int saved = 0;

        foreach (var (virtualPath, data) in originals)
        {
            if (!targets.Contains(virtualPath)) continue;

            var fileName = FileNameFor(virtualPath);
            var filePath = Path.Combine(dir, fileName);
            if (File.Exists(filePath)) continue;   // 保留最早的原始副本

            File.WriteAllBytes(filePath, data);
            manifest[virtualPath] = new BackupEntry
            {
                FileName = fileName,
                Length = data.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(data)),
                SavedAt = DateTime.Now,
                IsReconstructed = reconstructedPaths?.Contains(virtualPath) == true
            };
            saved++;
        }

        SaveManifest(dir, manifest);
        return saved;
    }

    /// <summary>
    /// 列出某个技能可还原的文件。
    /// </summary>
    public static IReadOnlyList<(string VirtualPath, string FilePath, int Length)> ListOriginals(string groupId)
    {
        var dir = GetGroupDirectory(groupId);
        var manifest = LoadManifest(dir);

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
    /// 读取某个分组的全部原始字节。
    /// </summary>
    public static List<(string VirtualPath, byte[] Data)> LoadOriginals(string groupId)
    {
        var result = new List<(string, byte[])>();
        foreach (var (virtualPath, filePath, _) in ListOriginals(groupId))
        {
            result.Add((virtualPath, File.ReadAllBytes(filePath)));
        }
        return result;
    }

    /// <summary>
    /// 移除指定路径列表的备份条目（当这些文件已成功写回客户端恢复为原件后调用）。
    /// </summary>
    public static void RemoveOriginals(string groupId, IEnumerable<string> virtualPaths)
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

    /// <summary>虚拟路径转成扁平文件名，避免在备份目录里再造一层目录。</summary>
    private static string FileNameFor(string virtualPath) =>
        virtualPath.Replace('\\', '/').TrimStart('/').Replace('/', '_');

    private static Dictionary<string, BackupEntry> LoadManifest(string dir)
    {
        var path = Path.Combine(dir, ManifestName);
        if (!File.Exists(path)) return new Dictionary<string, BackupEntry>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<Dictionary<string, BackupEntry>>(json);
            return data is null
                ? new Dictionary<string, BackupEntry>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, BackupEntry>(data, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // 清单损坏时按空清单处理：宁可重新备份一份，也不要因为清单读不出而跳过备份。
            return new Dictionary<string, BackupEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SaveManifest(string dir, Dictionary<string, BackupEntry> manifest)
    {
        var path = Path.Combine(dir, ManifestName);
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
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
    }
}
