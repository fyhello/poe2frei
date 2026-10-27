using FreiAtlas.Patcher.EffectPatch;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 补丁幂等性。
///
/// ReplaceCode 会插入新内容，在已打过补丁的文件上重复应用会叠加。
/// 实测 bloomcutoff.hlsl 两次应用后从 1,215 字节涨到 1,298 字节。
/// 每条 ReplaceCode 规则必须带幂等特征串，检测到就跳过。
/// </summary>
public sealed class EffectPatchIdempotencyTests
{
    [Fact]
    public void 所有ReplaceCode规则必须有幂等特征或明确豁免()
    {
        var missing = EffectPatchRules.All
            .Where(r => r.Action == EffectAction.ReplaceCode)
            .Where(r => r.AppliedSentinel is null)
            // 整文件覆盖型与 JSON 数值直接覆盖型规则天然幂等：重复应用结果不变
            .Where(r => r.Id is not ("all_particles" or "spell_effect_packages" or "dark_exposure" or "dark_light_enhance"
                or "fog2_worldmap_blend" or "fog2_worldmap_min" or "fog2_worldmap_max"))
            .Select(r => r.Id)
            .ToList();

        Assert.True(missing.Count == 0,
            $"以下 ReplaceCode 规则缺少幂等特征，重复应用会叠加：{string.Join(", ", missing)}");
    }

    [Fact]
    public void 幂等特征不能是过于宽泛的单词()
    {
        // 反例：只用 "return 0.0f;" 会误判到其他函数的同名语句。
        // 特征串必须带上足够的上下文（函数签名、注入语句与闭合括号的完整形态）。
        foreach (var rule in EffectPatchRules.All.Where(r => r.AppliedSentinel is not null))
        {
            var sentinel = rule.AppliedSentinel!;
            Assert.True(sentinel.Length >= 15,
                $"规则 {rule.Id} 的幂等特征过短（{sentinel.Length} 字符），可能误判");
        }
    }

    [Fact]
    public void 幂等特征必须出现在替换内容中()
    {
        // 特征串的目的就是「替换后能在文件里被找到」。
        // 如果替换内容里不含它，检测永远失败，幂等保护形同虚设。
        //
        // 只对文本替换类动作适用。FogDisable 改的是 JSON 节点图，
        // Replacement 为空，标记由代码写进顶层字段，不经过 Replacement。
        foreach (var rule in EffectPatchRules.All
                     .Where(r => r.AppliedSentinel is not null)
                     .Where(r => r.Action is EffectAction.ReplaceCode
                                        or EffectAction.AppendToBlock))
        {
            var normalized = rule.Replacement.Replace("\r\n", "\n");
            var sentinel = rule.AppliedSentinel!.Replace("\r\n", "\n");
            Assert.True(normalized.Contains(sentinel, StringComparison.Ordinal),
                $"规则 {rule.Id} 的幂等特征未出现在替换内容中，检测会永远失败");
        }
    }

    [Fact]
    public void 消除型动作无需幂等特征而追加型必须有()
    {
        // ClearBlock / RemoveObject 是「消除型」：块已空再清空结果不变，
        // 键已删再删无效果 —— 天然幂等，不需要特征串。
        //
        // AppendToBlock 是「追加型」：重复运行会不断往上叠内容，
        // 必须有特征串来拦截第二次应用。
        var clearOrRemove = EffectPatchRules.All
            .Where(r => r.Action is EffectAction.ClearBlock or EffectAction.RemoveObject)
            .ToList();
        Assert.NotEmpty(clearOrRemove);
        Assert.All(clearOrRemove, r => Assert.Null(r.AppliedSentinel));

        var appends = EffectPatchRules.All
            .Where(r => r.Action == EffectAction.AppendToBlock)
            .ToList();
        Assert.NotEmpty(appends);
        Assert.All(appends, r => Assert.False(string.IsNullOrWhiteSpace(r.AppliedSentinel)));
    }

    [Fact]
    public void 重复应用检测逻辑正确()
    {
        // 模拟：原文 → 应用一次 → 再应用一次，第二次应被检测拦截
        var rule = EffectPatchRules.All.Single(r => r.Id == "dark_bloom_cutoff");
        var sentinel = rule.AppliedSentinel!;

        const string original = """
            float4 BloomCutoff( PInput input ) : PIXEL_RETURN_SEMANTIC
            {
            	return color;
            }
            """;

        // 未应用过
        Assert.DoesNotContain(sentinel, original);

        // 首次应用后
        var first = EffectRuleApplier.Apply(original, rule);
        Assert.True(first.Changed);
        var applied = first.Content;

        // 施加后应能检测到特征
        bool detected = applied.Replace("\r\n", "\n").Contains(
            sentinel.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.True(detected, $"替换后未检测到幂等特征，特征设计有误。替换结果：\n{applied}");

        // 第二次应用应被幂等拦截
        var second = EffectRuleApplier.Apply(applied, rule);
        Assert.True(second.SkippedByIdempotency);
        Assert.False(second.Changed);
    }
}