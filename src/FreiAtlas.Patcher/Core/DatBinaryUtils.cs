using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FreiAtlas.Patcher.Core;

/// <summary>
/// .datc64 二进制结构的公用读取工具。
///
/// 文件布局：uint32 行数 + 定长行区 + 8 字节 0xBB 哨兵 + 变长字符串区。
/// 行内 string 字段是 8 字节小端偏移，相对哨兵位置。
/// </summary>
public static class DatBinaryUtils
{
    /// <summary>定长行区与字符串区之间的 8 字节哨兵。</summary>
    public static ReadOnlySpan<byte> Sentinel => [0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB];

    /// <summary>哨兵作为 ulong 的值，用于识别未写入的指针槽。</summary>
    public const ulong SentinelValue = 0xBBBBBBBBBBBBBBBBUL;

    /// <summary>
    /// 单个补丁目标文件的字节上限（8 MiB），超出即跳过。
    ///
    /// 客户端的特效脚本与怪物定义普遍在几十 KB 量级，超过 8 MiB 的文件
    /// 基本可以判定不是本模块该处理的目标（多为误判或数据异常）。
    /// 设这个上限是为了避免把内存耗在大文件上，同时防止正则作用于
    /// 异常内容时产生灾难性回溯。
    /// </summary>
    public const int MaxPatchFileBytes = 8 * 1024 * 1024;

    /// <summary>
    /// 查找字符串区起始哨兵的位置。
    /// </summary>
    /// <param name="data">完整文件字节。</param>
    /// <param name="rowCount">
    /// 行数。大于 0 时要求哨兵恰好落在行区末尾（<c>(位置 - 4) % rowCount == 0</c>），
    /// 避免把字符串区数据中的巧合字节序列误判为哨兵。
    /// </param>
    /// <returns>哨兵起始偏移；未找到返回 -1。</returns>
    public static int FindSentinel(byte[] data, int rowCount = 0)
    {
        if (data is null || data.Length < 12) return -1;

        for (int i = 4; i < data.Length - 7; i++)
        {
            if (rowCount > 0 && (i - 4) % rowCount != 0) continue;
            if (data[i] != 0xBB || data[i + 1] != 0xBB || data[i + 2] != 0xBB || data[i + 3] != 0xBB) continue;
            if (data[i + 4] != 0xBB || data[i + 5] != 0xBB || data[i + 6] != 0xBB || data[i + 7] != 0xBB) continue;
            return i;
        }
        return -1;
    }

    /// <summary>
    /// 读取 UTF-16LE 零结尾字符串，不裁剪空白。
    /// </summary>
    /// <param name="maxChars">最多读取的字符数，防止指针损坏时越界扫描整个文件。</param>
    public static string ReadUtf16LeZ(byte[] data, int start, int maxChars = 500)
    {
        if (data is null || start < 0 || start + 1 >= data.Length) return string.Empty;

        var builder = new StringBuilder(Math.Min(maxChars, 64));
        int limit = Math.Min(data.Length - 1, start + maxChars * 2);
        for (int i = start; i < limit; i += 2)
        {
            int code = data[i] | (data[i + 1] << 8);
            if (code == 0) break;
            builder.Append((char)code);
        }
        return builder.ToString();
    }

    /// <summary>
    /// 读取 UTF-16LE 零结尾字符串并裁剪首尾空白。
    /// 用于需要与词典文本做等值比较的场景。
    /// </summary>
    public static string ReadUtf16LeZTrimmed(byte[] data, int start, int maxChars = 500) =>
        ReadUtf16LeZ(data, start, maxChars).Trim();

    /// <summary>
    /// 读取行内指针字段指向的字符串。
    /// </summary>
    /// <param name="pointerPosition">指针槽在文件中的绝对偏移。</param>
    /// <param name="sentinelPosition">字符串区起始哨兵偏移。</param>
    public static string ReadPointerTarget(byte[] data, int pointerPosition, int sentinelPosition, int maxChars = 500)
    {
        if (data is null) return string.Empty;
        if (pointerPosition < 0 || pointerPosition + 8 > data.Length) return string.Empty;
        if (sentinelPosition <= 0 || sentinelPosition >= data.Length) return string.Empty;

        ulong offset = BitConverter.ToUInt64(data, pointerPosition);
        if (offset == 0 || offset == SentinelValue) return string.Empty;

        long position = sentinelPosition + (long)offset;
        if (position <= 0 || position >= data.Length - 1) return string.Empty;
        return ReadUtf16LeZ(data, (int)position, maxChars);
    }

    /// <summary>
    /// 探测 DAT 表布局：行数、行尺寸、字符串区起点。
    ///
    /// 该逻辑此前在 Price / Name / Rumour 三处各有一份，校验条件与异常文案
    /// 逐渐分叉。统一后所有失败路径都给出同样的可读原因。
    /// </summary>
    /// <param name="data">完整文件字节。</param>
    /// <param name="tableName">用于异常文案的表名。</param>
    /// <returns>(行数, 行尺寸, 字符串区起点)。</returns>
    /// <exception cref="ArgumentException">结构不合法时抛出，原因可直接展示给用户。</exception>
    public static (int RowCount, int RowSize, int StringBase) DetectLayout(
        byte[] data,
        string tableName = "数据表")
    {
        if (data is null || data.Length < 16)
        {
            throw new ArgumentException($"{tableName} 文件太小，无法解析。");
        }

        int rowCount = BitConverter.ToInt32(data, 0);
        if (rowCount <= 0)
        {
            throw new ArgumentException($"{tableName} 的行数无效：{rowCount}。");
        }

        // 带上 rowCount 校验，避免把字符串区里的巧合字节当成哨兵。
        int sentinelPos = FindSentinel(data, rowCount);
        if (sentinelPos < 0)
        {
            throw new ArgumentException($"{tableName} 中无法定位字符串区哨兵（0xBB×8）。");
        }

        int rowAreaBytes = sentinelPos - 4;
        if (rowAreaBytes % rowCount != 0)
        {
            throw new ArgumentException(
                $"{tableName} 的行总字节数 {rowAreaBytes} 不能被行数 {rowCount} 整除。");
        }

        return (rowCount, rowAreaBytes / rowCount, sentinelPos);
    }

    /// <summary>
    /// 把新字符串追加到字符串区末尾，并回写各指针槽。
    ///
    /// 写入规则（与游戏自身的 DAT 序列化一致）：
    ///   - 新字符串追加到文件末尾，按 UTF-16LE 编码；
    ///   - 每个字符串以 4 字节 0 终止；
    ///   - 起始位置相对字符串区起点（哨兵）2 字节对齐；
    ///   - 指针是 8 字节小端偏移，值为「相对哨兵的字节偏移」；
    ///   - 相同文本复用同一份，避免重复膨胀。
    ///
    /// 调用前必须确认 <paramref name="stringBase"/> 是真实的哨兵位置，
    /// 且 <paramref name="replacements"/> 里的每个 PointerPos 都指向一个
    /// string 槽 —— 指到 i32 / foreignrow 槽会写坏数据。
    /// </summary>
    /// <param name="data">原始文件字节。</param>
    /// <param name="stringBase">字符串区起始哨兵偏移。</param>
    /// <param name="replacements">（指针槽绝对偏移, 新文本）序列。</param>
    public static byte[] RewriteStringPointersAppend(
        byte[] data,
        int stringBase,
        IEnumerable<(int PointerPos, string NewText)> replacements)
    {
        using var stream = new MemoryStream();
        stream.Write(data, 0, data.Length);

        var dedup = new Dictionary<string, ulong>(StringComparer.Ordinal);

        foreach (var (pointerPos, newText) in replacements.OrderBy(x => x.PointerPos))
        {
            if (!dedup.TryGetValue(newText, out ulong stringOffset))
            {
                // 2 字节对齐：UTF-16 字符宽度为 2，奇数偏移会让后续读取错位。
                if ((stream.Length - stringBase) % 2 != 0L)
                {
                    stream.WriteByte(0);
                }

                stringOffset = (ulong)(stream.Length - stringBase);
                byte[] textBytes = Encoding.Unicode.GetBytes(newText);
                stream.Write(textBytes, 0, textBytes.Length);

                // 4 字节 NUL 终止符
                stream.WriteByte(0);
                stream.WriteByte(0);
                stream.WriteByte(0);
                stream.WriteByte(0);

                dedup[newText] = stringOffset;
            }

            byte[] pointerBytes = BitConverter.GetBytes(stringOffset);
            long resumePosition = stream.Position;
            stream.Position = pointerPos;
            stream.Write(pointerBytes, 0, 8);
            stream.Position = resumePosition;
        }

        return stream.ToArray();
    }
}
