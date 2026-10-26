using FreiAtlas.Patcher.EffectPatch;
using Xunit;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 着色器整函数替换的边界正确性。
///
/// 这里守的是一个真实事故：早期用 <c>[\s\S]*\z</c> 贪婪匹配函数体，
/// 而 postprocessuber.hlsl 的 ApplyBloom 只有 515 字符、后面还有 37,195 字符的
/// 其他 shader 声明，正则从函数头一路吃到文件末尾，把后半部分全删了。
/// 这种文件打进客户端必然是崩溃。
///
/// 现在的实现用括号配平确定函数右边界，本测试组锁死这个前提。
/// </summary>
public sealed class EffectFunctionBodyTests
{
    /// <summary>构造一条 FunctionBodyBraces 规则。</summary>
    private static EffectPatchRule BraceRule(string signaturePattern) => new()
    {
        Id = "test_brace_rule",
        Feature = "测试",
        PathPattern = @".*test\.hlsl$",
        Action = EffectAction.ReplaceCode,
        TargetPattern = signaturePattern,
        Replacement = "$1\n\t// FreiAtlas: test_brace_rule\n\treturn 0.0f;\n}",
        Description = "测试用",
        AppliedSentinel = "// FreiAtlas: test_brace_rule",
        ApplyMode = EffectApplyMode.FunctionBodyBraces
    };

    [Fact]
    public void 函数体替换不会吃掉后续内容()
    {
        // 目标函数在文件中间，后面还有另一个函数
        const string source =
            "float4 Target( PInput input )\n" +
            "{\n" +
            "\tfloat4 a = 1.0f;\n" +
            "\treturn a;\n" +
            "}\n" +
            "\n" +
            "float4 Other( PInput input )\n" +
            "{\n" +
            "\treturn float4(2.0f, 2.0f, 2.0f, 2.0f);\n" +
            "}\n";

        var rule = BraceRule(@"(float4\s+Target\s*\(\s*PInput\s+input\s*\)\s*\{)");
        var outcome = EffectRuleApplier.Apply(source, rule);

        Assert.True(outcome.Changed);
        Assert.Contains("// FreiAtlas: test_brace_rule", outcome.Content);
        // 后续函数必须原样保留
        Assert.Contains("float4 Other( PInput input )", outcome.Content);
        Assert.Contains("return float4(2.0f, 2.0f, 2.0f, 2.0f);", outcome.Content);
        // 括号必须配平
        Assert.Equal(
            outcome.Content.Count(c => c == '{'),
            outcome.Content.Count(c => c == '}'));
    }

    [Fact]
    public void 函数体内含嵌套大括号时边界仍正确()
    {
        const string source =
            "float4 Target( PInput input )\n" +
            "{\n" +
            "\tfor (int i = 0; i < 3; i++)\n" +
            "\t{\n" +
            "\t\tif (i > 1)\n" +
            "\t\t{\n" +
            "\t\t\ta += 1.0f;\n" +
            "\t\t}\n" +
            "\t}\n" +
            "\treturn a;\n" +
            "}\n" +
            "float4 After( )\n" +
            "{\n" +
            "\treturn 1.0f;\n" +
            "}\n";

        var rule = BraceRule(@"(float4\s+Target\s*\(\s*PInput\s+input\s*\)\s*\{)");
        var outcome = EffectRuleApplier.Apply(source, rule);

        Assert.True(outcome.Changed);
        Assert.Contains("float4 After( )", outcome.Content);
        Assert.Equal(
            outcome.Content.Count(c => c == '{'),
            outcome.Content.Count(c => c == '}'));
    }

    [Fact]
    public void 函数体内含注释中的大括号不干扰配平()
    {
        const string source =
            "float4 Target( PInput input )\n" +
            "{\n" +
            "\t// 这个 } 在注释里，不该被计数\n" +
            "\t/* 这个 { 也在块注释里 */\n" +
            "\treturn 0.0f;\n" +
            "}\n" +
            "float4 After( ) { return 1.0f; }\n";

        var rule = BraceRule(@"(float4\s+Target\s*\(\s*PInput\s+input\s*\)\s*\{)");
        var outcome = EffectRuleApplier.Apply(source, rule);

        Assert.True(outcome.Changed);
        Assert.Contains("float4 After( ) { return 1.0f; }", outcome.Content);
    }

    [Fact]
    public void 字符串字面量里的大括号不干扰配平()
    {
        const string source =
            "float4 Target( PInput input )\n" +
            "{\n" +
            "\tstring s = \"} not a brace\";\n" +
            "\treturn 0.0f;\n" +
            "}\n" +
            "float4 After( ) { return 1.0f; }\n";

        var rule = BraceRule(@"(float4\s+Target\s*\(\s*PInput\s+input\s*\)\s*\{)");
        var outcome = EffectRuleApplier.Apply(source, rule);

        Assert.True(outcome.Changed);
        Assert.Contains("float4 After( ) { return 1.0f; }", outcome.Content);
    }

    [Fact]
    public void 括号不配平时拒绝改动()
    {
        // 缺少闭合大括号：宁可不动，也不能写出坏文件
        const string source =
            "float4 Target( PInput input )\n" +
            "{\n" +
            "\treturn 0.0f;\n";

        var rule = BraceRule(@"(float4\s+Target\s*\(\s*PInput\s+input\s*\)\s*\{)");
        var outcome = EffectRuleApplier.Apply(source, rule);

        Assert.False(outcome.Changed);
        Assert.Equal(source, outcome.Content);
    }

    [Fact]
    public void 二次应用保持幂等()
    {
        const string source =
            "float4 Target( PInput input )\n{\n\treturn 111.0f;\n}\n";

        var rule = BraceRule(@"(float4\s+Target\s*\(\s*PInput\s+input\s*\)\s*\{)");
        var first = EffectRuleApplier.Apply(source, rule);
        Assert.True(first.Changed);

        var second = EffectRuleApplier.Apply(first.Content, rule);
        Assert.True(second.SkippedByIdempotency || !second.Changed);
    }
}
