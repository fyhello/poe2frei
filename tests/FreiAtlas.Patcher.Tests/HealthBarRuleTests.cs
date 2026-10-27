using FreiAtlas.Patcher.EffectPatch;
using FreiAtlas.Patcher.EffectPatch.HealthBar;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 头顶血条规则的行为验证。
///
/// 这三条规则把 <c>hide_mini_life_bar</c> 系列字段从隐藏改为显示。
/// 此前只有「规则结构」层面的测试（正则可编译、ID 唯一），没有验证
/// 规则作用在真实 .ot 文本上是否产生预期结果 —— 而正则写错的后果是
/// 补丁无效或误伤文件结构，结构测试发现不了。
///
/// 这里用构造的 .ot 片段锁定三类行为：正确改写、保留缩进、幂等。
/// </summary>
public sealed class HealthBarRuleTests
{
    private static EffectPatchRule RuleOf(string id)
        => HealthBarRules.All.Single(r => r.Id == id);

    /// <summary>
    /// 三条规则必须覆盖 hide_mini_life_bar 的三种声明形式。
    /// 少一条就会漏改对应的一批怪物（官方注释标明分别为 42 / 8 / 3 个文件）。
    /// </summary>
    [Fact]
    public void 三条规则覆盖三种声明形式()
    {
        Assert.Equal(3, HealthBarRules.All.Count);

        var ids = HealthBarRules.All.Select(r => r.Id).ToList();
        Assert.Contains("healthbar_mini_visible", ids);
        Assert.Contains("healthbar_base_visible", ids);
        Assert.Contains("healthbar_set_base_visible", ids);

        // 全部作用于 .ot，且都走 FunctionBody 模式（否则 $1 不会被展开）
        Assert.All(HealthBarRules.All, rule =>
        {
            Assert.Equal(@".*\.ot$", rule.PathPattern);
            Assert.Equal(EffectApplyMode.FunctionBody, rule.ApplyMode);
            Assert.Equal(EffectAction.ReplaceCode, rule.Action);
        });
    }

    /// <summary>
    /// hide_mini_life_bar = true 必须被改成 false。
    /// </summary>
    [Fact]
    public void 隐藏标记改为显示()
    {
        const string source = """
            client
            {
                Render
                {
                    hide_mini_life_bar = true
                }
            }
            """;

        var outcome = EffectRuleApplier.Apply(source, RuleOf("healthbar_mini_visible"));

        Assert.True(outcome.Changed, $"规则未生效：{outcome.Error}");
        Assert.Contains("hide_mini_life_bar = false", outcome.Content);
        Assert.DoesNotContain("hide_mini_life_bar = true", outcome.Content);
    }

    /// <summary>
    /// base_ 与 set_base_ 两种前缀都要改。
    /// 它们在 Stats 块里声明同一开关，效果等价。
    /// </summary>
    [Theory]
    [InlineData("healthbar_base_visible", "base_hide_mini_life_bar")]
    [InlineData("healthbar_set_base_visible", "set_base_hide_mini_life_bar")]
    public void Stats块前缀标记改为显示(string ruleId, string field)
    {
        // $$""" 让 {{ }} 成为插值边界，{ } 直接作为内容，避免手写大括号转义。
        string source = $$"""
            Stats
            {
                {{field}} = 1
            }
            """;

        var outcome = EffectRuleApplier.Apply(source, RuleOf(ruleId));

        Assert.True(outcome.Changed, $"{ruleId} 未生效：{outcome.Error}");
        Assert.Contains($"{field} = 0", outcome.Content);
        Assert.DoesNotContain($"{field} = 1", outcome.Content);
    }

    /// <summary>
    /// 缩进与行尾空白必须原样保留。
    ///
    /// 正则里的 ${1} 和 ${2} 就是为此存在的：若写成固定缩进，
    /// 嵌套在不同层级的文件会被改坏缩进，后续解析可能失败。
    /// </summary>
    [Fact]
    public void 保留原有缩进与行尾空白()
    {
        const string source = "        hide_mini_life_bar = true   \n";

        var outcome = EffectRuleApplier.Apply(source, RuleOf("healthbar_mini_visible"));

        Assert.True(outcome.Changed);
        Assert.Equal("        hide_mini_life_bar = false   \n", outcome.Content);
    }

    /// <summary>
    /// 幂等：已经改成 false 的文件再次应用不应产生变化。
    ///
    /// 补丁重复应用是常见操作（用户可能反复点应用），
    /// 若规则不幂等会导致文件被反复改写。
    /// </summary>
    [Fact]
    public void 已改写的文件再次应用无变化()
    {
        const string alreadyFixed = """
            client
            {
                Render
                {
                    hide_mini_life_bar = false
                }
            }
            """;

        var outcome = EffectRuleApplier.Apply(alreadyFixed, RuleOf("healthbar_mini_visible"));

        Assert.False(outcome.Changed);
        Assert.Equal(alreadyFixed, outcome.Content);
    }

    /// <summary>
    /// 不含该字段的文件必须原样返回，不能被误改。
    ///
    /// 客户端 8,281 个 .ot 里只有 53 个声明了该字段，
    /// 其余文件必须零改动。
    /// </summary>
    [Fact]
    public void 不含目标字段的文件不被改动()
    {
        const string unrelated = """
            client
            {
                Render
                {
                    some_other_field = true
                }
            }
            """;

        var outcome = EffectRuleApplier.Apply(unrelated, RuleOf("healthbar_mini_visible"));

        Assert.False(outcome.Changed);
        Assert.Equal(unrelated, outcome.Content);
    }

    /// <summary>
    /// 规则不能碰顶部大血条相关字段。
    ///
    /// boss_life_bar 与 MonsterVarieties.BossHealthBar 控制的是屏幕顶部大血条，
    /// 与头顶血条是两套独立机制。误改会让 Boss 血条显示异常。
    /// </summary>
    [Fact]
    public void 不触碰顶部大血条字段()
    {
        const string bossBar = """
            client
            {
                boss_life_bar = false
                MonsterVarieties_BossHealthBar = 0
            }
            """;

        var outcome = EffectRuleApplier.Apply(bossBar, RuleOf("healthbar_mini_visible"));

        Assert.False(outcome.Changed);
        Assert.Contains("boss_life_bar = false", outcome.Content);
        Assert.Contains("MonsterVarieties_BossHealthBar = 0", outcome.Content);
    }

    /// <summary>
    /// 同一文件里三种形式同时出现时，三条规则各改各的，互不干扰。
    /// </summary>
    [Fact]
    public void 三种形式并存时全部改写()
    {
        const string mixed = """
            client
            {
                Render
                {
                    hide_mini_life_bar = true
                }
            }
            Stats
            {
                base_hide_mini_life_bar = 1
                set_base_hide_mini_life_bar = 1
            }
            """;

        var (content, outcomes) = EffectRuleApplier.ApplyAll(mixed, HealthBarRules.All);

        Assert.Equal(3, outcomes.Count);
        Assert.All(outcomes, o => Assert.True(o.Outcome.Changed, $"{o.Rule.Id} 未生效：{o.Outcome.Error}"));
        Assert.Contains("hide_mini_life_bar = false", content);
        Assert.Contains("base_hide_mini_life_bar = 0", content);
        Assert.Contains("set_base_hide_mini_life_bar = 0", content);
    }

    /// <summary>
    /// MayApply 只放行 .ot 文件。
    ///
    /// 该方法用于规划阶段提前过滤：客户端 8,281 个 .ot 里只有 53 个声明了
    /// 该字段，其余文件无需读取内容。若判断写错会导致漏读或多读。
    /// </summary>
    [Theory]
    [InlineData("metadata/monsters/bossbase.ot", true)]
    [InlineData("metadata/monsters/act1/zombie.ot", true)]
    [InlineData("METADATA/MONSTERS/UPPER.OT", true)]
    [InlineData("metadata/monsters/bossbase.ao", false)]
    [InlineData("data/balance/baseitemtypes.datc64", false)]
    [InlineData("", false)]
    public void MayApply只放行ot文件(string path, bool expected)
    {
        Assert.Equal(expected, HealthBarRules.MayApply(path));
    }

    /// <summary>
    /// 传入 null 不能抛异常。规划阶段路径来源多样，可能为空。
    /// </summary>
    [Fact]
    public void MayApply对null返回false()
    {
        Assert.False(HealthBarRules.MayApply(null!));
    }
}
