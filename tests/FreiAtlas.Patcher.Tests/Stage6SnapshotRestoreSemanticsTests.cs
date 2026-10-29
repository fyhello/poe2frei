namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 快照恢复语义的回归测试。
///
/// 曾经的错误：把「索引指纹与当前不一致」直接当成「版本不匹配」并禁用恢复。
/// 但指纹变化正是「打完补丁」的常态，结果任何补丁之后所有快照都被禁用，
/// 用户永远无法回滚。
///
/// 正确划分：
///   versionMismatch = 游戏版本号不同（含版本号缺失）→ 禁止恢复
///   isStale         = 快照之后客户端被改过        → 仅提示，允许恢复
/// </summary>
public sealed class Stage6SnapshotRestoreSemanticsTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(params string[] parts)
    {
        string path = Path.Combine([RepoRoot(), .. parts]);
        Assert.True(File.Exists(path), $"未找到文件：{path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void 快照列表同时投影版本不匹配与指纹失效()
    {
        string mw = Read("src", "FreiAtlas.Settings", "MainWindow.xaml.cs");

        Assert.Contains("snapshotCurrentVersion", mw);
        Assert.Contains("versionMismatch =", mw);
        // 版本号缺失也要判为不匹配
        Assert.Contains("string.IsNullOrEmpty(s.GameVersion)", mw);
        Assert.Contains("string.IsNullOrEmpty(snapshotCurrentVersion)", mw);
        // 指纹标记仍保留，但不参与禁用
        Assert.Contains("isStale =", mw);
    }

    [Fact]
    public void 前端只在版本不匹配时禁用恢复()
    {
        string js = Read("src", "FreiAtlas.Settings", "Web", "patcher.js");

        Assert.Contains("const versionMismatch = Boolean(s.versionMismatch);", js);
        Assert.Contains("versionMismatch ? \"disabled\" : \"\"", js);

        // 旧的「stale 即禁用」必须消失
        Assert.DoesNotContain("stale ? \"disabled\" : \"\"", js);

        // 两种情况要有不同标签
        Assert.Contains("⚠ 版本不匹配", js);
        Assert.Contains("↺ 快照后已变更", js);
    }
}
