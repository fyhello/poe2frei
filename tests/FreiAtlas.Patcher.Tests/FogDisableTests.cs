using System.Text;
using System.Text.Json.Nodes;
using FreiAtlas.Patcher.EffectPatch;
using Xunit;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 环境雾关闭（.fxgraph JSON 节点图改写）的回归测试。
///
/// 守两个真实约束：
///   1. .fxgraph 是 UTF-16LE + **双重 BOM**（FF FE FF FE）。
///      按单 BOM 处理会残留 U+FEFF，JSON 解析直接失败。
///   2. 禁用雾不能删节点，只能换节点内容 —— 下游连线按 type 名引用，
///      删了会让渲染图断裂。
/// </summary>
public sealed class FogDisableTests
{
    /// <summary>构造一个最小的 .fxgraph 结构，含 AlbedoColor(PixelOutput)。</summary>
    private const string SampleJson = """
    {
        "version": 3,
        "nodes": [
            {
                "type": "ConstantFloat",
                "index": 0,
                "parameters": [ { "value": 1.0 } ],
                "ui_position": [ 100.0, 100.0 ]
            },
            {
                "type": "AlbedoColor",
                "index": 0,
                "stage": "Texturing",
                "ui_position": [ 200.0, 200.0 ]
            },
            {
                "type": "AlbedoColor",
                "index": 0,
                "stage": "PixelOutput",
                "ui_position": [ 300.0, 300.0 ]
            }
        ],
        "links": [
            { "src": { "type": "ConstantFloat", "variable": "output" },
              "dst": { "type": "AlbedoColor", "variable": "input" } },
            { "src": { "type": "AlbedoColor", "variable": "output" },
              "dst": { "type": "AutoOutput", "variable": "input" } }
        ]
    }
    """;

    private static EffectPatchRule FogRule() => new()
    {
        Id = "test_fog",
        Feature = "测试",
        PathPattern = @".*\.fxgraph$",
        Action = EffectAction.FogDisable,
        TargetPattern = "AlbedoColor|PixelOutput",
        Replacement = string.Empty,
        Description = "测试用",
        AppliedSentinel = "\"frei_atlas_fog_disabled\""
    };

    [Fact]
    public void 双BOM的fxgraph能正确解码不含残留字符()
    {
        // 真实 .fxgraph 开头是 FF FE FF FE
        var text = SampleJson.Replace("\r\n", "\n");
        var bytes = EffectTextCodec.Encode(text, EffectTextEncoding.Utf16LeWithDoubleBom);

        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xFE, bytes[1]);
        Assert.Equal(0xFF, bytes[2]);
        Assert.Equal(0xFE, bytes[3]);

        var detected = EffectTextCodec.Detect(bytes, "x.fxgraph");
        Assert.Equal(EffectTextEncoding.Utf16LeWithDoubleBom, detected);

        var decoded = EffectTextCodec.Decode(bytes, detected);
        // 关键：解码后首字符必须是 '{'，不能是 U+FEFF
        Assert.Equal('{', decoded[0]);
    }

    [Fact]
    public void 双BOM优先于单BOM判定()
    {
        // FF FE 同时也是单 BOM 的前缀，Detect 必须先判双 BOM
        var doubleBom = new byte[] { 0xFF, 0xFE, 0xFF, 0xFE, 0x7B, 0x00 };
        Assert.Equal(EffectTextEncoding.Utf16LeWithDoubleBom,
            EffectTextCodec.Detect(doubleBom, "x.fxgraph"));

        var singleBom = new byte[] { 0xFF, 0xFE, 0x7B, 0x00 };
        Assert.Equal(EffectTextEncoding.Utf16LeWithBom,
            EffectTextCodec.Detect(singleBom, "x.fxgraph"));
    }

    [Fact]
    public void 关闭雾时保持节点数与连线数不变()
    {
        var before = JsonNode.Parse(SampleJson) as JsonObject;
        int nodesBefore = (before!["nodes"] as JsonArray)!.Count;
        int linksBefore = (before["links"] as JsonArray)!.Count;

        var outcome = EffectRuleApplier.Apply(SampleJson, FogRule());
        Assert.True(outcome.Changed);

        var after = JsonNode.Parse(outcome.Content) as JsonObject;
        // 删节点会让下游连线指向不存在的源，必须保持数量不变
        Assert.Equal(nodesBefore, (after!["nodes"] as JsonArray)!.Count);
        Assert.Equal(linksBefore, (after["links"] as JsonArray)!.Count);
    }

    [Fact]
    public void 只改PixelOutput阶段的AlbedoColor()
    {
        var outcome = EffectRuleApplier.Apply(SampleJson, FogRule());
        var after = JsonNode.Parse(outcome.Content) as JsonObject;
        var nodes = (after!["nodes"] as JsonArray)!;

        // Texturing 阶段的 AlbedoColor 不能被改
        var texturing = nodes.Count(n => n?["type"]?.GetValue<string>() == "AlbedoColor"
                                          && n["stage"]?.GetValue<string>() == "Texturing");
        Assert.Equal(1, texturing);

        // PixelOutput 阶段的应已被换成 ConstantFloat4
        var pixelOutput = nodes.Count(n => n?["type"]?.GetValue<string>() == "AlbedoColor"
                                           && n["stage"]?.GetValue<string>() == "PixelOutput");
        Assert.Equal(0, pixelOutput);

        var transparent = nodes.Count(n => n?["type"]?.GetValue<string>() == "ConstantFloat4");
        Assert.Equal(1, transparent);
    }

    [Fact]
    public void 关闭雾后幂等不重复改写()
    {
        var first = EffectRuleApplier.Apply(SampleJson, FogRule());
        Assert.True(first.Changed);

        var second = EffectRuleApplier.Apply(first.Content, FogRule());
        Assert.True(second.SkippedByIdempotency || !second.Changed);
        Assert.Equal(first.Content, second.Content);
    }

    [Fact]
    public void 没有目标节点时不做改动()
    {
        const string noTarget = """
        {
            "version": 3,
            "nodes": [ { "type": "ConstantFloat", "index": 0 } ],
            "links": []
        }
        """;

        var outcome = EffectRuleApplier.Apply(noTarget, FogRule());
        Assert.False(outcome.Changed);
    }

    [Fact]
    public void 雾规则路径只匹配环境雾不匹配功能性视觉()
    {
        var rule = EffectPatchRules.All.Single(r => r.Id == "fog_env_disable");
        var rx = new System.Text.RegularExpressions.Regex(
            rule.PathPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // 应匹配：5 个纯环境雾
        foreach (var p in new[]
        {
            "metadata/materials/environment/screenspacefog.fxgraph",
            "metadata/materials/environment/screenspacefogenv.fxgraph",
            "metadata/materials/environment/screenspacefogmild.fxgraph",
            "metadata/materials/environment/screenspacefogoverwater.fxgraph",
            "metadata/materials/environment/screenspacefog_cutout.fxgraph",
        })
            Assert.True(rx.IsMatch(p), $"应匹配但未匹配：{p}");

        // 不应匹配：功能性视觉（引导光追踪、Ultimatum 血雾、BOSS 预警、藏身处、遮罩）
        foreach (var p in new[]
        {
            "metadata/effects/graphs/environment/guidingfogflat.fxgraph",
            "metadata/effects/graphs/environment/screenspacefoggroup.fxgraph",
            "metadata/effects/spells/monsters_effects/league_ultimatum/mechanics/graphs/screenspacebloodfogflat.fxgraph",
            "metadata/effects/spells/monsters_effects/act3_four/chaosgodowlboss_cold/graph/custom_screenspacefogflat_dynamicparam.fxgraph",
        })
            Assert.False(rx.IsMatch(p), $"不应匹配但匹配了：{p}");
    }
}
