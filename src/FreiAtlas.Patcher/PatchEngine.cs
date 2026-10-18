using System.IO;
using System.IO.Compression;
using LibBundle3;
using LibBundle3.Records;
using LibBundledGGPK3;

namespace FreiAtlas.Patcher;

public sealed class PatchEngine
{
    private readonly PatchBackupManager _backupManager;

    public PatchBackupManager BackupManager => _backupManager;

    public static Exception? LastReadException { get; set; }
    public static Exception? LastWriteException { get; set; }
    public static string? LastDebugMessage { get; set; }

    public PatchEngine(PatchBackupManager? backupManager = null)
    {
        _backupManager = backupManager ?? new PatchBackupManager();
    }

    private static readonly object _fileLock = new();

    public Dictionary<string, byte[]> ReadGameFiles(GameModeInfo info, IEnumerable<string> virtualPaths)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        OodleNative.EnsureLoaded(info.GameDirectory);

        lock (_fileLock)
        {
            try
            {
                if (info.Mode == GameMode.GGPK)
                {
                    var ggpkPath = Path.Combine(info.GameDirectory, "Content.ggpk");
                    if (!File.Exists(ggpkPath)) return result;

                    using var ggpk = new BundledGGPK(ggpkPath, parsePathsInIndex: false);
                    ggpk.Index.ParsePaths();

                    foreach (var vPath in virtualPaths)
                    {
                        var cleanPath = vPath.Replace('\\', '/').TrimStart('/');
                        if (ggpk.Index.TryGetFile(cleanPath, out var file) && file != null)
                        {
                            result[cleanPath] = file.Read().ToArray();
                            continue;
                        }

                        var matched = ggpk.Index.Files.Values.FirstOrDefault(f =>
                            !string.IsNullOrEmpty(f.Path) &&
                            string.Equals(f.Path, cleanPath, StringComparison.OrdinalIgnoreCase));
                        if (matched != null)
                        {
                            result[cleanPath] = matched.Read().ToArray();
                        }
                    }
                }
                else if (info.Mode == GameMode.Bundles2)
                {
                    var bundlesDir = Path.Combine(info.GameDirectory, "Bundles2");
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
                            if (index.TryGetFile(cleanPath, out var file) && file != null)
                            {
                                result[cleanPath] = file.Read().ToArray();
                                continue;
                            }

                            var matched = index.Files.Values.FirstOrDefault(f =>
                                !string.IsNullOrEmpty(f.Path) &&
                                string.Equals(f.Path, cleanPath, StringComparison.OrdinalIgnoreCase));
                            if (matched != null)
                            {
                                result[cleanPath] = matched.Read().ToArray();
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

    public bool WriteGameFiles(GameModeInfo info, IEnumerable<(string VirtualPath, byte[] Data)> files)
    {
        lock (_fileLock)
        {
            OodleNative.EnsureLoaded(info.GameDirectory);

            var fileList = files.ToList();
            if (fileList.Count == 0) return true;

            // Ensure clean backup exists before applying any modification
            if (!_backupManager.EnsureBackup(info))
            {
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
                var ggpkPath = Path.Combine(info.GameDirectory, "Content.ggpk");
                if (!File.Exists(ggpkPath)) return false;

                using var ggpk = new BundledGGPK(ggpkPath, parsePathsInIndex: false);
                ggpk.Index.ParsePaths();

                // 关键修复：写入前为所有新增文件在索引中预注册占位符，
                // 确保 LibBundle3.Index.Replace 能够将其作为已存在文件写入自定义补丁 Bundle，避免抛出 FileNotFoundException
                foreach (var (vPath, _) in fileList)
                {
                    var cleanPath = vPath.Replace('\\', '/').TrimStart('/');
                    ggpk.Index.AddFileRecordPlaceholder(cleanPath);
                }

                var count = LibBundle3.Index.Replace(
                    ggpk.Index,
                    readArchive.Entries,
                    callback: null,
                    saveIndex: true);
                return count > 0;
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
                    index.ParsePaths();

                    // 关键修复：写入前为所有新增文件在索引中预注册占位符
                    foreach (var (vPath, _) in fileList)
                    {
                        var cleanPath = vPath.Replace('\\', '/').TrimStart('/');
                        index.AddFileRecordPlaceholder(cleanPath);
                    }

                    var count = LibBundle3.Index.Replace(
                        index,
                        readArchive.Entries,
                        callback: null,
                        saveIndex: true);
                    return count > 0;
                }
                finally
                {
                    (driveFactory as IDisposable)?.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            LastWriteException = ex;
            LastDebugMessage = ex.Message;
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
            catch { }
        }

        return false;
        }
    }
}
