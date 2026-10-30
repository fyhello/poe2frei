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

    // 批量应用期间的写盘叠加层。非 null 时 WriteGameFiles 只登记不落盘。
    private PatchSession? _session;

    /// <summary>
    /// 当前正在执行的模块名。由批量调度器设置，用于统计每个模块实际写入的文件数。
    /// 没有它就无法区分「模块没干活」与「写了但游戏不认」。
    /// </summary>
    public string? CurrentModuleTag { get; set; }

    /// <summary>
    /// 本引擎当前是否持有客户端索引句柄。
    ///
    /// CheckResourceLock 用「能否独占打开」判断占用，会把本进程自己的只读句柄
    /// 误判成「其他进程占用」。占用检查必须先问这个标记。
    /// </summary>
    public static bool ClientIndexHeld { get; private set; }

    /// <summary>本次批量中各模块实际写入的文件数。</summary>
    public Dictionary<string, int> ModuleWriteCounts { get; } = new(StringComparer.Ordinal);

    private void AddWriteCount(int count)
    {
        if (count <= 0) return;
        string tag = string.IsNullOrEmpty(CurrentModuleTag) ? "(未标注)" : CurrentModuleTag;
        ModuleWriteCounts[tag] = ModuleWriteCounts.TryGetValue(tag, out var existing)
            ? existing + count
            : count;
    }

    // 批量期间的共享索引会话：一次打开，供多次读取复用。
    // 物理写入前必须释放（Windows 下 GGPK 句柄独占文件），写入后按需重建。
    private ClientIndexSession? _clientIndex;
    private string? _clientIndexKey;
    private int _clientIndexLeaseCount;

    /// <summary>当前仍被模块借用的索引会话数量（仅用于诊断与测试）。</summary>
    internal int ClientIndexLeaseCount => _clientIndexLeaseCount;

    /// <summary>
    /// 释放共享索引会话。
    ///
    /// Windows 下 GGPK 句柄会独占文件，写盘前不释放会导致 Replace 抛 IOException。
    /// 有模块仍持有借用凭证时必须先归还，否则写盘会关闭正在使用的会话。
    /// </summary>
    public void ReleaseClientIndex()
    {
        lock (_fileLock)
        {
            if (_clientIndexLeaseCount > 0)
            {
                throw new InvalidOperationException(
                    $"客户端索引仍有 {_clientIndexLeaseCount} 个借用凭证未归还，不能在借用期间写盘或释放。");
            }

            _clientIndex?.Dispose();
            _clientIndex = null;
            _clientIndexKey = null;
            ClientIndexHeld = false;
        }
    }

    /// <summary>
    /// 借出共享索引会话。调用方必须用 <c>using</c> 归还，且必须在写盘前释放。
    /// </summary>
    internal ClientIndexLease? AcquireClientIndex(GameModeInfo info)
    {
        lock (_fileLock)
        {
            var session = GetOrOpenClientIndex(info);
            if (session is null) return null;

            _clientIndexLeaseCount++;
            return new ClientIndexLease(this, session);
        }
    }

    /// <summary>归还一个索引借用凭证。</summary>
    internal void ReturnClientIndex()
    {
        lock (_fileLock)
        {
            if (_clientIndexLeaseCount > 0)
            {
                _clientIndexLeaseCount--;
            }
        }
    }

    /// <summary>
    /// 读取客户端文件：批量会话叠加层优先，其次才是磁盘索引。
    /// </summary>
    internal byte[]? ReadClientFile(ClientIndexSession session, string path)
    {
        lock (_fileLock)
        {
            return ReadWithOverlay(_session, path, session.ReadFile);
        }
    }

    /// <summary>判断客户端文件是否存在；叠加层中的新内容也视为存在。</summary>
    internal bool FileExistsInClient(ClientIndexSession session, string path)
    {
        lock (_fileLock)
        {
            return ExistsWithOverlay(_session, path, session.FileExists);
        }
    }

    internal static byte[]? ReadWithOverlay(
        PatchSession? session,
        string path,
        Func<string, byte[]?> readFromDisk)
    {
        var clean = path.Replace('\\', '/').TrimStart('/');
        var staged = session?.TryGet(clean);
        return staged ?? readFromDisk(clean);
    }

    internal static bool ExistsWithOverlay(
        PatchSession? session,
        string path,
        Func<string, bool> existsOnDisk)
    {
        var clean = path.Replace('\\', '/').TrimStart('/');
        return session?.TryGet(clean) is not null || existsOnDisk(clean);
    }

    private ClientIndexSession? GetOrOpenClientIndex(GameModeInfo info)
    {
        // 索引文件被恢复/写入/外部改动后，缓存的会话必须失效重建，
        // 否则后续模块会读到补丁前的旧内容，算出「无变化」而静默跳过写入。
        string indexPath = GetClientIndexPath(info);
        var indexFile = new FileInfo(indexPath);
        string fileStamp = indexFile.Exists
            ? $"{indexFile.Length}|{indexFile.LastWriteTimeUtc.Ticks}"
            : "missing";

        string key = $"{info.Mode}|{info.GameDirectory}|{fileStamp}";

        // 借用期间必须返回同一个会话：中途换索引会让借用方读到不一致的视图。
        // 索引文件 stamp 变化会等到最后一个借用归还后的下一次获取再处理。
        if (_clientIndex is not null && _clientIndexLeaseCount > 0)
        {
            return _clientIndex;
        }

        if (_clientIndex is not null &&
            string.Equals(_clientIndexKey, key, StringComparison.OrdinalIgnoreCase))
        {
            return _clientIndex;
        }

        _clientIndex?.Dispose();
        _clientIndex = ClientIndexSession.Open(info);
        _clientIndexKey = _clientIndex is null ? null : key;
        ClientIndexHeld = _clientIndex is not null;
        return _clientIndex;
    }

    /// <summary>开始一次批量写入会话，期间所有写入先停留在内存。</summary>
    public PatchSession BeginSession()
    {
        lock (_fileLock)
        {
            _session = new PatchSession();
            return _session;
        }
    }

    /// <summary>放弃会话：已登记的内容全部丢弃，客户端不受影响。</summary>
    public void AbortSession()
    {
        lock (_fileLock)
        {
            _session = null;
        }
    }

    /// <summary>
    /// 提交会话：把登记的内容一次性写入客户端。
    ///
    /// 会话无效（路径冲突或超出内存上限）时直接失败且不写盘，
    /// 由调用方读取 <see cref="PatchSession.Conflicts"/> 说明原因。
    /// </summary>
    public bool CommitSession(GameModeInfo info)
    {
        lock (_fileLock)
        {
            var session = _session;
            _session = null;

            if (session is null) return true;
            if (!session.IsValid) return false;
            if (session.Pending.Count == 0) return true;

            var files = session.Pending
                .Select(kv => (VirtualPath: kv.Key, Data: kv.Value))
                .ToList();

            return WriteGameFilesCore(
                info,
                files,
                allowedNewPaths: session.AllowedNewPaths.Count > 0 ? session.AllowedNewPaths : null);
        }
    }

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
                // 会话期间优先返回尚未落盘的改动：后一个模块必须看到前一个模块的输出。
                var session = _session;
                var remaining = new List<string>();
                foreach (var vPath in virtualPaths)
                {
                    var clean = vPath.Replace('\\', '/').TrimStart('/');
                    var staged = session?.TryGet(clean);
                    if (staged is not null) result[clean] = staged;
                    else remaining.Add(clean);
                }

                // 全部命中叠加层时不必再打开索引（GGPK 下一次打开要解析 110 MB 索引）。
                if (remaining.Count == 0) return result;
                virtualPaths = remaining;
                if (info.Mode == GameMode.GGPK)
                {
                    var ggpkPath = GetGgpkPath(info);
                    if (!File.Exists(ggpkPath)) return result;

                    // 共享索引：批量期间只打开一次，避免每次读取都重解析 426 万条路径。
                    // 精确查找走 NameHash，不需要路径树，因此这里不 ParsePaths。
                    var clientIndex = GetOrOpenClientIndex(info);
                    if (clientIndex is null) return result;

                    foreach (var vPath in virtualPaths)
                    {
                        var cleanPath = vPath.Replace('\\', '/').TrimStart('/');

                        // TryGetFile 走 NameHash 哈希查找，且 NameHash 内部对路径做了
                        // ToLowerInvariant，因此天然忽略大小写，任何大小写变体都能命中。
                        // 这里不需要大小写兜底：查不到就是索引里确实没有这个文件。
                        var data = clientIndex.ReadFile(cleanPath);
                        if (data is not null) result[cleanPath] = data;
                    }
                }
                else if (info.Mode == GameMode.Bundles2)
                {
                    // 与 GGPK 分支一致：复用引擎共享会话，避免每次读取都重建索引。
                    var clientIndex = GetOrOpenClientIndex(info);
                    if (clientIndex is null) return result;

                    foreach (var vPath in virtualPaths)
                    {
                        var cleanPath = vPath.Replace('\\', '/').TrimStart('/');
                        var data = clientIndex.ReadFile(cleanPath);
                        if (data is not null) result[cleanPath] = data;
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
                // 会话期间必须把尚未落盘的改动一并返回。
                // 这个方法按谓词通配枚举，调用方无法从返回值判断内容是否过期；
                // 一旦漏掉叠加层就是静默读到旧文件，比报错更难排查。
                var session = _session;
                if (session is not null)
                {
                    foreach (var (path, data) in session.Pending)
                    {
                        if (predicate(path)) result[path] = data;
                    }
                }

                if (info.Mode == GameMode.GGPK)
                {
                    var ggpkPath = GetGgpkPath(info);
                    if (!File.Exists(ggpkPath)) return result;

                    var clientIndex = GetOrOpenClientIndex(info);
                    if (clientIndex is null) return result;

                    // 按谓词枚举必须依赖路径树：整个批量期间只解析一次。
                    clientIndex.EnsurePathsParsed();

                    foreach (var (path, record) in clientIndex.Records)
                    {
                        if (!predicate(path)) continue;
                        // 叠加层优先：磁盘上的旧内容不能覆盖会话中已改写的版本。
                        if (session?.TryGet(path) is not null) continue;
                        result[path] = record.Read().ToArray();
                    }
                }
                else if (info.Mode == GameMode.Bundles2)
                {
                    // 与 GGPK 分支一致：复用引擎共享会话，路径树整个会话只解析一次。
                    var clientIndex = GetOrOpenClientIndex(info);
                    if (clientIndex is null) return result;

                    clientIndex.EnsurePathsParsed();
                    foreach (var (path, record) in clientIndex.Records)
                    {
                        if (!predicate(path)) continue;
                        if (session?.TryGet(path) is not null) continue;
                        result[path] = record.Read().ToArray();
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

    /// <summary>
    /// 写盘前自检：读几个客户端必然存在的文件，确认当前索引能正常解析。
    ///
    /// 索引与 bundle 一旦对不上，后续每个模块都会以「读取失败」告终，用户只能看到
    /// 一串互相牵连的报错。这里提前读一次，把问题拦在写盘之前。
    ///
    /// 返回 null 表示通过，否则返回可直接展示给用户的失败原因。
    /// </summary>
    public string? VerifyClientReadable(GameModeInfo info)
    {
        // 三个样本分别覆盖界面 XML、数据表、角色模型，对应字体、物价、特效三条链路。
        string[] samples =
        [
            "metadata/ui/uisettings.xml",
            "data/balance/baseitemtypes.datc64",
            "metadata/characters/character.ot"
        ];

        try
        {
            var batch = ReadGameFiles(info, samples);
            foreach (var path in samples)
            {
                if (!batch.TryGetValue(path, out var data) || data is null || data.Length == 0)
                {
                    return $"客户端自检失败：读不到 {path}。\n\n" +
                           "这说明当前索引与客户端资源已经对不上，继续写入只会产生更多错误。\n" +
                           "请先用 Steam / 官方平台【验证游戏文件完整性】，再回来应用补丁。";
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"客户端自检失败：{ex.Message}\n\n" +
                   "请先用 Steam / 官方平台【验证游戏文件完整性】，再回来应用补丁。";
        }
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
    /// 按候选路径顺序读取数据表，返回第一个成功读到的内容。
    ///
    /// 客户端的语言目录命名在不同版本间有过变化（data/balance 前缀、
    /// traditional/simplified/chinese 几种写法并存），因此每张表都要按
    /// 候选列表逐个尝试。候选来源优先级：客户端探测结果 → 已知历史路径。
    ///
    /// 空字符串候选会被跳过，重复路径只尝试一次。
    /// </summary>
    /// <param name="info">客户端信息。</param>
    /// <param name="candidatePaths">候选虚拟路径，按优先级排列。</param>
    /// <param name="actualPath">实际命中的路径；全部失败时为空字符串。</param>
    /// <returns>命中的文件内容；全部失败时返回 null。</returns>
    public byte[]? ReadFirstAvailableFile(
        GameModeInfo info,
        IEnumerable<string> candidatePaths,
        out string actualPath)
    {
        actualPath = string.Empty;

        // 一次性批量读，避免逐路径调用 ReadGameFile。
        // ReadGameFile 每次都会打开并解析整份 _.index.bin（Bundles2 下 100~200 MB），
        // 候选路径有 7~8 条时就会重复解析 7~8 次，物价/宝箱/传言的读取因此明显卡顿。
        var candidates = candidatePaths
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0) return null;

        var batch = ReadGameFiles(info, candidates);
        foreach (var path in candidates)
        {
            var cleanPath = path.Replace('\\', '/').TrimStart('/');
            if (batch.TryGetValue(cleanPath, out var bytes) && bytes.Length > 0)
            {
                actualPath = cleanPath;
                return bytes;
            }
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
    /// <summary>
    /// 写入文件。
    ///
    /// 批量会话进行中时只登记到叠加层，不落盘 —— 由 CommitSession 统一写一次。
    /// 会话之外的行为与之前完全一致。
    /// </summary>
    public bool WriteGameFiles(
        GameModeInfo info,
        IEnumerable<(string VirtualPath, byte[] Data)> files,
        IReadOnlySet<string>? allowedNewPaths = null)
    {
        lock (_fileLock)
        {
            var session = _session;
            var fileList = files as IList<(string VirtualPath, byte[] Data)> ?? files.ToList();

            if (session is null)
            {
                bool written = WriteGameFilesCore(info, fileList, allowedNewPaths);
                if (written) AddWriteCount(fileList.Count);
                return written;
            }

            // 会话合并写盘时必须保留各模块登记的新增路径许可，
            // 否则光柱新增的 .ao 会被 FilterExisting 当作「索引里不存在」丢弃。
            if (allowedNewPaths is not null) session.AllowNewPaths(allowedNewPaths);

            int staged = 0;
            foreach (var (vPath, data) in fileList)
            {
                if (!session.Stage(vPath, data))
                {
                    LastWriteException = new InvalidOperationException(
                        "批量写入出现路径冲突：" + string.Join("；", session.Conflicts));
                    return false;
                }
                staged++;
            }

            AddWriteCount(staged);
            return true;
        }
    }

    private bool WriteGameFilesCore(
        GameModeInfo info,
        IEnumerable<(string VirtualPath, byte[] Data)> files,
        IReadOnlySet<string>? allowedNewPaths = null)
    {
        lock (_fileLock)
        {
            OodleNative.EnsureLoaded(info.GameDirectory);

            // Windows 下 GGPK 句柄独占文件：写盘前必须先释放共享索引，否则 Replace 会 IOException。
            ReleaseClientIndex();

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
                    // 不调用 ParsePaths：FilterExisting 走 TryGetFile(NameHash)，
                    // AddFileRecordPlaceholder 与 Index.Replace 也只依赖 _Files / NameHash。
                    // 写盘路径解析 426 万条路径属于纯浪费，每次约 8-10 秒。

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
                        // 同 GGPK 分支：写盘链路只使用 NameHash / _Files，不需要 ParsePaths。
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
                // CompareAll 只按路径精确比对（TryGetFile），不需要路径树。
                return CompareAll(verify.Index, expected);
            }

            var bundlesDir = GetBundlesDirectory(info);
            var indexPath = Path.Combine(bundlesDir, "_.index.bin");
            var factory = new DriveBundleFactory(bundlesDir);
            try
            {
                using var index = new LibBundle3.Index(indexPath, parsePaths: false, factory);
                // CompareAll 只按路径精确比对（TryGetFile），不需要路径树。
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
