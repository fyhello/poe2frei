using FreiAtlas.Patcher.EffectPatch.MudMan;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 泥人换档位的回滚约束。
///
/// 背景：<c>MudManApplier.Apply</c> 曾直接从当前客户端索引规划改动
/// （<c>BuildPlan</c> → <c>ClientIndexSession.Open</c>），换档位前不回滚。
/// 结果是从高档位切低档位时，高档位写入的 3.2 万张灰贴图与文本清理
/// 全部滞留，降档形同虚设。
///
/// 这些测试锁定「换档位必须先回滚」的实现约束。由于回滚依赖真实客户端索引，
/// 这里只能做源码级断言 —— 但足以防止后续改动把回滚逻辑删掉。
/// </summary>
public sealed class MudManTierSwitchTests
{
    private static string ReadSource()
    {
        // 从编译产物定位源码目录：测试运行时 cwd 是 bin/Debug/net10.0-windows
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        var path = Path.Combine(
            dir!.FullName, "src", "FreiAtlas.Patcher", "EffectPatch", "MudMan", "MudManApplier.cs");
        Assert.True(File.Exists(path), $"未找到 MudManApplier.cs：{path}");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// Apply 必须在 BuildPlan 之前调用 RestoreSnapshot 做回滚。
    /// </summary>
    [Fact]
    public void Apply在规划前先回滚()
    {
        string source = ReadSource();

        int applyIdx = source.IndexOf("public bool Apply(", StringComparison.Ordinal);
        Assert.True(applyIdx >= 0, "未找到 Apply 方法");

        int planIdx = source.IndexOf("var plan = BuildPlan(", applyIdx, StringComparison.Ordinal);
        Assert.True(planIdx > applyIdx, "未找到 BuildPlan 调用");

        int restoreIdx = source.IndexOf("RestoreSnapshot(", applyIdx, StringComparison.Ordinal);

        Assert.True(restoreIdx > applyIdx,
            "Apply 中必须调用 RestoreSnapshot：换档位前不回滚会让高档位改动滞留。");
        Assert.True(restoreIdx < planIdx,
            "RestoreSnapshot 必须在 BuildPlan 之前：BuildPlan 读的是当前客户端索引，"
            + "回滚晚了规划出来的仍是污染数据。");
    }

    /// <summary>
    /// 回滚只在「已应用且档位不同」时触发，避免同档位重复应用时白白回滚。
    /// </summary>
    [Fact]
    public void 回滚条件判断档位是否变化()
    {
        string source = ReadSource();
        int applyIdx = source.IndexOf("public bool Apply(", StringComparison.Ordinal);
        int planIdx = source.IndexOf("var plan = BuildPlan(", applyIdx, StringComparison.Ordinal);
        string between = source[applyIdx..planIdx];

        Assert.Contains("ReadState()", between);
        Assert.Contains("Tier", between);
        Assert.Contains("needRollback", between);
    }

    /// <summary>
    /// 回滚失败必须中止，不能在污染索引上继续写。
    /// </summary>
    [Fact]
    public void 回滚失败时中止()
    {
        string source = ReadSource();
        int applyIdx = source.IndexOf("public bool Apply(", StringComparison.Ordinal);
        int planIdx = source.IndexOf("var plan = BuildPlan(", applyIdx, StringComparison.Ordinal);
        string between = source[applyIdx..planIdx];

        Assert.Contains("rollback.Success", between);
        Assert.Contains("return false", between);
    }

    /// <summary>
    /// 回滚后规划为空时要清状态文件，否则界面显示「已应用」而实际已回到干净状态。
    /// </summary>
    [Fact]
    public void 回滚后规划为空时清状态()
    {
        string source = ReadSource();
        int planIdx = source.IndexOf("var plan = BuildPlan(", StringComparison.Ordinal);
        int filesIdx = source.IndexOf("var files = plan", planIdx, StringComparison.Ordinal);
        Assert.True(filesIdx > planIdx);

        string between = source[planIdx..filesIdx];
        Assert.Contains("ClearState()", between);
    }
}
