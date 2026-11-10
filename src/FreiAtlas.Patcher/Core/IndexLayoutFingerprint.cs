using System.IO;
using System.Security.Cryptography;
using LibBundle3;

namespace FreiAtlas.Patcher.Core;

/// <summary>
/// 客户端索引的「官方 bundle 布局指纹」。
///
/// 只对官方 bundle（排除本工具与第三方补丁创建的 <c>Content/</c> bundle）的
/// 路径与解压后大小做哈希。它回答的是真正重要的问题：
/// 「这份索引引用的资源布局，与当前客户端的 bundle 是否一致」。
///
/// 代价：索引解析约 1 秒、哈希约 0.1 秒。只在构建戳不可用或构建戳不一致时兜底使用，
/// 常规恢复路径不调用。
/// </summary>
public static class IndexLayoutFingerprint
{
    /// <summary>LibBundle3 为补丁新建的 bundle 固定放在该前缀下。</summary>
    private const string CustomBundlePrefix = "Content/";

    public static string Compute(LibBundle3.Index index)
    {
        var bundles = new List<(string Path, int Size)>();
        foreach (var bundle in index.Bundles.Span)
        {
            string path = bundle.Path ?? string.Empty;
            if (path.StartsWith(CustomBundlePrefix, StringComparison.OrdinalIgnoreCase)) continue;
            bundles.Add((path, bundle.UncompressedSize));
        }

        bundles.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(bundles.Count);
        foreach (var (path, size) in bundles)
        {
            writer.Write(path);
            writer.Write(size);
        }
        writer.Flush();

        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    /// <summary>从快照的 .bin（即一份完整 _.index.bin）计算布局指纹；失败返回 null。</summary>
    public static string? ComputeFromIndexFile(string indexPath, string bundlesDirectory)
    {
        try
        {
            // 先整体读入内存再解析：LibBundle3 从路径构造时会持有文件句柄，
            // 导致随后的还原流程无法读取同一份 .bin（Windows 文件占用）。
            byte[] bytes = File.ReadAllBytes(indexPath);
            using var stream = new MemoryStream(bytes, writable: false);

            var factory = new DriveBundleFactory(bundlesDirectory);
            try
            {
                using var index = new LibBundle3.Index(stream, leaveOpen: false, parsePaths: false, factory);
                return Compute(index);
            }
            finally
            {
                (factory as IDisposable)?.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>计算当前客户端索引的布局指纹；读取失败返回 null。</summary>
    public static string? ComputeCurrent(GameModeInfo info)
    {
        try
        {
            // 方法返回前释放会话，确保后续恢复可以重新打开并写入索引。
            using var session = ClientIndexSession.Open(info);
            return session?.Index is { } index ? Compute(index) : null;
        }
        catch
        {
            return null;
        }
    }

}
