using FreiAtlas.Patcher.EffectPatch;
using Xunit;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 语法门禁的回归测试。
///
/// 守一个真实事故：校验器按「双引号不能跨行」判断，把 179 个合法 .ot 文件
/// 误报为坏文件，整批尸体清理规则被中止。而 PoE 的 .ot 语法允许这种写法：
///
///     define_shared_state =
///     "
///         weapon_health = 3;
///         emerged;
///     "
///
/// 门禁宁严不宁松，但「严」必须严在对的地方 —— 误报和漏报一样是故障。
/// </summary>
public sealed class SyntaxValidatorTests
{
    [Fact]
    public void 双引号跨行是合法语法()
    {
        // 实测 metadata/monsters/blackjawsuperunique/blackjawstandalone.ot 第 13-18 行
        const string source =
            "StateMachine\n" +
            "{\n" +
            "\tdefine_shared_state =\n" +
            "\t\"\n" +
            "\t\tweapon_health = 3;\n" +
            "\t\tout_of_breath = 1;\n" +
            "\t\temerged;\n" +
            "\t\tdeath;\n" +
            "\t\"\n" +
            "}\n";

        var result = EffectSyntaxValidator.ValidateFor(source, "test.ot");
        Assert.True(result.IsValid, result.Error);
    }

    [Fact]
    public void 单引号包裹的多行JSON不影响括号配平()
    {
        // .ao 里常见写法：animations = '[ { "name": "once" } ]'
        const string source =
            "Animated\n" +
            "{\n" +
            "\tanimations = '[\n" +
            "\t\t{ \"name\": \"once\", \"loop\": false }\n" +
            "\t]'\n" +
            "}\n";

        var result = EffectSyntaxValidator.ValidateFor(source, "test.ao");
        Assert.True(result.IsValid, result.Error);
    }

    [Fact]
    public void 真正未闭合的双引号仍被拦住()
    {
        const string source =
            "Life\n" +
            "{\n" +
            "\ton_death = \"这里忘了闭合\n" +
            "}\n";

        var result = EffectSyntaxValidator.ValidateFor(source, "test.ot");
        Assert.False(result.IsValid);
        Assert.Contains("双引号", result.Error);
    }

    [Fact]
    public void 大括号不配平被拦住()
    {
        const string source =
            "Life\n" +
            "{\n" +
            "\ton_death = { RemoveEffects(); }\n";
        // 少一个 }

        var result = EffectSyntaxValidator.ValidateFor(source, "test.ot");
        Assert.False(result.IsValid);
        Assert.Contains("大括号", result.Error);
    }

    [Fact]
    public void 多余的右括号被拦住()
    {
        const string source =
            "Life\n" +
            "{\n" +
            "}\n" +
            "}\n";

        var result = EffectSyntaxValidator.ValidateFor(source, "test.ot");
        Assert.False(result.IsValid);
        Assert.Contains("多余", result.Error);
    }

    [Fact]
    public void 注释里的括号不参与配平()
    {
        const string source =
            "Life\n" +
            "{\n" +
            "\t// 这个 } 在注释里\n" +
            "\t/* 这个 { 也在块注释里 */\n" +
            "\ton_death = { RemoveEffects(); }\n" +
            "}\n";

        var result = EffectSyntaxValidator.ValidateFor(source, "test.ot");
        Assert.True(result.IsValid, result.Error);
    }

    [Fact]
    public void HLSL入口函数缺失被拦住()
    {
        // PRECOMPILE 声明了 BloomCutoff，但函数体被误删
        const string source =
            "//PRECOMPILE ps_all BloomCutoff\n" +
            "CBUFFER_BEGIN(x)\n" +
            "CBUFFER_END\n";

        var result = EffectSyntaxValidator.ValidateFor(source, "test.hlsl");
        Assert.False(result.IsValid);
        Assert.Contains("BloomCutoff", result.Error);
    }

    [Fact]
    public void HLSL入口函数存在时通过()
    {
        const string source =
            "//PRECOMPILE ps_all BloomCutoff\n" +
            "float4 BloomCutoff( PInput input ) : PIXEL_RETURN_SEMANTIC\n" +
            "{\n" +
            "\t// FreiAtlas: pp_bloom_cutoff\n" +
            "\treturn 0.0f;\n" +
            "}\n";

        var result = EffectSyntaxValidator.ValidateFor(source, "test.hlsl");
        Assert.True(result.IsValid, result.Error);
    }

    [Fact]
    public void HLSL的CRLF行尾不影响PRECOMPILE解析()
    {
        // 实测 .hlsl 是 CRLF；不 Trim 行尾会让正则匹配不到函数名
        const string source =
            "//PRECOMPILE ps_all BloomCutoff\r\n" +
            "float4 BloomCutoff( PInput input ) : PIXEL_RETURN_SEMANTIC\r\n" +
            "{\r\n" +
            "\treturn 0.0f;\r\n" +
            "}\r\n";

        var result = EffectSyntaxValidator.ValidateFor(source, "test.hlsl");
        Assert.True(result.IsValid, result.Error);
    }
}
