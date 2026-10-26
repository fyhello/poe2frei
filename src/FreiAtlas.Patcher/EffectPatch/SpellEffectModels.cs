namespace FreiAtlas.Patcher.EffectPatch;

/// <summary>
/// 一个技能特效单元。
///
/// 客户端里 metadata/effects/spells/ 下按技能分目录，目录名就是技能标识，
/// 例如 lightning_balllightning、crossbow_stormblast。
/// 用户选择要削减哪些技能，而不是一刀切。
/// </summary>
public sealed class SpellEffectTarget
{
    /// <summary>技能标识（目录名）。</summary>
    public required string SpellId { get; init; }

    /// <summary>一级分类，例如 monsters_effects、supports、ascension_classes。</summary>
    public required string Category { get; init; }

    /// <summary>该技能下的文件总数。</summary>
    public required int FileCount { get; init; }

    /// <summary>按扩展名统计，例如 { ".mat": 49, ".ao": 3 }。</summary>
    public required IReadOnlyDictionary<string, int> ExtensionCounts { get; init; }

    /// <summary>
    /// 是否在保护名单内。受保护的技能不允许选择削减——
    /// 它们承载关键玩法信息（传送门、地面危险区域、BOSS 技能预警等）。
    /// </summary>
    public required bool IsProtected { get; init; }

    /// <summary>保护原因，展示给用户。</summary>
    public string? ProtectReason { get; init; }

    /// <summary>该技能下可安全处理的文件数（排除不可修改的扩展名）。</summary>
    public required int ProcessableCount { get; init; }
}

/// <summary>
/// 技能特效扫描结果。
/// </summary>
public sealed record SpellEffectScanResult(
    IReadOnlyList<SpellEffectTarget> Targets,
    int TotalFiles,
    IReadOnlyDictionary<string, int> ExtensionTotals,
    string? Error)
{
    public static SpellEffectScanResult Failure(string error) => new([], 0, new Dictionary<string, int>(), error);

    /// <summary>可选择的技能数（排除受保护的）。</summary>
    public int SelectableCount => Targets.Count(t => !t.IsProtected);

    /// <summary>受保护的技能数。</summary>
    public int ProtectedCount => Targets.Count(t => t.IsProtected);
}

/// <summary>
/// 技能特效的保护名单与可处理扩展名定义。
/// </summary>
public static class SpellEffectPolicy
{
    /// <summary>
    /// 受保护的技能关键词。命中任一即不可选择削减。
    ///
    /// 这些技能承载关键玩法信息，清掉会导致：
    ///   - 看不见传送门，无法回城
    ///   - 看不见地面危险区域，角色莫名暴毙
    ///   - BOSS 技能无预警
    ///
    /// 名单来自易刷规则，但做了扩展：易刷只列了 9 个，实测客户端有 670 个技能目录，
    /// 这里补充了更多已知的关键机制。
    /// </summary>
    public static readonly IReadOnlyList<(string Keyword, string Reason)> ProtectedKeywords =
    [
        ("ground_effects", "地面危险区域，看不见会导致暴毙"),
        ("portal", "传送门，看不见无法回城"),
        ("black_hole", "黑洞技能，致命机制"),
        ("volatile_dead", "不稳定亡魂，自爆机制"),
        ("curse_aura", "诅咒光环，需看清范围"),
        ("atlasofworldsbosses", "终局 BOSS 技能，需预警"),
        ("atlasmemories", "地图记忆机制"),
        ("atlasexiles", "地图流放者"),
        ("offering", "献祭机制"),
    ];

    /// <summary>
    /// 可以安全处理的扩展名。
    ///
    /// 实测技能特效目录下共 8 种扩展名，这里只列已确认可安全处理的：
    ///   .ao      动画对象，清空粒子块
    ///   .epk     特效包，整体清空
    ///   .pet     粒子定义，置 0
    ///   .trl     轨迹定义，置 0
    ///
    /// 刻意排除：
    ///   .mat      材质定义，占 69%，但清空会破坏渲染引用
    ///   .fxgraph  特效图，节点结构复杂，需先研究
    ///   .ot       脚本，改了可能影响逻辑
    ///   .act      动作定义
    /// </summary>
    public static readonly IReadOnlySet<string> ProcessableExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".ao", ".epk", ".pet", ".trl"
        };

    /// <summary>判断一个技能目录是否受保护。</summary>
    public static (bool IsProtected, string? Reason) CheckProtected(string spellId)
    {
        foreach (var (keyword, reason) in ProtectedKeywords)
        {
            if (spellId.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return (true, reason);
            }
        }
        return (false, null);
    }
}
