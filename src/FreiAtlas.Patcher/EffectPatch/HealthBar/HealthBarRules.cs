using System.Text.RegularExpressions;

namespace FreiAtlas.Patcher.EffectPatch.HealthBar;

/// <summary>
/// 「头顶血条」补丁规则。
///
/// 游戏里有两套完全独立的小怪血条：
///   · 头顶血条（mini life bar）—— 每个怪物独立一条，贴在怪物身上，
///     屏幕内的怪可同时显示。由 <c>hide_mini_life_bar</c> 控制。
///   · 顶部大血条（static life bar）—— 屏幕顶部固定位置，鼠标指向谁显示谁。
///     由 <c>boss_life_bar</c> + MonsterVarieties.BossHealthBar 共同控制。
///
/// 本模块只处理前者，不触碰后者。
///
/// 字段语义（来自 metadata/monsters/bossbase.ot 第 271-277 行的官方注释）：
///   client / Render 块内的 hide_mini_life_bar = true 表示隐藏头顶血条，
///   原文写着「every boss that inherits this uses the static life bar,
///   so doesn't need a mini one」——Boss 用大血条，因此隐藏小血条。
///   不声明该字段时为显示（客户端 8,228 个 .ot 均未声明）。
///
/// 另有 11 个文件在 Stats 块用 base_/set_base_ 前缀声明同一开关，
/// 与 set_max_frenzy_charges、set_cannot_die 等属于同一组属性修饰符，
/// 效果等价。
/// </summary>
public static class HealthBarRules
{
    /// <summary>
    /// 三条规则把头顶血条从隐藏改为显示。
    ///
    /// 正则只匹配值，保留原有缩进与行尾空白，因此不改变文件结构；
    /// 匹配失败时文件原样保留，不会误伤。
    /// </summary>
    public static IReadOnlyList<EffectPatchRule> All { get; } =
    [
        new EffectPatchRule
        {
            Id = "healthbar_mini_visible",
            Feature = "头顶血条",
            PathPattern = @".*\.ot$",
            Action = EffectAction.ReplaceCode,
            ApplyMode = EffectApplyMode.FunctionBody,
            TargetPattern = @"(?m)^(\s*)hide_mini_life_bar\s*=\s*true(\s*)$",
            Replacement = "${1}hide_mini_life_bar = false${2}",
            Description = "让原本隐藏的头顶血条显示（影响 42 个文件，含继承 bossbase 的 325 个 Boss）"
        },
        new EffectPatchRule
        {
            Id = "healthbar_base_visible",
            Feature = "头顶血条",
            PathPattern = @".*\.ot$",
            Action = EffectAction.ReplaceCode,
            ApplyMode = EffectApplyMode.FunctionBody,
            TargetPattern = @"(?m)^(\s*)base_hide_mini_life_bar\s*=\s*1(\s*)$",
            Replacement = "${1}base_hide_mini_life_bar = 0${2}",
            Description = "清除 Stats 块里的隐藏标记（8 个文件）"
        },
        new EffectPatchRule
        {
            Id = "healthbar_set_base_visible",
            Feature = "头顶血条",
            PathPattern = @".*\.ot$",
            Action = EffectAction.ReplaceCode,
            ApplyMode = EffectApplyMode.FunctionBody,
            TargetPattern = @"(?m)^(\s*)set_base_hide_mini_life_bar\s*=\s*1(\s*)$",
            Replacement = "${1}set_base_hide_mini_life_bar = 0${2}",
            Description = "清除 Stats 块里的 set_ 前缀隐藏标记（3 个文件）"
        }
    ];

    /// <summary>
    /// 该路径是否是本模块会改动的候选文件。
    ///
    /// 只用于规划阶段提前过滤：客户端 8,281 个 .ot 里只有 53 个声明了该字段，
    /// 其余不含 mini_life_bar 的文件无需读取内容。
    /// </summary>
    public static bool MayApply(string virtualPath)
        => !string.IsNullOrEmpty(virtualPath)
           && virtualPath.EndsWith(".ot", StringComparison.OrdinalIgnoreCase);
}
