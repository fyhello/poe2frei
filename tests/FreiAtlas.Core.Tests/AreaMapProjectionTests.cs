using System.Numerics;
using FreiAtlas.Core.Area;

namespace FreiAtlas.Core.Tests;

public sealed class AreaMapProjectionTests
{
    [Fact]
    public void TryProject_PlayerPointMapsToConfiguredAnchor()
    {
        var view = VerifiedView(
            AreaMapViewKind.MiniMap,
            new AreaUiRect(100, 50, 400, 240),
            new Vector2(8, -6),
            zoom: 1.5f);
        var parameters = new AreaMapProjectionParameters(
            AreaMapViewKind.MiniMap,
            AreaMapLinearTransform.Identity,
            AreaMapLinearTransform.Identity,
            new Vector2(3, -4));

        var projected = AreaMapProjection.TryProject(
            new Vector2(20, 30),
            new Vector2(20, 30),
            view,
            parameters,
            out var point);

        Assert.True(projected);
        Assert.Equal(new Vector2(311, 160), point);
    }

    [Fact]
    public void TryProject_AppliesIndependentShiftAndTargetMatrices()
    {
        var view = VerifiedView(
            AreaMapViewKind.LargeMap,
            new AreaUiRect(0, 0, 800, 600),
            new Vector2(2, 3),
            zoom: 0.5f);
        var parameters = new AreaMapProjectionParameters(
            AreaMapViewKind.LargeMap,
            new AreaMapLinearTransform(1, 2, 3, 4),
            new AreaMapLinearTransform(5, 6, 7, 8),
            Vector2.Zero);

        var projected = AreaMapProjection.TryProject(
            Vector2.Zero,
            new Vector2(1, -1),
            view,
            parameters,
            out var point);

        Assert.True(projected);
        Assert.Equal(new Vector2(128, 38), point);
    }

    [Fact]
    public void TryProject_RejectsMismatchedViewKind()
    {
        var view = VerifiedView(
            AreaMapViewKind.LargeMap,
            new AreaUiRect(0, 0, 800, 600),
            Vector2.Zero,
            zoom: 1f);
        var parameters = new AreaMapProjectionParameters(
            AreaMapViewKind.MiniMap,
            AreaMapLinearTransform.Identity,
            AreaMapLinearTransform.Identity,
            Vector2.Zero);

        var projected = AreaMapProjection.TryProject(
            Vector2.Zero,
            Vector2.One,
            view,
            parameters,
            out var point);

        Assert.False(projected);
        Assert.Equal(Vector2.Zero, point);
    }

    [Fact]
    public void TryProject_RejectsInvalidProjectionInputs()
    {
        var validView = VerifiedView(
            AreaMapViewKind.MiniMap,
            new AreaUiRect(10, 20, 400, 240),
            Vector2.Zero,
            zoom: 1f);
        var validParameters = new AreaMapProjectionParameters(
            AreaMapViewKind.MiniMap,
            AreaMapLinearTransform.Identity,
            AreaMapLinearTransform.Identity,
            Vector2.Zero);
        var invalidParameters = validParameters with
        {
            Target = new AreaMapLinearTransform(float.NaN, 0, 0, 1)
        };
        var invalidViews = new[]
        {
            validView with { Viewport = new AreaUiRect(10, 20, 0, 240) },
            validView with { Viewport = new AreaUiRect(float.NaN, 20, 400, 240) },
            validView with { Zoom = 0 },
            validView with { Zoom = float.PositiveInfinity },
            validView with { Shift = new Vector2(float.NaN, 0) }
        };

        AssertRejected(
            new Vector2(float.NaN, 0),
            Vector2.One,
            validView,
            validParameters);
        AssertRejected(
            Vector2.Zero,
            new Vector2(0, float.PositiveInfinity),
            validView,
            validParameters);
        AssertRejected(Vector2.Zero, Vector2.One, validView, invalidParameters);
        foreach (var invalidView in invalidViews)
        {
            AssertRejected(Vector2.Zero, Vector2.One, invalidView, validParameters);
        }
    }

    private static void AssertRejected(
        Vector2 playerGrid,
        Vector2 targetGrid,
        AreaMapViewSnapshot view,
        AreaMapProjectionParameters parameters)
    {
        var projected = AreaMapProjection.TryProject(
            playerGrid,
            targetGrid,
            view,
            parameters,
            out var point);

        Assert.False(projected);
        Assert.Equal(Vector2.Zero, point);
    }

    private static AreaMapViewSnapshot VerifiedView(
        AreaMapViewKind kind,
        AreaUiRect viewport,
        Vector2 shift,
        float zoom)
        => new(
            kind,
            AreaMapViewAvailability.Verified,
            true,
            shift,
            zoom,
            0f,
            false,
            viewport,
            1f);
}
