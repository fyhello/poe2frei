namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 配置与客户端数据解耦的回归测试。
///
/// 老问题：四个模块的配置页把「客户端扫描结果」当配置本体，
/// 没扫描就既配不了也提交不了（勾了模块却提示「未选择任何图标」）。
///
/// 现在要求：payload 在扫描列表为空时回落到已保存的规则；
/// 规则也为空时，提示必须告诉用户去哪里配置，而不是一句无从下手的报错。
/// </summary>
public sealed class Stage5RuleBasedConfigTests
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

    private static string ReadJs()
    {
        string path = Path.Combine(RepoRoot(), "src", "FreiAtlas.Settings", "Web", "patcher.js");
        Assert.True(File.Exists(path));
        return File.ReadAllText(path);
    }

    [Theory]
    [InlineData("buildIconPayload", "iconPatchSelectedPaths", "paths")]
    [InlineData("buildChestPayload", "chestItems", "items")]
    [InlineData("buildRumourPayload", "rumourItems", "items")]
    [InlineData("buildColorModPayload", "colorModRules", "rules")]
    public void 模块payload在未扫描时回落到已保存规则(string functionName, string settingsKey, string field)
    {
        string js = ReadJs();

        string body = ExtractFunction(js, functionName);

        // 必须读设置里的规则
        Assert.Contains(settingsKey, body);
        // 扫描列表优先，为空时用已保存规则
        Assert.Contains(".length > 0 ?", body);
        Assert.Contains(field, body);
    }

    /// <summary>
    /// 按大括号配对提取一个 JS 函数体。
    ///
    /// 原先用 "\n  function " 按两空格缩进切片：一旦缩进改成 4 空格，
    /// 切片会一直吞到文件末尾，后面的断言恒真 —— 典型的假通过。
    /// </summary>
    private static string ExtractFunction(string js, string functionName)
    {
        int start = js.IndexOf("function " + functionName, StringComparison.Ordinal);
        Assert.True(start >= 0, $"未找到 {functionName}");

        int brace = js.IndexOf('{', start);
        Assert.True(brace > start, $"{functionName} 缺少函数体");

        int depth = 0;
        for (int i = brace; i < js.Length; i++)
        {
            if (js[i] == '{') depth++;
            else if (js[i] == '}')
            {
                depth--;
                if (depth == 0) return js[start..(i + 1)];
            }
        }

        Assert.Fail($"{functionName} 的大括号未闭合");
        return string.Empty;
    }

    [Fact]
    public void 规则为空时不再拦截批量应用()
    {
        string js = ReadJs();

        // 图标/宝箱/传言/染色都有内置默认规则，空规则必须放行到后端，
        // 否则「勾选即用」永远走不到默认规则分支。
        Assert.DoesNotContain("emptyRuleHints", js);
        Assert.DoesNotContain("还没有配置规则", js);

        // 默认规则由后端兜底，这四个模块的配置提示不应再作为阻断条件出现。
        Assert.Contains("由后端套用内置默认规则", js);
    }

    [Fact]
    public void 染色规则不保存词缀文本()
    {
        string js = ReadJs();

        // 只存规则不存客户端文本：词缀显示名会随汉化/版本变化，
        // 保存下来既不能用于匹配，换语言后还会误导用户。
        int start = js.IndexOf("function buildColorModPayload", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = js.IndexOf("\n  function ", start + 10, StringComparison.Ordinal);
        Assert.True(end > start);
        string body = js[start..end];

        Assert.Contains("text: \"\"", body);
        Assert.DoesNotContain("text: r.text", body);
        // 回放旧规则时同样要抹掉历史文本
        Assert.Contains("saved.map(r => ({ ...r, text: \"\" }))", body);
    }
}
