using FreiAtlas.Patcher.EffectPatch;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 语法门禁。
///
/// 这是写入前的最后一道检查。校验器本身出错会导致两种后果：
///   - 误判为不合法 → 补丁写不进去（本次踩过）
///   - 误判为合法 → 坏内容写进客户端（更严重）
/// 所以校验器自身的正确性必须测到。
/// </summary>
public sealed class EffectSyntaxValidatorTests
{
    [Fact]
    public void 括号配平的文件通过校验()
    {
        const string text = """
            version 3
            Actor
            {
            	name = "x"
            }
            """;

        var result = EffectSyntaxValidator.Validate(text, "test.ot");
        Assert.True(result.IsValid, result.Error);
    }

    [Fact]
    public void 缺少闭括号被拒绝()
    {
        const string text = """
            Actor
            {
            	name = "x"
            """;

        var result = EffectSyntaxValidator.Validate(text, "test.ot");
        Assert.False(result.IsValid);
        Assert.Contains("大括号未配平", result.Error);
    }

    [Fact]
    public void 多余闭括号被拒绝()
    {
        const string text = "Actor\n{\n}\n}\n";

        var result = EffectSyntaxValidator.Validate(text, "test.ot");
        Assert.False(result.IsValid);
        Assert.Contains("多余的", result.Error);
    }

    [Fact]
    public void 字符串内的括号不计入配平()
    {
        const string text = """
            Actor
            {
            	on_death = "RemoveEffects(); DisableRendering();"
            	note = "这里有个 } 但它在字符串里"
            }
            """;

        var result = EffectSyntaxValidator.Validate(text, "test.ot");
        Assert.True(result.IsValid, result.Error);
    }

    [Fact]
    public void 注释内的括号不计入配平()
    {
        const string text = """
            Actor
            {
            	// 这里的 } 是注释
            	/* 这里的 { 也是注释 */
            	name = "x"
            }
            """;

        var result = EffectSyntaxValidator.Validate(text, "test.ot");
        Assert.True(result.IsValid, result.Error);
    }

    [Fact]
    public void 空内容被拒绝()
    {
        var result = EffectSyntaxValidator.Validate("", "test.ot");
        Assert.False(result.IsValid);
    }

    [Fact]
    public void HLSL入口函数存在时通过()
    {
        // 关键回归：文件是 CRLF 换行，PRECOMPILE 行分割后函数名可能带 \r。
        // 之前 TrimStart 只去行首空白，导致提取出 "BloomCutoff\r"，正则永远匹配失败。
        const string text = "//PRECOMPILE ps_all BloomCutoff\r\n\r\nfloat4 BloomCutoff( PInput input ) : PIXEL_RETURN_SEMANTIC\r\n{\r\n\treturn 0.0f;\r\n}\r\n";

        var result = EffectSyntaxValidator.ValidateHlslEntryPoints(text, "shaders/bloomcutoff.hlsl");
        Assert.True(result.IsValid, result.Error);
    }

    [Fact]
    public void HLSL入口函数缺失时拒绝()
    {
        const string text = "//PRECOMPILE ps_all BloomCutoff\r\n\r\nfloat4 SomethingElse( PInput input )\r\n{\r\n}\r\n";

        var result = EffectSyntaxValidator.ValidateHlslEntryPoints(text, "shaders/bloomcutoff.hlsl");
        Assert.False(result.IsValid);
        Assert.Contains("BloomCutoff", result.Error);
    }

    [Fact]
    public void 桩函数形态下入口函数仍被认为存在()
    {
        // 这是实际打补丁后的形态：名称被桩函数占用，原实现改名保留。
        // PRECOMPILE 引用的名字仍然能找到，校验应通过。
        const string text = """
            //PRECOMPILE ps_all BloomCutoff

            float4 BloomCutoff( PInput input ) : PIXEL_RETURN_SEMANTIC
            {
              return 0.0f;
            }
            float4 BloomCutoffDisabled( PInput input ) : PIXEL_RETURN_SEMANTIC
            {
            	return color;
            }
            """;

        var result = EffectSyntaxValidator.ValidateHlslEntryPoints(text, "shaders/bloomcutoff.hlsl");
        Assert.True(result.IsValid, result.Error);
    }
}