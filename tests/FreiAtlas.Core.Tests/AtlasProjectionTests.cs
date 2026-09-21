using System.Numerics;
using FreiAtlas.Core.Atlas;

namespace FreiAtlas.Core.Tests;

public sealed class AtlasProjectionTests
{
    [Fact]
    public void Project_AppliesScaleAndOffset()
    {
        var projection = new AtlasProjection(
            ScaleX: 2f,
            ScaleY: 3f,
            OffsetX: 100f,
            OffsetY: 200f);

        var result = projection.Project(new Vector2(10, 20));

        Assert.Equal(new Vector2(120, 260), result);
    }

    [Fact]
    public void Project_RejectsNonFiniteValues()
    {
        var projection = AtlasProjection.Identity;

        Assert.False(projection.TryProject(
            new Vector2(float.NaN, 1),
            out _));
    }

    [Fact]
    public void FromCanvas_UsesClientHeightZoomWithoutCanvasOffset()
    {
        var projection = AtlasProjection.FromCanvas(
            new AtlasViewport(1920, 1080),
            canvasRelativePosition: new Vector2(10, 20),
            canvasScale: 1f,
            atlasZoom: 0.85f,
            nodeSize: new Vector2(40, 40));

        var factor = 1080f / 1600f * 0.85f;
        Assert.Equal(factor, projection.ScaleX, 5);
        Assert.Equal(factor, projection.ScaleY, 5);
        Assert.Equal(0f, projection.OffsetX, 5);
        Assert.Equal(0f, projection.OffsetY, 5);
        Assert.Equal(1080f / 1600f, projection.UiScale, 5);
        Assert.Equal(0.85f, projection.AtlasZoom, 5);
    }

    [Fact]
    public void FromCanvas_DoesNotDoubleApplyNodeScaleOrCanvasPan()
    {
        var projection = AtlasProjection.FromCanvas(
            new AtlasViewport(1920, 1080),
            canvasRelativePosition: new Vector2(160, -90),
            canvasScale: 0.85f,
            atlasZoom: 0.85f,
            nodeSize: new Vector2(40, 40));

        var expectedScale = 1080f / 1600f * 0.85f;

        Assert.Equal(expectedScale, projection.ScaleX, 5);
        Assert.Equal(expectedScale, projection.ScaleY, 5);
        Assert.Equal(0f, projection.OffsetX, 5);
        Assert.Equal(0f, projection.OffsetY, 5);
    }
}
