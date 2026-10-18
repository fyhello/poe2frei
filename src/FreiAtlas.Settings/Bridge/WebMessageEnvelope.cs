using System.Text.Json;

namespace FreiAtlas.Settings.Bridge;

public sealed record WebMessageEnvelope(
    int Version,
    string Type,
    JsonElement Payload);

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

public sealed record ApplyUiColorPatchWebCommand(IReadOnlyList<FreiAtlas.Core.Settings.ClientPatchColorOption> Colors) : WebCommand;

public sealed record ApplyRumourPatchWebCommand(
    bool Enabled,
    IReadOnlyList<FreiAtlas.Core.Settings.RumourItemOption>? Items = null) : WebCommand;

public sealed record ApplyChestPatchWebCommand(
    bool Enabled,
    IReadOnlyList<FreiAtlas.Core.Settings.NamePatchOption>? Items = null) : WebCommand;

public sealed record ApplyArchnemesisPatchWebCommand(bool Enabled) : WebCommand;

public sealed record ApplyColorModPatchWebCommand(
    bool Enabled,
    string Style,
    IReadOnlyList<FreiAtlas.Core.Settings.ColorModRuleItem>? Rules = null) : WebCommand;

public sealed record ApplyMapBeamPatchWebCommand(
    bool Enabled,
    IReadOnlyList<FreiAtlas.Core.Settings.MapBeamTargetOption>? Targets = null) : WebCommand;

public sealed record ApplyPricePatchWebCommand(string? CsvPath, string Mode, string Separator) : WebCommand;

public sealed record ApplyCurrentPricesPatchWebCommand(string Mode, string Separator) : WebCommand;

public sealed record CleanPricePatchWebCommand(string Separator) : WebCommand;

public sealed record RestoreAllPatchesWebCommand : WebCommand;

public sealed record GetPatcherSnapshotsWebCommand : WebCommand;

public sealed record CreatePatcherSnapshotWebCommand(string Name, string Description) : WebCommand;

public sealed record RestorePatcherSnapshotWebCommand(string SnapshotId) : WebCommand;

public sealed record DeletePatcherSnapshotWebCommand(string SnapshotId) : WebCommand;

public sealed record ScanClientRumoursWebCommand : WebCommand;

public sealed record ScanClientChestsWebCommand : WebCommand;

public sealed record ScanClientFontSettingsWebCommand : WebCommand;

public sealed record ScanClientBaseItemsWebCommand : WebCommand;

