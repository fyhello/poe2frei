using System.IO.Compression;
using System.Text;
using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// GZip 解压上限的契约。
///
/// 全模块的嵌入资源都走 CompressedResource；如果没有上限，一份损坏或被替换的资源
/// 就能让解压把进程内存吃满。
/// </summary>
public sealed class CompressedResourceTests
{
    [Fact]
    public void DecompressToBytes_正常数据可还原()
    {
        byte[] payload = Encoding.UTF8.GetBytes(new string('A', 10_000));

        using var compressed = Compress(payload);

        Assert.Equal(payload, CompressedResource.DecompressToBytes(compressed, 64 * 1024));
    }

    [Fact]
    public void DecompressToBytes_超限时抛异常且信息含上限()
    {
        // 1 MiB 全零：压缩后约 1 KB，但解压后必然超过 1 KB 上限。
        byte[] payload = new byte[1024 * 1024];

        using var compressed = Compress(payload);

        var ex = Assert.Throws<InvalidDataException>(
            () => CompressedResource.DecompressToBytes(compressed, 1024));

        Assert.Contains("1024", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DecompressToString_按UTF8解码()
    {
        using var compressed = Compress(Encoding.UTF8.GetBytes("中文内容"));

        Assert.Equal("中文内容", CompressedResource.DecompressToString(compressed, 1024));
    }

    private static MemoryStream Compress(byte[] data)
    {
        var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(data, 0, data.Length);
        }

        buffer.Position = 0;
        return buffer;
    }
}
