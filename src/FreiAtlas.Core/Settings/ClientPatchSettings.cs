namespace FreiAtlas.Core.Settings;

public sealed record ClientPatchColorOption(
    string Key,
    string DisplayName,
    string Value,
    bool Enabled);

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
    bool Enabled);

public sealed record RumourItemOption(
    string OriginalText,
    string ModifiedText,
    bool Enabled,
    int RowIndex = 0,
    string MapName = "",
    string Category = "常规",
    string Color = "#ffffff");

public sealed record FontCustomPreset(
    string Id,
    string Name,
    string FontFamily = "Microsoft YaHei",
    int GlobalSizeDelta = 0,
    int DropItemSizeDelta = 0,
    int GeneralTextSizeDelta = 0,
    int TitleSizeDelta = 0,
    int TinyBadgeSizeDelta = 0,
    bool IsBold = false);

public sealed record ClientPatchSettings(
    string? CustomGameDirectory = null,
    string FontFamily = "Microsoft YaHei",
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
    bool ArchnemesisEnhanced = true,
    bool ColorModEnabled = true,
    string ColorModStyle = "Whole",
    bool PricePatchEnabled = false,
    string PricePatchMode = "append",
    string? PricePatchCsvPath = null,
    string PricePatchSeparator = " ",
    bool MapBeamEnabled = true,
    IReadOnlyList<ClientPatchColorOption>? CustomColors = null,
    IReadOnlyList<ColorModRuleItem>? ColorModRules = null,
    IReadOnlyList<MapBeamTargetOption>? MapBeamTargets = null,
    IReadOnlyList<NamePatchOption>? ChestItems = null,
    IReadOnlyList<RumourItemOption>? RumourItems = null)
{
    public static ClientPatchSettings Default => new(
        CustomGameDirectory: null,
        FontFamily: "Microsoft YaHei",
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
        ArchnemesisEnhanced: true,
        ColorModEnabled: true,
        ColorModStyle: "Whole",
        PricePatchEnabled: false,
        PricePatchMode: "append",
        PricePatchCsvPath: null,
        PricePatchSeparator: " ",
        MapBeamEnabled: true,
        CustomColors: null,
        ColorModRules: null,
        MapBeamTargets: null,
        ChestItems: null,
        RumourItems: null);
}

