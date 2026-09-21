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
        return page is "nodes" or "contents" or "atlasNavigation" or "areaMap" or "visuals" or "quickAssist" or "prices"
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
}
