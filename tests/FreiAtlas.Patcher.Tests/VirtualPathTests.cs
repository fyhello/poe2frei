using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 虚拟路径规范化的唯一契约。
///
/// 全模块的读、写、叠加层、备份目录名都依赖这一个实现；
/// 一旦规范化结果不一致，同一份内容会以不同 key 存放，模块之间读到旧版本。
/// </summary>
public sealed class VirtualPathTests
{
    [Theory]
    [InlineData(@"Data\Balance\X.datc64", "Data/Balance/X.datc64")]
    [InlineData("/data/balance/x.datc64", "data/balance/x.datc64")]
    [InlineData("  data/has space/x.ao  ", "data/has space/x.ao")]
    [InlineData("data/balance/x.datc64", "data/balance/x.datc64")]
    [InlineData("", "")]
    public void Normalize_统一分隔符并去掉前导斜杠(string input, string expected)
        => Assert.Equal(expected, VirtualPath.Normalize(input));

    [Theory]
    [InlineData("data/balance/x.datc64", true)]
    [InlineData("mudman", true)]
    [InlineData("../escape", false)]
    [InlineData("a/../../b", false)]
    [InlineData(@"C:\Windows\x", false)]
    [InlineData(@"\\server\share", false)]
    [InlineData("/absolute", false)]
    [InlineData("", false)]
    public void IsSafeRelative_拒绝越界与绝对路径(string input, bool expected)
        => Assert.Equal(expected, VirtualPath.IsSafeRelative(input));

    [Fact]
    public void GetGroupDirectory_拒绝越界分组名()
    {
        // 校验发生在读取根目录之前，不需要设置 RootDirectoryOverride
        //（那会与并行执行的 PatchFileBackupTests 争用同一个静态字段）。
        Assert.Throws<ArgumentException>(() => PatchFileBackup.GetGroupDirectory(".."));
        Assert.Throws<ArgumentException>(() => PatchFileBackup.GetGroupDirectory(@"C:\temp"));
    }
}
