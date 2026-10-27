using FreiAtlas.Patcher.EffectPatch;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 块定位对单引号字符串的处理。
///
/// 客户端 .ao 的 <c>animations = '[...]'</c> 用单引号包裹一段 JSON 数组，
/// 数组里全是花括号。定位器若不识别单引号，就会把字符串内的括号算进层级。
/// 实测客户端抽查 3001 个 .ao/.ot，1242 个（41.4%）的单引号串里含花括号。
///
/// 错位的后果不是写坏文件（定位失败时调用方会放弃改动），而是规则静默失效：
/// 用户勾了补丁、程序报成功，客户端却没有任何变化。
/// </summary>
public sealed class SingleQuoteBraceTests
{
    private static HashSet<string> Names(params string[] names)
        => new(names, StringComparer.Ordinal);

    /// <summary>
    /// 单引号串内的花括号不参与层级计算。
    ///
    /// 这是最有鉴别力的场景：串内 "{" 与 "}" 数量不对称（这里多一个 "{"）。
    /// 若把括号算进层级，深度会多出 1 且永远回不到 0，块定位直接返回失败。
    /// 数量对称的用例无法暴露缺陷 —— 深度最后仍会配平。
    /// </summary>
    [Fact]
    public void 单引号串内括号数量不对称时仍能正确定位()
    {
        const string content = "client\n{\n    animations = '[{\"name\": \"open\"}'\n    inner_value = 1\n}\n";

        var blocks = EffectBlockScanner.FindBlocks(content, Names("client"));

        Assert.Single(blocks);
        string body = content.Substring(blocks[0].OpenBracePos, blocks[0].CloseBracePos - blocks[0].OpenBracePos + 1);
        Assert.Contains("inner_value", body);
    }

    /// <summary>
    /// 双引号与单引号混排时各自配对，互不干扰。
    ///
    /// 若实现只记录「在字符串内」而不记录是哪一种引号，
    /// 双引号串里的单引号会把状态提前结束，后续字符被当代码扫描。
    /// </summary>
    [Fact]
    public void 两种引号各自配对互不干扰()
    {
        const string content = "client\n{\n    a = \"含 ' 单引号的双引号串\"\n    b = '含 \" 双引号的单引号串'\n    inner_value = 1\n}\n";

        var blocks = EffectBlockScanner.FindBlocks(content, Names("client"));

        Assert.Single(blocks);
        string body = content.Substring(blocks[0].OpenBracePos, blocks[0].CloseBracePos - blocks[0].OpenBracePos + 1);
        Assert.Contains("inner_value", body);
    }

    /// <summary>
    /// 双引号内的转义引号不应提前结束字符串状态。
    /// </summary>
    [Fact]
    public void 双引号内的转义引号不提前结束字符串()
    {
        const string content = "client\n{\n    name = \"a \\\" b\"\n    inner_value = 1\n}\n";

        var blocks = EffectBlockScanner.FindBlocks(content, Names("client"));

        Assert.Single(blocks);
        string body = content.Substring(blocks[0].OpenBracePos, blocks[0].CloseBracePos - blocks[0].OpenBracePos + 1);
        Assert.Contains("inner_value", body);
    }

    /// <summary>
    /// 清空块内容时，单引号串里的花括号不能把闭合位置算错，
    /// 否则会连后面的兄弟节点一起删掉。
    /// </summary>
    [Fact]
    public void 清空块不吞掉后续兄弟节点()
    {
        const string content = "client\n{\n    animations = '[{\"a\": {\"b\": 1}}]'\n}\nsibling\n{\n    keep = 1\n}\n";

        string cleared = EffectBlockScanner.ClearBlockContents(content, Names("client"));

        Assert.Contains("sibling", cleared);
        Assert.Contains("keep = 1", cleared);
        Assert.DoesNotContain("animations", cleared);
    }
}
