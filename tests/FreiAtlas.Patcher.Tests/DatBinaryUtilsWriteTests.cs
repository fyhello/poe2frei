using System.Text;
using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 统一的 DAT 字符串指针追加重写。
///
/// 这段算法此前在 5 个模块各有一份副本，行尺寸校验、去重、对齐的实现
/// 逐渐分叉。收敛成一份后，这里锁定它的全部关键约定：
/// 指针宽度、相对基准、对齐、终止符、去重。
/// </summary>
public sealed class DatBinaryUtilsWriteTests
{
    /// <summary>
    /// 构造一个最小合法 DAT：行数 + 单行 + 8 字节哨兵。
    /// 行内偏移 0 处放一个指向字符串区的指针槽。
    /// </summary>
    private static (byte[] Data, int SentinelPos, int PointerPos) BuildMinimalTable(
        string existingText)
    {
        const int rowCount = 1;
        const int rowSize = 16;

        byte[] textBytes = Encoding.Unicode.GetBytes(existingText);
        int stringAreaLength = textBytes.Length + 4;   // 4 字节终止符

        int sentinelPos = 4 + rowCount * rowSize;
        int totalLength = sentinelPos + 8 + stringAreaLength;

        var data = new byte[totalLength];
        BitConverter.GetBytes(rowCount).CopyTo(data, 0);

        // 哨兵
        for (int i = 0; i < 8; i++) data[sentinelPos + i] = 0xBB;

        // 字符串区（相对哨兵偏移 0）
        int stringStart = sentinelPos + 8;
        textBytes.CopyTo(data, stringStart);

        // 指针槽（行 0，偏移 0）指向偏移 0
        int pointerPos = 4;
        BitConverter.GetBytes(0UL).CopyTo(data, pointerPos);

        return (data, sentinelPos, pointerPos);
    }

    /// <summary>
    /// 追加字符串后，指针必须能被读回为写入的文本。
    /// 这是端到端正确性的核心断言。
    /// </summary>
    [Fact]
    public void 追加后指针可读回新文本()
    {
        var (data, sentinelPos, pointerPos) = BuildMinimalTable("原始文本");

        byte[] patched = DatBinaryUtils.RewriteStringPointersAppend(
            data, sentinelPos, [(pointerPos, "新文本")]);

        string readBack = DatBinaryUtils.ReadPointerTarget(patched, pointerPos, sentinelPos);

        Assert.Equal("新文本", readBack);
        Assert.True(patched.Length > data.Length);
    }

    /// <summary>
    /// 指针必须是 8 字节、小端、相对哨兵的偏移。
    /// </summary>
    [Fact]
    public void 指针为8字节相对哨兵的偏移()
    {
        var (data, sentinelPos, pointerPos) = BuildMinimalTable("原始");

        byte[] patched = DatBinaryUtils.RewriteStringPointersAppend(
            data, sentinelPos, [(pointerPos, "X")]);

        ulong offset = BitConverter.ToUInt64(patched, pointerPos);
        int expectedStringStart = sentinelPos + (int)offset;

        // 该位置起应是 UTF-16LE 的 "X"
        Assert.Equal((byte)'X', patched[expectedStringStart]);
        Assert.Equal(0, patched[expectedStringStart + 1]);
    }

    /// <summary>
    /// 新字符串起始必须相对哨兵 2 字节对齐。
    /// 奇数偏移会让 UTF-16 解码错位。
    /// </summary>
    [Fact]
    public void 字符串起始相对哨兵2字节对齐()
    {
        var (data, sentinelPos, pointerPos) = BuildMinimalTable("A");

        // 连写两段不同文本，第二段必须对齐
        byte[] patched = DatBinaryUtils.RewriteStringPointersAppend(
            data, sentinelPos, [(pointerPos, "第一段")]);

        // 再追加一次（新实例，模拟第二批写入）
        var (data2, sentinel2, pointer2) = BuildMinimalTable("B");
        byte[] patched2 = DatBinaryUtils.RewriteStringPointersAppend(
            data2, sentinel2, [(pointer2, "第二段")]);

        ulong offset = BitConverter.ToUInt64(patched, pointerPos);
        Assert.Equal(0, (int)offset % 2);

        ulong offset2 = BitConverter.ToUInt64(patched2, pointer2);
        Assert.Equal(0, (int)offset2 % 2);
    }

    /// <summary>
    /// 每个字符串必须以 4 字节 0 终止，否则读取会越过边界吞掉后面的字符串。
    /// </summary>
    [Fact]
    public void 字符串以4字节零终止()
    {
        var (data, sentinelPos, pointerPos) = BuildMinimalTable("原");

        byte[] patched = DatBinaryUtils.RewriteStringPointersAppend(
            data, sentinelPos, [(pointerPos, "AB")]);

        ulong offset = BitConverter.ToUInt64(patched, pointerPos);
        int stringStart = sentinelPos + (int)offset;
        int terminatorPos = stringStart + "AB".Length * 2;

        Assert.Equal(0, patched[terminatorPos]);
        Assert.Equal(0, patched[terminatorPos + 1]);
        Assert.Equal(0, patched[terminatorPos + 2]);
        Assert.Equal(0, patched[terminatorPos + 3]);
    }

    /// <summary>
    /// 相同文本只追加一份，多个指针槽复用它。
    /// 名称补丁会在多行上用同一文本，不去重会让文件线性膨胀。
    /// </summary>
    [Fact]
    public void 相同文本只追加一份()
    {
        const int rowCount = 2;
        const int rowSize = 16;
        int sentinelPos = 4 + rowCount * rowSize;

        var data = new byte[sentinelPos + 8 + 16];
        BitConverter.GetBytes(rowCount).CopyTo(data, 0);
        for (int i = 0; i < 8; i++) data[sentinelPos + i] = 0xBB;

        int pointerA = 4;
        int pointerB = 4 + rowSize;

        byte[] patched = DatBinaryUtils.RewriteStringPointersAppend(
            data, sentinelPos, [(pointerA, "同文本"), (pointerB, "同文本")]);

        ulong offsetA = BitConverter.ToUInt64(patched, pointerA);
        ulong offsetB = BitConverter.ToUInt64(patched, pointerB);

        Assert.Equal(offsetA, offsetB);
    }

    /// <summary>
    /// 原始内容（行区与哨兵）必须逐字节保持不变，只在尾部追加。
    /// </summary>
    [Fact]
    public void 原始行区与哨兵不被改动()
    {
        var (data, sentinelPos, pointerPos) = BuildMinimalTable("原始");

        byte[] patched = DatBinaryUtils.RewriteStringPointersAppend(
            data, sentinelPos, [(pointerPos, "新的")]);

        // 行区（不含指针槽）与哨兵逐字节相同
        for (int i = 0; i < data.Length; i++)
        {
            if (i >= pointerPos && i < pointerPos + 8) continue;   // 指针槽允许变化
            Assert.Equal(data[i], patched[i]);
        }
    }
}
