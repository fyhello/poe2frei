using System.Text.Json;
using System.Text.Json.Serialization;
using FreiAtlas.Core.Settings;

namespace FreiAtlas.Settings.Bridge;

public sealed class WebMessageRouter
{
    private const int ProtocolVersion = 1;

    private static readonly string[] ForbiddenFieldFragments =
    [
        "address",
        "offset",
        "pointer",
        "handle",
        "modulebase"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IReadOnlyList<string> _contentIds;

    public WebMessageRouter(IEnumerable<string> contentIds)
    {
        ArgumentNullException.ThrowIfNull(contentIds);
        _contentIds = contentIds.Distinct(StringComparer.Ordinal).ToArray();
    }

    public bool TryRoute(
        string json,
        out WebCommand? command,
        out string? error)
    {
        command = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "empty-message";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || ContainsForbiddenField(root)
                || !root.TryGetProperty("version", out var versionElement)
                || !versionElement.TryGetInt32(out var version)
                || version != ProtocolVersion
                || !root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(typeElement.GetString())
                || !root.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object)
            {
                error = "invalid-envelope";
                return false;
            }

            command = Route(typeElement.GetString()!, payload);
            if (command is null)
            {
                error = "invalid-command";
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            error = "invalid-json";
            return false;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or InvalidOperationException
            or NotSupportedException)
        {
            error = "invalid-payload";
            return false;
        }
    }

    private WebCommand? Route(string type, JsonElement payload)
        => type switch
        {
            "ready" => new ReadyWebCommand(),
            "refreshProcesses" => new RefreshProcessesWebCommand(),
            "selectProcess" => RouteSelectProcess(payload),
            "startOverlay" => new StartOverlayWebCommand(),
            "stopOverlay" => new StopOverlayWebCommand(),
            "updateSettings" => RouteUpdateSettings(payload),
            "updateHotkey" => RouteUpdateHotkey(payload),
            "updateQuickAssist" => RouteUpdateQuickAssist(payload),
            "triggerPortalSqueeze" => new TriggerPortalSqueezeWebCommand(),
            "selectPriceLeague" => RouteSelectPriceLeague(payload),
            "refreshPrices" => new RefreshPricesWebCommand(),
            "setRecoveryKeyRecording" => payload.TryGetProperty("recording", out var recording)
                && recording.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? new SetRecoveryKeyRecordingWebCommand(recording.GetBoolean()) : null,
            "getPatcherStatus" => new GetPatcherStatusWebCommand(),
            "scanClientDictionary" => new ScanClientDictionaryWebCommand(),
            "scanClientRumours" => new ScanClientRumoursWebCommand(),
            "scanClientChests" => new ScanClientChestsWebCommand(),
            "scanClientFontSettings" => new ScanClientFontSettingsWebCommand(),
            "scanClientBaseItems" => new ScanClientBaseItemsWebCommand(),
            "setPatcherGameDir" => RouteSetPatcherGameDir(payload),
            "applyFontPatch" => RouteApplyFontPatch(payload),
            "applyUiColorPatch" => RouteApplyUiColorPatch(payload),
            "applyRumourPatch" => RouteApplyRumourPatch(payload),
            "applyChestPatch" => RouteApplyChestPatch(payload),
            "applyArchnemesisPatch" => RouteApplyArchnemesisPatch(payload),
            "applyColorModPatch" => RouteApplyColorModPatch(payload),
            "applyMapBeamPatch" => RouteApplyMapBeamPatch(payload),
            "applyPricePatch" => RouteApplyPricePatch(payload),
            "applyCurrentPricesPatch" => RouteApplyCurrentPricesPatch(payload),
            "cleanPricePatch" => RouteCleanPricePatch(payload),
            "restoreAllPatches" => new RestoreAllPatchesWebCommand(),
            "getPatcherSnapshots" => new GetPatcherSnapshotsWebCommand(),
            "createPatcherSnapshot" => RouteCreatePatcherSnapshot(payload),
            "restorePatcherSnapshot" => RouteRestorePatcherSnapshot(payload),
            "deletePatcherSnapshot" => RouteDeletePatcherSnapshot(payload),
            "resetPage" => RouteResetPage(payload),
            "resetAll" => new ResetAllWebCommand(),
            _ => null
        };

    private static WebCommand? RouteSelectPriceLeague(JsonElement payload)
    {
        if (!payload.TryGetProperty("leagueId", out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        var settings = new PriceSettings(value.GetString()!);
        settings.Validate();
        return new SelectPriceLeagueWebCommand(settings.LeagueId);
    }

    private static WebCommand? RouteSelectProcess(JsonElement payload)
        => payload.TryGetProperty("processId", out var processIdElement)
           && processIdElement.TryGetInt32(out var processId)
           && processId > 0
            ? new SelectProcessWebCommand(processId)
            : null;

    private WebCommand? RouteUpdateSettings(JsonElement payload)
    {
        if (!payload.TryGetProperty("settings", out var settingsElement)
            || settingsElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var settings = settingsElement.Deserialize<AtlasDisplaySettings>(
            JsonOptions);
        return settings is null
            ? null
            : new UpdateSettingsWebCommand(
                AtlasDisplaySettingsValidator.Validate(settings, _contentIds));
    }

    private static WebCommand? RouteUpdateQuickAssist(JsonElement payload)
    {
        if (!payload.TryGetProperty("settings", out var value) || value.ValueKind != JsonValueKind.Object) return null;
        var settings = value.Deserialize<FreiAtlas.Core.Recovery.QuickAssistSettings>(JsonOptions);
        if (settings is null) return null;
        settings.Validate();
        return new UpdateQuickAssistWebCommand(settings);
    }

    private static WebCommand? RouteUpdateHotkey(JsonElement payload)
    {
        if (!payload.TryGetProperty("gesture", out var gestureElement)
            || gestureElement.ValueKind != JsonValueKind.String
            || !FreiAtlas.App.Settings.AtlasHotkey.TryParse(
                gestureElement.GetString(),
                out var hotkey))
        {
            return null;
        }

        return new UpdateHotkeyWebCommand(hotkey!);
    }

    private static WebCommand? RouteResetPage(JsonElement payload)
    {
        if (!payload.TryGetProperty("page", out var pageElement)
            || pageElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var page = pageElement.GetString();
        return page is "nodes" or "contents" or "atlasNavigation" or "areaMap" or "visuals" or "quickAssist" or "prices" or "clientPatch"
            ? new ResetPageWebCommand(page)
            : null;
    }

    private static bool ContainsForbiddenField(JsonElement element)
        => ContainsForbiddenField(element, Array.Empty<string>());

    private static bool ContainsForbiddenField(
        JsonElement element,
        IReadOnlyList<string> path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var isNavigationRules = path.Count >= 3
                    && path[^3] == "settings"
                    && path[^2] == "navigation"
                    && path[^1] == "rules";
                foreach (var property in element.EnumerateObject())
                {
                    if ((!isNavigationRules
                         && ForbiddenFieldFragments.Any(fragment =>
                            property.Name.Contains(
                                fragment,
                                StringComparison.OrdinalIgnoreCase)))
                        || ContainsForbiddenField(
                            property.Value,
                            [.. path, property.Name]))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Array:
                return element.EnumerateArray().Any(item =>
                    ContainsForbiddenField(item, path));
            default:
                return false;
        }
    }

    private static WebCommand? RouteSetPatcherGameDir(JsonElement payload)
    {
        if (payload.TryGetProperty("path", out var pathElement) && pathElement.ValueKind == JsonValueKind.String)
        {
            return new SetPatcherGameDirWebCommand(pathElement.GetString() ?? string.Empty);
        }
        return null;
    }

    private static WebCommand? RouteApplyFontPatch(JsonElement payload)
    {
        string fontFamily = payload.TryGetProperty("fontFamily", out var f) && f.ValueKind == JsonValueKind.String
            ? f.GetString() ?? "Microsoft YaHei"
            : "Microsoft YaHei";
        int fontSizeDelta = payload.TryGetProperty("fontSizeDelta", out var s) && s.TryGetInt32(out var delta)
            ? delta
            : 0;
        bool applyPresets = payload.TryGetProperty("applyPresetScales", out var p) && p.ValueKind == JsonValueKind.True;
        bool isBold = payload.TryGetProperty("isBold", out var b) && b.ValueKind == JsonValueKind.True;

        int dropItem = payload.TryGetProperty("dropItemSizeDelta", out var di) && di.TryGetInt32(out var dVal) ? dVal : 0;
        int general = payload.TryGetProperty("generalTextSizeDelta", out var gt) && gt.TryGetInt32(out var gVal) ? gVal : 0;
        int title = payload.TryGetProperty("titleSizeDelta", out var tt) && tt.TryGetInt32(out var tVal) ? tVal : 0;
        int tiny = payload.TryGetProperty("tinyBadgeSizeDelta", out var tb) && tb.TryGetInt32(out var tbVal) ? tbVal : 0;
        string presetId = payload.TryGetProperty("presetId", out var pid) && pid.ValueKind == JsonValueKind.String ? pid.GetString() ?? "default" : "default";

        List<FreiAtlas.Core.Settings.FontCustomPreset>? customPresets = null;
        if (payload.TryGetProperty("customPresets", out var cpEl) && cpEl.ValueKind == JsonValueKind.Array)
        {
            customPresets = new();
            foreach (var item in cpEl.EnumerateArray())
            {
                string id = item.TryGetProperty("id", out var iEl) ? iEl.GetString() ?? "" : "";
                string name = item.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                string ff = item.TryGetProperty("fontFamily", out var ffEl) ? ffEl.GetString() ?? "Microsoft YaHei" : "Microsoft YaHei";
                int gd = item.TryGetProperty("globalSizeDelta", out var gdEl) && gdEl.TryGetInt32(out var gv) ? gv : 0;
                int dd = item.TryGetProperty("dropItemSizeDelta", out var ddEl) && ddEl.TryGetInt32(out var dv) ? dv : 0;
                int gend = item.TryGetProperty("generalTextSizeDelta", out var gendEl) && gendEl.TryGetInt32(out var genv) ? genv : 0;
                int td = item.TryGetProperty("titleSizeDelta", out var tdEl) && tdEl.TryGetInt32(out var tv) ? tv : 0;
                int tbd = item.TryGetProperty("tinyBadgeSizeDelta", out var tbdEl) && tbdEl.TryGetInt32(out var tbv) ? tbv : 0;
                bool bold = item.TryGetProperty("isBold", out var bdEl) && bdEl.ValueKind == JsonValueKind.True;
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
                {
                    customPresets.Add(new FreiAtlas.Core.Settings.FontCustomPreset(id, name, ff, gd, dd, gend, td, tbd, bold));
                }
            }
        }

        return new ApplyFontPatchWebCommand(
            fontFamily,
            fontSizeDelta,
            applyPresets,
            isBold,
            dropItem,
            general,
            title,
            tiny,
            presetId,
            customPresets);
    }

    private static WebCommand? RouteApplyUiColorPatch(JsonElement payload)
    {
        if (!payload.TryGetProperty("colors", out var colorsElement) || colorsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<FreiAtlas.Core.Settings.ClientPatchColorOption>();
        foreach (var item in colorsElement.EnumerateArray())
        {
            string key = item.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? "" : "";
            string displayName = item.TryGetProperty("displayName", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
            string val = item.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            bool enabled = !item.TryGetProperty("enabled", out var e) || e.ValueKind != JsonValueKind.False;
            if (!string.IsNullOrEmpty(key))
            {
                list.Add(new FreiAtlas.Core.Settings.ClientPatchColorOption(key, displayName, val, enabled));
            }
        }
        return new ApplyUiColorPatchWebCommand(list);
    }

    private static WebCommand? RouteApplyRumourPatch(JsonElement payload)
    {
        bool enabled = !payload.TryGetProperty("enabled", out var e) || e.ValueKind != JsonValueKind.False;
        List<FreiAtlas.Core.Settings.RumourItemOption>? items = null;
        if (payload.TryGetProperty("items", out var itemsElem) && itemsElem.ValueKind == JsonValueKind.Array)
        {
            items = [];
            foreach (var el in itemsElem.EnumerateArray())
            {
                string orig = el.TryGetProperty("originalText", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() ?? "" : "";
                string mod = el.TryGetProperty("modifiedText", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
                bool itemEnabled = !el.TryGetProperty("enabled", out var ie) || ie.ValueKind != JsonValueKind.False;
                int rowIndex = el.TryGetProperty("rowIndex", out var ri) && ri.TryGetInt32(out var rVal) ? rVal : 0;
                string mapName = el.TryGetProperty("mapName", out var mn) && mn.ValueKind == JsonValueKind.String ? mn.GetString() ?? "" : "";
                string cat = el.TryGetProperty("category", out var ct) && ct.ValueKind == JsonValueKind.String ? ct.GetString() ?? "常规" : "常规";
                string col = el.TryGetProperty("color", out var cl) && cl.ValueKind == JsonValueKind.String ? cl.GetString() ?? "#ffffff" : "#ffffff";
                if (!string.IsNullOrEmpty(orig))
                {
                    items.Add(new FreiAtlas.Core.Settings.RumourItemOption(orig, mod, itemEnabled, rowIndex, mapName, cat, col));
                }
            }
        }
        return new ApplyRumourPatchWebCommand(enabled, items);
    }

    private static WebCommand? RouteApplyChestPatch(JsonElement payload)
    {
        bool enabled = !payload.TryGetProperty("enabled", out var e) || e.ValueKind != JsonValueKind.False;
        List<FreiAtlas.Core.Settings.NamePatchOption>? items = null;
        if (payload.TryGetProperty("items", out var itemsElem) && itemsElem.ValueKind == JsonValueKind.Array)
        {
            items = [];
            foreach (var el in itemsElem.EnumerateArray())
            {
                string orig = el.TryGetProperty("originalName", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() ?? "" : "";
                string mod = el.TryGetProperty("modifiedName", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
                bool itemEnabled = !el.TryGetProperty("enabled", out var ie) || ie.ValueKind != JsonValueKind.False;
                if (!string.IsNullOrEmpty(orig))
                {
                    items.Add(new FreiAtlas.Core.Settings.NamePatchOption(orig, mod, itemEnabled));
                }
            }
        }
        return new ApplyChestPatchWebCommand(enabled, items);
    }

    private static WebCommand? RouteApplyArchnemesisPatch(JsonElement payload)
    {
        bool enabled = !payload.TryGetProperty("enabled", out var e) || e.ValueKind != JsonValueKind.False;
        return new ApplyArchnemesisPatchWebCommand(enabled);
    }

    private static WebCommand? RouteApplyColorModPatch(JsonElement payload)
    {
        bool enabled = !payload.TryGetProperty("enabled", out var e) || e.ValueKind != JsonValueKind.False;
        string style = payload.TryGetProperty("style", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "Whole" : "Whole";
        List<FreiAtlas.Core.Settings.ColorModRuleItem>? rules = null;
        if (payload.TryGetProperty("rules", out var rulesElem) && rulesElem.ValueKind == JsonValueKind.Array)
        {
            rules = [];
            foreach (var el in rulesElem.EnumerateArray())
            {
                string id = el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() ?? "" : "";
                string text = el.TryGetProperty("text", out var tEl) && tEl.ValueKind == JsonValueKind.String ? tEl.GetString() ?? "" : "";
                string group = el.TryGetProperty("group", out var gEl) && gEl.ValueKind == JsonValueKind.String ? gEl.GetString() ?? "通用" : "通用";
                string color = el.TryGetProperty("color", out var cEl) && cEl.ValueKind == JsonValueKind.String ? cEl.GetString() ?? "#e64545" : "#e64545";
                string itemStyle = el.TryGetProperty("style", out var stEl) && stEl.ValueKind == JsonValueKind.String ? stEl.GetString() ?? "Whole" : "Whole";
                string tag = el.TryGetProperty("tag", out var tagEl) && tagEl.ValueKind == JsonValueKind.String ? tagEl.GetString() ?? "" : "";
                bool isDanger = el.TryGetProperty("isDanger", out var dEl) && dEl.ValueKind == JsonValueKind.True;
                bool itemEnabled = !el.TryGetProperty("enabled", out var ie) || ie.ValueKind != JsonValueKind.False;
                if (!string.IsNullOrEmpty(text))
                {
                    rules.Add(new FreiAtlas.Core.Settings.ColorModRuleItem(id, text, group, color, itemStyle, tag, isDanger, itemEnabled));
                }
            }
        }
        return new ApplyColorModPatchWebCommand(enabled, style, rules);
    }


    private static WebCommand? RouteApplyMapBeamPatch(JsonElement payload)
    {
        bool enabled = !payload.TryGetProperty("enabled", out var e) || e.ValueKind != JsonValueKind.False;
        List<FreiAtlas.Core.Settings.MapBeamTargetOption>? targets = null;
        if (payload.TryGetProperty("targets", out var targetsElem) && targetsElem.ValueKind == JsonValueKind.Array)
        {
            targets = [];
            foreach (var el in targetsElem.EnumerateArray())
            {
                string map = el.TryGetProperty("mapName", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
                string display = el.TryGetProperty("displayName", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
                string color = el.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "Farudin" : "Farudin";
                bool itemEnabled = !el.TryGetProperty("enabled", out var ie) || ie.ValueKind != JsonValueKind.False;
                if (!string.IsNullOrEmpty(map))
                {
                    targets.Add(new FreiAtlas.Core.Settings.MapBeamTargetOption(map, display, color, itemEnabled));
                }
            }
        }
        return new ApplyMapBeamPatchWebCommand(enabled, targets);
    }


    private static WebCommand? RouteApplyPricePatch(JsonElement payload)
    {
        string? csvPath = payload.TryGetProperty("csvPath", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        string mode = payload.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "append" : "append";
        string separator = payload.TryGetProperty("separator", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? " " : " ";
        return new ApplyPricePatchWebCommand(csvPath, mode, separator);
    }

    private static WebCommand? RouteApplyCurrentPricesPatch(JsonElement payload)
    {
        string mode = payload.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "append" : "append";
        string separator = payload.TryGetProperty("separator", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? " " : " ";
        return new ApplyCurrentPricesPatchWebCommand(mode, separator);
    }

    private static WebCommand? RouteCleanPricePatch(JsonElement payload)
    {
        string separator = payload.TryGetProperty("separator", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? " " : " ";
        return new CleanPricePatchWebCommand(separator);
    }

    private static WebCommand? RouteCreatePatcherSnapshot(JsonElement payload)
    {
        string name = payload.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
        string description = payload.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
        return new CreatePatcherSnapshotWebCommand(name, description);
    }

    private static WebCommand? RouteRestorePatcherSnapshot(JsonElement payload)
    {
        if (!payload.TryGetProperty("snapshotId", out var id) || id.ValueKind != JsonValueKind.String)
            return null;
        return new RestorePatcherSnapshotWebCommand(id.GetString() ?? "");
    }

    private static WebCommand? RouteDeletePatcherSnapshot(JsonElement payload)
    {
        if (!payload.TryGetProperty("snapshotId", out var id) || id.ValueKind != JsonValueKind.String)
            return null;
        return new DeletePatcherSnapshotWebCommand(id.GetString() ?? "");
    }
}

