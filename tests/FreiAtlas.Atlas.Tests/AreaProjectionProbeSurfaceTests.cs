using System.Drawing;
using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Platform.Windows.Overlay;

namespace FreiAtlas.Atlas.Tests;

public sealed class AreaProjectionProbeSurfaceTests
{
    [Fact]
    public void BuildCrossSegments_LeavesThreePixelCenterGap()
    {
        var segments = AreaProjectionProbeSurface.BuildCrossSegments(
            new Vector2(100, 80),
            innerGap: 3,
            radius: 9);

        Assert.Equal(4, segments.Count);
        Assert.Contains(
            segments,
            line => line.Start == new Vector2(91, 80)
                    && line.End == new Vector2(97, 80));
        Assert.Contains(
            segments,
            line => line.Start == new Vector2(103, 80)
                    && line.End == new Vector2(109, 80));
        Assert.Contains(
            segments,
            line => line.Start == new Vector2(100, 71)
                    && line.End == new Vector2(100, 77));
        Assert.Contains(
            segments,
            line => line.Start == new Vector2(100, 83)
                    && line.End == new Vector2(100, 89));
    }

    [Fact]
    public void BuildCrossSegments_RejectsInvalidGeometry()
    {
        Assert.Empty(AreaProjectionProbeSurface.BuildCrossSegments(
            new Vector2(float.NaN, 80),
            innerGap: 3,
            radius: 9));
        Assert.Empty(AreaProjectionProbeSurface.BuildCrossSegments(
            new Vector2(100, 80),
            innerGap: 9,
            radius: 9));
        Assert.Empty(AreaProjectionProbeSurface.BuildCrossSegments(
            new Vector2(100, 80),
            innerGap: -1,
            radius: 9));
    }

    [Fact]
    public void BuildViewportBounds_PreservesClientCoordinates()
    {
        var bounds = AreaProjectionProbeSurface.BuildViewportBounds(
            new AreaUiRect(283.875f, 187.5f, 340.5f, 225f));

        Assert.Equal(
            new RectangleF(283.875f, 187.5f, 340.5f, 225f),
            bounds);
    }

    [Fact]
    public void BuildViewportBounds_RejectsInvalidRectangle()
    {
        Assert.Null(AreaProjectionProbeSurface.BuildViewportBounds(
            new AreaUiRect(0, 0, 0, 225)));
        Assert.Null(AreaProjectionProbeSurface.BuildViewportBounds(
            new AreaUiRect(0, float.PositiveInfinity, 340.5f, 225)));
    }

    [Fact]
    public void BuildLabelBounds_DoesNotMoveCrossCenter()
    {
        var center = new Vector2(100, 80);

        var bounds = AreaProjectionProbeSurface.BuildLabelBounds(center);

        Assert.Equal(new RectangleF(111, 71, 64, 18), bounds);
        Assert.Equal(center, new Vector2(100, 80));
    }
}
