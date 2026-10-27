using FreiAtlas.Core.Settings;
using FreiAtlas.Patcher.EffectPatch;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 特效补丁规则集的结构约束。
///
/// 规则直接改游戏脚本与着色器，语法出错会让客户端崩溃。这里固化几条硬约束，
/// 防止后续改动引入危险规则。
/// </summary>
public sealed class EffectPatchRuleTests
{
    [Fact]
    public void 规则集非空且ID唯一()
    {
        var rules = EffectPatchRules.All;

        Assert.NotEmpty(rules);
        var ids = rules.Select(r => r.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void 每条规则的路径正则可编译()
    {
        foreach (var rule in EffectPatchRules.All)
        {
            var ex = Record.Exception(() => new System.Text.RegularExpressions.Regex(rule.PathPattern));
            Assert.True(ex is null, $"{rule.Id} 的路径正则无效：{ex?.Message}");
        }
    }

    [Fact]
    public void 每条规则的目标正则可编译()
    {
        foreach (var rule in EffectPatchRules.All)
        {
            var ex = Record.Exception(() => new System.Text.RegularExpressions.Regex(rule.TargetPattern));
            Assert.True(ex is null, $"{rule.Id} 的目标正则无效：{ex?.Message}");
        }
    }

    [Fact]
    public void ReplaceCode规则必须有替换内容()
    {
        // ReplaceCode 若替换为空，等价于把匹配内容删掉。
        // ClearBlock 与 RemoveObject 本来就不用替换内容，不在此列。
        var emptyReplacements = EffectPatchRules.All
            .Where(r => r.Action == EffectAction.ReplaceCode && string.IsNullOrEmpty(r.Replacement))
            .Select(r => r.Id)
            .ToList();

        // 清空特效包（.epk）是刻意置空：把文件内容整体清空即达到移除特效的目的。
        Assert.All(emptyReplacements, id =>
            Assert.True(id is "spell_effect_packages",
                $"规则 {id} 的替换内容为空，请确认是否为刻意删除"));
    }

    [Fact]
    public void 高危规则必须显式标记()
    {
        // 一键全清与移除音效影响范围过大，必须标记为高危，界面默认不勾选
        var dangerous = EffectPatchRules.All.Where(r => r.IsDangerous).Select(r => r.Id).ToList();

        Assert.Contains("all_particles", dangerous);
        Assert.Contains("sound_events", dangerous);
    }

    [Fact]
    public void 依赖ao或aoc的规则必须同时兼容两种扩展名()
    {
        // 实测当前 PoE2 客户端里 .aoc 数量为 0，已并入 .ao（73,548 个）。
        // 规则若只写 .aoc 会完全失效，必须两种扩展名都覆盖。
        // 路径正则写作 \.(ao|aoc)$，扩展名在括号里，所以按 (ao|aoc) 判断
        var targeted = EffectPatchRules.All
            .Where(r => r.PathPattern.Contains("ao|aoc", StringComparison.OrdinalIgnoreCase)
                        || r.PathPattern.Contains("aoc|ao", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(targeted);
        foreach (var rule in targeted)
        {
            // 缺少 ao 分支说明只写了 .aoc，在当前客户端会完全失效
            Assert.Contains("ao", rule.PathPattern);
        }
    }

    [Fact]
    public void 音效规则必须排除商城外观与NPC()
    {
        // 全量 metadata/ 会命中 7.3 万个文件，把环境音、NPC 台词、地形音效全部清空，
        // 游戏会变成默片。metadata/effects/microtransactions 更是玩家付费购买的商城外观，
        // 清掉音效等于破坏已购内容。路径必须收紧。
        var rule = EffectPatchRules.All.Single(r => r.Id == "sound_events");

        Assert.DoesNotContain("metadata/.*", rule.PathPattern);
        Assert.Contains("effects/spells", rule.PathPattern);
        Assert.Contains("monsters", rule.PathPattern);
        Assert.DoesNotContain("microtransactions", rule.PathPattern);
    }

    [Fact]
    public void 复活规则正则必须兼容true和false()
    {
        // 实测 monster.ot 里是 slow_animations_go_to_idle = false，
        // 易刷规则写死 true 导致匹配 0 处。正则必须兼容两种取值。
        var rule = EffectPatchRules.All.Single(r => r.Id == "monster_dead_revive");

        Assert.Contains("true|false", rule.TargetPattern);
    }

    [Fact]
    public void 动作优先级中替换先于删键()
    {
        // 同一文件上 ReplaceCode 必须先于 RemoveObject，否则先删键会让
        // 后面的替换目标消失。顺序与易刷能力包一致。
        Assert.True(EffectAction.ReplaceCode.Priority() < EffectAction.RemoveObject.Priority());
        Assert.Equal(EffectAction.ReplaceCode.Priority(), EffectAction.ClearBlock.Priority());
    }

    [Fact]
    public void 技能特效规则必须带保护名单()
    {
        // 保护名单防止传送门、黑洞等关键特效被误删。
        // 缺了它会让游戏无法正常游玩。
        var spellRules = EffectPatchRules.All
            .Where(r => r.Id.StartsWith("spell_", StringComparison.Ordinal) || r.Id == "all_particles")
            .ToList();

        Assert.NotEmpty(spellRules);
        foreach (var rule in spellRules)
        {
            Assert.Contains("portal", rule.PathPattern);
            Assert.Contains("black_hole", rule.PathPattern);
            Assert.Contains("ground_effects", rule.PathPattern);
        }
    }

    [Fact]
    public void 功能分组覆盖全部规则()
    {
        var features = EffectPatchRules.Features;
        var covered = features.Sum(f => f.Rules.Count);

        Assert.Equal(EffectPatchRules.All.Count, covered);
    }

    [Fact]
    public void 怪物死亡规则必须成对存在()
    {
        // 只注入死亡回调而不注入复活回调，会让复活怪变成隐形。
        //
        // 尸体清理现在是三级方案：
        //   monster_dead_base   改基类 monster.ot 的空 Life（覆盖 2028 个继承者）
        //   monster_dead_child  给自带 Life 业务的怪物追加事件（覆盖 703 个）
        //   monster_dead_revive 复活时恢复渲染
        var ids = EffectPatchRules.All.Select(r => r.Id).ToList();

        Assert.Contains("monster_dead_base", ids);
        Assert.Contains("monster_dead_child", ids);
        Assert.Contains("monster_dead_revive", ids);
    }

    [Fact]
    public void 尸体清理规则必须覆盖基类与子类两条路径()
    {
        // 实测 metadata/monsters 下 2,806 个 .ot：
        //   2,028 个 extends monster.ot 且不定义 Life → 靠基类规则
        //     673 个自己写了非空 Life              → 靠子类追加规则
        // 只做其中一条会漏掉大部分怪物，清尸体效果大打折扣。
        var feature = EffectPatchRules.Features.Single(f => f.Name == "怪物死亡特效移除");
        var baseRule = feature.Rules.Single(r => r.Id == "monster_dead_base");
        var childRule = feature.Rules.Single(r => r.Id == "monster_dead_child");

        // 基类规则只打 monster.ot 本体
        Assert.Contains("monster\\.ot", baseRule.PathPattern);
        // 子类规则要覆盖所有 .ot
        Assert.Contains(".*\\.ot", childRule.PathPattern);
        // 子类必须用追加而不是替换，否则会破坏怪物自带的 Life 业务逻辑
        Assert.Equal(EffectAction.AppendToBlock, childRule.Action);
    }

    [Fact]
    public void 追加到块必须跳过已存在的键()
    {
        // 实测 673 个非空 Life 里，494 个已经写了 on_death、195 个有 on_spawned_dead。
        // 盲目追加会产生重复键，GEAL 对重复键的行为未定义。
        const string source =
            "Life\n{\n\ton_death = { DoSomething(); }\n}\n";

        var rule = new EffectPatchRule
        {
            Id = "test_append",
            Feature = "测试",
            PathPattern = @".*\.ot$",
            Action = EffectAction.AppendToBlock,
            TargetPattern = "Life",
            Replacement = "\ton_death = { RemoveEffects(); }\n\ton_revive = { EnableRendering(); }\n",
            Description = "测试用",
            AppliedSentinel = "test_append_sentinel_marker"
        };

        var outcome = EffectRuleApplier.Apply(source, rule);

        Assert.True(outcome.Changed);
        // on_death 已存在，不该重复插入
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(outcome.Content, "on_death\\s*="));
        // on_revive 原本没有，应该被加上
        Assert.Contains("on_revive", outcome.Content);
        // 原有业务逻辑必须保留
        Assert.Contains("DoSomething();", outcome.Content);
    }

    [Theory]
    [InlineData(0.1, "0.1")]
    [InlineData(0.3, "0.3")]
    [InlineData(0.5, "0.5")]
    [InlineData(1.0, "1")]
    [InlineData(1.5, "1.5")]
    public void 去黑暗纯曝光档位生成四条规则且替换正确(double level, string expectedExposure)
    {
        var rules = EffectPatchRules.CreateRemoveDarkRules(level);
        Assert.Equal(4, rules.Count);

        var envRules = rules.Where(r => r.PathPattern == EffectPatchRules.EnvProtectedPattern).ToList();
        Assert.Equal(2, envRules.Count);
        Assert.Contains(envRules, r => r.Id == "dark_light_objects" && r.Action == EffectAction.RemoveObject);
        Assert.Contains(envRules, r => r.Id == "dark_exposure" && r.Action == EffectAction.ReplaceCode);

        const string sampleEnv = """
            {
              "directional_light": { "multiplier": 0.2 },
              "player_light": { "multiplier": 0.3 },
              "environment_mapping": { "enabled": true },
              "global_illumination": { "enabled": true },
              "post_processing": {
                "exposure": -1.25,
                "contrast": 1.0
              }
            }
            """;

        var (envResult, _) = EffectRuleApplier.ApplyAll(sampleEnv, envRules);
        Assert.DoesNotContain("directional_light", envResult);
        Assert.DoesNotContain("player_light", envResult);
        Assert.DoesNotContain("environment_mapping", envResult);
        Assert.DoesNotContain("global_illumination", envResult);
        Assert.Contains($"\"exposure\": {expectedExposure}", envResult);
        Assert.True(EffectSyntaxValidator.ValidateFor(envResult, "metadata/environmentsettings/act1/town.env").IsValid);

        var cutoffRule = rules.Single(r => r.Id == "dark_bloom_cutoff");
        const string sampleCutoff =
            "float4 BloomCutoff( PInput input ) : PIXEL_RETURN_SEMANTIC\n" +
            "{\n" +
            "\tfloat4 c = float4(1.0f, 1.0f, 1.0f, 1.0f);\n" +
            "\treturn c;\n" +
            "}\n" +
            "float4 Helper() { return 1.0f; }\n";
        var cutoffOutcome = EffectRuleApplier.Apply(sampleCutoff, cutoffRule);
        Assert.True(cutoffOutcome.Changed);
        Assert.Contains("return 0.0f;\n}", cutoffOutcome.Content);
        Assert.Contains("float4 Helper() { return 1.0f; }", cutoffOutcome.Content);

        var gatherRule = rules.Single(r => r.Id == "dark_bloom_gather");
        const string sampleGather =
            "float4 BloomGather( PInput input ) : PIXEL_RETURN_SEMANTIC\n" +
            "{\n" +
            "\treturn float4(0.5f, 0.5f, 0.5f, 1.0f);\n" +
            "}\n";
        var gatherOutcome = EffectRuleApplier.Apply(sampleGather, gatherRule);
        Assert.True(gatherOutcome.Changed);
        Assert.Contains("return 0.0f;\n}", gatherOutcome.Content);
    }

    [Theory]
    [InlineData(990.5, "0.5")]
    [InlineData(991.0, "1")]
    [InlineData(991.5, "1.5")]
    public void 去黑暗包含光影档位仅替换低于0点5的multiplier(double level, string expectedMult)
    {
        var rules = EffectPatchRules.CreateRemoveDarkRules(level);
        var rule = Assert.Single(rules);
        Assert.Equal("dark_light_enhance", rule.Id);
        Assert.Equal(EffectAction.ReplaceCode, rule.Action);

        const string sampleEnv = """
            {
              "directional_light": {
                "multiplier": 0.25,
                "color": [1.0, 1.0, 1.0]
              },
              "player_light": {
                "multiplier": 0.5
              },
              "secondary_light": {
                "multiplier": 1.2
              }
            }
            """;

        var outcome = EffectRuleApplier.Apply(sampleEnv, rule);
        Assert.True(outcome.Changed);
        Assert.Contains("directional_light", outcome.Content);
        Assert.Contains($"\"multiplier\": {expectedMult},", outcome.Content);
        Assert.Contains("\"multiplier\": 0.5", outcome.Content);
        Assert.Contains("\"multiplier\": 1.2", outcome.Content);
        Assert.True(EffectSyntaxValidator.ValidateFor(outcome.Content, "metadata/environmentsettings/act1/town.env").IsValid);
    }

    [Fact]
    public void 去黑暗路径正则排除特殊Boss与赛季场景白名单()
    {
        var regex = new System.Text.RegularExpressions.Regex(
            EffectPatchRules.EnvProtectedPattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        Assert.Matches(regex, "metadata/environmentsettings/act1/town.env");
        Assert.Matches(regex, "metadata/environmentsettings/endgame/map_desert.env");

        Assert.DoesNotMatch(regex, "metadata/environmentsettings/bosses/p3_3_boss_blizzard.env");
        Assert.DoesNotMatch(regex, "metadata/environmentsettings/leagues/azmeri/woods.env");
        Assert.DoesNotMatch(regex, "metadata/environmentsettings/leagues/League_Ritual_fog.env");
        Assert.DoesNotMatch(regex, "metadata/environmentsettings/atlas/lost_towers.env");
        Assert.DoesNotMatch(regex, "metadata/environmentsettings/effects/ghostflamelanterns.env");
        Assert.DoesNotMatch(regex, "metadata/environmentsettings/items/lantern_glow.env");
    }

    [Fact]
    public void 环境去雾生成三条规则且白名单排除Boss与Uber()
    {
        var rules = EffectPatchRules.CreateRemoveFogRules();
        Assert.Equal(3, rules.Count);

        var rEnv = rules.Single(r => r.Id == "fog_env_objects");
        Assert.Equal(EffectAction.RemoveObject, rEnv.Action);
        Assert.Equal(EffectPatchRules.EnvFogProtectedPattern, rEnv.PathPattern);

        var regex = new System.Text.RegularExpressions.Regex(
            EffectPatchRules.EnvFogProtectedPattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        Assert.Matches(regex, "metadata/environmentsettings/act1/forest.env");
        Assert.DoesNotMatch(regex, "metadata/environmentsettings/bosses/uber_elder.env");
        Assert.DoesNotMatch(regex, "metadata/environmentsettings/bosses/p3_3_boss_blizzard.env");
        Assert.DoesNotMatch(regex, "metadata/environmentsettings/leagues/azmeri/woods.env");

        var rAffliction = rules.Single(r => r.Id == "fog_affliction_attachment");
        Assert.Equal(EffectAction.ClearBlock, rAffliction.Action);
        Assert.Contains("fogattachment", rAffliction.PathPattern);

        var rWeather = rules.Single(r => r.Id == "fog_weather_d_fog");
        Assert.Equal(EffectAction.ClearBlock, rWeather.Action);
        Assert.Contains("d_fog", rWeather.PathPattern);
    }

    [Fact]
    public void 异界去雾显线生成五条规则且双重幂等生效()
    {
        var rules = EffectPatchRules.CreateRemoveFog2Rules();
        Assert.Equal(5, rules.Count);

        // 1. worldmap_fogofwar.fxgraph 规则测试
        var rBlend = rules.Single(r => r.Id == "fog2_worldmap_blend");
        var rOutput = rules.Single(r => r.Id == "fog2_worldmap_output");
        var rMin = rules.Single(r => r.Id == "fog2_worldmap_min");
        var rMax = rules.Single(r => r.Id == "fog2_worldmap_max");

        const string sampleFxGraph = """
            {
              "blend_mode": "DownScaledAddablend",
              "ranges": {
                "min": -1.0,
                "max": 1.0
              },
              "ui_size": [1920, 1080]
            }
            """;

        var out1 = EffectRuleApplier.Apply(sampleFxGraph, rBlend);
        Assert.Contains("\"blend_mode\": \"None\"", out1.Content);

        var out2 = EffectRuleApplier.Apply(out1.Content, rOutput);
        Assert.Contains("\"output\": [0, 0, 0, 0],\n\"ui_size\": [", out2.Content);

        // 二次应用防叠加测试
        var out2Second = EffectRuleApplier.Apply(out2.Content, rOutput);
        Assert.True(out2Second.SkippedByIdempotency);

        var out3 = EffectRuleApplier.Apply(out2.Content, rMin);
        Assert.Contains("\"min\": 0.0", out3.Content);

        var out4 = EffectRuleApplier.Apply(out3.Content, rMax);
        Assert.Contains("\"max\": 0.0", out4.Content);

        // 2. windeffects.ffx 规则测试
        var rWind = rules.Single(r => r.Id == "fog2_windeffects");
        const string sampleWind = "float4 wind_wave_2_0 = 0.5f - abs(wind_phase_2_0);";
        var outWind = EffectRuleApplier.Apply(sampleWind, rWind);
        Assert.True(outWind.Changed);
        Assert.Contains("= 0.75f - abs(wind_phase_2_0);", outWind.Content);

        // 二次应用防叠加测试
        var outWindSecond = EffectRuleApplier.Apply(outWind.Content, rWind);
        Assert.True(outWindSecond.SkippedByIdempotency);
    }

    [Fact]
    public void 小地图默认规则生成七条且ID唯一()
    {
        var rules = EffectPatchRules.CreateMinimapRules();
        Assert.Equal(7, rules.Count);
        var uniqueIds = rules.Select(r => r.Id).Distinct().ToList();
        Assert.Equal(7, uniqueIds.Count);
    }

    [Fact]
    public void 小地图自定义颜色正确注入着色器文本且支持双重幂等()
    {
        var custom = new MinimapColorSettings(
            ExploredR: 20, ExploredG: 30, ExploredB: 40, ExploredA: 80,
            BorderR: 0, BorderG: 120, BorderB: 215, BorderA: 26,
            ExploringR: 0, ExploringG: 128, ExploringB: 255, ExploringA: 128,
            BorderGlow: 13f, ExploringGlow: 1f, VisibilityThreshold: 0.18f);

        var rules = EffectPatchRules.CreateMinimapRules(custom);

        // 1. minimap_visibility_pixel.hlsl 测试
        var rVisReset = rules.Single(r => r.Id == "minimap_visibility_reset");
        const string sampleVis = "if(visibility_reset > 0.5f)\n    res_color = float4(0.0f, 0.0f, 0.0f, 1.0f);";
        var outVis = EffectRuleApplier.Apply(sampleVis, rVisReset);
        Assert.True(outVis.Changed);
        Assert.Contains("res_color = float4(0.18f, 0.0f, 0.0f, 1.0f);", outVis.Content);

        // 2. minimap_pixel.hlsl 测试
        var rClamp = rules.Single(r => r.Id == "minimap_sample_clamp");
        const string samplePix = "return SAMPLE_TEX2D( visibility_sampler, SamplerLinearClampOffsetNoBias, frac(map_coord) );";
        var outPix = EffectRuleApplier.Apply(samplePix, rClamp);
        Assert.True(outPix.Changed);
        Assert.Contains("if (color.r < 0.18f)", outPix.Content);

        // 3. minimap_blending_pixel.hlsl 测试
        var rClean = rules.Single(r => r.Id == "minimap_legacy_cleanup");
        var rWalkable = rules.Single(r => r.Id == "minimap_walkable_color");
        var rBorder = rules.Single(r => r.Id == "minimap_border_color");
        var rExploring = rules.Single(r => r.Id == "minimap_exploring_color");
        var rMask = rules.Single(r => r.Id == "minimap_content_mask");

        const string sampleBlend = """
            float4 walkable_color = float4(1.0f, 1.0f, 1.0f, 0.01f);
            float4 walkability_map_color = lerp(walkable_color, float4(0.5f, 0.5f, 1.0f, 0.5f), walkable_to_edge_ratio);
            float visibility = 1.0f;
            walkability_map_color = lerp(walkability_map_color * float4(1.0f, 1.0f, 1.0f, visibility), float4(0.0f, 0.5f, 1.0f, 0.5f), pow(saturate(unexplored_edge), 25.0f));
            return OutputSDR(res_color, sdr_scale);
            """;

        var outClean = EffectRuleApplier.Apply(sampleBlend, rClean);
        Assert.Contains("float visibility = saturate(walkability_sample.g * 2.0f);", outClean.Content);

        var outWalk = EffectRuleApplier.Apply(outClean.Content, rWalkable);
        Assert.Contains("float4 walkable_color = float4(0.08f, 0.12f, 0.16f, 0.31f);", outWalk.Content);

        var outBorder = EffectRuleApplier.Apply(outWalk.Content, rBorder);
        Assert.Contains("float4 walkability_map_color = lerp(walkable_color, float4(0.0f, 6.12f, 10.96f, 0.1f), walkable_to_edge_ratio);", outBorder.Content);

        var outExp = EffectRuleApplier.Apply(outBorder.Content, rExploring);
        Assert.Contains("float4(0.0f, 0.5f, 1.0f, 0.5f)", outExp.Content);

        var outMask = EffectRuleApplier.Apply(outExp.Content, rMask);
        Assert.Contains("float map_content_mask = saturate(geometry_sample.a * 20.0f + walkability_sample.r * 5.0f);", outMask.Content);
        Assert.Contains("res_color.a *= map_content_mask;", outMask.Content);

        // 幂等性校验
        var outMaskSecond = EffectRuleApplier.Apply(outMask.Content, rMask);
        Assert.True(outMaskSecond.SkippedByIdempotency);
    }
}