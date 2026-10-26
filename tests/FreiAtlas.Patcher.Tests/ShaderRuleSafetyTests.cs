using System.Text.RegularExpressions;
using FreiAtlas.Patcher.EffectPatch;
using Xunit;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 着色器规则「不引入新标识符」的硬约束。
///
/// 守一次真实事故：上一版后处理规则用「整函数替换成 return 常量」的写法，
/// 注入的 SAMPLE_TEX2D / src_sampler / SamplerPointClamp 在 blur.hlsl、
/// depthoffield.hlsl、screenspaceambientocclusion.hlsl 里根本不存在
/// （每个着色器有自己的采样器名与采样函数），着色器编译失败，
/// 客户端启动后直接闪退，连登录界面都进不去。
///
/// 教训：括号配平、JSON 可解析这些「结构校验」拦不住语义错误。
/// 着色器用的是 GGG 私有语法（PIXEL_RETURN_SEMANTIC、SAMPLE_TEX2DLOD_BINDLESS、
/// TEXTURE2D_DECL_BINDLESS），本地没有可用的编译器，只能靠这条静态约束兜底。
/// </summary>
public sealed class ShaderRuleSafetyTests
{
    /// <summary>HLSL 内置关键字与类型，允许出现在替换内容里。</summary>
    private static readonly HashSet<string> HlslBuiltins = new(StringComparer.Ordinal)
    {
        "float", "float2", "float3", "float4", "half", "int", "int2", "int3", "int4",
        "uint", "uint2", "uint3", "uint4", "bool", "void", "return", "true", "false",
        "if", "else", "for", "while", "do", "break", "continue",
        "min", "max", "pow", "abs", "saturate", "lerp", "dot", "length", "normalize",
        "frac", "sqrt", "exp", "log", "sin", "cos", "floor", "ceil", "round", "clamp",
        "step", "smoothstep", "sign", "mad",
    };

    [Fact]
    public void 后处理规则只能改常量不能引入新标识符()
    {
        // 这条测试不依赖真实客户端：它检查的是「替换内容自身」是否符合约束。
        // 替换内容里的每个标识符，必须或者是 HLSL 内置词，或者来自 TargetPattern
        // 所匹配的原文 —— 后者由 EffectPatchRules 的写法保证（只改常量字面量）。
        var rules = EffectPatchRules.All
            .Where(r => r.Feature == "后处理精简")
            .ToList();

        Assert.NotEmpty(rules);

        foreach (var rule in rules)
        {
            var ids = Regex.Matches(rule.Replacement, @"\b([a-zA-Z_][a-zA-Z0-9_]*)\b")
                .Select(m => m.Groups[1].Value)
                .Where(v => !HlslBuiltins.Contains(v))
                .Distinct()
                .ToList();

            // 替换内容里允许出现的标识符，必须能在 TargetPattern 里找到 ——
            // 说明它来自原文，不是凭空造的。
            var invented = ids
                .Where(id => !rule.TargetPattern.Contains(id, StringComparison.Ordinal))
                .ToList();

            Assert.True(invented.Count == 0,
                $"规则 {rule.Id} 的替换内容引入了 TargetPattern 中不存在的标识符：" +
                $"{string.Join(", ", invented)}。这会导致着色器编译失败、客户端闪退。");
        }
    }

    [Fact]
    public void 后处理规则不能注入采样器或采样函数()
    {
        // 采样器名（src_sampler / data_sampler / color_sampler）与采样函数
        // （SAMPLE_TEX2D / SAMPLE_TEX2DLOD_BINDLESS）在每个着色器里都不同，
        // 跨文件套用必然出错。声称改后处理但写入新采样调用的规则一律拦下。
        var forbidden = new[]
        {
            "SAMPLE_TEX2D", "SAMPLE_TEX2DLOD", "SAMPLE_TEX2D_BINDLESS",
            "SAMPLE_TEX2DLOD_BINDLESS", "LOAD_TEX_BINDLESS",
            "src_sampler", "data_sampler", "color_sampler",
            "normal_sampler", "depth_sampler", "framebuffer_sampler",
        };

        foreach (var rule in EffectPatchRules.All.Where(r => r.Feature == "后处理精简"))
        {
            foreach (var f in forbidden)
            {
                Assert.False(rule.Replacement.Contains(f, StringComparison.Ordinal),
                    $"规则 {rule.Id} 的替换内容包含 {f}。后处理规则只允许改常量，" +
                    $"不允许写采样调用 —— 采样器名与采样函数名逐文件不同，套用会编译失败。");
            }
        }
    }

    [Fact]
    public void 后处理规则必须保留PRECOMPILE入口函数名()
    {
        // PRECOMPILE 按函数名编译；改函数签名或改名会让编译失败。
        // 规则只改函数体内的常量，所以 TargetPattern 不该包含函数签名，
        // Replacement 也不该包含新的函数定义。
        foreach (var rule in EffectPatchRules.All.Where(r => r.Feature == "后处理精简"))
        {
            Assert.DoesNotContain("PRECOMPILE", rule.Replacement, StringComparison.Ordinal);
            // 不允许出现函数定义形态（类型 名字 ( ... ) { ）
            Assert.False(Regex.IsMatch(rule.Replacement, @"\w+\s+\w+\s*\([^)]*\)\s*\{"),
                $"规则 {rule.Id} 的替换内容像函数定义，会破坏 PRECOMPILE 入口。");
        }
    }

    [Fact]
    public void 后处理规则数量与预期一致()
    {
        // 从 10 条收缩到 7 条：BuildReflection / ComputeScreenspaceRays /
        // ApplyBloom 三条无法安全地只改常量，已放弃。
        // 数量变化必须显式更新这条断言，防止无意的增删。
        var rules = EffectPatchRules.All.Where(r => r.Feature == "后处理精简").ToList();
        Assert.Equal(7, rules.Count);

        var expected = new[]
        {
            "pp_bloom_cutoff", "pp_bloom_gather", "pp_blur",
            "pp_screenspace_shadows", "pp_depth_minmax",
            "pp_depth_of_field", "pp_ambient_occlusion",
        };
        Assert.Equal(expected.OrderBy(x => x), rules.Select(r => r.Id).OrderBy(x => x));
    }
}
