using System;
using System.Collections.Generic;
using System.IO;
using LibBundle3;
using LibBundle3.Records;
using LibBundledGGPK3;

namespace FreiAtlas.Patcher.Core;

/// <summary>
/// 客户端索引会话：一次打开，供路径枚举与逐文件读取共用。
///
/// 存在的理由：索引解析（ParsePaths）在 426 万条路径的客户端上需要数秒，
/// 若按调用反复开关索引，一次批量补丁会在重复解析上耗掉几分钟。
///
/// 生命周期：由 <see cref="PatchEngine"/> 在批量期间持有；
/// 物理写入前必须显式释放（Windows 下 GGPK 句柄会独占文件），写入后按需重建。
/// </summary>
internal sealed class ClientIndexSession : IDisposable
{
    private readonly BundledGGPK? _ggpk;
    private readonly FileStream? _ggpkStream;
    private readonly LibBundle3.Index? _index;
    private readonly IBundleFactory? _factory;
    private readonly string? _indexPath;
    private readonly Dictionary<BundleRecord, Bundle> _openBundles = [];
    private bool _pathsParsed;

    /// <summary>会话是否已经释放。释放后再读取会明确报错，而不是访问已关闭的句柄。</summary>
    internal bool IsDisposed { get; private set; }

    /// <summary>当前会话是否已经解析过路径树。</summary>
    internal bool PathsParsed => _pathsParsed;

    private ClientIndexSession(
        BundledGGPK? ggpk,
        FileStream? ggpkStream,
        LibBundle3.Index? index,
        IBundleFactory? factory,
        string? indexPath,
        bool pathsParsed)
    {
        _ggpk = ggpk;
        _ggpkStream = ggpkStream;
        _index = index;
        _factory = factory;
        _indexPath = indexPath;
        _pathsParsed = pathsParsed;
    }

    /// <summary>
    /// 确保路径树已解析。
    ///
    /// 精确查找（<see cref="ReadFile"/>）走 NameHash，不需要路径树；
    /// 只有按谓词枚举（<see cref="Records"/>）才需要。整个会话最多解析一次。
    /// </summary>
    public void EnsurePathsParsed()
    {
        ThrowIfDisposed();
        if (_pathsParsed) return;

        _ggpk?.Index.ParsePaths();
        _index?.ParsePaths();
        _pathsParsed = true;
    }

    /// <summary>全部已解析路径与其记录的对应关系，路径统一为正斜杠。</summary>
    public IEnumerable<(string Path, FileRecord Record)> Records
    {
        get
        {
            EnsurePathsParsed();

            var files = _ggpk?.Index.Files ?? _index?.Files;
            if (files is null) yield break;

            foreach (var record in files.Values)
            {
                var p = record.Path;
                if (string.IsNullOrEmpty(p)) continue;
                yield return (VirtualPath.Normalize(p), record);
            }
        }
    }

    /// <summary>按路径读取文件内容；不存在时返回 null。</summary>
    public byte[]? ReadFile(string path)
    {
        ThrowIfDisposed();
        var clean = VirtualPath.Normalize(path);
        var index = _ggpk?.Index ?? _index;
        if (index is null) return null;

        if (!index.TryGetFile(clean, out var record) || record is null) return null;

        // GGPK 模式：Index 内部自带 factory，直接无参读取即可。
        if (_factory is null) return record.Read().ToArray();

        // Bundles2 模式：同一 bundle 复用实例。
        // bundle 内部会缓存已解压的 chunk，反复 new 会把这些缓存全部丢掉。
        if (!_openBundles.TryGetValue(record.BundleRecord, out var bundle))
        {
            bundle = _factory.GetBundle(record.BundleRecord);
            _openBundles[record.BundleRecord] = bundle;
        }

        return record.Read(bundle).ToArray();
    }

    /// <summary>检查指定路径是否存在于索引中。</summary>
    public bool FileExists(string path)
    {
        ThrowIfDisposed();
        var clean = VirtualPath.Normalize(path);
        var index = _ggpk?.Index ?? _index;
        return index is not null && index.TryGetFile(clean, out var record) && record is not null;
    }

    /// <summary>返回文件大小；索引中没有该路径时返回 null。</summary>
    public int? GetFileSize(string path)
    {
        ThrowIfDisposed();
        var clean = VirtualPath.Normalize(path);
        var index = _ggpk?.Index ?? _index;
        if (index is null) return null;
        return index.TryGetFile(clean, out var record) && record is not null ? record.Size : null;
    }

    /// <summary>
    /// 底层索引对象。仅限 Core 内部使用（需要遍历 Bundles、读取索引节点时）。
    /// 外部模块应通过本类的 ReadFile / FileExists / Records 访问，避免各自持有索引句柄。
    /// </summary>
    internal LibBundle3.Index? Index => _ggpk?.Index ?? _index;

    /// <summary>
    /// 读取 Bundles2/_.index.bin 的完整字节。
    /// GGPK 模式从包内节点读取；Bundles2 模式直接读索引文件。读取失败返回 null。
    /// </summary>
    public byte[]? ReadIndexBytes()
    {
        ThrowIfDisposed();

        if (_ggpk is not null)
        {
            return _ggpk.Root.TryFindNode("Bundles2/_.index.bin", out var node)
                   && node is LibGGPK3.Records.FileRecord record
                ? record.Read()
                : null;
        }

        return _indexPath is null ? null : File.ReadAllBytes(_indexPath);
    }

    /// <summary>
    /// 完成一次路径解析并返回全部路径，供 <see cref="PatchEngine"/> 建立跨索引会话的路径缓存。
    /// </summary>
    internal IReadOnlyList<string> GetAllPaths()
    {
        EnsurePathsParsed();

        var files = _ggpk?.Index.Files ?? _index?.Files;
        if (files is null) return Array.Empty<string>();

        var paths = new List<string>(files.Count);
        foreach (var record in files.Values)
        {
            var path = record.Path;
            if (!string.IsNullOrEmpty(path))
            {
                paths.Add(VirtualPath.Normalize(path));
            }
        }
        return paths;
    }

    /// <summary>
    /// 用缓存路径在当前索引上枚举记录；不调用 ParsePaths。
    /// 缓存中已不存在的路径（例如游戏更新后）会被 TryGetFile 自动跳过。
    /// </summary>
    internal IEnumerable<(string Path, FileRecord Record)> EnumerateRecords(IReadOnlyList<string> paths)
    {
        ThrowIfDisposed();
        var index = _ggpk?.Index ?? _index;
        if (index is null) yield break;

        foreach (var path in paths)
        {
            if (index.TryGetFile(path, out var record) && record is not null)
            {
                yield return (path, record);
            }
        }
    }

    /// <summary>
    /// 打开客户端索引；客户端无效或文件缺失时返回 null。
    /// </summary>
    /// <param name="parsePaths">
    /// 是否立即解析路径树。只做精确查找时传 false，避免白付解析成本；
    /// 之后需要枚举时 <see cref="EnsurePathsParsed"/> 会补上。
    /// </param>
    public static ClientIndexSession? Open(GameModeInfo info, bool parsePaths = false)
    {
        if (info.Mode == GameMode.GGPK)
        {
            var ggpkPath = PatchEngine.GetGgpkPath(info);
            if (!File.Exists(ggpkPath)) return null;

            // 只读打开并允许其它句柄读写：既不需要写权限，也不会独占 Content.ggpk。
            var stream = new FileStream(ggpkPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var ggpk = new BundledGGPK(stream, leaveOpen: true, parsePathsInIndex: false);
            if (parsePaths) ggpk.Index.ParsePaths();
            return new ClientIndexSession(ggpk, stream, null, null, ggpkPath, parsePaths);
        }

        var bundlesDir = PatchEngine.GetBundlesDirectory(info);
        var indexPath = PatchEngine.GetBundlesIndexPath(info);
        if (!File.Exists(indexPath)) return null;

        var factory = new DriveBundleFactory(bundlesDir);
        var index = new LibBundle3.Index(indexPath, parsePaths: false, factory);
        if (parsePaths) index.ParsePaths();
        return new ClientIndexSession(null, null, index, factory, indexPath, parsePaths);
    }

    private void ThrowIfDisposed()
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(ClientIndexSession));
        }
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;

        foreach (var bundle in _openBundles.Values)
        {
            bundle?.Dispose();
        }
        _openBundles.Clear();

        _index?.Dispose();
        (_factory as IDisposable)?.Dispose();
        _ggpk?.Dispose();
        _ggpkStream?.Dispose();
    }
}
