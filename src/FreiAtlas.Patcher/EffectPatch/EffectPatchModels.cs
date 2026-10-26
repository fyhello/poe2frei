namespace FreiAtlas.Patcher.EffectPatch;

/// <summary>
/// 一条特效补丁规则。
///
/// 规则集内置在程序里，不允许用户自定义：改游戏脚本和着色器一旦出错会直接
/// 导致客户端崩溃，把这种能力开放给用户等于制造事故。
/// </summary>
public sealed class EffectPatchRule
{
    /// <summary>规则唯一标识，用于持久化用户勾选状态。</summary>
    public required string Id { get; init; }

    /// <summary>所属功能开关，同一功能的规则会一起启用。</summary>
    public required string Feature { get; init; }

    /// <summary>目标文件的虚拟路径正则。</summary>
    public required string PathPattern { get; init; }

    public required EffectAction Action { get; init; }

    /// <summary>要匹配的内容正则。ClearBlock 时是块名列表。</summary>
    public required string TargetPattern { get; init; }

    /// <summary>替换内容。RemoveObject 与 ClearBlock 不使用。</summary>
    public string Replacement { get; init; } = string.Empty;

    /// <summary>人类可读说明，展示在界面上。</summary>
    public required string Description { get; init; }

    /// <summary>高危规则默认不勾选。</summary>
    public bool IsDangerous { get; init; }

    /// <summary>
    /// 幂等特征串。内容里已含此串时表示补丁已应用，跳过替换。
    ///
    /// 为什么需要：ReplaceCode 会插入新内容，重复应用会叠加。
    /// 实测 bloomcutoff.hlsl 应用两次会从 1,215 字节涨到 1,298 字节。
    ///
    /// 必须用「注入内容的完整形态」而不是单个词，否则会误判到其他函数的同名语句。
    /// ClearBlock 与 RemoveObject 天然幂等（消除型操作），留空即可。
    /// </summary>
    public string? AppliedSentinel { get; init; }

    /// <summary>
    /// 当前客户端不支持该规则（目标文件类型不存在）。
    ///
    /// 实测发现 PoE2 已用 .ao 取代 .aoc，依赖 .aoc 的规则在现版本上命中数为 0。
    /// 这类规则保留定义以便后续版本适配，但界面上必须标记为不可用，
    /// 不能让用户勾选后以为生效了。
    /// </summary>
    public bool IsUnsupported { get; init; }

    /// <summary>
    /// ReplaceCode 的替换模式。
    ///
    /// 默认 <see cref="EffectApplyMode.Literal"/> 把 Replacement 当字面文本写入，
    /// 其中 <c>$</c> 原样保留。整函数替换需要保留函数签名、只换函数体，
    /// 必须用 <see cref="EffectApplyMode.FunctionBody"/> 让 <c>$1</c> 展开成捕获组。
    /// </summary>
    public EffectApplyMode ApplyMode { get; init; } = EffectApplyMode.Literal;
}

/// <summary>
/// ReplaceCode 的替换模式。
/// </summary>
public enum EffectApplyMode
{
    /// <summary>Replacement 原样写入，不做替换语法展开。</summary>
    Literal = 0,

    /// <summary>
    /// Replacement 按 Regex 替换语法展开，<c>$1</c> 引用第一个捕获组。
    /// 用于「保留函数签名、只替换函数体」的着色器整函数改写。
    /// </summary>
    FunctionBody = 1,

    /// <summary>
    /// 同 <see cref="FunctionBody"/>，但函数体结束位置用**括号配平**确定，不用正则。
    ///
    /// 为什么必须单独一种模式：着色器函数体可能长达数万字符，内部含 #if 预处理块、
    /// 嵌套大括号、行首无缩进的 <c>}</c>。用 <c>[\s\S]*?</c> 或 <c>[\s\S]*\z</c>
    /// 都会吃掉后续整个文件 —— 实测 postprocessuber.hlsl 的 ApplyBloom 只有 515 字符，
    /// 但贪婪正则从函数头一路吃到文件末尾，多删 37,195 字符，把后面所有 shader
    /// 声明全抹掉，客户端必然崩溃。
    ///
    /// 该模式下 TargetPattern 只需匹配「函数签名 + 开括号」，函数体的结束位置
    /// 由 <see cref="EffectRuleApplier"/> 逐字符计数得到。
    /// </summary>
    FunctionBodyBraces = 2
}

/// <summary>
/// 补丁动作类型。
///
/// 注意：枚举成员的值必须唯一。优先级不能直接写进枚举值——两个成员取同值
/// 会让它们在运行期成为同一个值，ToString 也会返回同一个名字，规则会被静默
/// 当成另一种动作执行。优先级见 <see cref="EffectActionExtensions.Priority"/>。
/// </summary>
public enum EffectAction
{
    /// <summary>内容替换：按正则匹配后替换成新内容。</summary>
    ReplaceCode = 0,

    /// <summary>清空配置块内容，保留空括号。</summary>
    ClearBlock = 1,

    /// <summary>删除键：移除指定的键值对。</summary>
    RemoveObject = 2,

    /// <summary>
    /// 在配置块的闭合括号前追加内容，保留块内已有内容。
    ///
    /// 用于 .ot 里的 Life 块：674 个怪物文件在 Life 里写了业务逻辑
    /// （武器挂载、状态机等），整体替换会破坏这些逻辑；但我们又必须
    /// 在里面加 on_death / on_revive 才能清尸体。只能追加。
    ///
    /// TargetPattern 是块名（如 <c>Life</c>），块边界用括号配平确定。
    /// </summary>
    AppendToBlock = 3,

    /// <summary>
    /// 关闭 .fxgraph 的屏幕空间雾：把 stage=PixelOutput 的 AlbedoColor 节点
    /// 替换成输出全透明的 ConstantFloat4 节点。
    ///
    /// 为什么需要专用动作而不是通用 JSON 编辑：.fxgraph 是可视化节点图，
    /// 节点由 type/index/stage 三元组定位，连线按 type 名引用上游节点。
    /// 直接删节点会让下游连线指向不存在的源，渲染图断裂；
    /// 只换节点内容（保持 index 与位置）才是安全的改法。
    ///
    /// TargetPattern 形如 <c>AlbedoColor|PixelOutput</c>：
    /// 前一段是节点类型，后一段是必须匹配的 stage。
    /// </summary>
    FogDisable = 4,

    /// <summary>
    /// 泥人贴图替换：不读原图，直接给出预置字节。
    ///
    /// 贴图为名单表（mt）使用 172 字节默认中性灰块（qd()），定制表（ua）使用预置色调块；
    /// 名单外或受保护目录/发光贴图一律不碰。
    /// </summary>
    MudManTexture = 5
}

public static class EffectActionExtensions
{
    /// <summary>
    /// 执行优先级。数值小的先执行。
    ///
    /// 顺序不能任意：同一文件上 ReplaceCode 必须先于 RemoveObject 执行，
    /// 否则先删键会让后面的替换目标消失，规则静默失效。
    /// 该顺序与易刷能力包内的实现一致。
    /// </summary>
    public static int Priority(this EffectAction action) => action switch
    {
        EffectAction.ReplaceCode => 20,
        EffectAction.ClearBlock => 20,
        EffectAction.AppendToBlock => 25,
        EffectAction.FogDisable => 25,
        EffectAction.MudManTexture => 25,
        EffectAction.RemoveObject => 30,
        _ => 100
    };
}

/// <summary>
/// 单条规则在客户端上的匹配情况。
/// </summary>
public sealed record EffectPatchRuleStatus(
    EffectPatchRule Rule,
    int MatchedFileCount,
    int MatchedOccurrences,
    IReadOnlyList<string> SamplePaths)
{
    /// <summary>路径存在且有内容命中，才算这条规则真正可用。</summary>
    public bool IsEffective => MatchedFileCount > 0 && MatchedOccurrences > 0;
}

/// <summary>
/// 扫描结果。不修改任何文件，仅报告每条规则能否在当前客户端生效。
/// </summary>
public sealed record EffectPatchScanResult(
    IReadOnlyList<EffectPatchRuleStatus> Rules,
    int TotalClientFiles,
    string? Error)
{
    public static EffectPatchScanResult Failure(string error) => new([], 0, error);

    public int EffectiveCount => Rules.Count(r => r.IsEffective);

    public int TotalCount => Rules.Count;
}
