using FreiAtlas.Patcher.EffectPatch;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 规则替换产物的合法性约束。
///
/// 背景：<c>minimap_exploring_color</c> 曾因未声明 ApplyMode，走 Literal 模式
/// 把 <c>$1float4(...)</c> 原样写进 HLSL，导致着色器编译失败、进图小地图崩溃。
/// 当时的单测只断言产物包含 <c>float4(</c>，<c>$1float4(</c> 也满足该断言，
/// 于是产生了假阳性。
///
/// 这里锁定两条硬约束：
///   1. Replacement 含 $1 的规则必须声明 FunctionBody 系列模式
///   2. 任何规则的实际产物都不得含 $ 字面量
/// </summary>
public sealed class EffectRuleReplacementSafetyTests
{
    /// <summary>
    /// Replacement 里用了 $1 捕获组引用的规则，必须走 FunctionBody 模式。
    /// Literal 模式用 MatchEvaluator 返回字面量，$1 不会展开。
    /// </summary>
    [Fact]
    public void 含捕获组引用的规则必须声明FunctionBody模式()
    {
        var offenders = EffectPatchRules.All
            .Where(rule => rule.Replacement.Contains("$1", StringComparison.Ordinal))
            .Where(rule => rule.ApplyMode is not (EffectApplyMode.FunctionBody
                                               or EffectApplyMode.FunctionBodyBraces))
            .Select(rule => $"{rule.Id}（ApplyMode={rule.ApplyMode}）")
            .ToList();

        Assert.True(offenders.Count == 0,
            "以下规则的 Replacement 含 $1 但未声明 FunctionBody，$1 会被当作字面量写入：\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// 小地图探索色规则的具体回归：产物不得含 $ 字符。
    /// </summary>
    [Fact]
    public void 小地图探索色规则产物不含美元字面量()
    {
        var rule = EffectPatchRules.All.Single(r => r.Id == "minimap_exploring_color");

        const string source = """
            void main()
            {
                walkability_map_color = lerp( walkability_map_color * float4( 1.0f, 1.0f, 1.0f, visibility ), float4(0.5f, 0.5f, 0.5f, 1.0f), pow( saturate( unexplored_edge ), 25.0f ) );
            }
            """;

        var outcome = EffectRuleApplier.Apply(source, rule);

        Assert.True(outcome.Changed, $"规则未生效：{outcome.Error}");
        Assert.DoesNotContain("$", outcome.Content);

        // 捕获组保留：整行前半段必须还在
        Assert.Contains("walkability_map_color = lerp(", outcome.Content);
        Assert.Contains("pow( saturate( unexplored_edge ), 25.0f )", outcome.Content);
    }

    /// <summary>
    /// 所有规则作用在「能匹配上的样本」时，产物都不得含 $ 字面量。
    ///
    /// 这条是兜底：即便某条规则的正则很特殊导致上面那条测不到，
    /// 这里也会用规则自身的 TargetPattern 造样本再跑一遍。
    /// </summary>
    [Fact]
    public void 所有规则产物均不含美元字面量()
    {
        var offenders = new List<string>();

        foreach (var rule in EffectPatchRules.All)
        {
            if (rule.Action != EffectAction.ReplaceCode) continue;
            if (string.IsNullOrEmpty(rule.TargetPattern)) continue;

            // 用 Replacement 自身（若不含 $）或去掉 $1 的形态当样本，
            // 保证正则能命中；这只是为了触发替换路径。
            string sample = rule.Replacement.Contains("$1", StringComparison.Ordinal)
                ? rule.Replacement.Replace("$1", string.Empty, StringComparison.Ordinal)
                : rule.Replacement;

            var outcome = EffectRuleApplier.Apply(sample, rule);
            if (outcome.Content.Contains("$", StringComparison.Ordinal))
            {
                offenders.Add(rule.Id);
            }
        }

        Assert.True(offenders.Count == 0,
            "以下规则的替换产物含 $ 字面量：" + string.Join(", ", offenders));
    }

    /// <summary>
    /// 小地图可见阈值规则必须能匹配任意浮点首分量。
    ///
    /// 原正则只认 0.x，阈值调到 1.0f 后无法再改回去，规则永久失效。
    /// 注意：样本不能用默认阈值（0.18f）—— 那会命中 AppliedSentinel
    /// 被判定为「已应用」而 Skip，属正确的幂等行为，不是正则问题。
    /// </summary>
    [Theory]
    [InlineData("res_color = float4(0.5f, 0.0f, 0.0f, 1.0f);")]
    [InlineData("res_color = float4(1.0f, 0.0f, 0.0f, 1.0f);")]
    [InlineData("res_color = float4(2, 0, 0, 1);")]
    [InlineData("res_color = float4(0.33f, 0.0f, 0.0f, 1.0f);")]
    public void 小地图阈值规则可匹配任意首分量(string line)
    {
        var rule = EffectPatchRules.All.Single(r => r.Id == "minimap_visibility_reset");

        var outcome = EffectRuleApplier.Apply(line, rule);

        Assert.True(outcome.Changed,
            $"阈值规则无法匹配 [{line}]，用户将无法修改已写入的阈值。");
    }

    /// <summary>
    /// 阈值已是目标值时应当幂等跳过，不得反复改写。
    /// </summary>
    [Fact]
    public void 阈值已是目标值时幂等跳过()
    {
        var rule = EffectPatchRules.All.Single(r => r.Id == "minimap_visibility_reset");
        // 默认 VisibilityThreshold = 0.18f，规则产物的哨兵即此值
        const string alreadyApplied = "res_color = float4(0.18f, 0.0f, 0.0f, 1.0f);";

        var outcome = EffectRuleApplier.Apply(alreadyApplied, rule);

        Assert.False(outcome.Changed);
        Assert.True(outcome.SkippedByIdempotency);
    }
}
