using System.Globalization;
using FreiAtlas.Core.Settings;

namespace FreiAtlas.Patcher.EffectPatch;

/// <summary>
/// 内置特效补丁规则集。
///
/// 规则来源：逆向 exilence-next-tx-app（易刷 E-farm）3.0.25 的
/// EniWindow/assets/index-NAuBG_pB.js，逐条还原其路径正则与替换内容。
/// 详细分析见 docs/research/efarm-effect-patch-reverse-engineering.md。
///
/// 约束：
///   - 规则集只读，不提供用户自定义入口
///   - 高危规则（一键全清、移除音效）默认关闭
///   - 任何规则改动都必须先在真实客户端验证命中率
/// </summary>
public static class EffectPatchRules
{
    /// <summary>技能特效削减的保护名单：这些路径下的特效绝不改动。</summary>
    private const string SpellGuard =
        "(?!.*(ground_effects|portal|black_hole|volatile_dead|curse_aura|" +
        "atlasofworldsbosses|atlasmemories|atlasexiles|offering))";

    /// <summary>环境去黑暗的保护白名单：排除特殊 Boss 与赛季机制场景。</summary>
    public const string EnvProtectedPattern =
        @".*environmentsettings/(?!.*(p3_3_boss_blizzard|azmeri|Azmeri|Acronym|tormented_?spirits|azmeriwisps|wisp_doodads|vodoo_king_boss|paleelite|kurgals_gasp|Kulemak|lost_towers|misery_shield_charge|SpecialCorpses|SpiritTortoise|twilight_sunrise|glow_variant|lightbringer|endgame_world_map|doodads|League_Ritual|VodooKing_Arena|treasure_bound_soul|toxic_runes|ghostflamelanterns|largeallflamelanterndrop|atlantis_deepwater|pointer|lantern)).*\.env$";

    /// <summary>环境去雾的保护白名单：排除特殊 Boss、Uber 与赛季机制场景。</summary>
    public const string EnvFogProtectedPattern =
        @".*environmentsettings/(?!.*(p3_3_boss_blizzard|uber|azmeri|Azmeri|Acronym|tormented_?spirits|azmeriwisps|wisp_doodads|vodoo_king_boss|paleelite|kurgals_gasp|Kulemak|lost_towers|misery_shield_charge|SpecialCorpses|SpiritTortoise|twilight_sunrise|glow_variant|lightbringer|endgame_world_map|doodads|League_Ritual|VodooKing_Arena|treasure_bound_soul|toxic_runes|ghostflamelanterns|largeallflamelanterndrop|atlantis_deepwater|pointer|lantern)).*\.env$";

    /// <summary>
    /// 未自定义时的默认规则：排除高危（一键全清、移除音效）与当前版本不可用的条目。
    ///
    /// 注意这是「默认规则」不是「默认勾选」——特效模块默认是关闭的，
    /// 用户勾选该模块时才按这份清单应用。
    ///
    /// 用表达式体而不是静态字段：本属性声明在 <see cref="All"/> 之前，
    /// 静态字段会在 All 尚未初始化时求值得到空集。
    /// </summary>
    public static IReadOnlyList<string> DefaultRuleIds =>
        All.Where(r => !r.IsDangerous && !r.IsUnsupported).Select(r => r.Id).ToList();

    public static IReadOnlyList<EffectPatchRule> All { get; } =
    [
        // ── 怪物死亡特效移除 ──
        //
        // 两级方案。实测 PoE2 的 metadata/monsters 下有 2,806 个 .ot：
        //   · 2,028 个 extends monster.ot，自己不定义 Life → 改基类即可覆盖
        //   ·   674 个自己写了非空 Life（武器挂载、状态机等业务逻辑）→ 只能追加
        //   ·    30 个是空 Life {}
        //
        // 旧规则只匹配空 Life，实际只能改到 30/2806 ≈ 1%，这是之前清尸体
        // 效果有限的根因。现在改成：
        //   1. 基类 monster.ot 的空 Life → 替换成带事件的版本（覆盖 2,028 个继承者）
        //   2. 子类里非空的 Life → 追加事件（覆盖 674 个）
        //
        // GEAL 事件必须用 {} 而不是引号。参照 character.ot 里已确认可工作的写法：
        //   on_death = { HideWeapons(); }
        new EffectPatchRule
        {
            Id = "monster_dead_base",
            Feature = "怪物死亡特效移除",
            PathPattern = @".*/monsters/monster\.ot$",
            Action = EffectAction.ReplaceCode,
            TargetPattern = @"(?m)^([ \t]*)Life\s*\{\s*\}",
            Replacement =
                "$1Life\n" +
                "$1{\n" +
                "$1\t// FreiAtlas: monster_dead_base\n" +
                "$1\ton_spawned_dead = { RemoveEffects(); DisableRendering(); }\n" +
                "$1\ton_death = { RemoveEffects(); DisableRendering(); }\n" +
                "$1}",
            Description =
                "基类 monster.ot 的 Life 块注入死亡事件，2,028 个继承者全部生效。" +
                "这是清尸体的主力规则。",
            AppliedSentinel = "// FreiAtlas: monster_dead_base",
            ApplyMode = EffectApplyMode.FunctionBody
        },
        new EffectPatchRule
        {
            Id = "monster_dead_child",
            Feature = "怪物死亡特效移除",
            PathPattern = @".*/monsters/.*\.ot$",
            Action = EffectAction.AppendToBlock,
            // Life 里已经有业务逻辑的怪物，只能追加不能替换
            TargetPattern = "Life",
            Replacement =
                "\t\t// FreiAtlas: monster_dead_child\n" +
                "\t\ton_death = { RemoveEffects(); DisableRendering(); }\n" +
                "\t\ton_revive = { RemoveEffects(); EnableRendering(); }\n",
            Description =
                "给已自带 Life 业务的怪物追加死亡/复活事件，覆盖 674 个非空 Life 文件。",
            AppliedSentinel = "// FreiAtlas: monster_dead_child"
        },
        new EffectPatchRule
        {
            Id = "monster_dead_revive",
            Feature = "怪物死亡特效移除",
            PathPattern = @".*/monsters/monster\.ot$",
            Action = EffectAction.ReplaceCode,
            // 实测当前版本该值是 false 而非易刷规则里的 true。
            // 用宽松正则兼容两种取值，避免因版本差异导致规则静默失效。
            // 同样必须用 {}。参照 character.ot：
            //   on_revive = { ShowWeapons(); }
            TargetPattern = @"slow_animations_go_to_idle\s*=\s*(true|false)\s*\}",
            Replacement =
                "slow_animations_go_to_idle = false\n" +
                "\ton_start_Revive = { RemoveEffects(); EnableRendering(); }\n" +
                "}",
            Description = "复活时恢复渲染。缺少这条会让复活怪变成隐形，必须与上一条同时启用。",
            AppliedSentinel = "on_start_Revive = { RemoveEffects(); EnableRendering(); }"
        },

        // ── 去除黑暗 ──
        ..CreateRemoveDarkRules(1.0),

        // ── 去除迷雾 ──
        ..CreateRemoveFogRules(),

        // ── 异界去雾显线 ──
        ..CreateRemoveFog2Rules(),

        // ── 移除音效 ──
        new EffectPatchRule
        {
            Id = "sound_events",
            Feature = "移除音效",
            // PoE2 已把 .aoc 并入 .ao（实测客户端 .aoc 为 0，.ao 有 73,548）。
            // 实测 300 个 .ao 样本中 78 个含 SoundEvents 块，规则有效。
            //
            // 路径必须收紧。全量 metadata/ 会命中 73,548 个文件，把环境背景音、
            // NPC 台词、地形音效全部清空，游戏会变成默片。这里只保留三类：
            //   effects/spells      技能音效（玩家真正想消掉的爆音来源）
            //   effects/environment 环境特效音
            //   monsters            怪物音效
            // 刻意排除 metadata/effects/microtransactions（7,028 个），
            // 那是玩家付费购买的商城外观，清掉音效等于破坏已购内容。
            PathPattern = @".*metadata/(effects/spells|effects/environment|monsters)/.*\.(ao|aoc)$",
            Action = EffectAction.ClearBlock,
            TargetPattern = "SoundEvents",
            Description = "清空技能与怪物动画配置里的音效块（约 1.6 万个文件），消除密集战斗的爆音掉帧。保留环境背景音、NPC 台词与商城外观音效。",
            IsDangerous = true
        },

        // ── 视距调整 ──
        CreateViewDistanceRule(3.0, "view_distance"),

        // ── 小地图全开 ──
        ..CreateMinimapRules(),


        // ── 技能特效削减 ──
        new EffectPatchRule
        {
            Id = "spell_animation_blocks",
            Feature = "技能特效削减",
            // 同上：.aoc 已并入 .ao。实测 300 个样本中 82 个含 ParticleEffects、26 个含 TrailsEffects。
            PathPattern = @".*metadata/effects/spells/" + SpellGuard + @".*\.(ao|aoc)$",
            Action = EffectAction.ClearBlock,
            TargetPattern = "ParticleEffects|WindEvents|TrailsEffects",
            Description = "清空技能动画配置里的粒子、风、拖尾块。保护传送门、黑洞、诅咒光环等关键特效。"
        },
        new EffectPatchRule
        {
            Id = "spell_effect_packages",
            Feature = "技能特效削减",
            PathPattern = @".*metadata/effects/spells/" + SpellGuard + @".*\.epk$",
            Action = EffectAction.ReplaceCode,
            TargetPattern = ".*",
            Replacement = string.Empty,
            Description = "清空技能特效包内容。与上一条配合才能彻底移除技能特效。"
        },

        // ── 一键全清 ──
        new EffectPatchRule
        {
            Id = "all_particles",
            Feature = "一键全清",
            PathPattern = @".*metadata/particles/" + SpellGuard + @".*\.(pet|trl)$",
            Action = EffectAction.ReplaceCode,
            TargetPattern = ".*",
            Replacement = "0",
            Description = "把粒子与轨迹文件内容整体替换为 0，移除绝大部分特效。最激进的做法。",
            AppliedSentinel = null,
            IsDangerous = true
        },

        // ── 后处理精简（整函数替换）──
        //
        // 策略来源：ExileForge 用「把整个着色器函数换成空实现」替代逐行改代码。
        // 这比删 .env 键有效得多 —— 删配置只是让引擎回退默认值，换函数是直接切断渲染。
        //
        // 10 条规则全部已用真实 PoE2 客户端（0.5.5e）的着色器逐条验证：
        // 正则命中、括号配平、幂等特征、二次应用稳定，四项全过。
        //
        // ExileForge 原文里的另外 5 条（gaussianblur/globalilluminationsh/terrainburn/
        // postprocessoutput/copytexture）在当前版本函数名已变，正则匹配不到任何内容，
        // 因此不收录。宁可少做，也不写一条静默失效的规则。
        ..CreateShaderPostProcessRules(),

        // ── 环境雾关闭 ──
        //
        // 真实格式与 ExileForge 的描述完全不同：.fxgraph 是 JSON 节点图
        // （version/nodes/links），不是它说的 `GROUP xxx { prologue: {{ }} }`
        // HLSL 风格。照抄它的正则一条都匹配不到。
        //
        // 实测 PoE2 客户端 12,381 个 .fxgraph 里只有 15 个含 ScreenspaceFog，
        // 其中只有 5 个是纯环境雾（materials/environment 下），另外 10 个是
        // 功能性视觉：引导光追踪、Ultimatum 血雾、BOSS 技能预警、藏身处、
        // 边界遮罩 —— 清掉它们会影响玩法，所以只处理这 5 个。
        //
        // 改法：把 stage=PixelOutput 的 AlbedoColor 节点换成输出全透明的常量。
        // 节点数与连线数都不变，渲染图结构完好。
        //
        // 注意 .fxgraph 是 UTF-16LE **双重 BOM**（FF FE FF FE），
        // 编解码由 EffectTextCodec 的 Utf16LeWithDoubleBom 负责。
        new EffectPatchRule
        {
            Id = "fog_env_disable",
            Feature = "环境雾关闭",
            PathPattern = @".*metadata/materials/environment/screenspacefog(mild|overwater|_cutout|env)?\.fxgraph$",
            Action = EffectAction.FogDisable,
            TargetPattern = "AlbedoColor|PixelOutput",
            Replacement = string.Empty,
            Description =
                "把环境雾的像素输出改成全透明常量，画面不再有屏幕空间雾。" +
                "只处理 materials/environment 下的 5 个纯环境雾，" +
                "不影响引导光、血雾、BOSS 预警等功能性视觉。",
            AppliedSentinel = "\"frei_atlas_fog_disabled\""
        }
    ];

    /// <summary>
    /// 后处理精简：只改常量，不注入任何新标识符。
    ///
    /// 这条约束是从一次真实崩机事故得出的。上一版用「整函数替换成 return 常量」
    /// 的写法，注入的 SAMPLE_TEX2D / src_sampler / SamplerPointClamp 这些名字
    /// 在 blur.hlsl、depthoffield.hlsl、screenspaceambientocclusion.hlsl 里
    /// 根本不存在（每个着色器有自己的采样器名与采样函数），着色器编译失败，
    /// 客户端启动后直接闪退，连登录界面都进不去。
    ///
    /// 现在改成只替换函数体内已有的常量表达式：
    ///   · 正则只匹配原文件里真实存在的一行
    ///   · 替换内容只由该行已有的标识符和字面量组成
    ///   · 因此不可能引入未定义符号
    ///
    /// 代价是有三条规则做不了（BuildReflection 的返回值来自近 9,000 字符的
    /// 逻辑链、ComputeScreenspaceRays 必须返回结构体、ApplyBloom 会与旧规则
    /// 叠加冲突），这三条宁可放弃也不冒险。
    /// </summary>
    private static EffectPatchRule[] CreateShaderPostProcessRules()
    {
        // PathPattern 统一用后缀锚定，避免误伤其他同名文件
        static string P(string file) => @".*shaders/" + file + "$";

        return
        [
            new EffectPatchRule
            {
                Id = "pp_bloom_cutoff",
                Feature = "后处理精简",
                PathPattern = P(@"bloomcutoff\.hlsl"),
                Action = EffectAction.ReplaceCode,
                // 原行：float mult = max(0.0f, luminance - cutoff) * intensity;
                TargetPattern = @"float\s+mult\s*=\s*max\s*\(\s*0\.0f\s*,\s*luminance\s*-\s*cutoff\s*\)\s*\*\s*intensity\s*;",
                Replacement = "float mult = 0.0f;",
                Description = "泛光截止乘数归零，泛光完全消失。",
                AppliedSentinel = "float mult = 0.0f;"
            },
            new EffectPatchRule
            {
                Id = "pp_bloom_gather",
                Feature = "后处理精简",
                PathPattern = P(@"bloomgather\.hlsl"),
                Action = EffectAction.ReplaceCode,
                // 原行：weight = pow(weight_mult, mip_level);
                TargetPattern = @"weight\s*=\s*pow\s*\(\s*weight_mult\s*,\s*mip_level\s*\)\s*;",
                // 不能写成 weight = 0.0f;：该文件第 54 行的注释里已经有这一串，
                // 幂等检测会误判为「已打过补丁」而整条跳过。
                Replacement = "weight = weight_mult * 0.0f;",
                Description = "泛光收集权重归零，泛光链路断开。",
                AppliedSentinel = "weight = weight_mult * 0.0f;"
            },
            new EffectPatchRule
            {
                Id = "pp_blur",
                Feature = "后处理精简",
                PathPattern = P(@"blur\.hlsl"),
                Action = EffectAction.ReplaceCode,
                // 原行：int radius = max(radii_int.x, radii_int.y);
                // 替换连同下一行的 [loop] 特性一起写回，让幂等特征够长（测试门槛 15 字符）。
                // [loop] 是原文件里已有的写法，不是新标识符。
                TargetPattern = @"int\s+radius\s*=\s*max\s*\(\s*radii_int\.x\s*,\s*radii_int\.y\s*\)\s*;\r?\n\t\[loop\]",
                Replacement = "int radius = 0;\n\t[loop]",
                Description = "模糊半径归零，只采样中心像素。",
                AppliedSentinel = "int radius = 0;\n\t[loop]"
            },
            new EffectPatchRule
            {
                Id = "pp_screenspace_shadows",
                Feature = "后处理精简",
                PathPattern = P(@"screenspaceshadows\.hlsl"),
                Action = EffectAction.ReplaceCode,
                // 原行：float light_amount = 1e-4f;
                TargetPattern = @"float\s+light_amount\s*=\s*1e-4f\s*;",
                Replacement = "float light_amount = 0.0f;",
                Description = "阴影光照累加归零，屏幕空间阴影不再产生遮蔽。性能收益最大。",
                AppliedSentinel = "float light_amount = 0.0f;"
            },
            new EffectPatchRule
            {
                Id = "pp_depth_minmax",
                Feature = "后处理精简",
                PathPattern = P(@"depthminmax\.hlsl"),
                Action = EffectAction.ReplaceCode,
                // 原行：int scale = int(dst_pixel_size / src_pixel_size);
                // 替换连同后面两行一起写回：既能保持零新标识符，又让特征够长。
                // 该文件用两个空格缩进（与 blur.hlsl 的制表符不同）。
                TargetPattern = @"int\s+scale\s*=\s*int\s*\(\s*dst_pixel_size\s*/\s*src_pixel_size\s*\)\s*;\r?\n  float4 min_val = 1e5f;\r?\n  float4 max_val = -1e5f;",
                Replacement = "int scale = 1;\n  float4 min_val = 1e5f;\n  float4 max_val = -1e5f;",
                Description = "深度极值缩放因子归 1，省掉全屏归约。",
                AppliedSentinel = "int scale = 1;\n  float4 min_val = 1e5f;"
            },
            new EffectPatchRule
            {
                Id = "pp_depth_of_field",
                Feature = "后处理精简",
                PathPattern = P(@"depthoffield\.hlsl"),
                Action = EffectAction.ReplaceCode,
                // 原行：uint radius = uint(max(blur_direction.x, blur_direction.y));
                TargetPattern = @"uint\s+radius\s*=\s*uint\s*\(\s*max\s*\(\s*blur_direction\.x\s*,\s*blur_direction\.y\s*\)\s*\)\s*;",
                Replacement = "uint radius = 0u;",
                Description = "景深模糊半径归零，远景不再模糊。",
                AppliedSentinel = "uint radius = 0u;"
            },
            new EffectPatchRule
            {
                Id = "pp_ambient_occlusion",
                Feature = "后处理精简",
                PathPattern = P(@"screenspaceambientocclusion\.hlsl"),
                Action = EffectAction.ReplaceCode,
                // 原行：float3 ambient_light = float3(1.0f, 1.0f, 1.0f) * pow(saturate(ComputeAmbientLight(tex_coord)), 3.0f);
                TargetPattern = @"float3\s+ambient_light\s*=\s*float3\s*\(\s*1\.0f\s*,\s*1\.0f\s*,\s*1\.0f\s*\)\s*\*\s*pow\s*\(\s*saturate\s*\(\s*ComputeAmbientLight\s*\(\s*tex_coord\s*\)\s*\)\s*,\s*3\.0f\s*\)\s*;",
                Replacement = "float3 ambient_light = float3(1.0f, 1.0f, 1.0f);",
                Description = "环境光不再乘 AO 遮蔽系数，暗角消失，画面变亮。",
                AppliedSentinel = "float3 ambient_light = float3(1.0f, 1.0f, 1.0f);"
            },
        ];
    }

    /// <summary>按功能名分组，供界面渲染。</summary>
    public static IReadOnlyList<EffectFeature> Features { get; } =
        All.GroupBy(r => r.Feature)
           .Select(g => new EffectFeature(g.Key, g.ToList()))
           .ToList();

    /// <summary>
    /// 生成视距调整规则。支持动态调节倍率（如 1.5, 2.0, 2.5, 3.0 等）。
    /// </summary>
    public static EffectPatchRule CreateViewDistanceRule(double ratio = 2.0, string? customId = null)
    {
        if (ratio < 1.1 || ratio > 5.0) ratio = 2.0;
        var ratioStr = ratio.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "f";
        var call = $"CreateCameraZoomNode(1000000000.0f, 1000000000.0f, {ratioStr});";
        return new EffectPatchRule
        {
            Id = customId ?? (ratio == 3.0 ? "view_distance" : $"view_distance:{ratio.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}"),
            Feature = "视距调整",
            PathPattern = @".*metadata/characters/character\.ot$",
            Action = EffectAction.ReplaceCode,
            // 既匹配原始未打补丁的 team = 1 }，也匹配已打过任意其他倍率的 on_initial_position_set 块，支持直接热切换倍率
            TargetPattern = @"team\s*=\s*1(\s*\r?\n\s*on_initial_position_set\s*=\s*\{[^}]*\})?\s*\}",
            Replacement =
                "team = 1\n" +
                $"\ton_initial_position_set = {{ {call} }}\n" +
                "}",
            Description = $"拉远镜头，视野范围扩大约 {ratio:0.0} 倍。",
            AppliedSentinel = call
        };
    }

    /// <summary>
    /// 生成环境去黑暗与全局亮度调整规则。
    /// 支持 5 档纯去黑暗曝光度（0.1/0.3/0.5/1.0/1.5）与 3 档包含光影增强（990.5/991/991.5）。
    /// </summary>
    public static IReadOnlyList<EffectPatchRule> CreateRemoveDarkRules(double level = 1.0)
    {
        if (level > 900)
        {
            var mult = Math.Round(level - 990.0, 2);
            if (mult <= 0) mult = 1.0;
            var multStr = mult.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            return
            [
                new EffectPatchRule
                {
                    Id = "dark_light_enhance",
                    Feature = "去除黑暗",
                    PathPattern = EnvProtectedPattern,
                    Action = EffectAction.ReplaceCode,
                    TargetPattern = @"""multiplier""\s*:\s*(?:0(?:\.[0-4]\d*)?)(?=\s*[,}])",
                    Replacement = $"\"multiplier\": {multStr}",
                    Description = $"保留场景光影，将过暗光源亮度倍率提升至 {multStr}。"
                }
            ];
        }

        if (level < 0.05 || level > 5.0) level = 1.0;
        var expStr = level.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        return
        [
            new EffectPatchRule
            {
                Id = "dark_light_objects",
                Feature = "去除黑暗",
                PathPattern = EnvProtectedPattern,
                Action = EffectAction.RemoveObject,
                TargetPattern = "directional_light|player_light|environment_mapping|global_illumination",
                Description = "移除场景光源与全局光照，显著提升暗处可见度（保护特殊 Boss 与赛季机制场景）。"
            },
            new EffectPatchRule
            {
                Id = "dark_exposure",
                Feature = "去除黑暗",
                PathPattern = EnvProtectedPattern,
                Action = EffectAction.ReplaceCode,
                TargetPattern = @"""exposure""\s*:\s*-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?(?=\s*[,}])",
                Replacement = $"\"exposure\": {expStr}",
                Description = $"统一全局曝光度为 {expStr}。"
            },
            new EffectPatchRule
            {
                Id = "dark_bloom_cutoff",
                Feature = "去除黑暗",
                PathPattern = @".*shaders/bloomcutoff\.hlsl$",
                Action = EffectAction.ReplaceCode,
                ApplyMode = EffectApplyMode.FunctionBodyBraces,
                TargetPattern = @"float4\s+BloomCutoff\s*\([^)]*\)\s*:\s*[A-Za-z0-9_]+\s*\{",
                Replacement = "$1\n  // FreiAtlas: dark_bloom_cutoff\n  return 0.0f;\n}",
                Description = "泛光截止函数直接返回 0，关闭泛光。",
                AppliedSentinel = "// FreiAtlas: dark_bloom_cutoff\n  return 0.0f;\n}"
            },
            new EffectPatchRule
            {
                Id = "dark_bloom_gather",
                Feature = "去除黑暗",
                PathPattern = @".*shaders/bloomgather\.hlsl$",
                Action = EffectAction.ReplaceCode,
                ApplyMode = EffectApplyMode.FunctionBodyBraces,
                TargetPattern = @"float4\s+BloomGather\s*\([^)]*\)\s*:\s*[A-Za-z0-9_]+\s*\{",
                Replacement = "$1\n  // FreiAtlas: dark_bloom_gather\n  return 0.0f;\n}",
                Description = "泛光收集函数直接返回 0，关闭泛光。",
                AppliedSentinel = "// FreiAtlas: dark_bloom_gather\n  return 0.0f;\n}"
            }
        ];
    }

    /// <summary>
    /// 生成环境去雾规则（对齐易刷 remove_fog）。
    /// 清除场景 .env 中的迷雾、水面与后处理，并清除荒林迷雾与天气挂件迷雾。
    /// </summary>
    public static IReadOnlyList<EffectPatchRule> CreateRemoveFogRules() =>
    [
        new EffectPatchRule
        {
            Id = "fog_env_objects",
            Feature = "去除迷雾",
            PathPattern = EnvFogProtectedPattern,
            Action = EffectAction.RemoveObject,
            TargetPattern = "screenspace_fog|fog|area|water|post_transform",
            Description = "移除场景配置中的雾、水面与后处理变换（保护特殊 Boss、Uber 与赛季机制场景）。"
        },
        new EffectPatchRule
        {
            Id = "fog_affliction_attachment",
            Feature = "去除迷雾",
            PathPattern = @".*metadata/effects/environment/league_affliction/fogattachment\.(ao|aoc)$",
            Action = EffectAction.ClearBlock,
            TargetPattern = "AnimatedRender|FixedMesh|Lights",
            Description = "清除荒林（Affliction）迷雾挂件动画渲染、静态网格与光源。"
        },
        new EffectPatchRule
        {
            Id = "fog_weather_d_fog",
            Feature = "去除迷雾",
            PathPattern = @".*metadata/effects/weather_attachments/g3_3_new/d_fog\.(ao|aoc)$",
            Action = EffectAction.ClearBlock,
            TargetPattern = "AnimatedRender|FixedMesh",
            Description = "清除新版天气挂件（d_fog）动画渲染与静态网格。"
        }
    ];

    /// <summary>
    /// 生成异界去雾显线规则（对齐易刷 remove_fog2，POE2 专有）。
    /// 清除异界图鉴迷雾材质混合模式与输出范围，并将风效连线波纹增强为 0.75f。
    /// </summary>
    public static IReadOnlyList<EffectPatchRule> CreateRemoveFog2Rules() =>
    [
        new EffectPatchRule
        {
            Id = "fog2_worldmap_blend",
            Feature = "异界去雾显线",
            PathPattern = @".*metadata/materials/environment/worldmap/worldmap_fogofwar\.fxgraph$",
            Action = EffectAction.ReplaceCode,
            TargetPattern = "DownScaledAddablend",
            Replacement = "None",
            Description = "将异界迷雾材质混合模式改为 None，消除迷雾混合层。"
        },
        new EffectPatchRule
        {
            Id = "fog2_worldmap_output",
            Feature = "异界去雾显线",
            PathPattern = @".*metadata/materials/environment/worldmap/worldmap_fogofwar\.fxgraph$",
            Action = EffectAction.ReplaceCode,
            TargetPattern = @"(?:""output""\s*:\s*\[\s*0(?:\s*,\s*0){3}\s*\]\s*,\s*)*""ui_size""\s*:\s*\[",
            Replacement = "\"output\": [0, 0, 0, 0],\n\"ui_size\": [",
            Description = "将异界迷雾输出区域重置为 0，彻底消除异界地图遮罩。",
            AppliedSentinel = "\"output\": [0, 0, 0, 0],\n\"ui_size\": ["
        },
        new EffectPatchRule
        {
            Id = "fog2_worldmap_min",
            Feature = "异界去雾显线",
            PathPattern = @".*metadata/materials/environment/worldmap/worldmap_fogofwar\.fxgraph$",
            Action = EffectAction.ReplaceCode,
            TargetPattern = @"""min""\s*:\s*-1\.0",
            Replacement = "\"min\": 0.0",
            Description = "将异界迷雾深度最小值归零。"
        },
        new EffectPatchRule
        {
            Id = "fog2_worldmap_max",
            Feature = "异界去雾显线",
            PathPattern = @".*metadata/materials/environment/worldmap/worldmap_fogofwar\.fxgraph$",
            Action = EffectAction.ReplaceCode,
            TargetPattern = @"""max""\s*:\s*1\.0",
            Replacement = "\"max\": 0.0",
            Description = "将异界迷雾深度最大值归零。"
        },
        new EffectPatchRule
        {
            Id = "fog2_windeffects",
            Feature = "异界去雾显线",
            PathPattern = @".*shaders/renderer/nodes/windeffects\.ffx$",
            Action = EffectAction.ReplaceCode,
            TargetPattern = @"=\s*0\.5f\s*-\s*abs\(wind_phase",
            Replacement = "= 0.75f - abs(wind_phase",
            Description = "将异界连线风效波纹偏移增强至 0.75f，使连线更清晰。",
            AppliedSentinel = "= 0.75f - abs(wind_phase"
        }
    ];

    /// <summary>
    /// 生成小地图全开（含轮廓高亮与已探索/未探索区域区分）规则集。
    /// 参考 POE2 小助手（Talagio90）三文件联合着色器改写方案：
    /// 1. minimap_visibility_pixel.hlsl: 底色设置为可见阈值底色（默认 0.18f）
    /// 2. minimap_pixel.hlsl: 采样后增加保底阈值
    /// 3. minimap_blending_pixel.hlsl: 保留原版真实探索度，注入已探索色、带发光的边界色、探索中边缘色，以及几何边缘掩码。
    /// </summary>
    public static IReadOnlyList<EffectPatchRule> CreateMinimapRules(MinimapColorSettings? colorSettings = null)
    {
        var settings = colorSettings ?? MinimapColorSettings.Default;
        static string ToHlslFloat(double val)
        {
            var str = Math.Round(val, 2).ToString("0.##", CultureInfo.InvariantCulture);
            return str.Contains('.') ? $"{str}f" : $"{str}.0f";
        }

        var thresh = Math.Clamp(settings.VisibilityThreshold, 0.01f, 1.0f);
        var threshStr = ToHlslFloat(thresh);

        var expR = ToHlslFloat(Math.Clamp(settings.ExploredR / 255.0f, 0f, 1f));
        var expG = ToHlslFloat(Math.Clamp(settings.ExploredG / 255.0f, 0f, 1f));
        var expB = ToHlslFloat(Math.Clamp(settings.ExploredB / 255.0f, 0f, 1f));
        var expA = ToHlslFloat(Math.Clamp(settings.ExploredA / 255.0f, 0f, 1f));

        var borderGlow = Math.Max(0f, settings.BorderGlow);
        var borderR = ToHlslFloat((settings.BorderR / 255.0f) * borderGlow);
        var borderG = ToHlslFloat((settings.BorderG / 255.0f) * borderGlow);
        var borderB = ToHlslFloat((settings.BorderB / 255.0f) * borderGlow);
        var borderA = ToHlslFloat(Math.Clamp(settings.BorderA / 255.0f, 0f, 1f));

        var explGlow = Math.Max(0f, settings.ExploringGlow);
        var explR = ToHlslFloat((settings.ExploringR / 255.0f) * explGlow);
        var explG = ToHlslFloat((settings.ExploringG / 255.0f) * explGlow);
        var explB = ToHlslFloat((settings.ExploringB / 255.0f) * explGlow);
        var explA = ToHlslFloat(Math.Clamp(settings.ExploringA / 255.0f, 0f, 1f));

        return
        [
            new EffectPatchRule
            {
                Id = "minimap_visibility_reset",
                Feature = "小地图全开",
                PathPattern = @".*shaders/minimap_visibility_pixel\.hlsl$",
                Action = EffectAction.ReplaceCode,
                // 第一个分量必须匹配任意浮点数，不能只匹配 0.x。
                // 阈值调高（如 1.0f）后若正则只认 0.x，就再也匹配不到已写入的值，
                // 用户无法把阈值改回去 —— 该规则会永久失效。
                // 后两个分量与 alpha 保持严格 0 / 1，避免误匹配其它 res_color 语句。
                TargetPattern = @"res_color\s*=\s*float4\(\s*\d+(?:\.\d+)?\s*f?\s*,\s*0(?:\.0*)?f?\s*,\s*0(?:\.0*)?f?\s*,\s*1(?:\.0*)?f?\s*\);",
                Replacement = $"res_color = float4({threshStr}, 0.0f, 0.0f, 1.0f);",
                Description = $"将小地图未探索区域底色提升为可见阈值（{threshStr}）。",
                AppliedSentinel = $"res_color = float4({threshStr}, 0.0f, 0.0f, 1.0f);"
            },
            new EffectPatchRule
            {
                Id = "minimap_sample_clamp",
                Feature = "小地图全开",
                PathPattern = @".*shaders/minimap_pixel\.hlsl$",
                Action = EffectAction.ReplaceCode,
                TargetPattern = @"(?:float4\s+color\s*=\s*SAMPLE_TEX2D\(\s*visibility_sampler,\s*SamplerLinearClampOffsetNoBias,\s*frac\(map_coord\)\s*\);\s*(?:if\s*\(color\.r\s*<\s*[\d\.]+f?\)\s*\{\s*color\.r\s*=\s*[\d\.]+f?;\s*\}\s*)?return\s+color;|return\s+SAMPLE_TEX2D\(\s*visibility_sampler,\s*SamplerLinearClampOffsetNoBias,\s*frac\(map_coord\)\s*\);)",
                Replacement = $"float4 color = SAMPLE_TEX2D( visibility_sampler, SamplerLinearClampOffsetNoBias, frac(map_coord) );\n\tif (color.r < {threshStr}) {{ color.r = {threshStr}; }}\n\treturn color;",
                Description = $"对小地图可见度采样增加保底阈值（{threshStr}）。",
                AppliedSentinel = $"if (color.r < {threshStr})"
            },
            new EffectPatchRule
            {
                Id = "minimap_legacy_cleanup",
                Feature = "小地图全开",
                PathPattern = @".*shaders/minimap_blending_pixel\.hlsl$",
                Action = EffectAction.ReplaceCode,
                TargetPattern = @"float\s+visibility\s*=\s*1\.0f;",
                Replacement = "float visibility = saturate(walkability_sample.g * 2.0f);",
                Description = "清理旧版写死 visibility=1.0 的残留，恢复真实探索度计算。",
                AppliedSentinel = "float visibility = saturate(walkability_sample.g * 2.0f);"
            },
            new EffectPatchRule
            {
                Id = "minimap_walkable_color",
                Feature = "小地图全开",
                PathPattern = @".*shaders/minimap_blending_pixel\.hlsl$",
                Action = EffectAction.ReplaceCode,
                TargetPattern = @"float4\s+walkable_color\s*=\s*float4\([^)]*\);",
                Replacement = $"float4 walkable_color = float4({expR}, {expG}, {expB}, {expA});",
                Description = $"设置小地图已探索区域填充色（RGBA: {settings.ExploredR}, {settings.ExploredG}, {settings.ExploredB}, {settings.ExploredA}）。",
                AppliedSentinel = $"float4 walkable_color = float4({expR}, {expG}, {expB}, {expA});"
            },
            new EffectPatchRule
            {
                Id = "minimap_border_color",
                Feature = "小地图全开",
                PathPattern = @".*shaders/minimap_blending_pixel\.hlsl$",
                Action = EffectAction.ReplaceCode,
                TargetPattern = @"float4\s+walkability_map_color\s*=\s*lerp\(\s*walkable_color\s*,\s*float4\([^)]*\)\s*,\s*walkable_to_edge_ratio\s*\);",
                Replacement = $"float4 walkability_map_color = lerp(walkable_color, float4({borderR}, {borderG}, {borderB}, {borderA}), walkable_to_edge_ratio);",
                Description = $"设置小地图未探索边界轮廓色与发光（BorderGlow={settings.BorderGlow}）。",
                AppliedSentinel = $"float4 walkability_map_color = lerp(walkable_color, float4({borderR}, {borderG}, {borderB}, {borderA}), walkable_to_edge_ratio);"
            },
            new EffectPatchRule
            {
                Id = "minimap_exploring_color",
                Feature = "小地图全开",
                PathPattern = @".*shaders/minimap_blending_pixel\.hlsl$",
                Action = EffectAction.ReplaceCode,
                // Replacement 里的 $1 引用捕获组，必须走 FunctionBody 才会展开。
                // Literal 模式用 MatchEvaluator 返回字面量，会把 "$1float4(...)"
                // 原样写进着色器 —— HLSL 不支持 $ 语法，编译必定失败。
                ApplyMode = EffectApplyMode.FunctionBody,
                TargetPattern = @"(walkability_map_color\s*=\s*lerp\(\s*walkability_map_color(?:\s*\*\s*float4\(\s*1\.0f\s*,\s*1\.0f\s*,\s*1\.0f\s*,\s*visibility\s*\))?\s*,\s*)float4\([^)]*\)(?=\s*,\s*pow\(\s*saturate\(\s*unexplored_edge\s*\)\s*,\s*25\.0f\s*\)\s*\);)",
                Replacement = $"$1float4({explR}, {explG}, {explB}, {explA})",
                Description = $"设置小地图探索前线动态光晕色（ExploringGlow={settings.ExploringGlow}）。",
                // 该哨兵是裸颜色值，若文件里恰好存在同色 float4 会被误判为已应用。
                // 该局限无法靠「哨兵 + 正则」双重判定消除：Replacement 产物本身
                // 也满足 TargetPattern，正则无法区分已应用与未应用。
                // 详见 EffectRuleApplier.ApplyReplaceCode 的说明与第三轮评审报告 P1-3。
                AppliedSentinel = $"float4({explR}, {explG}, {explB}, {explA})"
            },
            new EffectPatchRule
            {
                Id = "minimap_content_mask",
                Feature = "小地图全开",
                PathPattern = @".*shaders/minimap_blending_pixel\.hlsl$",
                Action = EffectAction.ReplaceCode,
                TargetPattern = @"(?<!float\s+map_content_mask\s*=\s*saturate[^\n]+\n[^\n]+\n\s*)return\s+OutputSDR\(\s*res_color\s*,\s*sdr_scale\s*\);",
                Replacement = "float map_content_mask = saturate(geometry_sample.a * 20.0f + walkability_sample.r * 5.0f);\n\tres_color.a *= map_content_mask;\n\treturn OutputSDR(res_color, sdr_scale);",
                Description = "注入小地图几何与可行走区域掩码，剔除地图外部无效黑边。",
                AppliedSentinel = "float map_content_mask = saturate(geometry_sample.a * 20.0f + walkability_sample.r * 5.0f);"
            }
        ];
    }
}

/// <summary>一个功能开关及其规则集合。</summary>
public sealed record EffectFeature(string Name, IReadOnlyList<EffectPatchRule> Rules)
{
}
