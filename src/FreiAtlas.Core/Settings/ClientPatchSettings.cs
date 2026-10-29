namespace FreiAtlas.Core.Settings;

public sealed record ColorModRuleItem(
    string Id,
    string Text,
    string Group,
    string Color,
    string Style,
    string Tag,
    bool IsDanger,
    bool Enabled);

public sealed record MapBeamTargetOption(
    string MapName,
    string DisplayName,
    string Color,
    bool Enabled);

public sealed record NamePatchOption(
    string OriginalName,
    string ModifiedName,
    bool Enabled,
    // 规则匹配键：同族多稀有度共用一条规则，不受汉化影响。
    string FamilyName = "");

public sealed record RumourItemOption(
    string OriginalText,
    string ModifiedText,
    bool Enabled,
    int RowIndex = 0,
    string MapName = "",
    string Category = "常规",
    string Color = "#ffffff",
    // 规则匹配键：区域索引，跨语言稳定。
    int WorldAreaIndex = -1);

public sealed record FontCustomPreset(
    string Id,
    string Name,
    string FontFamily = "",
    int GlobalSizeDelta = 0,
    int DropItemSizeDelta = 0,
    int GeneralTextSizeDelta = 0,
    int TitleSizeDelta = 0,
    int TinyBadgeSizeDelta = 0,
    bool IsBold = false);

public sealed record MinimapColorSettings(
    int ExploredR = 0,
    int ExploredG = 0,
    int ExploredB = 0,
    int ExploredA = 76,
    int BorderR = 0,
    int BorderG = 120,
    int BorderB = 215,
    int BorderA = 26,
    int ExploringR = 0,
    int ExploringG = 128,
    int ExploringB = 255,
    int ExploringA = 128,
    float BorderGlow = 13f,
    float ExploringGlow = 1f,
    float VisibilityThreshold = 0.18f)
{
    public static MinimapColorSettings Default { get; } = new();
}

public sealed record ClientPatchSettings(
    string? CustomGameDirectory = null,
    string FontFamily = "",
    int FontSizeDelta = 0,
    bool FontBold = false,
    string FontPresetId = "default",
    int FontDropItemSizeDelta = 0,
    int FontGeneralTextSizeDelta = 0,
    int FontTitleSizeDelta = 0,
    int FontTinyBadgeSizeDelta = 0,
    IReadOnlyList<FontCustomPreset>? FontCustomPresets = null,
    bool RumourEnhanced = true,
    bool ChestEnhanced = true,
    bool ColorModEnabled = false,
    string ColorModStyle = "Whole",
    bool PricePatchEnabled = false,
    string PricePatchSeparator = " ",
    decimal? PricePatchMinExalted = null,
    bool? PricePatchStripExisting = null,
    IReadOnlyList<string>? PricePatchCategories = null,
    bool MapBeamEnabled = false,
    IReadOnlyList<ColorModRuleItem>? ColorModRules = null,
    IReadOnlyList<MapBeamTargetOption>? MapBeamTargets = null,
    IReadOnlyList<NamePatchOption>? ChestItems = null,
    IReadOnlyList<RumourItemOption>? RumourItems = null,
    bool IconPatchEnabled = false,
    IReadOnlyList<string>? IconPatchSelectedPaths = null,
    IReadOnlyList<string>? EffectPatchRuleIds = null,
    IReadOnlyList<string>? BlockedSpellIds = null,
    bool SpellBlockIncludeAudio = true,
    double ViewDistanceRatio = 2.0,
    int MudManTier = -1,
    bool HealthBarEnabled = false,
    IReadOnlyList<string>? MtxSelectedNames = null,
    string? MtxSelectedArmourSet = null,
    string? MtxSelectedPortal = null,
    string? MtxSelectedPet = null,
    string MtxSelectedClass = "all",

    // ── 批量补丁的大类门控 ──
    //
    // 大类与小功能是 AND 关系：只有大类勾选、且该小功能也勾选，才会被应用。
    // 两者都独立持久化，用户不必每次重新勾选。
    bool BatchCategoryTranslation = false,
    bool BatchCategoryText = false,
    bool BatchCategoryEffect = false,
    bool BatchCategoryMtx = false,

    // ── 此前没有启用标志的模块 ──
    bool TranslationEnabled = false,
    bool FontPatchEnabled = false,
    bool EffectPatchEnabled = false,
    bool MtxPatchEnabled = false)
{
    public MinimapColorSettings MinimapColor { get; init; } = MinimapColorSettings.Default;

    public static ClientPatchSettings Default => new(
        CustomGameDirectory: null,
        FontFamily: "",
        FontSizeDelta: 0,
        FontBold: false,
        FontPresetId: "default",
        FontDropItemSizeDelta: 0,
        FontGeneralTextSizeDelta: 0,
        FontTitleSizeDelta: 0,
        FontTinyBadgeSizeDelta: 0,
        FontCustomPresets: null,
        RumourEnhanced: true,
        ChestEnhanced: true,
        ColorModEnabled: true,
        ColorModStyle: "Whole",
        PricePatchEnabled: false,
        PricePatchSeparator: " ",
        MapBeamEnabled: true,
        ColorModRules: null,
        MapBeamTargets: null,
        ChestItems: null,
        RumourItems: null,
        IconPatchEnabled: false,
        IconPatchSelectedPaths: null,
        EffectPatchRuleIds: null,
        BlockedSpellIds: null,
        SpellBlockIncludeAudio: true,
        ViewDistanceRatio: 2.0,
        MudManTier: -1,
        HealthBarEnabled: false,
        MtxSelectedNames: null,
        MtxSelectedArmourSet: null,
        MtxSelectedPortal: null,
        MtxSelectedPet: null,
        MtxSelectedClass: "all");
}

