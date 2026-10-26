using System.Text.RegularExpressions;

namespace FreiAtlas.Patcher.EffectPatch.MudMan;

/// <summary>
/// 泥人档位。
///
/// 六个档位 = 三个特效范围 × 是否压缩贴图。这个结构直接对应易刷的
/// 「低级/中级/高级」×「+ 大幅压缩材质」组合，规则完整取自其最新热更能力包
/// （remote_ni.js 中的 nini / Re / Me / ae / k 规则生成函数）。
///
/// 特效范围：
///   · 低级  清理 11 大目录（排除技能 spells 与核心机制）下的特效块、音效、灯光、震屏与 .epk，
///           清理 F 表地面装饰（.ao/.atlas/.dlp）与怪物死亡尸体，不含技能特效
///   · 中级  在低级基础上放开对技能（spells）的排除，清理技能粒子/灯光/震屏/音效块与技能 .epk/.atlas
///   · 高级  在中级基础上对 11 大目录额外清理模型渲染块（EffectPack/FixedMesh/AnimatedRender/SkinMesh）
///           并清空 11 大目录下的 .pet/.trl 粒子与拖尾定义
/// </summary>
public enum MudManTier
{
    /// <summary>低级（技能除外）</summary>
    Low = 0,

    /// <summary>中级（包括技能）</summary>
    Medium = 1,

    /// <summary>高级（包括模型）</summary>
    High = 2,

    /// <summary>低级 + 大幅压缩材质</summary>
    LowTextures = 3,

    /// <summary>中级 + 大幅压缩材质</summary>
    MediumTextures = 4,

    /// <summary>高级 + 大幅压缩材质</summary>
    HighTextures = 5
}

/// <summary>
/// 档位元信息与规则集。
/// </summary>
public static class MudManTierCatalog
{
    /// <summary>
    /// 易泥规则覆盖的 11 个 metadata 子目录（对应 remote_ni.js 的 a 变量）。
    /// </summary>
    private const string TargetCategories =
        "terrain|particles|miscellaneousobjects|monsters|items|effects|critters|utility|shrines|characters|cinematic";

    /// <summary>
    /// 易泥核心保护名单（对应 remote_ni.js 的 i(false) 与公共排除列表 y）：
    /// 保护地面危险圈、传送门、黑洞、诅咒、Boss 战、NPC、任务、荒林妖精、先祖秘藏、祭坛等关键机制视觉。
    /// </summary>
    private const string CoreExcludes =
        "tencent|leagueritual|leagueultimatum|leaguesanctum|league_affliction|" +
        "ground|ground_effects|grd_zones|willowisp|portal|black_hole|volatile_dead|curse|curse_aura|" +
        "atlasofworldsbosses|atlasmemories|atlasexiles|offering|Boss|boss|atlas|npc|quest|" +
        "azmeri|Azmeri|Acronym|tormented_?spirits|azmeriwisps|wisp_doodads|vodoo_king_boss|paleelite|" +
        "kurgals_gasp|Kulemak|lost_towers|misery_shield_charge|SpecialCorpses|SpiritTortoise|" +
        "twilight_sunrise|glow_variant|lightbringer|endgame_world_map|doodads|League_Ritual|" +
        "VodooKing_Arena|treasure_bound_soul|toxic_runes|ghostflamelanterns|largeallflamelanterndrop|" +
        "atlantis_deepwater|pointer|lantern";

    private const string ExcludeWithSpells = "(?!.*(" + CoreExcludes + "|spells))";
    private const string ExcludeWithoutSpells = "(?!.*(" + CoreExcludes + "))";

    /// <summary>全部档位，按从轻到重排列。</summary>
    public static readonly MudManTier[] All =
    [
        MudManTier.Low,
        MudManTier.Medium,
        MudManTier.High,
        MudManTier.LowTextures,
        MudManTier.MediumTextures,
        MudManTier.HighTextures
    ];

    /// <summary>该档位是否包含贴图压缩。</summary>
    public static bool HasTextures(this MudManTier tier) =>
        tier is MudManTier.LowTextures or MudManTier.MediumTextures or MudManTier.HighTextures;

    /// <summary>档位对应的特效范围，去掉贴图维度。</summary>
    public static MudManTier EffectScope(this MudManTier tier) => tier switch
    {
        MudManTier.LowTextures => MudManTier.Low,
        MudManTier.MediumTextures => MudManTier.Medium,
        MudManTier.HighTextures => MudManTier.High,
        _ => tier
    };

    /// <summary>界面显示名。</summary>
    public static string DisplayName(this MudManTier tier) => tier switch
    {
        MudManTier.Low => "低级（技能除外）",
        MudManTier.Medium => "中级（包括技能）",
        MudManTier.High => "高级（包括模型）",
        MudManTier.LowTextures => "低级 + 大幅压缩材质",
        MudManTier.MediumTextures => "中级 + 大幅压缩材质",
        MudManTier.HighTextures => "高级 + 大幅压缩材质",
        _ => tier.ToString()
    };

    /// <summary>界面上的一句话说明。</summary>
    public static string Description(this MudManTier tier) => tier switch
    {
        MudManTier.Low =>
            "清理 11 大目录下的非技能特效、灯光、震屏与地面装饰，保留技能特效与核心机制提示。",
        MudManTier.Medium =>
            "在低级基础上再清掉技能特效与音效事件，战斗画面最干净。",
        MudManTier.High =>
            "在中级基础上额外清理模型渲染块与全部粒子/拖尾定义，画面元素最少。",
        MudManTier.LowTextures =>
            "低级特效 + 把场景贴图替换为预置色调小块与中性灰块。显存占用大幅下降。",
        MudManTier.MediumTextures =>
            "中级特效 + 贴图压缩。显存与战斗画面同时优化，是最常用的组合。",
        MudManTier.HighTextures =>
            "高级特效 + 贴图压缩。压到最狠的一档，画面损失也最大。",
        _ => string.Empty
    };

    /// <summary>
    /// 该档位包含的全部规则。贴图规则由 <see cref="MudManTier.HasTextures"/> 单独判定，
    /// 不出现在这里 —— 它的执行路径与文本规则完全不同（不读原文件）。
    /// </summary>
    public static IReadOnlyList<EffectPatchRule> Rules(this MudManTier tier)
    {
        var scope = tier.EffectScope();
        var rules = new List<EffectPatchRule>();

        rules.AddRange(MonsterDeathRules());
        rules.Add(GpuParticlesRestoreRule());
        rules.AddRange(StaticSceneRules());
        rules.AddRange(SceneEffectRules());

        if (scope >= MudManTier.Medium)
        {
            rules.AddRange(SpellEffectRules());
            rules.AddRange(SoundRules());
        }

        if (scope >= MudManTier.High)
        {
            rules.AddRange(AllParticleRules());
        }

        return rules;
    }

    /// <summary>
    /// 怪物死亡特效移除（兼容 PoE2 GEAL 花括号语法）。
    /// </summary>
    private static IEnumerable<EffectPatchRule> MonsterDeathRules()
    {
        yield return new EffectPatchRule
        {
            Id = "mudman_monster_dead",
            Feature = "泥人-怪物死亡特效",
            PathPattern = @".*/monsters/monster\.ot$",
            Action = EffectAction.ReplaceCode,
            TargetPattern = @"(?m)^Life \{\}\r?\n",
            Replacement =
                "Life\n" +
                "{\n" +
                "    on_spawned_dead =\n    {\n" +
                "        RemoveEffects();\n        DisableRendering();\n" +
                "    }\n" +
                "    on_death =\n    {\n" +
                "        RemoveEffects();\n        DisableRendering();\n" +
                "    }\n" +
                "}\n",
            Description = "怪物死亡时移除特效并停止渲染，尸体不再占用渲染资源。"
        };

        yield return new EffectPatchRule
        {
            Id = "mudman_monster_revive",
            Feature = "泥人-怪物死亡特效",
            PathPattern = @".*/monsters/monster\.ot$",
            Action = EffectAction.ReplaceCode,
            TargetPattern = @"(?m)^(\tslow_animations_go_to_idle = (?:true|false)\r?\n)",
            Replacement =
                "$1\ton_start_Revive =\n\t{\n\t\tRemoveEffects();\n\t\tEnableRendering();\n\t}\n",
            Description = "怪物复活时恢复渲染，避免复活的怪物隐形。",
            ApplyMode = EffectApplyMode.FunctionBody
        };
    }

    /// <summary>
    /// 还原 GPU 粒子发射器缩放（对应 remote_ni.js 的 ae(0)）。
    /// </summary>
    private static EffectPatchRule GpuParticlesRestoreRule() => new()
    {
        Id = "mudman_gpu_particles_restore",
        Feature = "泥人-GPU粒子还原",
        PathPattern = @".*shaders/renderer/nodes/gpuparticles\.ffx$",
        Action = EffectAction.ReplaceCode,
        TargetPattern = @"gpu_particle_emit_scale\s*\*\s*emitter_duration\s*\*\s*(?:0\.15|0\.05)f(\s*\)?;)",
        Replacement = "gpu_particle_emit_scale * emitter_duration$1",
        Description = "将 gpuparticles.ffx 中被缩放的发射时长系数还原为默认，由泥人规则接管粒子控制。",
        ApplyMode = EffectApplyMode.FunctionBody
    };

    /// <summary>
    /// 静态场景装饰清理（对应 remote_ni.js 的 Me() 与 F 表：地形网格、贴花图集 .atlas、地面草丛贴花包 .dlp）。
    /// </summary>
    private static IEnumerable<EffectPatchRule> StaticSceneRules()
    {
        yield return new EffectPatchRule
        {
            Id = "mudman_static_terrain",
            Feature = "泥人-静态场景",
            PathPattern = BuildExactPathsRegex(StaticTerrainAoPaths),
            Action = EffectAction.ClearBlock,
            TargetPattern = "FixedMesh",
            Replacement = string.Empty,
            Description = "清空特定地图影响装饰物的 FixedMesh 静态网格块。"
        };

        yield return new EffectPatchRule
        {
            Id = "mudman_static_terrain_skin",
            Feature = "泥人-静态场景",
            PathPattern = BuildExactPathsRegex(StaticTerrainSkinAoPaths),
            Action = EffectAction.ClearBlock,
            TargetPattern = "SkinMesh",
            Replacement = string.Empty,
            Description = "清空特定地图影响装饰物的 SkinMesh 蒙皮网格块。"
        };

        yield return new EffectPatchRule
        {
            Id = "mudman_static_atlases",
            Feature = "泥人-静态场景",
            PathPattern = BuildExactPathsRegex(StaticAtlasLowPaths),
            Action = EffectAction.ReplaceCode,
            TargetPattern = @"\A(\uFEFF?version[ \t]+2(?:\r?\n)+""[^""\r\n]+\.mat""(?:\r?\n)+)[0-9]+([ \t]+)[0-9]+(?=\r?\n|\z)",
            Replacement = "${1}0${2}0",
            Description = "将血液、焦痕、碎屑等地面贴花图集（.atlas）宽高置零。",
            ApplyMode = EffectApplyMode.FunctionBody
        };

        yield return CreateDecalRule("mudman_static_decals_4", StaticDecal4Paths, 4);
        yield return CreateDecalRule("mudman_static_decals_5", StaticDecal5Paths, 5);
        yield return CreateDecalRule("mudman_static_decals_10", StaticDecal10Paths, 10);
    }

    private static EffectPatchRule CreateDecalRule(string id, string[] paths, int fields) => new()
    {
        Id = id,
        Feature = "泥人-静态场景",
        PathPattern = BuildExactPathsRegex(paths),
        Action = EffectAction.ReplaceCode,
        TargetPattern = $@"\A(\uFEFF?)[0-9]+(?:[ \t]+[0-9]+){{{fields - 1}}}(?=\r?\n|\z)",
        Replacement = "${1}" + string.Join(" ", Enumerable.Repeat("0", fields)),
        Description = $"将地面植被与环境贴花包（.dlp，{fields} 字段）首行参数置零。",
        ApplyMode = EffectApplyMode.FunctionBody
    };

    /// <summary>
    /// 11 大目录非技能特效削减（对应 remote_ni.js 的 Re(1)）。
    /// </summary>
    private static IEnumerable<EffectPatchRule> SceneEffectRules()
    {
        yield return new EffectPatchRule
        {
            Id = "mudman_scene_blocks",
            Feature = "泥人-场景特效",
            PathPattern = $@".*metadata/({TargetCategories})/{ExcludeWithSpells}.*/.*\.(ao|aoc)$",
            Action = EffectAction.ClearBlock,
            TargetPattern = "SoundEvents|Lights|ParticleEffects|WindEvents|TrailsEffects|ScreenShake",
            Replacement = string.Empty,
            Description = "清空 11 大目录（技能除外）下 .ao/.aoc 的音效、灯光、粒子、风、拖尾与震屏块。"
        };

        yield return new EffectPatchRule
        {
            Id = "mudman_scene_epk",
            Feature = "泥人-场景特效",
            PathPattern = $@".*metadata/({TargetCategories})/{ExcludeWithSpells}.*/.*\.epk$",
            Action = EffectAction.ReplaceCode,
            TargetPattern = @"\A[\s\S]+\z",
            Replacement = string.Empty,
            Description = "清空 11 大目录（技能除外）下的特效包（.epk）内容。"
        };
    }

    /// <summary>
    /// 技能特效削减（对应 remote_ni.js 的 Re(2) 相较于 Re(1) 放开 spells 的部分）。
    /// </summary>
    private static IEnumerable<EffectPatchRule> SpellEffectRules()
    {
        yield return new EffectPatchRule
        {
            Id = "mudman_spell_blocks",
            Feature = "泥人-技能特效",
            PathPattern = $@".*metadata/({TargetCategories})/{ExcludeWithoutSpells}.*spells.*\.(ao|aoc)$",
            Action = EffectAction.ClearBlock,
            TargetPattern = "Lights|ParticleEffects|WindEvents|TrailsEffects|ScreenShake",
            Replacement = string.Empty,
            Description = "清空技能特效里的灯光、粒子、风、拖尾与震屏块，保留地面效果与光环等功能性视觉。"
        };

        yield return new EffectPatchRule
        {
            Id = "mudman_spell_epk",
            Feature = "泥人-技能特效",
            PathPattern = $@".*metadata/({TargetCategories})/{ExcludeWithoutSpells}.*spells.*\.epk$",
            Action = EffectAction.ReplaceCode,
            TargetPattern = @"\A[\s\S]+\z",
            Replacement = string.Empty,
            Description = "清空技能粒子包（.epk）内容。"
        };

        yield return new EffectPatchRule
        {
            Id = "mudman_spell_atlases",
            Feature = "泥人-技能特效",
            PathPattern = BuildExactPathsRegex(StaticAtlasSpellPaths),
            Action = EffectAction.ReplaceCode,
            TargetPattern = @"\A(\uFEFF?version[ \t]+2(?:\r?\n)+""[^""\r\n]+\.mat""(?:\r?\n)+)[0-9]+([ \t]+)[0-9]+(?=\r?\n|\z)",
            Replacement = "${1}0${2}0",
            Description = "将技能产生的地面血迹与焦痕贴花图集（.atlas）宽高置零。",
            ApplyMode = EffectApplyMode.FunctionBody
        };
    }

    /// <summary>
    /// 技能音效事件移除（与 SceneEffectRules 合并覆盖全 11 大目录的 SoundEvents）。
    /// </summary>
    private static IEnumerable<EffectPatchRule> SoundRules()
    {
        yield return new EffectPatchRule
        {
            Id = "mudman_sound_events",
            Feature = "泥人-音效",
            PathPattern = $@".*metadata/({TargetCategories})/{ExcludeWithoutSpells}.*spells.*\.(ao|aoc)$",
            Action = EffectAction.ClearBlock,
            TargetPattern = "SoundEvents",
            Replacement = string.Empty,
            Description = "移除技能特效音效事件，降低密集战斗的音频开销。"
        };
    }

    /// <summary>
    /// 高级档模型块与全量粒子清空（对应 remote_ni.js 的 Re(3) 专属规则）。
    /// </summary>
    private static IEnumerable<EffectPatchRule> AllParticleRules()
    {
        yield return new EffectPatchRule
        {
            Id = "mudman_high_model_blocks",
            Feature = "泥人-模型与特效包",
            PathPattern = $@".*metadata/({TargetCategories})/{ExcludeWithoutSpells}.*/.*\.(ao|aoc)$",
            Action = EffectAction.ClearBlock,
            TargetPattern = "EffectPack|FixedMesh|AnimatedRender|SkinMesh",
            Replacement = string.Empty,
            Description = "高级档额外清空 11 大目录下 .ao/.aoc 的特效包与模型网格渲染块。"
        };

        yield return new EffectPatchRule
        {
            Id = "mudman_all_particles",
            Feature = "泥人-粒子",
            PathPattern = $@".*metadata/({TargetCategories})/{ExcludeWithoutSpells}.*/.*\.(pet|trl)$",
            Action = EffectAction.ReplaceCode,
            TargetPattern = @"\A[\s\S]*\z",
            Replacement = "\n0\n            ",
            Description = "清空 11 大目录下全部粒子（.pet）与拖尾（.trl）定义，画面元素最少。"
        };
    }

    private static string BuildExactPathsRegex(IEnumerable<string> paths) =>
        "^(?:" + string.Join("|", paths.Select(Regex.Escape)) + ")$";

    private static readonly string[] StaticTerrainAoPaths =
    [
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/hellpimple_v01_11.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglebiomass_01.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglebiomass_02.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglebiomass_03.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglebiomass_04.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglebiomass_05.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglebiomass_06.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglebiomass_07.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglebloodandbone_v03_01.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglebloodandbone_v03_02.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglebloodandbone_v03_03.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglebloodandbone_v03_04.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglebloodandbone_v03_05.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglebonepile_01.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglebonepile_02.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglebonepile_03.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglebonepile_04.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglebonepile_05.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglebonepile_06.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglehead_01.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglepimple_v01_01.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglepimple_v01_02.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglepimple_v01_03.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglepimple_v01_04.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglepimple_v01_05.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglepimple_v01_06.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglepimple_v01_07.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglepimple_v01_08.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglepimple_v01_09.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglepimple_v01_10.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglepool_01.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tangleribs_01.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tangleroots_01.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tangleroots_02.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglespine_01.ao"
    ];

    private static readonly string[] StaticTerrainSkinAoPaths =
    [
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglebellyglob_v02_01.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tanglebellyglob_v03_01.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tangleeyecluster_v01_01.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tangleeyecluster_v01_02.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tangleeyecluster_v01_03.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tangleeyeclustersingle_v01.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tangleeyeclustersingle_v02.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tangleeyeclustersingle_v03.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tangleeyeclustersingle_v04.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tangleeyeclustersingle_v05.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tangleeyeclustersingle_v06.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tangleglob_v08_01.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tangleglob_v10_01.ao",
        "metadata/effects/environment/atlasinvaders/tangleinfluencedoodads/tangleglob_v11_01.ao"
    ];

    private static readonly string[] StaticAtlasLowPaths =
    [
        "metadata/decals/act3/ant_lightdust01.atlas",
        "metadata/decals/act4/sirencave/srn_slimedecal01_splat.atlas",
        "metadata/decals/aggoratpetals.atlas",
        "metadata/decals/aggoratpetals_02.atlas",
        "metadata/decals/aggoratpowder.atlas",
        "metadata/decals/arenapetals.atlas",
        "metadata/decals/arenapetalsrandom.atlas",
        "metadata/decals/blood/bloodsplat_round_2_big.atlas",
        "metadata/decals/blood_harvest.atlas",
        "metadata/decals/blood_pbr/blood_basic.atlas",
        "metadata/decals/blood_robot.atlas",
        "metadata/decals/bloodnew.atlas",
        "metadata/decals/bloodnew_old.atlas",
        "metadata/decals/bloodsplat_alphatest_round.atlas",
        "metadata/decals/bloodsplat_multi_round.atlas",
        "metadata/decals/bloodtrail.atlas",
        "metadata/decals/bloodtraildim.atlas",
        "metadata/decals/bloodtrailfresh.atlas",
        "metadata/decals/bloodtrailold.atlas",
        "metadata/decals/burn_ring_scorch.atlas",
        "metadata/decals/burn_ring_scorch_02.atlas",
        "metadata/decals/burn_ring_scorch_03.atlas",
        "metadata/decals/burn_scorch.atlas",
        "metadata/decals/burnmarks_decal.atlas",
        "metadata/decals/cavedecals.atlas",
        "metadata/decals/ceremonialfeathers01.atlas",
        "metadata/decals/cityplants.atlas",
        "metadata/decals/cityplants_dry.atlas",
        "metadata/decals/cityplantsb.atlas",
        "metadata/decals/citystalkerpattern.atlas",
        "metadata/decals/corpseexplosion.atlas",
        "metadata/decals/corrupted_bloodsplat.atlas",
        "metadata/decals/corruption/sanctifiedcorruption/sanctifiedminordecals01.atlas",
        "metadata/decals/deadeels.atlas",
        "metadata/decals/driedlakerocks.atlas",
        "metadata/decals/footprints/fire/fire_footprint.atlas",
        "metadata/decals/forestdecals.atlas",
        "metadata/decals/forestflower.atlas",
        "metadata/decals/forestgrunge.atlas",
        "metadata/decals/fx/act2/blackstrider_web.atlas",
        "metadata/decals/fx/act2/serpentclan_spit.atlas",
        "metadata/decals/fx/scorch1.atlas",
        "metadata/decals/fx/scorch2.atlas",
        "metadata/decals/general/dirt/stain/decal_dirtgrunge01_edge_stain.atlas",
        "metadata/decals/general/dirt/stain/decal_dirtgrunge01_longsplat_stain_irongrey02.atlas",
        "metadata/decals/general/dirt/stain/decal_dirtgrunge01_longsplat_stain_irongrey03.atlas",
        "metadata/decals/general/dirt/stain/decal_dirtgrunge01_splat_stain.atlas",
        "metadata/decals/general/dirt/stain/decal_dirtgrunge01_splat_stain_irongrey02.atlas",
        "metadata/decals/general/dirt/stain/decal_dirtgrunge01_splat_stain_irongrey03.atlas",
        "metadata/decals/grungedecal.atlas",
        "metadata/decals/grungedecal2.atlas",
        "metadata/decals/grungedecal2_highres.atlas",
        "metadata/decals/grungedecal3.atlas",
        "metadata/decals/grungedecal3dim.atlas",
        "metadata/decals/grungedecal3old.atlas",
        "metadata/decals/grungedecalold.atlas",
        "metadata/decals/harbingerdecalset1.atlas",
        "metadata/decals/harbingerdecalset2.atlas",
        "metadata/decals/innocencelunarisa.atlas",
        "metadata/decals/innocencelunarisb.atlas",
        "metadata/decals/innocencesolarisa.atlas",
        "metadata/decals/innocencesolarisb.atlas",
        "metadata/decals/interlude/part1/p1_4/ihlt_blooddecal01_splat.atlas",
        "metadata/decals/interlude/part1/p1_4/ihlt_blooddecal01_streak.atlas",
        "metadata/decals/kaom_walkway_small.atlas",
        "metadata/decals/kaomrocks.atlas",
        "metadata/decals/kaomrocksdarkgrey.atlas",
        "metadata/decals/kitavadecalset1.atlas",
        "metadata/decals/kitavadecalset2.atlas",
        "metadata/decals/kitavadecalset3.atlas",
        "metadata/decals/kowhaiflowers01.atlas",
        "metadata/decals/leaves.atlas",
        "metadata/decals/leaves_02.atlas",
        "metadata/decals/ledgedecals.atlas",
        "metadata/decals/machinariumscorchmarks.atlas",
        "metadata/decals/machinariumscratches.atlas",
        "metadata/decals/marshdecals01.atlas",
        "metadata/decals/meltedgold_splatdecal01.atlas",
        "metadata/decals/mudflatgrass.atlas",
        "metadata/decals/mudflatgrassshipgraveyard.atlas",
        "metadata/decals/omnitectarena.atlas",
        "metadata/decals/pbrblooddecals.atlas",
        "metadata/decals/pbrblooddecals2.atlas",
        "metadata/decals/pbrbloodmultiplicative.atlas",
        "metadata/decals/rockyclimbdecals.atlas",
        "metadata/decals/ruststains01.atlas",
        "metadata/decals/sandgrass.atlas",
        "metadata/decals/sandstonegrass.atlas",
        "metadata/decals/scorchmarks.atlas",
        "metadata/decals/seaweed.atlas",
        "metadata/decals/seaweedwet.atlas",
        "metadata/decals/shaper_blood/blood.atlas",
        "metadata/decals/shrubdecal.atlas",
        "metadata/decals/shrubs.atlas",
        "metadata/decals/skeleton.atlas",
        "metadata/decals/skeletondelve.atlas",
        "metadata/decals/slavepen_x_blood_random.atlas",
        "metadata/decals/slavepen_x_blood_unique.atlas",
        "metadata/decals/snakeskins.atlas",
        "metadata/decals/spiderdecals.atlas",
        "metadata/decals/spiral_scrabble.atlas",
        "metadata/decals/splatter.atlas",
        "metadata/effects/microtransactions/misc/uniques/gore_crunch/decal/corpseexplosion.atlas"
    ];

    private static readonly string[] StaticAtlasSpellPaths =
    [
        "metadata/effects/microtransactions/spells/blood_scythe/star_fall/decal/blood_basic.atlas",
        "metadata/effects/spells/blood_scythe/decal/blood_splat.atlas",
        "metadata/effects/spells/blood_tendrils/decals/blood_splat.atlas",
        "metadata/effects/spells/flask_throw/scorch.atlas",
        "metadata/effects/spells/lightning_arrows/decal/burnt_mark.atlas",
        "metadata/effects/spells/rage_slash/decal/scratch.atlas"
    ];

    private static readonly string[] StaticDecal4Paths =
    [
        "metadata/doodads/boilinglake_plant.dlp",
        "metadata/doodads/branchleave_small.dlp",
        "metadata/doodads/branchleave_sparse.dlp",
        "metadata/doodads/branchleave.dlp",
        "metadata/doodads/branchleavedamp_sparse.dlp",
        "metadata/doodads/cactus1.dlp",
        "metadata/doodads/city_moss_decals1.dlp",
        "metadata/doodads/citypalm.dlp",
        "metadata/doodads/citypalm2.dlp",
        "metadata/doodads/desertgrass1.dlp",
        "metadata/doodads/fernhigh_small.dlp",
        "metadata/doodads/fernhigh.dlp",
        "metadata/doodads/fernlow_more.dlp",
        "metadata/doodads/fernlow.dlp",
        "metadata/doodads/fernlowbig.dlp",
        "metadata/doodads/fernlowdamp.dlp",
        "metadata/doodads/forest_plant_bushgreen.dlp",
        "metadata/doodads/forest_plant_green2.dlp",
        "metadata/doodads/forest_plant_greensharp.dlp",
        "metadata/doodads/forest_plant_lowyellow.dlp",
        "metadata/doodads/forest_plant_shrubblack2.dlp",
        "metadata/doodads/forest_plant_shrubblack3.dlp",
        "metadata/doodads/forest_plant_weedred.dlp",
        "metadata/doodads/forest_plant_weeds1.dlp",
        "metadata/doodads/grass.dlp",
        "metadata/doodads/grass2_dirt.dlp",
        "metadata/doodads/grass2.dlp",
        "metadata/doodads/grass2x_dirt.dlp",
        "metadata/doodads/grass2x.dlp",
        "metadata/doodads/grass3.dlp",
        "metadata/doodads/grass4_reducedscale.dlp",
        "metadata/doodads/grass4_sparse.dlp",
        "metadata/doodads/grassbig.dlp",
        "metadata/doodads/grassbig2.dlp",
        "metadata/doodads/plantpurple.dlp",
        "metadata/doodads/shrub.dlp",
        "metadata/doodads/tropic.dlp",
        "metadata/doodads/tropic2.dlp",
        "metadata/doodads/tropicalsmall.dlp",
        "metadata/terrain/doodads/leagues/hellscape/dry_human_b.dlp",
        "metadata/terrain/doodads/leagues/hellscape/dry_human_r.dlp",
        "metadata/terrain/doodads/leagues/hellscape/fresh_human_b.dlp",
        "metadata/terrain/doodads/leagues/hellscape/fresh_human_r.dlp",
        "metadata/terrain/doodads/leagues/hellscape/scorch_a.dlp",
        "metadata/terrain/doodads/leagues/hellscape/scorch_b.dlp",
        "metadata/terrain/leagues/expedition/crater_doodads.dlp",
        "metadata/terrain/leagues/expedition/crater_tiles.dlp",
        "metadata/terrain/leagues/expedition/dirt.dlp"
    ];

    private static readonly string[] StaticDecal5Paths =
    [
        "metadata/doodads/ascent_grass_01.dlp",
        "metadata/doodads/ascent_grass_02.dlp",
        "metadata/doodads/branchleave_sparse_nonwalkable.dlp",
        "metadata/doodads/burngrass_ocndcjdh_nonwalkable.dlp",
        "metadata/doodads/burngrass_ocndcjdh.dlp",
        "metadata/doodads/city_garden_grass1.dlp",
        "metadata/doodads/citygrass1.dlp",
        "metadata/doodads/citygrass2.dlp",
        "metadata/doodads/citygrass2small.dlp",
        "metadata/doodads/citygrass3.dlp",
        "metadata/doodads/citygrassmix1.dlp",
        "metadata/doodads/citygrassmix2.dlp",
        "metadata/doodads/citygrassmix3.dlp",
        "metadata/doodads/drygarden_grass1.dlp",
        "metadata/doodads/drygarden_grass2.dlp",
        "metadata/doodads/drygarden_grass3.dlp",
        "metadata/doodads/dryimperialfieldsgrass1.dlp",
        "metadata/doodads/dryimperialfieldsgrass2.dlp",
        "metadata/doodads/fallenleafpilesbf.dlp",
        "metadata/doodads/foothills_grass_02.dlp",
        "metadata/doodads/foothills_grass_03.dlp",
        "metadata/doodads/foothills_grass_04.dlp",
        "metadata/doodads/foothills_grass_06.dlp",
        "metadata/doodads/grass2_nonwalkable.dlp",
        "metadata/doodads/grass2x_nonwalkable.dlp",
        "metadata/doodads/grass4_nonwalkable.dlp",
        "metadata/doodads/islands/g_toc_grass01.dlp",
        "metadata/doodads/islands/g_toc_grass02.dlp",
        "metadata/doodads/islands/g_toc_grass03.dlp",
        "metadata/doodads/kaomgrass.dlp",
        "metadata/doodads/mtx_white_grass_large.dlp",
        "metadata/doodads/mtx_white_grass.dlp",
        "metadata/doodads/radiantgrass.dlp",
        "metadata/doodads/reefweed1.dlp",
        "metadata/doodads/reefweed2.dlp",
        "metadata/doodads/wetforest_floweringplant_slgkm_nonwalkable.dlp",
        "metadata/doodads/wetforest_grasslawn_rhjo3dp_nonwalkable_02.dlp",
        "metadata/doodads/wetforest_grasslawn_rhjo3dp_nonwalkable.dlp",
        "metadata/doodads/wetforest_oceanseaweed.dlp"
    ];

    private static readonly string[] StaticDecal10Paths =
    [
        "metadata/doodads/maps/bluff/mblu_manukashrub01.dlp",
        "metadata/doodads/maps/bluff/mblu_manukashrubflowering01.dlp"
    ];
}
