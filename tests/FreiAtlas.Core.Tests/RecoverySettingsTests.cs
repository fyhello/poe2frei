using FreiAtlas.Core.Player;
using FreiAtlas.Core.Recovery;

namespace FreiAtlas.Core.Tests;

public sealed class RecoverySettingsTests
{
    [Theory]
    [InlineData(1704, 0, 2500, 1278)]
    [InlineData(101, 3, 2500, 72)]
    [InlineData(100, 0, 0, 100)]
    [InlineData(0, 0, 0, 0)]
    public void AvailableMaximum_RoundsReservationUp(int maximum, int flat, int fraction, int expected)
        => Assert.Equal(expected, new VitalPool(0, maximum, flat, fraction).AvailableMaximum);

    [Theory]
    [InlineData("1", 0x31, 0)]
    [InlineData("Ctrl+Alt+Shift+A", 0x41, 3)]
    [InlineData("Numpad9", 0x69, 0)]
    [InlineData("F12", 0x7B, 0)]
    [InlineData("Space", 0x20, 0)]
    public void KeyParsing_MapsSupportedGestures(string gesture, ushort code, int modifiers)
    {
        Assert.True(RecoveryKey.TryParse(gesture, out var key));
        Assert.Equal(code, key!.VirtualKey);
        Assert.Equal(modifiers, key.Modifiers.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ctrl+Ctrl+A")]
    [InlineData("Win+A")]
    [InlineData("Escape")]
    [InlineData("Enter")]
    [InlineData("F13")]
    [InlineData("Ctrl")]
    public void KeyParsing_RejectsUnsupportedGestures(string? gesture)
        => Assert.False(RecoveryKey.TryParse(gesture, out _));

    [Fact]
    public void Defaults_AreDisabledAndInvalidValuesAreRejected()
    {
        var settings = QuickAssistSettings.Default;
        settings.Validate();
        Assert.False(settings.Health.Enabled);
        Assert.False(settings.Mana.Enabled);
        Assert.Throws<ArgumentException>(() => (settings.Health with { Percentage = 101 }).Validate());
        Assert.Throws<ArgumentException>(() => (settings.Health with { FixedValue = 0 }).Validate());
        Assert.Throws<ArgumentException>(() => (settings.Health with { IntervalMilliseconds = 249 }).Validate());
        Assert.Throws<ArgumentException>(() => (settings.Health with { Mode = (RecoveryThresholdMode)9 }).Validate());
    }
}
