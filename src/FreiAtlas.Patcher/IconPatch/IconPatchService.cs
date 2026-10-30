using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using LibBundle3;
using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.IconPatch;

/// <summary>
/// 内置图标包的资源访问。
///
/// 资源来自 scripts/iconpatch/extract_iconpack.py 的产物，以嵌入资源形式随程序分发：
///   icons.dat      所有 DDS 原始字节连续排列
///   icons.index.json  路径、分类、偏移、大小、雪碧图坐标
///   icons.sheet.webp  缩略图雪碧图
///
/// 保留原始 DDS 而不重新编码，是因为游戏只接受原始纹理格式；重新编码会改变
/// 像素布局，写进去客户端可能无法识别。
/// </summary>
internal static class IconPatchAssets
{
    private const string BinResource = "FreiAtlas.Patcher.Resources.iconpatch.icons.dat";
    private const string IndexResource = "FreiAtlas.Patcher.Resources.iconpatch.icons.index.json";
    private const string SheetResource = "FreiAtlas.Patcher.Resources.iconpatch.icons.sheet.webp";

    private static readonly object Gate = new();
    private static byte[]? _blob;
    private static IconIndexDocument? _index;
    private static string? _loadError;

    public sealed class IconIndexDocument
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("cellSize")] public int CellSize { get; set; }
        [JsonPropertyName("columns")] public int Columns { get; set; }
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("entries")] public List<IconIndexItem> Entries { get; set; } = [];
    }

    public sealed class IconIndexItem
    {
        [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
        [JsonPropertyName("fileName")] public string FileName { get; set; } = string.Empty;
        [JsonPropertyName("groupIndex")] public int GroupIndex { get; set; }
        [JsonPropertyName("category")] public string Category { get; set; } = string.Empty;
        [JsonPropertyName("offset")] public int Offset { get; set; }
        [JsonPropertyName("size")] public int Size { get; set; }
        [JsonPropertyName("spriteX")] public int SpriteX { get; set; }
        [JsonPropertyName("spriteY")] public int SpriteY { get; set; }
    }

    public static bool IsAvailable
    {
        get { EnsureLoaded(); return _index is not null && _blob is not null; }
    }

    public static string? LoadError
    {
        get { EnsureLoaded(); return _loadError; }
    }

    public static IReadOnlyList<IconIndexItem> Entries
    {
        get { EnsureLoaded(); return _index?.Entries ?? []; }
    }

    public static IconSpriteInfo SpriteInfo
    {
        get
        {
            EnsureLoaded();
            return new IconSpriteInfo(_index?.CellSize ?? 48, _index?.Columns ?? 32);
        }
    }

    /// <summary>读取某条目的 DDS 原始字节。</summary>
    public static byte[]? ReadIcon(IconIndexItem item)
    {
        EnsureLoaded();
        if (_blob is null) return null;
        if (item.Offset < 0 || item.Size <= 0 || item.Offset + item.Size > _blob.Length) return null;
        return _blob.AsSpan(item.Offset, item.Size).ToArray();
    }

    /// <summary>读取雪碧图字节，供前端展示缩略图。</summary>
    public static byte[]? ReadSpriteSheet()
    {
        using var stream = OpenResource(SheetResource);
        if (stream is null) return null;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void EnsureLoaded()
    {
        if (_index is not null || _loadError is not null) return;

        lock (Gate)
        {
            if (_index is not null || _loadError is not null) return;

            try
            {
                using var indexStream = OpenResource(IndexResource);
                if (indexStream is null)
                {
                    _loadError = "内置图标索引缺失，图标功能不可用。";
                    return;
                }

                var document = JsonSerializer.Deserialize<IconIndexDocument>(indexStream);
                if (document is null || document.Entries.Count == 0)
                {
                    _loadError = "内置图标索引为空，图标功能不可用。";
                    return;
                }

                using var binStream = OpenResource(BinResource);
                if (binStream is null)
                {
                    _loadError = "内置图标数据缺失，图标功能不可用。";
                    return;
                }

                using var gzip = new GZipStream(binStream, CompressionMode.Decompress);
                using var blobBuffer = new MemoryStream();
                gzip.CopyTo(blobBuffer);

                _blob = blobBuffer.ToArray();
                _index = document;
            }
            catch (Exception ex)
            {
                _loadError = $"加载内置图标资源失败：{ex.Message}";
            }
        }
    }

    private static Stream? OpenResource(string name)
        => Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
}

/// <summary>
/// 图标替换服务。
///
/// 数据流是单向的：扫描时读取客户端索引确定哪些路径真实存在，写入时只使用扫描
/// 得到的路径并再次校验。任何环节都不缓存「客户端有哪些文件」这一事实，避免
/// 用过期数据写入。
/// </summary>
public sealed class IconPatchService
{
    /// <summary>逐文件备份分组标识，与其它模块的备份互不干扰。</summary>
    public const string BackupGroup = "icon_patch_v1";

    private readonly PatchEngine _engine;

    public IconPatchService(PatchEngine? engine = null)
    {
        _engine = engine ?? new PatchEngine();
    }

    public static bool IsAvailable => IconPatchAssets.IsAvailable;

    public static string? LoadError => IconPatchAssets.LoadError;

    public static IconSpriteInfo SpriteInfo => IconPatchAssets.SpriteInfo;

    /// <summary>雪碧图原始字节。WebView2 只能读文件系统，由宿主导出到 Web 目录。</summary>
    public static byte[]? SpriteSheetBytes => IconPatchAssets.ReadSpriteSheet();

    /// <summary>
    /// 读取客户端索引，逐条校验内置图标的虚拟路径是否存在。
    ///
    /// 不存在的路径直接剔除——游戏版本差异会让部分路径失效，把它们展示给用户
    /// 只会得到「勾了却没效果」的结果。剔除数量通过 <see cref="IconPatchScanResult.SkippedCount"/> 上报。
    /// </summary>
    public IconPatchScanResult Scan(GameModeInfo info, Action<string>? progress = null)
    {
        if (!info.IsValid)
        {
            return IconPatchScanResult.Failure(info.ErrorMessage ?? "游戏客户端无效。");
        }
        if (!IconPatchAssets.IsAvailable)
        {
            return IconPatchScanResult.Failure(IconPatchAssets.LoadError ?? "内置图标资源不可用。");
        }

        var (isLocked, lockReason) = PatchBackupManager.CheckResourceLock(info);
        if (isLocked)
        {
            return IconPatchScanResult.Failure(lockReason ?? "客户端资源被占用，无法读取图标清单。");
        }

        var items = IconPatchAssets.Entries;
        progress?.Invoke($"正在比对客户端索引（内置 {items.Count} 个图标）...");

        var result = new List<IconPatchEntry>(items.Count);
        int skipped = 0;

        try
        {
            progress?.Invoke("正在打开客户端索引...");
            using var probe = ClientIndexProbe.Open(info);
            foreach (var item in items)
            {
                var file = probe.Find(item.Path);
                if (file is null)
                {
                    skipped++;
                    continue;
                }

                result.Add(new IconPatchEntry
                {
                    VirtualPath = item.Path,
                    FileName = item.FileName,
                    Category = item.Category,
                    GroupIndex = item.GroupIndex,
                    OriginalSize = file.Value,
                    ReplacementSize = item.Size,
                    SpriteX = item.SpriteX,
                    SpriteY = item.SpriteY
                });
            }
        }
        catch (Exception ex)
        {
            return IconPatchScanResult.Failure($"读取客户端索引失败：{ex.Message}");
        }

        progress?.Invoke($"比对完成：可用 {result.Count} 个，跳过 {skipped} 个。");
        return new IconPatchScanResult(result, skipped, null);
    }

    /// <summary>
    /// 应用图标替换，并把「上次选过、这次取消」的图标还原成官方原版。
    ///
    /// 为什么需要 <paramref name="previousPaths"/>：图标补丁直接覆盖 DDS，
    /// 没有「取消勾选即还原」的语义。若只写入本次选中的图标，
    /// 被取消的那些在客户端里仍然是修改版 —— 用户看到勾选数变少，
    /// 实际残留没清，只有关总开关才能全量恢复。
    /// </summary>
    /// <param name="previousPaths">上次应用时选中的路径；null 表示不做差集还原。</param>
    public bool Apply(
        GameModeInfo info,
        IReadOnlyList<string> paths,
        IReadOnlyList<string>? previousPaths,
        Action<string>? progress,
        out string message)
    {
        message = string.Empty;

        if (!info.IsValid)
        {
            message = info.ErrorMessage ?? "游戏客户端无效。";
            return false;
        }
        if (paths.Count == 0 && (previousPaths is null || previousPaths.Count == 0))
        {
            message = "未选择任何图标。";
            return false;
        }

        var (isLocked, lockReason) = PatchBackupManager.CheckResourceLock(info);
        if (isLocked)
        {
            message = lockReason ?? "客户端资源被占用，无法写入。";
            return false;
        }

        // 差集：上次选过但这次取消的图标，需要写回官方原件。
        var current = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var removed = previousPaths is null
            ? []
            : previousPaths.Where(p => !current.Contains(p)).ToList();

        var items = IconPatchAssets.Entries.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        var files = new List<(string VirtualPath, byte[] Data)>(paths.Count + removed.Count);
        var missing = new List<string>();
        int restoredCount = 0;
        // 待写入成功后清理的备份条目（见下方 RemoveOriginals 的说明）
        List<string> restoredPathsForCleanup = [];

        progress?.Invoke(removed.Count > 0
            ? $"准备写入 {paths.Count} 个图标，还原 {removed.Count} 个已取消的图标..."
            : $"准备写入 {paths.Count} 个图标...");

        // 先处理取消项：从备份读官方原件写回。
        if (removed.Count > 0)
        {
            var backups = PatchFileBackup.LoadOriginals(BackupGroup)
                .ToDictionary(x => x.VirtualPath, x => x.Data, StringComparer.OrdinalIgnoreCase);

            var restoredPaths = new List<string>(removed.Count);
            foreach (var path in removed)
            {
                if (backups.TryGetValue(path, out var original) && original.Length > 0)
                {
                    files.Add((path, original));
                    restoredPaths.Add(path);
                    restoredCount++;
                }
                else
                {
                    // 没有备份说明该图标从未被本工具改过，无需还原。
                    progress?.Invoke($"图标 {path} 没有备份记录，跳过还原。");
                }
            }

            // 注意：这里【不能】立刻移除备份。
            // 必须等下面 WriteGameFiles 真正落盘成功后再删，
            // 否则写入失败时原版图标已被物理删除，用户永久失去原件。
            restoredPathsForCleanup = restoredPaths;
        }

        try
        {
            progress?.Invoke("正在打开客户端索引并校验图标路径...");
            using var probe = ClientIndexProbe.Open(info);
            foreach (var path in paths)
            {
                if (!items.TryGetValue(path, out var item))
                {
                    missing.Add(path);
                    continue;
                }
                if (probe.Find(path) is null)
                {
                    missing.Add(path);
                    continue;
                }

                var data = IconPatchAssets.ReadIcon(item);
                if (data is null)
                {
                    missing.Add(path);
                    continue;
                }
                files.Add((item.Path, data));
            }
        }
        catch (Exception ex)
        {
            message = $"读取客户端索引失败：{ex.Message}";
            return false;
        }

        if (missing.Count > 0)
        {
            progress?.Invoke($"跳过 {missing.Count} 个当前客户端不存在的图标。");
        }
        if (files.Count == 0)
        {
            message = "所选图标在当前客户端中均不存在，未写入任何内容。";
            return false;
        }

        progress?.Invoke($"写入 {files.Count} 个图标到客户端...");

        // 首次改写前把客户端原始 DDS 存进逐文件备份，供「恢复原版图标」写回。
        // 已存在同名备份时不覆盖，保证保存的始终是最初的原件。
        // 只备份本次真正要新写入的图标（还原项已在上方处理）。
        var newWritePaths = files.Select(f => f.VirtualPath)
            .Where(p => !removed.Contains(p, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (newWritePaths.Count > 0)
        {
            SaveOriginalIcons(info, newWritePaths, progress);
        }

        bool ok = _engine.WriteGameFiles(info, files);
        if (!ok)
        {
            message = PatchEngine.LastWriteException?.Message ?? "写入失败。";
            return false;
        }

        // 写入已成功落盘，此时才能安全移除已还原项的备份记录。
        if (restoredPathsForCleanup.Count > 0)
        {
            PatchFileBackup.RemoveOriginals(BackupGroup, restoredPathsForCleanup);
        }

        progress?.Invoke($"已写入 {files.Count} 个图标。");
        var parts = new List<string>(3);
        if (newWritePaths.Count > 0) parts.Add($"写入 {newWritePaths.Count} 个图标");
        if (restoredCount > 0) parts.Add($"还原 {restoredCount} 个取消项");
        if (missing.Count > 0) parts.Add($"跳过 {missing.Count} 个不存在的");

        message = "已成功" + string.Join("，", parts) + "。";
        return true;
    }

    /// <summary>
    /// 把待覆盖图标的客户端原始内容存入逐文件备份。
    ///
    /// 备份是按组存放的，`PatchFileBackup.SaveOriginals` 对已存在的同名文件
    /// 不覆盖，因此重复应用只会保留最初那份官方原件。
    /// 读不到内容（路径不存在 / 读取失败）的条目会被跳过并计入提示，不阻断写入。
    /// </summary>
    private static void SaveOriginalIcons(
        GameModeInfo info,
        IReadOnlyList<string> targetPaths,
        Action<string>? progress)
    {
        var originals = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var probe = ClientIndexProbe.Open(info);
            foreach (var path in targetPaths)
            {
                var bytes = probe.Read(path);
                if (bytes is not null && bytes.Length > 0)
                {
                    originals[path] = bytes;
                }
            }
        }
        catch (Exception ex)
        {
            // 备份失败不阻断写入：图标补丁本身可正常应用，只是失去单项还原能力。
            // 必须让用户看到原因，否则「恢复原版图标」失效时无从排查。
            progress?.Invoke($"警告：未能建立图标原始备份（{ex.Message}），本次将无法单项还原。");
            return;
        }

        if (originals.Count == 0) return;
        PatchFileBackup.SaveOriginals(BackupGroup, targetPaths, originals);
    }

    /// <summary>
    /// 把此前备份的官方图标写回客户端。
    ///
    /// 逐文件备份覆盖不到的情况（历史版本应用过、备份已被清理）会在返回值里
    /// 说明还有多少文件没能还原，由调用方决定是否提示用户改用全量还原底包。
    /// </summary>
    public IconPatchRestoreResult Restore(
        GameModeInfo info,
        Action<string>? progress,
        out string message)
    {
        message = string.Empty;

        if (!info.IsValid)
        {
            message = info.ErrorMessage ?? "游戏客户端无效。";
            return IconPatchRestoreResult.Failure(message);
        }

        var originals = PatchFileBackup.LoadOriginals(BackupGroup);
        if (originals.Count == 0)
        {
            message = "没有可还原的图标备份。若客户端图标仍为替换后的版本，请使用「恢复纯净客户端」。";
            return IconPatchRestoreResult.NoBackup(message);
        }

        var (isLocked, lockReason) = PatchBackupManager.CheckResourceLock(info);
        if (isLocked)
        {
            message = lockReason ?? "客户端资源被占用，无法还原。";
            return IconPatchRestoreResult.Failure(message);
        }

        progress?.Invoke($"正在还原 {originals.Count} 个官方图标...");

        var files = originals
            .Select(o => (VirtualPath: o.VirtualPath, Data: o.Data))
            .ToList();

        if (!_engine.WriteGameFiles(info, files))
        {
            message = PatchEngine.LastWriteException?.Message ?? "还原图标失败。";
            return IconPatchRestoreResult.Failure(message);
        }

        PatchFileBackup.Clear(BackupGroup);
        message = $"已还原 {files.Count} 个官方图标。";
        progress?.Invoke(message);
        return IconPatchRestoreResult.Ok(files.Count, message);
    }

    /// <summary>是否有可还原的图标备份。</summary>
    /// <remarks>
    /// 只查清单条目数，不读实际文件内容。
    /// 原先走 LoadOriginals 会把每个 DDS 全读进内存：任一文件损坏或被占用就抛异常，
    /// 而本方法在界面快照路径上被调用（SendPatcherSnapshot），
    /// 异常会直接打断整个界面刷新。
    /// </remarks>
    public static bool HasBackup() => PatchFileBackup.CountOriginals(BackupGroup) > 0;

    /// <summary>
    /// 内置图标包的全部虚拟路径。
    ///
    /// 这是「未自定义规则」时的默认规则：用户只勾选图标替换、不进配置页，
    /// 就直接按这份全量清单应用。应用时 Apply 会逐条校验路径是否存在，
    /// 缺失的自动跳过并汇总，不会写入无效条目。
    /// </summary>
    public static IReadOnlyList<string> DefaultPaths =>
        IconPatchAssets.IsAvailable
            ? IconPatchAssets.Entries.Select(e => e.Path).Where(p => !string.IsNullOrWhiteSpace(p)).ToList()
            : [];

    /// <summary>已备份的图标数量。</summary>
    public static int BackupCount() => PatchFileBackup.CountOriginals(BackupGroup);

    /// <summary>
    /// 客户端索引探针。封装 GGPK 与 Bundles2 两种形态，只暴露「按路径查大小」。
    /// </summary>
    private sealed class ClientIndexProbe : IDisposable
    {
        private readonly LibBundledGGPK3.BundledGGPK? _ggpk;
        private readonly LibBundle3.Index? _index;
        private readonly DriveBundleFactory? _factory;

        private ClientIndexProbe(
            LibBundledGGPK3.BundledGGPK? ggpk,
            LibBundle3.Index? index,
            DriveBundleFactory? factory)
        {
            _ggpk = ggpk;
            _index = index;
            _factory = factory;
        }

        public static ClientIndexProbe Open(GameModeInfo info)
        {
            if (info.Mode == GameMode.GGPK)
            {
                var ggpkPath = PatchEngine.GetGgpkPath(info);
                if (!File.Exists(ggpkPath))
                {
                    throw new FileNotFoundException($"未找到客户端文件：{ggpkPath}");
                }
                var ggpk = new LibBundledGGPK3.BundledGGPK(ggpkPath, parsePathsInIndex: false);
                ggpk.Index.ParsePaths();
                return new ClientIndexProbe(ggpk, null, null);
            }

            var bundlesDir = PatchEngine.GetBundlesDirectory(info);
            var indexPath = PatchEngine.GetBundlesIndexPath(info);
            if (!File.Exists(indexPath))
            {
                throw new FileNotFoundException($"未找到客户端索引：{indexPath}");
            }
            var factory = new DriveBundleFactory(bundlesDir);
            var index = new LibBundle3.Index(indexPath, parsePaths: false, factory);
            index.ParsePaths();
            return new ClientIndexProbe(null, index, factory);
        }

        /// <summary>返回文件大小；客户端不存在该路径时返回 null。</summary>
        public int? Find(string virtualPath)
        {
            var target = _ggpk?.Index ?? _index;
            if (target is null) return null;
            return target.TryGetFile(virtualPath, out var record) ? record.Size : null;
        }

        /// <summary>读取客户端中该路径的原始内容；不存在或读取失败时返回 null。</summary>
        public byte[]? Read(string virtualPath)
        {
            var target = _ggpk?.Index ?? _index;
            if (target is null) return null;
            if (!target.TryGetFile(virtualPath, out var record)) return null;
            try
            {
                return record.Read().ToArray();
            }
            catch
            {
                // 单个文件读取失败按「拿不到原件」处理，由调用方决定是否继续。
                return null;
            }
        }

        public void Dispose()
        {
            _ggpk?.Dispose();
            _index?.Dispose();
            (_factory as IDisposable)?.Dispose();
        }
    }
}
