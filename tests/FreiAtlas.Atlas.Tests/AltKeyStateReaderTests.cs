using FreiAtlas.Platform.Windows.Windows;

namespace FreiAtlas.Atlas.Tests;

public sealed class AltKeyStateReaderTests
{
    [Theory]
    [InlineData(unchecked((short)0x8000), true)]
    [InlineData(unchecked((short)0x8001), true)]
    [InlineData((short)0x0001, false)]
    [InlineData((short)0x0000, false)]
    public void IsDown_UsesOnlyCurrentKeyStateBit(short state, bool expected)
        => Assert.Equal(expected, AltKeyStateReader.IsDown(state));
}
