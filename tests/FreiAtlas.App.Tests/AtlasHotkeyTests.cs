using FreiAtlas.App.Settings;

namespace FreiAtlas.App.Tests;

public sealed class AtlasHotkeyTests
{
    [Theory]
    [InlineData("F12", "F12")]
    [InlineData("f1", "F1")]
    [InlineData("alt+r", "Alt+R")]
    [InlineData("shift+ctrl+1", "Ctrl+Shift+1")]
    [InlineData(" alt + ctrl + z ", "Ctrl+Alt+Z")]
    public void Parse_ValidGesture_ReturnsCanonicalText(
        string input,
        string expected)
    {
        Assert.Equal(expected, AtlasHotkey.Parse(input).Gesture);
    }

    [Theory]
    [InlineData("R")]
    [InlineData("1")]
    [InlineData("F13")]
    [InlineData("Ctrl")]
    [InlineData("Meta+R")]
    [InlineData("Ctrl+Ctrl+R")]
    [InlineData("Ctrl+F12")]
    public void Parse_InvalidGesture_Throws(string input)
    {
        Assert.Throws<ArgumentException>(() => AtlasHotkey.Parse(input));
    }

    [Fact]
    public void Default_IsF12()
        => Assert.Equal("F12", AtlasHotkey.Default.Gesture);
}
