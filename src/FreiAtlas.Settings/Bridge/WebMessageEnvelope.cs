namespace FreiAtlas.Settings.Bridge;

public abstract record WebCommand;

public sealed record ReadyWebCommand : WebCommand;

public sealed record RefreshProcessesWebCommand : WebCommand;

public sealed record SelectProcessWebCommand(int ProcessId) : WebCommand;

public sealed record StartOverlayWebCommand : WebCommand;

public sealed record StopOverlayWebCommand : WebCommand;

public sealed record UpdateSettingsWebCommand(
    FreiAtlas.Core.Settings.AtlasDisplaySettings Settings) : WebCommand;

public sealed record UpdateHotkeyWebCommand(
    FreiAtlas.App.Settings.AtlasHotkey Hotkey) : WebCommand;

public sealed record ResetPageWebCommand(string Page) : WebCommand;

public sealed record ResetAllWebCommand : WebCommand;

public sealed record UpdateQuickAssistWebCommand(FreiAtlas.Core.Recovery.QuickAssistSettings Settings) : WebCommand;

public sealed record SetRecoveryKeyRecordingWebCommand(bool Recording) : WebCommand;

public sealed record TriggerPortalSqueezeWebCommand : WebCommand;

public sealed record SelectPriceLeagueWebCommand(string LeagueId) : WebCommand;

public sealed record RefreshPricesWebCommand : WebCommand;

public sealed record GetPatcherStatusWebCommand : WebCommand;

public sealed record SetPatcherGameDirWebCommand(string Path) : WebCommand;

public sealed record ScanClientDictionaryWebCommand : WebCommand;

public sealed record ApplyFontPatchWebCommand(
    string FontFamily,
    int FontSizeDelta,
    bool ApplyPresetScales,
    bool IsBold = false,
    int DropItemSizeDelta = 0,
    int GeneralTextSizeDelta = 0,
    int TitleSizeDelta = 0,
    int TinyBadgeSizeDelta = 0,
    string PresetId = "default",
    IReadOnlyList<FreiAtlas.Core.Settings.FontCustomPreset>? CustomPresets = null) : WebCommand;


public sealed record ApplyRumourPatchWebCommand(
    bool Enabled,
    IReadOnlyList<FreiAtlas.Core.Settings.RumourItemOption>? Items = null) : WebCommand;

public sealed record ApplyChestPatchWebCommand(
    bool Enabled,
    IReadOnlyList<FreiAtlas.Core.Settings.NamePatchOption>? Items = null) : WebCommand;


public sealed record ApplyColorModPatchWebCommand(
    bool Enabled,
    string Style,
    IReadOnlyList<FreiAtlas.Core.Settings.ColorModRuleItem>? Rules = null) : WebCommand;

/// <summary>读取客户端索引，返回当前客户端实际可用的图标清单。</summary>
public sealed record ScanIconPatchWebCommand : WebCommand;

/// <summary>写入图标。Paths 必须来自扫描结果，后端会再次校验存在性。</summary>
public sealed record ApplyIconPatchWebCommand(
    bool Enabled,
    IReadOnlyList<string>? Paths = null) : WebCommand;

public sealed record ApplyMapBeamPatchWebCommand(
    bool Enabled,
    IReadOnlyList<FreiAtlas.Core.Settings.MapBeamTargetOption>? Targets = null) : WebCommand;

public sealed record ApplyCurrentPricesPatchWebCommand(
    string Mode,
    string Separator,
    decimal MinExalted = 1m,
    string UnitMode = "auto",
    bool StripExisting = true,
    IReadOnlyList<string>? Categories = null) : WebCommand;

public sealed record PreviewCurrentPricesPatchWebCommand(
    string Mode,
    string Separator,
    decimal MinExalted = 1m,
    string UnitMode = "auto",
    bool StripExisting = true,
    IReadOnlyList<string>? Categories = null) : WebCommand;

public sealed record ConfirmCurrentPricesPatchWebCommand(string Token) : WebCommand;

public sealed record CleanPricePatchWebCommand(string Separator) : WebCommand;

public sealed record RestoreAllPatchesWebCommand : WebCommand;

public sealed record GetPatcherSnapshotsWebCommand : WebCommand;

public sealed record CreatePatcherSnapshotWebCommand(string Name, string Description) : WebCommand;

public sealed record RestorePatcherSnapshotWebCommand(string SnapshotId) : WebCommand;

public sealed record DeletePatcherSnapshotWebCommand(string SnapshotId) : WebCommand;

public sealed record ScanClientRumoursWebCommand : WebCommand;

public sealed record ScanClientChestsWebCommand : WebCommand;

public sealed record ScanClientFontSettingsWebCommand : WebCommand;

public sealed record ApplyTranslationPatchWebCommand : WebCommand;

public sealed record ApplySpellBlockWebCommand(IReadOnlyList<string> SpellIds, bool IncludeAudio) : WebCommand;

public sealed record RestoreSpellBlockWebCommand(IReadOnlyList<string> SpellIds) : WebCommand;

public sealed record ApplyAllEffectsWebCommand(
    IReadOnlyList<string> RuleIds,
    IReadOnlyList<string> SpellIds,
    bool IncludeAudio,
    double ViewDistanceRatio = 2.0,
    int MudManTier = -1,
    // 头顶血条也纳入统一保存：勾选框只改前端状态，点保存时随其他特效一起下发。
    // null 表示本次不涉及血条（保持客户端现状）。
    bool? HealthBarEnabled = null) : WebCommand;

/// <summary>还原泥人改动的全部文件。</summary>
public sealed record RestoreMudManWebCommand : WebCommand;

/// <summary>应用头顶血条补丁（把隐藏的头顶血条全部改为显示）。</summary>
public sealed record ApplyHealthBarWebCommand : WebCommand;

/// <summary>还原头顶血条补丁。</summary>
public sealed record RestoreHealthBarWebCommand : WebCommand;

/// <summary>获取商城外观 (MTX) 全部特效元数据与数据库。</summary>
public sealed record GetMtxDatabaseWebCommand : WebCommand;

/// <summary>应用选中的商城外观 (MTX) 特效。</summary>
public sealed record ApplyMtxPatchWebCommand(
    IReadOnlyList<string> SelectedNames,
    string? SelectedArmourSetName,
    string? SelectedPortalName,
    string? SelectedPetName,
    string SelectedClassCode) : WebCommand;

/// <summary>还原所有商城外观 (MTX) 特效文件。</summary>
public sealed record RestoreMtxPatchWebCommand : WebCommand;
