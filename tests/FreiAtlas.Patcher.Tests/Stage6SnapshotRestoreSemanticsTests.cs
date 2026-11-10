namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 快照恢复语义的回归测试。
///
/// 曾经的错误：把「索引指纹与当前不一致」直接当成「版本不匹配」并禁用恢复。
/// 但指纹变化正是「打完补丁」的常态，结果任何补丁之后所有快照都被禁用，
/// 用户永远无法回滚。
///
/// 当前划分：
///   buildMismatch = 客户端构建戳不同（Authenticode）→ 提示并做布局校验
///   buildUnknown  = 旧快照没有构建戳                → 待校验
///   isStale       = 快照之后客户端被改过            → 仅提示，允许恢复
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
    public void 快照列表投影构建戳与指纹状态()
    {
        string mw = Read("src", "FreiAtlas.Settings", "MainWindow.xaml.cs");

        Assert.Contains("snapshotCurrentBuild", mw);
        Assert.Contains("ClientBuildStamp.Compute(gameInfo)", mw);
        Assert.Contains("buildMismatch =", mw);
        Assert.Contains("buildUnknown =", mw);

        // 指纹标记仍保留，但不参与禁用
        Assert.Contains("isStale =", mw);

        // 日志版本号不再作为恢复资格
        Assert.DoesNotContain("versionMismatch =", mw);
    }

    [Fact]
    public void 前端用构建戳标签且不再按版本号禁用恢复()
    {
        string js = Read("src", "FreiAtlas.Settings", "Web", "patcher.js");

        Assert.Contains("const buildMismatch = Boolean(s.buildMismatch);", js);
        Assert.Contains("const buildUnknown = Boolean(s.buildUnknown);", js);
        Assert.Contains("⚠ 构建已变化", js);
        Assert.Contains("待校验", js);

        // 不再按旧版本号字段禁用按钮
        Assert.DoesNotContain("versionMismatch ? \"disabled\"", js);
        Assert.DoesNotContain("stale ? \"disabled\" : \"\"", js);
    }
}
