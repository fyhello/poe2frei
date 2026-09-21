using System.Text.Json;
using System.Text.Json.Serialization;
using FreiAtlas.Core.Recovery;
using FreiAtlas.Settings.Bridge;

namespace FreiAtlas.Settings.Tests;

public sealed class QuickAssistMessageTests
{
    [Fact]
    public void SettingsMessage_PreservesBothRulesAndModes()
    {
        var settings = new QuickAssistSettings
        {
            Health = new() { Enabled = true, Mode = RecoveryThresholdMode.Fixed, FixedValue = 1234, Key = "4" },
            Mana = new() { Percentage = 35, Key = "Ctrl+2" }
        };
        Assert.True(Route("updateQuickAssist", new { settings }, out var command));
        Assert.Equal(settings, Assert.IsType<UpdateQuickAssistWebCommand>(command).Settings);
    }

    [Theory]
    [InlineData("{\"health\":null}")]
    [InlineData("{\"health\":{\"percentage\":101}}")]
    [InlineData("{\"mana\":{\"intervalMilliseconds\":0}}")]
    [InlineData("{\"health\":{\"key\":\"Escape\"}}")]
    [InlineData("{\"health\":{\"mode\":\"invalid\"}}")]
    public void InvalidSettings_AreRejected(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        Assert.False(Route("updateQuickAssist", new { settings = document.RootElement }, out _));
    }

    [Fact]
    public void RecordingAndIndependentReset_AreTypedMessages()
    {
        Assert.True(Route("setRecoveryKeyRecording", new { recording = true }, out var start));
        Assert.True(Assert.IsType<SetRecoveryKeyRecordingWebCommand>(start).Recording);
        Assert.True(Route("setRecoveryKeyRecording", new { recording = false }, out var end));
        Assert.False(Assert.IsType<SetRecoveryKeyRecordingWebCommand>(end).Recording);
        Assert.False(Route("setRecoveryKeyRecording", new { recording = "true" }, out _));
        Assert.True(Route("resetPage", new { page = "quickAssist" }, out var reset));
        Assert.Equal("quickAssist", Assert.IsType<ResetPageWebCommand>(reset).Page);
    }

    [Fact]
    public void PortalSqueezeSettingsAndTrigger_AreTypedMessages()
    {
        var settings = new QuickAssistSettings
        {
            PortalSqueeze = new()
            {
                Enabled = true,
                Hotkey = "Ctrl+J",
                MaxDistanceGrid = 60,
                MaxAttempts = 2,
                ConfirmTimeoutMilliseconds = 5000
            }
        };

        Assert.True(Route("updateQuickAssist", new { settings }, out var update));
        Assert.Equal(settings, Assert.IsType<UpdateQuickAssistWebCommand>(update).Settings);
        Assert.True(Route("triggerPortalSqueeze", new { }, out var trigger));
        Assert.IsType<TriggerPortalSqueezeWebCommand>(trigger);
    }

    private static bool Route(string type, object payload, out WebCommand? command)
    {
        var json = JsonSerializer.Serialize(new { version = 1, type, payload }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        });
        return new WebMessageRouter([]).TryRoute(json, out command, out _);
    }
}
