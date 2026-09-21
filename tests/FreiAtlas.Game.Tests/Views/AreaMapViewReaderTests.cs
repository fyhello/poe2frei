using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Game.Views;

namespace FreiAtlas.Game.Tests.Views;

public sealed class AreaMapViewReaderTests
{
    private static readonly AreaUiRect Client = new(0, 0, 1920, 1080);

    [Fact]
    public void Read_PrefersDirectPointerIdentityOverGeometryHeuristics()
    {
        var reader = new AreaMapViewReader();
        var large = Candidate(
            "direct/large",
            false,
            new Vector2(10, 20),
            Client);
        var mini = Candidate(
            "direct/mini",
            true,
            new Vector2(-4, 6),
            new AreaUiRect(640, 400, 1100, 700));
        var geometricDecoy = Candidate(
            "root/decoy",
            true,
            new Vector2(99, 99),
            new AreaUiRect(1500, 20, 200, 150));

        var views = reader.Read(20, [large, mini, geometricDecoy], Client);

        Assert.Equal(new Vector2(-4, 6), views.MiniMap.Shift);
        Assert.Equal(mini.Viewport, views.MiniMap.Viewport);
    }

    [Fact]
    public void Read_TracksLargeMapToggleAndPersistentCornerMiniMapSeparately()
    {
        var reader = new AreaMapViewReader();
        var largeHidden = Candidate(
            "root/2",
            false,
            new Vector2(10, 20),
            new AreaUiRect(0, 0, 1800, 1000));
        var largeVisible = largeHidden with
        {
            IsVisible = true,
            Shift = new Vector2(80, -40),
            Zoom = 0.8f
        };
        var mini = Candidate(
            "root/5",
            true,
            new Vector2(-2, 3),
            new AreaUiRect(1500, 20, 380, 300));

        _ = reader.Read(7, [largeHidden, mini], Client);
        var views = reader.Read(7, [largeVisible, mini], Client);

        Assert.True(views.LargeMap.IsVisible);
        Assert.False(views.MiniMap.IsVisible);
        Assert.Equal(AreaMapViewAvailability.Verified, views.LargeMap.Availability);
        Assert.Equal(AreaMapViewAvailability.Verified, views.MiniMap.Availability);
        Assert.Equal(new Vector2(80, -40), views.LargeMap.Shift);
        Assert.Equal(0.8f, views.LargeMap.Zoom);
        Assert.Equal(new Vector2(-2, 3), views.MiniMap.Shift);
        Assert.Equal(0.5f, views.MiniMap.Zoom);
        Assert.NotEqual(views.LargeMap.Shift, views.MiniMap.Shift);
        Assert.True(views.LargeMap.Confidence > 0.6f);
        Assert.True(views.MiniMap.Confidence > 0.6f);
    }

    [Fact]
    public void Read_ResolvedPairIsVerifiedBeforeTheLargeMapHasBeenToggled()
    {
        var reader = new AreaMapViewReader();
        var large = Candidate(
            "root/6/0",
            false,
            Vector2.Zero,
            new AreaUiRect(0, 0, 1920, 1080),
            zoom: 1.5f);
        var mini = Candidate(
            "root/61/3",
            true,
            Vector2.Zero,
            new AreaUiRect(1400, 20, 480, 360),
            zoom: 1.5f);

        var views = reader.Read(12, [large, mini], Client);

        Assert.Equal(AreaMapViewAvailability.Verified, views.LargeMap.Availability);
        Assert.Equal(AreaMapViewAvailability.Verified, views.MiniMap.Availability);
        Assert.False(views.LargeMap.IsVisible);
        Assert.True(views.MiniMap.IsVisible);
        Assert.Equal(1.5f, views.LargeMap.Zoom);
        Assert.Equal(1.5f, views.MiniMap.Zoom);
        Assert.Equal(0f, views.MiniMap.RotationRadians);
        Assert.False(views.MiniMap.RotatesWithPlayer);
    }

    [Fact]
    public void Read_MissingMiniMapEvidenceDoesNotReuseLargeMapParameters()
    {
        var reader = new AreaMapViewReader();
        var large = Candidate(
            "root/2",
            true,
            new Vector2(44, 55),
            new AreaUiRect(0, 0, 1800, 1000));

        var views = reader.Read(9, [large], Client);

        Assert.Equal(AreaMapViewAvailability.Unverified, views.MiniMap.Availability);
        Assert.Equal(Vector2.Zero, views.MiniMap.Shift);
        Assert.Equal(0f, views.MiniMap.Zoom);
        Assert.Null(views.MiniMap.Viewport);
    }

    [Fact]
    public void Read_ChangingSessionClearsToggleHistory()
    {
        var reader = new AreaMapViewReader();
        var hidden = Candidate(
            "root/2",
            false,
            Vector2.Zero,
            new AreaUiRect(0, 0, 1800, 1000));
        var visible = hidden with { IsVisible = true };
        _ = reader.Read(1, [hidden], Client);
        var verifiedIdentity = reader.Read(1, [visible], Client);
        var newArea = reader.Read(2, [visible], Client);

        Assert.True(verifiedIdentity.LargeMap.Confidence > newArea.LargeMap.Confidence);
    }

    [Fact]
    public void Reset_ClearsCandidateIdentityHistoryWithinTheSameAreaSession()
    {
        var reader = new AreaMapViewReader();
        var hidden = Candidate(
            "root/2",
            false,
            Vector2.Zero,
            new AreaUiRect(0, 0, 1800, 1000));
        var visible = hidden with { IsVisible = true };
        _ = reader.Read(1, [hidden], Client);
        var verifiedIdentity = reader.Read(1, [visible], Client);

        reader.Reset();
        var afterReset = reader.Read(1, [visible], Client);

        Assert.True(verifiedIdentity.LargeMap.Confidence > afterReset.LargeMap.Confidence);
    }

    [Fact]
    public void Read_MissingLargeCandidateKeepsMiniMapUnverifiedWithFixedOrientation()
    {
        var reader = new AreaMapViewReader();
        var mini = Candidate(
            "root/5",
            true,
            Vector2.One,
            new AreaUiRect(1500, 20, 380, 300));

        _ = reader.Read(3, [mini], Client);
        var views = reader.Read(3, [mini], Client);

        Assert.Equal(AreaMapViewAvailability.Unverified, views.MiniMap.Availability);
        Assert.Equal(0f, views.MiniMap.RotationRadians);
        Assert.False(views.MiniMap.RotatesWithPlayer);
    }

    [Fact]
    public void Read_DoesNotTreatUnverifiedCandidateScalarAsMapRotation()
    {
        var reader = new AreaMapViewReader();
        var large = Candidate(
            "root/6/0",
            false,
            Vector2.Zero,
            new AreaUiRect(0, 0, 1920, 1080));
        var mini = Candidate(
            "root/61/3",
            true,
            Vector2.Zero,
            new AreaUiRect(1400, 20, 480, 360)) with
        {
            RotationRaw = 1.25f,
            HasRotationEvidence = true
        };

        var views = reader.Read(13, [large, mini], Client);

        Assert.Equal(AreaMapViewAvailability.Verified, views.MiniMap.Availability);
        Assert.Equal(0f, views.MiniMap.RotationRadians);
        Assert.False(views.MiniMap.RotatesWithPlayer);
    }

    [Fact]
    public void Read_UsesPersistentVisibleCandidateWhenMiniMapViewportIsNotInCorner()
    {
        var reader = new AreaMapViewReader();
        var large = Candidate(
            "root/6/0",
            false,
            new Vector2(10, 20),
            new AreaUiRect(0, 0, 2421, 1600));
        var mini = Candidate(
            "root/61/3",
            true,
            new Vector2(-4, 6),
            new AreaUiRect(642, 424, 1137, 752));

        _ = reader.Read(11, [large, mini], Client);
        var views = reader.Read(11, [large, mini], Client);

        Assert.Equal(new Vector2(-4, 6), views.MiniMap.Shift);
        Assert.Equal(0.5f, views.MiniMap.Zoom);
        Assert.Equal(mini.Viewport, views.MiniMap.Viewport);
        Assert.True(views.MiniMap.Confidence > 0.6f);
    }

    [Fact]
    public void Read_UiRebuildUsesOnlyCandidatesFromTheCurrentSample()
    {
        var reader = new AreaMapViewReader();
        var oldLarge = Candidate(
            "root/6/0",
            false,
            new Vector2(10, 20),
            new AreaUiRect(0, 0, 1920, 1080));
        var oldMini = Candidate(
            "root/61/3",
            true,
            new Vector2(-4, 6),
            new AreaUiRect(1400, 20, 480, 360));
        var rebuiltLarge = oldLarge with
        {
            Fingerprint = "root/7/0",
            Shift = new Vector2(30, 40)
        };
        var rebuiltMini = oldMini with
        {
            Fingerprint = "root/62/3",
            Shift = new Vector2(-8, 12)
        };

        _ = reader.Read(14, [oldLarge, oldMini], Client);
        var views = reader.Read(14, [rebuiltLarge, rebuiltMini], Client);

        Assert.Equal(new Vector2(30, 40), views.LargeMap.Shift);
        Assert.Equal(new Vector2(-8, 12), views.MiniMap.Shift);
        Assert.Equal(AreaMapViewAvailability.Verified, views.LargeMap.Availability);
        Assert.Equal(AreaMapViewAvailability.Verified, views.MiniMap.Availability);
    }

    [Fact]
    public void Read_MissingCurrentCandidatesDoesNotPublishStaleViews()
    {
        var reader = new AreaMapViewReader();
        var large = Candidate(
            "root/6/0",
            false,
            Vector2.Zero,
            new AreaUiRect(0, 0, 1920, 1080));
        var mini = Candidate(
            "root/61/3",
            true,
            Vector2.Zero,
            new AreaUiRect(1400, 20, 480, 360));

        _ = reader.Read(15, [large, mini], Client);
        var views = reader.Read(15, [], Client);

        Assert.Equal(AreaMapViewAvailability.Unverified, views.LargeMap.Availability);
        Assert.Equal(AreaMapViewAvailability.Unverified, views.MiniMap.Availability);
        Assert.Null(views.LargeMap.Viewport);
        Assert.Null(views.MiniMap.Viewport);
    }

    private static MapUiCandidate Candidate(
        string fingerprint,
        bool visible,
        Vector2 shift,
        AreaUiRect viewport,
        float zoom = 0.5f)
        => new(
            fingerprint,
            visible,
            shift,
            zoom,
            viewport,
            0f,
            false);
}
