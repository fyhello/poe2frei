using System.Text;
using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// DAT 布局探测的统一实现。
///
/// 该逻辑此前在 Price / Name / Rumour 三处各有一份，校验条件与异常文案
/// 逐渐分叉 —— 同样是「哨兵找不到」，三个模块给出三种说法。
/// 收敛后这里锁定它对各种损坏输入的拒绝行为，以及合法表的正确结果。
/// </summary>
public sealed class DatLayoutDetectTests
{
    /// <summary>构造一个合法的最小表：2 行 × 8 字节 + 哨兵 + 字符串区。</summary>
    private static byte[] BuildValidTable(int rowCount = 2, int rowSize = 8)
    {
        int sentinelPos = 4 + rowCount * rowSize;
        var data = new byte[sentinelPos + 8 + 16];
        BitConverter.GetBytes(rowCount).CopyTo(data, 0);
        for (int i = 0; i < 8; i++) data[sentinelPos + i] = 0xBB;
        return data;
    }

    /// <summary>合法表必须解析出正确的行数、行尺寸与哨兵位置。</summary>
    [Fact]
    public void 合法表解析出正确布局()
    {
        byte[] data = BuildValidTable(rowCount: 3, rowSize: 12);

        var (rowCount, rowSize, stringBase) = DatBinaryUtils.DetectLayout(data);

        Assert.Equal(3, rowCount);
        Assert.Equal(12, rowSize);
        Assert.Equal(4 + 3 * 12, stringBase);
    }

    /// <summary>文件过短必须拒绝，不能越界读取。</summary>
    [Fact]
    public void 文件过短时拒绝()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => DatBinaryUtils.DetectLayout(new byte[8]));

        Assert.Contains("文件太小", ex.Message);
    }

    /// <summary>行数为 0 或负数必须拒绝 —— 否则后续会除以零。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void 行数非正时拒绝(int rowCount)
    {
        var data = new byte[64];
        BitConverter.GetBytes(rowCount).CopyTo(data, 0);

        var ex = Assert.Throws<ArgumentException>(
            () => DatBinaryUtils.DetectLayout(data));

        Assert.Contains("行数无效", ex.Message);
    }

    /// <summary>找不到哨兵时必须拒绝，而不是按错误偏移继续写。</summary>
    [Fact]
    public void 找不到哨兵时拒绝()
    {
        // 没有 0xBB 序列
        var data = new byte[64];
        BitConverter.GetBytes(2).CopyTo(data, 0);

        var ex = Assert.Throws<ArgumentException>(
            () => DatBinaryUtils.DetectLayout(data));

        Assert.Contains("哨兵", ex.Message);
    }

    /// <summary>
    /// 哨兵必须落在 (位置-4) 是行数整数倍的地方，否则视为「找不到哨兵」。
    ///
    /// 这项约束由 FindSentinel 承担，所以 DetectLayout 里后续的整除检查
    /// 实际不可达（属防御性冗余，保留以防将来有人去掉 FindSentinel 的 rowCount）。
    /// 这里验证的是「错位的哨兵会被拒绝」这层保护确实生效。
    /// </summary>
    [Fact]
    public void 哨兵位置与行数不对齐时拒绝()
    {
        const int declaredRowCount = 5;
        const int sentinelOffset = 12;           // 12 不是 5 的整数倍

        int sentinelPos = 4 + sentinelOffset;
        var data = new byte[sentinelPos + 8 + 16];
        BitConverter.GetBytes(declaredRowCount).CopyTo(data, 0);
        for (int i = 0; i < 8; i++) data[sentinelPos + i] = 0xBB;

        var ex = Assert.Throws<ArgumentException>(
            () => DatBinaryUtils.DetectLayout(data));

        Assert.Contains("哨兵", ex.Message);
    }

    /// <summary>
    /// 异常文案必须带表名，便于用户判断是哪张表出了问题。
    /// </summary>
    [Fact]
    public void 异常文案包含表名()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => DatBinaryUtils.DetectLayout(new byte[8], "chests.datc64"));

        Assert.Contains("chests.datc64", ex.Message);
    }
}
