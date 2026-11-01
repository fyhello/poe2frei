using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace FreiAtlas.Patcher.Core;

/// <summary>
/// 内嵌压缩资源的统一解压入口，支持 gzip 与 Brotli 两种容器，并按魔数自动识别。
///
/// 资源历史上都是 gzip；Brotli 在同样的 JSON/二进制上能再省 16%~30%，
/// 因此逐步迁移到 Brotli。自动识别让新旧资源可以共存，迁移不必一次性完成。
///
/// 所有解压都有解压后大小上限：资源被替换、损坏或构造为压缩炸弹时，
/// 无界分配会把进程内存吃满。
/// </summary>
public static class CompressedResource
{
    /// <summary>默认解压上限（64 MiB）。调用方应按资源实际体量给更紧的值。</summary>
    public const int DefaultMaxBytes = 64 * 1024 * 1024;

    /// <summary>压缩输入本身的上限，避免把超大文件整体读进内存。</summary>
    private const int MaxCompressedBytes = 64 * 1024 * 1024;

    /// <summary>
    /// 解压到字节数组。自动识别 gzip（1F 8B）与 Brotli。
    /// 解压后超过 <paramref name="maxBytes"/> 时抛出 <see cref="InvalidDataException"/>。
    /// 输入流保持打开（由调用方释放）。
    /// </summary>
    public static byte[] DecompressToBytes(Stream compressed, int maxBytes = DefaultMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(compressed);
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));

        byte[] payload = ReadAllCompressed(compressed);

        // gzip 头：1F 8B 08（第三个字节是压缩方法，8 = deflate）。
        // 校验 CM 字节而不是只看前两字节：Brotli 没有魔数，首字节恰好是 1F 的概率不低。
        bool isGzip = payload.Length >= 3
                      && payload[0] == 0x1F
                      && payload[1] == 0x8B
                      && payload[2] == 0x08;

        using var input = new MemoryStream(payload, writable: false);
        using Stream decoder = isGzip
            ? new GZipStream(input, CompressionMode.Decompress, leaveOpen: true)
            : new BrotliStream(input, CompressionMode.Decompress, leaveOpen: true);
        using var output = new MemoryStream();

        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = decoder.Read(buffer, 0, buffer.Length)) > 0)
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

    private static byte[] ReadAllCompressed(Stream compressed)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = compressed.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > MaxCompressedBytes)
                {
                    throw new InvalidDataException(
                        $"压缩输入超过 {MaxCompressedBytes} 字节上限。");
                }

                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return buffer.ToArray();
    }
}
