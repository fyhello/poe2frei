using FreiAtlas.Core.Atlas;

namespace FreiAtlas.Core.Tests;

public sealed class AtlasEdgeClassifierTests
{
    [Theory]
    [InlineData(false, false, AtlasEdgeColor.Red)]
    [InlineData(true, false, AtlasEdgeColor.Green)]
    [InlineData(false, true, AtlasEdgeColor.Green)]
    public void Classify_UsesAccessibleFrontier(
        bool fromAccessible,
        bool toAccessible,
        AtlasEdgeColor expected)
    {
        var edge = new AtlasEdgeSnapshot(
            new AtlasGridPos(0, 0),
            new AtlasGridPos(1, 0),
            AtlasEdgeState.Known,
            AtlasEdgeColor.Red);

        var result = AtlasEdgeClassifier.Classify(
            edge,
            fromAccessible,
            toAccessible);

        Assert.Equal(expected, result.RenderColor);
    }
}
