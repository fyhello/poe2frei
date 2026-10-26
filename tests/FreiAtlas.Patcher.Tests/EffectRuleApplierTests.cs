using FreiAtlas.Patcher.EffectPatch;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 规则应用的替换语义。
///
/// 关键约束来自真实客户端实测：minimap_blending_pixel.hlsl 里
/// res_color.rgb *= res_color.a; 出现 2 次（两个条件分支），
/// 只替换第一处会让补丁半失效。
/// </summary>
public sealed class EffectRuleApplierTests
{
    private const string MultiOccurrenceContent = """
        void main()
        {
        	if( !use_royale_data )
        	{
        		res_color.a *= saturate(1.0f - walkability_sample.b * 2.0f);
        		res_color.rgb *= res_color.a;
        	}
        	else
        	{
        		res_color.rgb *= res_color.a;
        	}
        }
        """;

    private static EffectPatchRule ReplaceRule(string target, string replacement, string? sentinel = null) => new()
    {
        Id = "test_rule",
        Feature = "测试",
        PathPattern = @".*\.hlsl$",
        Action = EffectAction.ReplaceCode,
        TargetPattern = target,
        Replacement = replacement,
        Description = "测试用",
        AppliedSentinel = sentinel
    };

    [Fact]
    public void ReplaceCode必须替换全部匹配处()
    {
        // 实测依据：minimap_blending_pixel.hlsl 的目标串出现 2 次，
        // 分属 if / else 两个分支，只换第一处会导致 else 分支保持原样。
        var rule = ReplaceRule("res_color\\.rgb \\*= res_color\\.a;", "res_color.rgb *= 1.0f;");

        var outcome = EffectRuleApplier.Apply(MultiOccurrenceContent, rule);

        Assert.True(outcome.Changed);
        Assert.DoesNotContain("res_color.rgb *= res_color.a;", outcome.Content);
        Assert.Equal(2, CountOccurrences(outcome.Content, "res_color.rgb *= 1.0f;"));
    }

    [Fact]
    public void ReplaceCode无匹配时不变更()
    {
        var rule = ReplaceRule("不存在的模式", "替换");

        var outcome = EffectRuleApplier.Apply(MultiOccurrenceContent, rule);

        Assert.False(outcome.Changed);
        Assert.Equal(MultiOccurrenceContent, outcome.Content);
    }

    [Fact]
    public void ReplaceCode有幂等特征时跳过()
    {
        const string sentinel = "res_color.rgb *= 1.0f;";
        var alreadyApplied = MultiOccurrenceContent.Replace(
            "res_color.rgb *= res_color.a;", sentinel, StringComparison.Ordinal);
        var rule = ReplaceRule("res_color\\.rgb \\*= res_color\\.a;", sentinel, sentinel);

        var outcome = EffectRuleApplier.Apply(alreadyApplied, rule);

        Assert.True(outcome.SkippedByIdempotency);
        Assert.False(outcome.Changed);
    }

    [Fact]
    public void ClearBlock保留空括号()
    {
        const string input = """
            ParticleEffects
            {
                Particle { name = "fire" }
            }
            """;
        var rule = new EffectPatchRule
        {
            Id = "test_clear",
            Feature = "测试",
            PathPattern = @".*\.ao$",
            Action = EffectAction.ClearBlock,
            TargetPattern = "ParticleEffects",
            Description = "测试用"
        };

        var outcome = EffectRuleApplier.Apply(input, rule);

        Assert.True(outcome.Changed);
        Assert.Equal("ParticleEffects\n{}", outcome.Content.TrimEnd());
    }

    [Fact]
    public void RemoveObject删除JSON键并保持LF格式()
    {
        // 实测 .env 全部 LF + 2 空格缩进，JsonWriter 默认 CRLF，必须显式配置
        const string input = """
            {
              "directional_light": {
                "intensity": 1.0
              },
              "player_light": {
                "intensity": 0.0
              }
            }
            """;
        var rule = new EffectPatchRule
        {
            Id = "test_remove",
            Feature = "测试",
            PathPattern = @".*\.env$",
            Action = EffectAction.RemoveObject,
            TargetPattern = "directional_light",
            Description = "测试用"
        };

        var outcome = EffectRuleApplier.Apply(input, rule);

        Assert.True(outcome.Changed);
        Assert.DoesNotContain("directional_light", outcome.Content);
        Assert.Contains("player_light", outcome.Content);
        // 输出必须是 LF，不能是 CRLF
        Assert.DoesNotContain("\r\n", outcome.Content);
        Assert.Contains("\n", outcome.Content);
    }

    [Fact]
    public void RemoveObject容忍尾随逗号()
    {
        // 实测 861 个 .env 里有 2 个带尾随逗号，不配置 AllowTrailingCommas 会抛异常
        const string input = """
            {
              "directional_light": {
                "cinematic_mode": false,
              },
              "player_light": {
                "intensity": 0.0
              }
            }
            """;
        var rule = new EffectPatchRule
        {
            Id = "test_trailing",
            Feature = "测试",
            PathPattern = @".*\.env$",
            Action = EffectAction.RemoveObject,
            TargetPattern = "directional_light",
            Description = "测试用"
        };

        var outcome = EffectRuleApplier.Apply(input, rule);

        Assert.Null(outcome.Error);
        Assert.True(outcome.Changed);
        Assert.DoesNotContain("directional_light", outcome.Content);
    }

    [Fact]
    public void 按键优先级排序执行()
    {
        // RemoveObject(30) 必须在 ReplaceCode(20) 之后执行。
        // 顺序错会让先删的键导致后续替换目标消失。
        var replace = ReplaceRule("keep = \"a\"", "keep = \"b\"");
        var remove = new EffectPatchRule
        {
            Id = "test_order",
            Feature = "测试",
            PathPattern = @".*\.env$",
            Action = EffectAction.RemoveObject,
            TargetPattern = "directional_light",
            Description = "测试用"
        };

        Assert.True(replace.Action.Priority() < remove.Action.Priority());
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}