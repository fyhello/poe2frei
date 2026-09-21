using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FreiAtlas.App.Content;
using FreiAtlas.Core.Settings;
using FreiAtlas.Settings.Bridge;

namespace FreiAtlas.Settings.Tests;

public sealed class WebMessageRouterTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static TheoryData<string, object, Type> ValidMessages => new()
    {
        { "ready", new { }, typeof(ReadyWebCommand) },
        { "refreshProcesses", new { }, typeof(RefreshProcessesWebCommand) },
        { "selectProcess", new { processId = 123 }, typeof(SelectProcessWebCommand) },
        { "startOverlay", new { }, typeof(StartOverlayWebCommand) },
        { "stopOverlay", new { }, typeof(StopOverlayWebCommand) },
        { "updateSettings", new { settings = AtlasDisplaySettings.Default }, typeof(UpdateSettingsWebCommand) },
        { "updateHotkey", new { gesture = "Alt+R" }, typeof(UpdateHotkeyWebCommand) },
        { "resetPage", new { page = "nodes" }, typeof(ResetPageWebCommand) },
        { "resetPage", new { page = "contents" }, typeof(ResetPageWebCommand) },
        { "resetPage", new { page = "areaMap" }, typeof(ResetPageWebCommand) },
        { "resetPage", new { page = "atlasNavigation" }, typeof(ResetPageWebCommand) },
        { "resetAll", new { }, typeof(ResetAllWebCommand) }
    };

    [Theory]
    [MemberData(nameof(ValidMessages))]
    public void TryRoute_WithValidMessage_ReturnsTypedCommand(
        string type,
        object payload,
        Type commandType)
    {
        var router = Router();

        var routed = router.TryRoute(
            Envelope(1, type, payload),
            out var command,
            out var error);

        Assert.True(routed, error);
        Assert.IsType(commandType, command);
    }

    [Fact]
    public void TryRoute_WithUnknownVersion_Rejects()
    {
        Assert.False(Router().TryRoute(
            Envelope(2, "ready", new { }),
            out _,
            out _));
    }

    [Fact]
    public void TryRoute_WithUnknownType_Rejects()
    {
        Assert.False(Router().TryRoute(
            Envelope(1, "readMemory", new { }),
            out _,
            out _));
    }

    [Fact]
    public void TryRoute_SelectProcessWithoutPid_Rejects()
    {
        Assert.False(Router().TryRoute(
            Envelope(1, "selectProcess", new { }),
            out _,
            out _));
    }

    [Fact]
    public void TryRoute_UpdateSettingsWithInvalidColor_Rejects()
    {
        var settings = AtlasDisplaySettings.Default with
        {
            Edges = AtlasDisplaySettings.Default.Edges with
            {
                ReachableColor = "green"
            }
        };

        Assert.False(Router().TryRoute(
            Envelope(1, "updateSettings", new { settings }),
            out _,
            out _));
    }

    [Fact]
    public void TryRoute_UpdateSettingsWithOutOfRangeWidth_Rejects()
    {
        var settings = AtlasDisplaySettings.Default with
        {
            Edges = AtlasDisplaySettings.Default.Edges with { Width = 20f }
        };

        Assert.False(Router().TryRoute(
            Envelope(1, "updateSettings", new { settings }),
            out _,
            out _));
    }

    [Fact]
    public void TryRoute_UpdateSettingsWithInvalidAreaMapFontSize_Rejects()
    {
        var settings = AtlasDisplaySettings.Default with
        {
            AreaMap = AtlasDisplaySettings.Default.AreaMap with
            {
                LargeMapLabelFontSize = 31f
            }
        };

        Assert.False(Router().TryRoute(
            Envelope(1, "updateSettings", new { settings }),
            out _,
            out _));
    }

    [Theory]
    [InlineData("R")]
    [InlineData("F13")]
    [InlineData("Meta+R")]
    public void TryRoute_UpdateHotkeyWithInvalidGesture_Rejects(string gesture)
    {
        Assert.False(Router().TryRoute(
            Envelope(1, "updateHotkey", new { gesture }),
            out _,
            out _));
    }

    [Theory]
    [InlineData("address")]
    [InlineData("offset")]
    [InlineData("pointer")]
    [InlineData("handle")]
    public void TryRoute_WithForbiddenMemoryFieldAnywhere_Rejects(string field)
    {
        var json = $$"""
        {
          "version": 1,
          "type": "ready",
          "payload": {
            "nested": { "{{field}}": 123 }
          }
        }
        """;

        Assert.False(Router().TryRoute(json, out _, out _));
    }

    [Fact]
    public void TryRoute_UpdateSettingsAllowsForbiddenFragmentInNavigationRuleName()
    {
        var settings = AtlasDisplaySettings.Default with
        {
            Navigation = new AtlasNavigationSettings(
                AtlasNavigationTargetMode.Nearest,
                new Dictionary<string, AtlasNavigationRule>
                {
                    ["Shattered Handle"] = new(true, false, false)
                })
        };

        var routed = Router().TryRoute(
            Envelope(1, "updateSettings", new { settings }),
            out var command,
            out var error);

        Assert.True(routed, error);
        var update = Assert.IsType<UpdateSettingsWebCommand>(command);
        Assert.True(update.Settings.Navigation.Rules.ContainsKey("Shattered Handle"));
    }

    [Fact]
    public void TryRoute_UpdateSettingsPreservesHideCompletedMaps()
    {
        var settings = AtlasDisplaySettings.Default with
        {
            Navigation = AtlasDisplaySettings.Default.Navigation with
            {
                HideCompletedMaps = false
            }
        };

        var routed = Router().TryRoute(
            Envelope(1, "updateSettings", new { settings }),
            out var command,
            out var error);

        Assert.True(routed, error);
        var update = Assert.IsType<UpdateSettingsWebCommand>(command);
        Assert.False(update.Settings.Navigation.HideCompletedMaps);
    }

    [Fact]
    public void TryRoute_UpdateSettingsPreservesMapContentVisibility()
    {
        var nodes = AtlasDisplaySettings.Default.Nodes.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        nodes[AtlasNodeCategory.Completed] = nodes[AtlasNodeCategory.Completed] with
        {
            ShowMapContents = false
        };
        var settings = AtlasDisplaySettings.Default with { Nodes = nodes };

        var routed = Router().TryRoute(
            Envelope(1, "updateSettings", new { settings }),
            out var command,
            out var error);

        Assert.True(routed, error);
        var update = Assert.IsType<UpdateSettingsWebCommand>(command);
        Assert.False(update.Settings.Nodes[AtlasNodeCategory.Completed].ShowMapContents);
        Assert.True(update.Settings.Nodes[AtlasNodeCategory.Unlocked].ShowMapContents);
    }

    [Fact]
    public void TryRoute_UpdateSettingsPreservesAltOverlayMode()
    {
        var settings = AtlasDisplaySettings.Default with
        {
            AltOverlayMode = AltOverlayMode.ToggleOnPress
        };

        var routed = Router().TryRoute(
            Envelope(1, "updateSettings", new { settings }),
            out var command,
            out var error);

        Assert.True(routed, error);
        var update = Assert.IsType<UpdateSettingsWebCommand>(command);
        Assert.Equal(AltOverlayMode.ToggleOnPress, update.Settings.AltOverlayMode);
    }

    [Fact]
    public void TryRoute_UpdateSettingsRejectsUndefinedAltOverlayMode()
    {
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(Envelope(
            1,
            "updateSettings",
            new { settings = AtlasDisplaySettings.Default })));
        var payload = Assert.IsType<JsonObject>(root["payload"]);
        var settings = Assert.IsType<JsonObject>(payload["settings"]);
        settings["altOverlayMode"] = 999;

        Assert.False(Router().TryRoute(root.ToJsonString(), out _, out _));
    }

    [Theory]
    [InlineData("pointer")]
    [InlineData("offset")]
    [InlineData("address")]
    [InlineData("processHandle")]
    [InlineData("moduleBase")]
    [InlineData("nativeHandle")]
    public void TryRoute_UpdateSettingsRejectsForbiddenFieldInsideNavigationRule(
        string field)
    {
        var settings = AtlasDisplaySettings.Default with
        {
            Navigation = new AtlasNavigationSettings(
                AtlasNavigationTargetMode.Nearest,
                new Dictionary<string, AtlasNavigationRule>
                {
                    ["Dunes"] = new(true, false, false)
                })
        };
        var json = Envelope(1, "updateSettings", new { settings });
        json = json.Replace(
            "\"highlight\":true",
            $"\"{field}\":123,\"highlight\":true",
            StringComparison.Ordinal);

        Assert.False(Router().TryRoute(json, out _, out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TryRoute_PreservesPanelEntryDefault(bool expanded)
    {
        var settings = AtlasDisplaySettings.Default with
        {
            AreaMap = AtlasDisplaySettings.Default.AreaMap with
            {
                ExpeditionPanel = AreaMapExpeditionPanelSettings.Default with { ExpandOnAreaEntry = expanded }
            }
        };

        Assert.True(Router().TryRoute(Envelope(1, "updateSettings", new { settings }), out var command, out _));
        Assert.Equal(expanded, Assert.IsType<UpdateSettingsWebCommand>(command).Settings.AreaMap.ExpeditionPanel.ExpandOnAreaEntry);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"collapsed\"")]
    [InlineData("0")]
    public void TryRoute_RejectsInvalidPanelEntryDefault(string value)
    {
        var message = JsonNode.Parse(Envelope(1, "updateSettings", new { settings = AtlasDisplaySettings.Default }))!;
        message["payload"]!["settings"]!["areaMap"]!["expeditionPanel"]!["expandOnAreaEntry"] = JsonNode.Parse(value);

        Assert.False(Router().TryRoute(message.ToJsonString(), out _, out _));
    }

    [Fact]
    public void TryRoute_MissingPanelEntryDefaultKeepsExpanded()
    {
        var message = JsonNode.Parse(Envelope(1, "updateSettings", new { settings = AtlasDisplaySettings.Default }))!;
        message["payload"]!["settings"]!["areaMap"]!["expeditionPanel"]!.AsObject().Remove("expandOnAreaEntry");

        Assert.True(Router().TryRoute(message.ToJsonString(), out var command, out _));
        Assert.True(Assert.IsType<UpdateSettingsWebCommand>(command).Settings.AreaMap.ExpeditionPanel.ExpandOnAreaEntry);
    }

    private static WebMessageRouter Router()
        => new(AtlasContentCatalog.Embedded.ContentIds);

    private static string Envelope(int version, string type, object payload)
        => JsonSerializer.Serialize(
            new { version, type, payload },
            JsonOptions);
}
