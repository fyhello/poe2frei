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
