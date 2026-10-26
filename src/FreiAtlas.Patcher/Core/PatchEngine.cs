using System.IO;
using System.IO.Compression;
using LibBundle3;
using LibBundle3.Records;
using LibBundledGGPK3;

namespace FreiAtlas.Patcher.Core;

public sealed class PatchEngine
{
    private readonly PatchBackupManager _backupManager;

    public PatchBackupManager BackupManager => _backupManager;

    public static Exception? LastReadException { get; set; }
    public static Exception? LastWriteException { get; set; }

    public PatchEngine(PatchBackupManager? backupManager = null)
    {
        _backupManager = backupManager ?? new PatchBackupManager();
    }

    private static readonly object _fileLock = new();

    /// <summary>
    /// 客户端 GGPK 文件路径（仅 GGPK 模式有意义）。
    ///
    /// 该拼法原本散落在 16 处，改为统一入口，避免某一处漏写 "Content.ggpk"
    /// 或把目录层级拼错。
    /// </summary>
    public static string GetGgpkPath(GameModeInfo info)
        => Path.Combine(info.GameDirectory, "Content.ggpk");

    /// <summary>客户端 Bundles2 目录路径。</summary>
    public static string GetBundlesDirectory(GameModeInfo info)
        => Path.Combine(info.GameDirectory, "Bundles2");

    /// <summary>客户端 Bundles2 索引文件路径。</summary>
    public static string GetBundlesIndexPath(GameModeInfo info)
        => Path.Combine(GetBundlesDirectory(info), "_.index.bin");

    /// <summary>
    /// 主索引文件路径：GGPK 模式为 Content.ggpk，Bundles2 模式为 _.index.bin。
    /// 备份与版本检测都以它为准。
    /// </summary>
    public static string GetClientIndexPath(GameModeInfo info)
        => info.Mode == GameMode.GGPK ? GetGgpkPath(info) : GetBundlesIndexPath(info);

    public Dictionary<string, byte[]> ReadGameFiles(GameModeInfo info, IEnumerable<string> virtualPaths)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        OodleNative.EnsureLoaded(info.GameDirectory);

        lock (_fileLock)
        {
            // 每次读取前清空上一次的异常，避免调用方读到陈旧的错误信息。
            LastReadException = null;
            try
            {
                if (info.Mode == GameMode.GGPK)
                {
                    var ggpkPath = GetGgpkPath(info);
                    if (!File.Exists(ggpkPath)) return result;

                    using var ggpk = new BundledGGPK(ggpkPath, parsePathsInIndex: false);
                    ggpk.Index.ParsePaths();

                    foreach (var vPath in virtualPaths)
                    {
                        var cleanPath = vPath.Replace('\\', '/').TrimStart('/');

                        // TryGetFile 走 NameHash 哈希查找，且 NameHash 内部对路径做了
                        // ToLowerInvariant，因此天然忽略大小写，任何大小写变体都能命中。
                        // 这里不需要大小写兜底：查不到就是索引里确实没有这个文件。
                        if (ggpk.Index.TryGetFile(cleanPath, out var file) && file != null)
                        {
                            result[cleanPath] = file.Read().ToArray();
                        }
                    }
                }
                else if (info.Mode == GameMode.Bundles2)
                {
                    var bundlesDir = GetBundlesDirectory(info);
                    var indexPath = Path.Combine(bundlesDir, "_.index.bin");
                    if (!File.Exists(indexPath)) return result;

                    var driveFactory = new DriveBundleFactory(bundlesDir);
                    try
                    {
                        using var index = new LibBundle3.Index(indexPath, parsePaths: false, driveFactory);
                        index.ParsePaths();

                        foreach (var vPath in virtualPaths)
                        {
                            var cleanPath = vPath.Replace('\\', '/').TrimStart('/');

                            // 同 GGPK 分支：TryGetFile 已忽略大小写，无需线性兜底。
                            if (index.TryGetFile(cleanPath, out var file) && file != null)
                            {
                                result[cleanPath] = file.Read().ToArray();
                            }
                        }
                    }
                    finally
                    {
                        (driveFactory as IDisposable)?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                LastReadException = ex;
            }
        }

        return result;
    }

    public Dictionary<string, byte[]> ReadGameFilesMatching(GameModeInfo info, Func<string, bool> predicate)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        OodleNative.EnsureLoaded(info.GameDirectory);

        lock (_fileLock)
        {
            // 每次读取前清空上一次的异常，避免调用方读到陈旧的错误信息。
            LastReadException = null;
            try
            {
                if (info.Mode == GameMode.GGPK)
                {
                    var ggpkPath = GetGgpkPath(info);
                    if (!File.Exists(ggpkPath)) return result;

                    using var ggpk = new BundledGGPK(ggpkPath, parsePathsInIndex: false);
                    ggpk.Index.ParsePaths();

                    var matched = ggpk.Index.Files.Values
                        .Where(f => !string.IsNullOrEmpty(f.Path) && predicate(f.Path))
                        .ToList();

                    foreach (var f in matched)
                    {
                        result[f.Path!] = f.Read().ToArray();
                    }
                }
                else if (info.Mode == GameMode.Bundles2)
                {
                    var bundlesDir = GetBundlesDirectory(info);
                    var indexPath = Path.Combine(bundlesDir, "_.index.bin");
                    if (!File.Exists(indexPath)) return result;

                    var driveFactory = new DriveBundleFactory(bundlesDir);
                    try
                    {
                        using var index = new LibBundle3.Index(indexPath, parsePaths: false, driveFactory);
                        index.ParsePaths();

                        var matched = index.Files.Values
                            .Where(f => !string.IsNullOrEmpty(f.Path) && predicate(f.Path))
                            .ToList();

                        foreach (var f in matched)
                        {
                            result[f.Path!] = f.Read().ToArray();
                        }
                    }
                    finally
                    {
                        (driveFactory as IDisposable)?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                LastReadException = ex;
            }
        }

        return result;
    }

    public byte[]? ReadGameFile(GameModeInfo info, string virtualPath)
        => ReadGameFile(info, virtualPath, out _);

    public byte[]? ReadGameFile(GameModeInfo info, string virtualPath, out string actualPath)
    {
        actualPath = virtualPath;
        var batch = ReadGameFiles(info, [virtualPath]);
        var cleanPath = virtualPath.Replace('\\', '/').TrimStart('/');
        if (batch.TryGetValue(cleanPath, out var bytes))
        {
            return bytes;
        }
        return null;
    }

    /// <summary>
    /// 把文件写入客户端索引。
    /// </summary>
    /// <param name="info">客户端信息。</param>
    /// <param name="files">待写入的（虚拟路径, 内容）列表。</param>
    /// <param name="allowedNewPaths">
    /// 允许在索引中新建的路径集合。默认 null 表示只写已存在的路径。
    ///
    /// 仅光柱补丁需要：它把原始 .ao 复制到新路径，再让原路径的 wrapper
    /// 引用该副本。新路径在当前客户端中并不存在，必须显式放行，否则副本
    /// 会被静默丢弃，wrapper 变成悬空引用（引用不存在的资源，模型不显示）。
    /// </param>
    public bool WriteGameFiles(
        GameModeInfo info,
        IEnumerable<(string VirtualPath, byte[] Data)> files,
        IReadOnlySet<string>? allowedNewPaths = null)
    {
        lock (_fileLock)
        {
            OodleNative.EnsureLoaded(info.GameDirectory);

            var fileList = files.ToList();
            if (fileList.Count == 0) return true;

            // Ensure clean backup exists before applying any modification
            if (!_backupManager.EnsureBackup(info))
            {
                // 备份不可用是写入失败最常见的原因，必须给出可读原因，
                // 否则上层只能报「写入失败」，用户无从判断该做什么。
                LastWriteException = new InvalidOperationException(
                    _backupManager.IsAvailable
                        ? "无法生成补丁前的纯净备份，已中止写入。请先恢复纯净客户端后再打补丁。"
                        : _backupManager.UnavailableReason ?? "补丁备份目录不可用。");
                return false;
            }

            var tempZipPath = Path.Combine(
                Path.GetTempPath(),
                $"freiatlas_patch_{Guid.NewGuid():N}.zip");

            try
            {
                using (var stream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write))
                {
                    using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
                foreach (var (vPath, data) in fileList)
                {
                    var cleanPath = vPath.Replace('\\', '/').TrimStart('/');
                    var entry = archive.CreateEntry(cleanPath, CompressionLevel.Fastest);
                    using var entryStream = entry.Open();
                    entryStream.Write(data, 0, data.Length);
                }
            }

            using var readArchive = ZipFile.OpenRead(tempZipPath);

            LastWriteException = null;

            if (info.Mode == GameMode.GGPK)
            {
                var ggpkPath = GetGgpkPath(info);
                if (!File.Exists(ggpkPath))
                {
                    LastWriteException = new FileNotFoundException($"游戏 Content.ggpk 资源文件未找到: {ggpkPath}");
                    return false;
                }

                // 写入必须放在独立作用域里：GGPK 句柄在 using 结束前独占文件，
                // 期间无法再打开同一文件做验证。先落盘、释放句柄，再验证。
                int count;
                List<(string VirtualPath, byte[] Data)> validFiles;
                using (var ggpk = new BundledGGPK(ggpkPath, parsePathsInIndex: false))
                {
                    ggpk.Index.ParsePaths();

                    // 只注入当前客户端真实存在的路径。向索引写入不存在的条目会让游戏
                    // 加载时找不到对应资源，是启动异常的直接来源。
                    validFiles = FilterExisting(ggpk.Index, fileList, allowedNewPaths);
                    if (validFiles.Count == 0)
                    {
                        LastWriteException = new InvalidOperationException($"待写入的文件在客户端索引中未找到对应的目标文件（共规划 {fileList.Count} 个文件）。可能是客户端缺少相应的基础资源或路径不匹配。");
                        return false;
                    }

                    // 写入前为所有待写文件在索引中预注册占位符，
                    // 确保 LibBundle3.Index.Replace 能够将其作为已存在文件写入自定义补丁 Bundle，避免抛出 FileNotFoundException
                    foreach (var (vPath, _) in validFiles)
                    {
                        var cleanPath = vPath.Replace('\\', '/').TrimStart('/');
                        ggpk.Index.AddFileRecordPlaceholder(cleanPath);
                    }

                    count = LibBundle3.Index.Replace(
                        ggpk.Index,
                        readArchive.Entries,
                        callback: null,
                        saveIndex: true);
                }

                if (count <= 0)
                {
                    LastWriteException = new InvalidOperationException("客户端索引替换未生效（写入条目数为 0）。");
                    return false;
                }

                var verified = VerifyWrittenFiles(info, validFiles);
                if (!verified && LastWriteException is null)
                {
                    LastWriteException = new InvalidOperationException("写入后验证失败，写入的数据与客户端实际读取校验不一致。");
                }
                return verified;
            }
            else if (info.Mode == GameMode.Bundles2)
            {
                var bundlesDir = GetBundlesDirectory(info);
                var indexPath = Path.Combine(bundlesDir, "_.index.bin");
                if (!File.Exists(indexPath))
                {
                    LastWriteException = new FileNotFoundException($"游戏 Bundles2 索引文件未找到: {indexPath}");
                    return false;
                }

                // 同 GGPK 分支：写入必须在独立作用域内完成，句柄释放后再验证。
                int count;
                List<(string VirtualPath, byte[] Data)> validFiles;
                var driveFactory = new DriveBundleFactory(bundlesDir);
                try
                {
                    using (var index = new LibBundle3.Index(indexPath, parsePaths: false, driveFactory))
                    {
                        index.ParsePaths();

                        // 只注入当前客户端真实存在的路径
                        validFiles = FilterExisting(index, fileList, allowedNewPaths);
                        if (validFiles.Count == 0)
                        {
                            LastWriteException = new InvalidOperationException($"待写入的文件在客户端索引中未找到对应的目标文件（共规划 {fileList.Count} 个文件）。可能是客户端缺少相应的基础资源或路径不匹配。");
                            return false;
                        }

                        // 写入前为所有待写文件在索引中预注册占位符
                        foreach (var (vPath, _) in validFiles)
                        {
                            var cleanPath = vPath.Replace('\\', '/').TrimStart('/');
                            index.AddFileRecordPlaceholder(cleanPath);
                        }

                        count = LibBundle3.Index.Replace(
                            index,
                            readArchive.Entries,
                            callback: null,
                            saveIndex: true);
                    }
                }
                finally
                {
                    (driveFactory as IDisposable)?.Dispose();
                }

                if (count <= 0)
                {
                    LastWriteException = new InvalidOperationException("客户端索引替换未生效（写入条目数为 0）。");
                    return false;
                }

                var verified = VerifyWrittenFiles(info, validFiles);
                if (!verified && LastWriteException is null)
                {
                    LastWriteException = new InvalidOperationException("写入后验证失败，写入的数据与客户端实际读取校验不一致。");
                }
                return verified;
            }
        }
        catch (Exception ex)
        {
            LastWriteException = ex;
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(tempZipPath))
                {
                    File.Delete(tempZipPath);
                }
            }
            catch
            {
                // finally 中的临时文件清理：删除失败不影响本次写入结果，
                // 也不应掩盖上方 try 里真正的异常。残留的临时 zip 会在下次写入时被覆盖。
            }
        }

        return false;
        }
    }

    /// <summary>
    /// 写入后逐个验证文件能否读回，且内容与写入的一致。
    ///
    /// 这一步不可省略：索引记录写成功不代表数据真的落到了可读位置。
    /// 历史上出现过索引里 Size/Offset 看着正常、但实际读取越界的情况
    /// （offset 超出 bundle 长度），客户端加载该资源时直接抛解析异常。
    /// 只有真正读回并比对字节，才能确认写入有效。
    ///
    /// 验证时重新打开一份索引，避免复用写入过程中的句柄——那个句柄的状态
    /// 不能代表磁盘上的最终结果。
    /// </summary>
    private static bool VerifyWrittenFiles(
        GameModeInfo info,
        List<(string VirtualPath, byte[] Data)> written)
    {
        if (written.Count == 0) return true;

        var expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (vPath, data) in written)
        {
            expected[vPath.Replace('\\', '/').TrimStart('/')] = data;
        }

        try
        {
            if (info.Mode == GameMode.GGPK)
            {
                var ggpkPath = GetGgpkPath(info);
                using var verify = new BundledGGPK(ggpkPath, parsePathsInIndex: false);
                verify.Index.ParsePaths();
                return CompareAll(verify.Index, expected);
            }

            var bundlesDir = GetBundlesDirectory(info);
            var indexPath = Path.Combine(bundlesDir, "_.index.bin");
            var factory = new DriveBundleFactory(bundlesDir);
            try
            {
                using var index = new LibBundle3.Index(indexPath, parsePaths: false, factory);
                index.ParsePaths();
                return CompareAll(index, expected);
            }
            finally
            {
                (factory as IDisposable)?.Dispose();
            }
        }
        catch (Exception ex)
        {
            LastWriteException = new InvalidOperationException(
                $"写入后验证失败：读取索引时出错 —— {ex.Message}", ex);
            return false;
        }
    }

    private static bool CompareAll(
        LibBundle3.Index index,
        Dictionary<string, byte[]> expected)
    {
        foreach (var (vPath, data) in expected)
        {
            if (!index.TryGetFile(vPath, out var record))
            {
                PatchEngine.LastWriteException = new InvalidOperationException(
                    $"写入后验证失败：{vPath} 在索引中不存在。");
                return false;
            }

            byte[] actual;
            try
            {
                actual = record.Read().ToArray();
            }
            catch (Exception ex)
            {
                PatchEngine.LastWriteException = new InvalidOperationException(
                    $"写入后验证失败：{vPath} 无法读取 —— {ex.Message}", ex);
                return false;
            }

            if (actual.Length != data.Length)
            {
                PatchEngine.LastWriteException = new InvalidOperationException(
                    $"写入后验证失败：{vPath} 长度不符（期望 {data.Length}，实际 {actual.Length}）。");
                return false;
            }

            if (!actual.AsSpan().SequenceEqual(data))
            {
                PatchEngine.LastWriteException = new InvalidOperationException(
                    $"写入后验证失败：{vPath} 内容与写入值不一致。");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 只保留当前客户端索引中真实存在的路径。
    ///
    /// 向索引写入不存在的条目会让游戏加载时找不到资源，是启动异常的直接来源。
    /// <summary>
    /// 过滤掉当前客户端索引中不存在的路径，只保留可安全写入的文件。
    ///
    /// 调用方可能传入跨版本失效的路径，这里统一拦下 —— 向索引写入不存在的
    /// 条目会让游戏加载时找不到资源，是启动异常的直接来源。
    ///
    /// <paramref name="allowedNewPaths"/> 用于少数确实需要新建文件的场景
    /// （光柱补丁要把原始 .ao 复制成 wrapper 引用的新路径）。只有显式列出的
    /// 路径才允许不存在，其余仍按原规则拦截。
    /// </summary>
    private static List<(string VirtualPath, byte[] Data)> FilterExisting(
        LibBundle3.Index index,
        List<(string VirtualPath, byte[] Data)> files,
        IReadOnlySet<string>? allowedNewPaths = null)
    {
        var result = new List<(string, byte[])>(files.Count);
        foreach (var file in files)
        {
            var cleanPath = file.VirtualPath.Replace('\\', '/').TrimStart('/');
            bool exists = index.TryGetFile(cleanPath, out _);
            bool allowedNew = !exists
                && allowedNewPaths is not null
                && allowedNewPaths.Contains(cleanPath);
            if (!exists && !allowedNew) continue;
            result.Add(file);
        }
        return result;
    }
}
