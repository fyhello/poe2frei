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
    private readonly Dictionary<BundleRecord, Bundle> _openBundles = [];
    private bool _pathsParsed;

    private ClientIndexSession(
        BundledGGPK? ggpk,
        FileStream? ggpkStream,
        LibBundle3.Index? index,
        IBundleFactory? factory,
        bool pathsParsed)
    {
        _ggpk = ggpk;
        _ggpkStream = ggpkStream;
        _index = index;
        _factory = factory;
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
                yield return (p.Replace('\\', '/'), record);
            }
        }
    }

    /// <summary>按路径读取文件内容；不存在时返回 null。</summary>
    public byte[]? ReadFile(string path)
    {
        var clean = path.Replace('\\', '/').TrimStart('/');
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
        var clean = path.Replace('\\', '/').TrimStart('/');
        var index = _ggpk?.Index ?? _index;
        return index is not null && index.TryGetFile(clean, out var record) && record is not null;
    }

    /// <summary>全部已解析的虚拟路径集合。</summary>
    public IEnumerable<string> AllFilePaths => Records.Select(r => r.Path);

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
            return new ClientIndexSession(ggpk, stream, null, null, parsePaths);
        }

        var bundlesDir = PatchEngine.GetBundlesDirectory(info);
        var indexPath = PatchEngine.GetBundlesIndexPath(info);
        if (!File.Exists(indexPath)) return null;

        var factory = new DriveBundleFactory(bundlesDir);
        var index = new LibBundle3.Index(indexPath, parsePaths: false, factory);
        if (parsePaths) index.ParsePaths();
        return new ClientIndexSession(null, null, index, factory, parsePaths);
    }

    public void Dispose()
    {
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
