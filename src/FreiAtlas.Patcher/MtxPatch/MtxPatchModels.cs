using System.Text.Json.Serialization;

namespace FreiAtlas.Patcher.MtxPatch;

/// <summary>
/// 商城特效大类枚举。
/// </summary>
public enum MtxCategory
{
    [JsonPropertyName("ArmourSets")]
    ArmourSets,

    [JsonPropertyName("Skills")]
    Skills,

    [JsonPropertyName("Portals")]
    Portals,

    [JsonPropertyName("Pets")]
    Pets,

    [JsonPropertyName("CharacterEffects")]
    CharacterEffects,

    [JsonPropertyName("Misc")]
    Misc
}

/// <summary>
/// 单个商城特效条目。
/// </summary>
public sealed class MtxEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public MtxCategory Category { get; set; }

    [JsonPropertyName("group")]
    public string? Group { get; set; }

    [JsonPropertyName("displayGroup")]
    public string? DisplayGroup { get; set; }

    [JsonPropertyName("iconDdsPath")]
    public string? IconDdsPath { get; set; }

    [JsonPropertyName("previewUrl")]
    public string? PreviewUrl { get; set; }

    [JsonPropertyName("slotAttach")]
    public string? SlotAttach { get; set; }

    [JsonPropertyName("slotAttachLabel")]
    public string? SlotAttachLabel { get; set; }

    [JsonPropertyName("armourSetFolder")]
    public string? ArmourSetFolder { get; set; }

    [JsonPropertyName("armourSetFilePrefix")]
    public string? ArmourSetFilePrefix { get; set; }

    [JsonPropertyName("isArmourSetGlobal")]
    public bool IsArmourSetGlobal { get; set; }

    [JsonPropertyName("isConstructionEffectPack")]
    public bool IsConstructionEffectPack { get; set; }

    [JsonPropertyName("internalPaths")]
    public string[]? InternalPaths { get; set; }

    /// <summary>
    /// 前端卡片显示的中文名称。
    /// </summary>
    [JsonPropertyName("displayNameZh")]
    public string DisplayNameZh => MtxTranslationDictionary.Translate(Name, Category);

    /// <summary>
    /// 前端卡片显示副标题或槽位/类型描述。
    /// </summary>
    [JsonPropertyName("subtitle")]
    public string Subtitle => IsArmourSetGlobal
        ? "全局防具套装 (全职业/全底子生效)"
        : (Category == MtxCategory.CharacterEffects && IsConstructionEffectPack)
            ? "翻滚动作常驻特效 (全局生效)"
            : !string.IsNullOrWhiteSpace(Group)
                ? Group
                : Category switch
                {
                    MtxCategory.Skills => "技能替换",
                    MtxCategory.Portals => "传送门替换",
                    MtxCategory.Pets => "沙虫宠物替换",
                    MtxCategory.Misc => "杂项/变身",
                    _ => "角色特效"
                };
}

/// <summary>
/// 用户选择的 MTX 应用选项。
/// </summary>
public sealed class MtxPatchOptions
{
    /// <summary>
    /// 勾选生效的所有普通 MTX 条目名称集合（技能、翅膀、光效、翻滚、杂项等）。
    /// </summary>
    public HashSet<string> SelectedNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 选中的全局防具时装套装名称（单选，如 "Abyss Core Armour"；空则不替换防具）。
    /// </summary>
    public string? SelectedArmourSetName { get; set; }

    /// <summary>
    /// 选中套装所在的资源目录。
    ///
    /// 205 款防具里有 2 组同名（Assassin Armour / Blood Guard Armour），
    /// 只靠名称无法区分。前端把这个目录一起传上来，后端据此精确定位，
    /// 避免「界面显示 A、实际写入 B」。为空时退回按名称取第一条。
    /// </summary>
    public string? SelectedArmourSetFolder { get; set; }

    /// <summary>
    /// 选中的传送门外观名称（单选；空则不替换门）。
    /// </summary>
    public string? SelectedPortalName { get; set; }

    /// <summary>
    /// 选中的宠物外观名称（单选；空则不替换沙虫）。
    /// </summary>
    public string? SelectedPetName { get; set; }

    /// <summary>
    /// 选中的角色职业代号（如 "int"、"str"、"dex" 等，控制翅膀背饰挂载到哪个职业模型；默认 "all" 或 "int"）。
    /// </summary>
    public string SelectedClassCode { get; set; } = "all";
}

/// <summary>
/// PoE 2 角色职业模型映射项。
/// </summary>
public sealed record MtxCharacterClassItem(
    string Code,
    string DisplayName,
    string ModelPath);
