using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace FreiAtlas.Patcher.Core;

/// <summary>
/// GZip 解压的统一入口（带解压后大小上限）。
///
/// 直接 <c>GZipStream.CopyTo</c> 没有上限：资源被替换、损坏或构造为压缩炸弹时，
/// 解压结果会把进程内存吃满。所有内嵌资源都走这里，超限抛出可读异常。
/// </summary>
public static class GzipUtil
{
    /// <summary>默认解压上限（64 MiB）。调用方应按资源实际体量给更紧的值。</summary>
    public const int DefaultMaxBytes = 64 * 1024 * 1024;

    /// <summary>
    /// 解压到字节数组，解压后超过 <paramref name="maxBytes"/> 时抛出
    /// <see cref="InvalidDataException"/>。输入流保持打开（由调用方释放）。
    /// </summary>
    public static byte[] DecompressToBytes(Stream compressed, int maxBytes = DefaultMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(compressed);
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));

        using var gzip = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: true);
        using var output = new MemoryStream();

        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > maxBytes)
                {
                    throw new InvalidDataException(
                        $"解压后的数据超过 {maxBytes} 字节上限（疑似压缩炸弹或资源损坏）。");
                }

                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return output.ToArray();
    }

    /// <summary>解压为 UTF-8 文本，同样受 <paramref name="maxBytes"/> 限制。</summary>
    public static string DecompressToString(Stream compressed, int maxBytes = DefaultMaxBytes)
        => Encoding.UTF8.GetString(DecompressToBytes(compressed, maxBytes));
}
