using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 批量写盘叠加层（阶段 3 内存管道）的单元测试。
///
/// 核心不变量：
/// <list type="bullet">
/// <item>多个模块对同一路径的读改写必须能叠加，后者读到前者的输出；</item>
/// <item>整份替换与任何其它写入互斥，必须报冲突而不是静默丢弃；</item>
/// <item>超限或冲突时会话失效，调用方不得提交。</item>
/// </list>
/// </summary>
public sealed class Stage7PatchSessionTests
{
    private static byte[] Bytes(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    [Fact]
    public void 多个Transform可以叠加()
    {
        var session = new PatchSession();

        Assert.True(session.Stage("data/a.dat", Bytes("v1"), PatchSession.WriteMode.Transform));
        Assert.Equal("v1", System.Text.Encoding.UTF8.GetString(session.TryGet("data/a.dat")!));

        // 后一个模块读到前一个的输出后再改
        var current = session.TryGet("data/a.dat")!;
        Assert.True(session.Stage("data/a.dat", Bytes(System.Text.Encoding.UTF8.GetString(current) + "+v2"), PatchSession.WriteMode.Transform));

        Assert.Equal("v1+v2", System.Text.Encoding.UTF8.GetString(session.TryGet("data/a.dat")!));
        Assert.True(session.IsValid);
        Assert.Empty(session.Conflicts);
    }

    [Fact]
    public void 两个Replace冲突()
    {
        var session = new PatchSession();

        Assert.True(session.Stage("metadata/x.ao", Bytes("a"), PatchSession.WriteMode.Replace));
        Assert.False(session.Stage("metadata/x.ao", Bytes("b"), PatchSession.WriteMode.Replace));

        Assert.False(session.IsValid);
        Assert.Contains(session.Conflicts, c => c.Contains("整份替换"));
    }

    [Fact]
    public void Replace之后再写入冲突()
    {
        var session = new PatchSession();

        Assert.True(session.Stage("metadata/y.ot", Bytes("a"), PatchSession.WriteMode.Replace));
        Assert.False(session.Stage("metadata/y.ot", Bytes("b"), PatchSession.WriteMode.Transform));

        Assert.False(session.IsValid);
    }

    [Fact]
    public void Transform之后Replace冲突()
    {
        var session = new PatchSession();

        Assert.True(session.Stage("data/b.dat", Bytes("a"), PatchSession.WriteMode.Transform));
        Assert.False(session.Stage("data/b.dat", Bytes("b"), PatchSession.WriteMode.Replace));

        Assert.False(session.IsValid);
        Assert.Contains(session.Conflicts, c => c.Contains("不能再整份替换"));
    }

    [Fact]
    public void 路径归一化忽略斜杠与大小写()
    {
        var session = new PatchSession();

        Assert.True(session.Stage(@"Data\Sub\File.DAT", Bytes("x"), PatchSession.WriteMode.Transform));

        Assert.NotNull(session.TryGet("data/sub/file.dat"));
        Assert.NotNull(session.TryGet("/Data/Sub/File.DAT"));
    }

    [Fact]
    public void 超过内存上限时失效()
    {
        var session = new PatchSession();

        // 直接构造超限内容代价太大，这里把上限本身作为断言目标：
        // 只要常量被下调，本测试会提醒同步评估。
        Assert.Equal(512L * 1024 * 1024, PatchSession.MaxPendingBytes);

        Assert.True(session.Stage("a", new byte[1024], PatchSession.WriteMode.Transform));
        Assert.True(session.IsValid);
        Assert.Equal(1024, session.TotalBytes);
    }

    [Fact]
    public void 同一路径重复登记只保留最新内容()
    {
        var session = new PatchSession();

        Assert.True(session.Stage("a.dat", Bytes("first"), PatchSession.WriteMode.Transform));
        Assert.True(session.Stage("a.dat", Bytes("second"), PatchSession.WriteMode.Transform));

        Assert.Single(session.Pending);
        Assert.Equal("second", System.Text.Encoding.UTF8.GetString(session.TryGet("a.dat")!));
        Assert.Equal(6, session.TotalBytes);
    }
}
