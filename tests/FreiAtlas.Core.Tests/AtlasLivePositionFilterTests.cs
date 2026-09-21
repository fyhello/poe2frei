using System.Numerics;
using FreiAtlas.Core.Atlas;

namespace FreiAtlas.Core.Tests;

public sealed class AtlasLivePositionFilterTests
{
    [Fact]
    public void TryStabilize_ReplacesOffscreenOutlierWithCoherentPan()
    {
        var previous = new Dictionary<AtlasGridPos, Vector2>
        {
            [new(0, 0)] = new(100, 100),
            [new(1, 0)] = new(200, 100),
            [new(2, 0)] = new(300, 100),
            [new(3, 0)] = new(400, 100)
        };
        var current = new Dictionary<AtlasGridPos, Vector2>
        {
            [new(0, 0)] = new(112, 95),
            [new(1, 0)] = new(212, 95),
            [new(2, 0)] = new(9999, -8000),
            [new(3, 0)] = new(412, 95)
        };

        var result = AtlasLivePositionFilter.TryStabilize(
            previous,
            current,
            out var stabilized);

        Assert.True(result);
        Assert.Equal(new Vector2(312, 95), stabilized[new(2, 0)]);
    }

    [Fact]
    public void TryStabilize_RejectsFrameWithNoCoherentMovement()
    {
        var previous = new Dictionary<AtlasGridPos, Vector2>
        {
            [new(0, 0)] = new(100, 100),
            [new(1, 0)] = new(200, 100),
            [new(2, 0)] = new(300, 100),
            [new(3, 0)] = new(400, 100)
        };
        var current = new Dictionary<AtlasGridPos, Vector2>
        {
            [new(0, 0)] = new(10, 900),
            [new(1, 0)] = new(700, -200),
            [new(2, 0)] = new(30, 40),
            [new(3, 0)] = new(800, 1200)
        };

        var result = AtlasLivePositionFilter.TryStabilize(
            previous,
            current,
            out _);

        Assert.False(result);
    }

    [Fact]
    public void TryStabilize_PreservesVisiblePanWhenOffscreenNodesStayStill()
    {
        var previous = Enumerable.Range(0, 8)
            .ToDictionary(
                index => new AtlasGridPos(index, 0),
                index => new Vector2(100 + index * 100, 100));
        var current = new Dictionary<AtlasGridPos, Vector2>(previous)
        {
            [new AtlasGridPos(0, 0)] = new Vector2(120, 95)
        };

        var result = AtlasLivePositionFilter.TryStabilize(
            previous,
            current,
            out var stabilized);

        Assert.True(result);
        Assert.Equal(new Vector2(120, 95), stabilized[new AtlasGridPos(0, 0)]);
    }
}
