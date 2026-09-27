using System.Collections.Immutable;
using System.Drawing;
using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Platform.Windows.Overlay;
using FreiAtlas.Plugin.AreaMap;

namespace FreiAtlas.Atlas.Tests;

public sealed class AreaMapOverlaySurfaceTests
{
    [Theory]
    [InlineData(AreaMapOverlayVisualState.BossInactive, "#FF9F43")]
    [InlineData(AreaMapOverlayVisualState.BossAvailable, "#FF4D5E")]
    [InlineData(AreaMapOverlayVisualState.BossCompleted, "#8B949E")]
    [InlineData(AreaMapOverlayVisualState.ExpeditionAvailable, "#FF4D5E")]
    [InlineData(AreaMapOverlayVisualState.ExpeditionSelected, "#33E661")]
    [InlineData(AreaMapOverlayVisualState.ExpeditionCompleted, "#8B949E")]
    [InlineData(AreaMapOverlayVisualState.AbyssAvailable, "#31C7C9")]
    [InlineData(AreaMapOverlayVisualState.RitualAvailable, "#E14C5A")]
    [InlineData(AreaMapOverlayVisualState.BreachAvailable, "#A77BF3")]
    [InlineData(AreaMapOverlayVisualState.EssenceAvailable, "#59D18C")]
    [InlineData(AreaMapOverlayVisualState.IncursionAvailable, "#F2D45C")]
    [InlineData(AreaMapOverlayVisualState.StrongboxAvailable, "#F29A4A")]
    [InlineData(AreaMapOverlayVisualState.OmenAltarAvailable, "#F2C14E")]
    [InlineData(AreaMapOverlayVisualState.RareMonster, "#F2D45C")]
    [InlineData(AreaMapOverlayVisualState.RareChest, "#E8C44F")]
    [InlineData(AreaMapOverlayVisualState.UniqueChest, "#D9822B")]
    [InlineData(AreaMapOverlayVisualState.PollenWild, "#E1C14C")]
    [InlineData(AreaMapOverlayVisualState.PollenSoul, "#B14CE1")]
    [InlineData(AreaMapOverlayVisualState.PollenPrimal, "#35C9C9")]
    [InlineData(AreaMapOverlayVisualState.PollenSacred, "#E17C3D")]
    [InlineData(AreaMapOverlayVisualState.PollenAvailable, "#D8D8D8")]
    public void ResolveVisualStyle_UsesMarkerColorsWithoutLabels(
        AreaMapOverlayVisualState state,
        string expectedHex)
    {
        var style = AreaMapOverlaySurface.ResolveVisualStyle(state);

        Assert.Equal(expectedHex, style.ColorHex);
        Assert.Null(style.Label);
    }

    [Theory]
    [InlineData(AreaMapOverlayMarkerKind.Boss, "Boss")]
    [InlineData(AreaMapOverlayMarkerKind.Expedition, "Expedition")]
    [InlineData(AreaMapOverlayMarkerKind.Abyss, "Abyss")]
    [InlineData(AreaMapOverlayMarkerKind.Ritual, "Ritual")]
    [InlineData(AreaMapOverlayMarkerKind.Breach, "Breach")]
    [InlineData(AreaMapOverlayMarkerKind.Essence, "Essence")]
    [InlineData(AreaMapOverlayMarkerKind.Incursion, "Incursion")]
    [InlineData(AreaMapOverlayMarkerKind.Strongbox, "Strongbox")]
    [InlineData(AreaMapOverlayMarkerKind.RareMonster, "RareMonster")]
    [InlineData(AreaMapOverlayMarkerKind.RareChest, "RareChest")]
    [InlineData(AreaMapOverlayMarkerKind.UniqueChest, "UniqueChest")]
    [InlineData(AreaMapOverlayMarkerKind.Pollen, "Pollen")]
    [InlineData(AreaMapOverlayMarkerKind.OmenAltar, "OmenAltar")]
    public void ResolveGlyph_UsesDedicatedGlyphForEveryMarker(
        AreaMapOverlayMarkerKind kind,
        string expected)
    {
        Assert.Equal(expected, AreaMapOverlaySurface.ResolveGlyph(kind).ToString());
    }

    [Theory]
    [InlineData(AreaMapOverlayMarkerKind.Boss, "AtlasIconContentMapBoss")]
    [InlineData(AreaMapOverlayMarkerKind.Abyss, "AtlasIconContentAbyss")]
    [InlineData(AreaMapOverlayMarkerKind.Ritual, "AtlasIconContentRitual")]
    [InlineData(AreaMapOverlayMarkerKind.Breach, "AtlasIconContentBreach")]
    [InlineData(AreaMapOverlayMarkerKind.Essence, "AtlasIconContentEssence")]
    [InlineData(AreaMapOverlayMarkerKind.Incursion, "AtlasIconContentIncursion")]
    [InlineData(AreaMapOverlayMarkerKind.Strongbox, "AtlasIconContentStrongBox")]
    public void ResolveReferenceIconId_MapsValidatedGameArtwork(
        AreaMapOverlayMarkerKind kind,
        string expected)
        => Assert.Equal(expected, AreaMapOverlaySurface.ResolveReferenceIconId(kind));

    [Theory]
    [InlineData(AreaMapOverlayMarkerKind.Expedition)]
    [InlineData(AreaMapOverlayMarkerKind.OmenAltar)]
    [InlineData(AreaMapOverlayMarkerKind.RareMonster)]
    [InlineData(AreaMapOverlayMarkerKind.RareChest)]
    [InlineData(AreaMapOverlayMarkerKind.UniqueChest)]
    [InlineData(AreaMapOverlayMarkerKind.Pollen)]
    public void ResolveReferenceIconId_LeavesNonBitmapMarkersUnmapped(
        AreaMapOverlayMarkerKind kind)
        => Assert.Null(AreaMapOverlaySurface.ResolveReferenceIconId(kind));

    [Theory]
    [InlineData(AreaMapOverlayMarkerKind.Boss, "Bitmap")]
    [InlineData(AreaMapOverlayMarkerKind.Abyss, "Bitmap")]
    [InlineData(AreaMapOverlayMarkerKind.Ritual, "Bitmap")]
    [InlineData(AreaMapOverlayMarkerKind.Breach, "Bitmap")]
    [InlineData(AreaMapOverlayMarkerKind.Essence, "Bitmap")]
    [InlineData(AreaMapOverlayMarkerKind.Incursion, "Bitmap")]
    [InlineData(AreaMapOverlayMarkerKind.Strongbox, "Bitmap")]
    [InlineData(AreaMapOverlayMarkerKind.Expedition, "ExpeditionTag")]
    [InlineData(AreaMapOverlayMarkerKind.OmenAltar, "Vector")]
    [InlineData(AreaMapOverlayMarkerKind.RareMonster, "Vector")]
    [InlineData(AreaMapOverlayMarkerKind.RareChest, "Vector")]
    [InlineData(AreaMapOverlayMarkerKind.UniqueChest, "Vector")]
    [InlineData(AreaMapOverlayMarkerKind.Pollen, "Vector")]
    public void ResolveRenderMode_UsesExactlyOnePrimaryMode(
        AreaMapOverlayMarkerKind kind,
        string expected)
        => Assert.Equal(expected, AreaMapOverlaySurface.ResolveRenderMode(kind).ToString());

    [Theory]
    [InlineData(100f, 7f)]
    [InlineData(600f, 8.4f)]
    [InlineData(2000f, 13f)]
    public void CalculateIconRadius_ScalesWithViewportAndStaysBounded(
        float viewportHeight,
        float expected)
    {
        Assert.Equal(expected, AreaMapOverlaySurface.CalculateIconRadius(viewportHeight), 3);
    }

    [Fact]
    public void CalculateBitmapBounds_UsesExistingMarkerDiameter()
    {
        var bounds = AreaMapOverlaySurface.CalculateBitmapBounds(
            new Vector2(100, 80),
            13f);

        Assert.Equal(87f, bounds.Left);
        Assert.Equal(67f, bounds.Top);
        Assert.Equal(113f, bounds.Right);
        Assert.Equal(93f, bounds.Bottom);
    }

    [Theory]
    [InlineData(AreaMapViewKind.LargeMap)]
    [InlineData(AreaMapViewKind.MiniMap)]
    public void BuildDrawCommands_UsesBitmapTagAndVectorModesWithoutGeneratedLabels(
        AreaMapViewKind viewKind)
    {
        var placements = Enum.GetValues<AreaMapOverlayMarkerKind>()
            .Select((kind, index) => Placement(kind, index))
            .ToImmutableArray();
        var scene = new AreaMapOverlayScene(
            viewKind,
            new AreaUiRect(0, 0, 600, 400),
            placements);

        var commands = AreaMapOverlaySurface.BuildDrawCommands(
            new Rectangle(0, 0, 600, 400),
            scene,
            13f,
            Measure);

        Assert.Equal(placements.Length, commands.Length);
        Assert.Equal(
            Enum.GetValues<AreaMapOverlayGlyph>(),
            commands.Select(command => command.Glyph));

        var bitmapCommands = commands
            .Where(command => command.RenderMode == AreaMapOverlayRenderMode.Bitmap)
            .ToArray();
        Assert.Equal(7, bitmapCommands.Length);
        Assert.All(bitmapCommands, command =>
        {
            Assert.NotNull(command.ReferenceIconId);
            Assert.Null(command.Label);
            Assert.Null(command.LabelBounds);
            Assert.Null(command.ExpeditionTag);
        });

        var expedition = Assert.Single(
            commands.Where(command => command.RenderMode == AreaMapOverlayRenderMode.ExpeditionTag));
        Assert.Null(expedition.ReferenceIconId);
        Assert.Null(expedition.Label);
        Assert.Null(expedition.LabelBounds);
        Assert.NotNull(expedition.ExpeditionTag);
        Assert.Equal("6孔", expedition.ExpeditionTag!.HoleText);
        Assert.Equal(
            viewKind == AreaMapViewKind.LargeMap ? "6.5D" : null,
            expedition.ExpeditionTag.ValueText);

        var vectorCommands = commands
            .Where(command => command.RenderMode == AreaMapOverlayRenderMode.Vector)
            .ToArray();
        Assert.Equal(5, vectorCommands.Length);
        Assert.All(vectorCommands, command =>
        {
            Assert.Null(command.ReferenceIconId);
            Assert.Null(command.Label);
            Assert.Null(command.LabelBounds);
            Assert.Null(command.ExpeditionTag);
        });
    }

    [Fact]
    public void BuildDrawCommands_PreservesCenterAndIndependentInstanceIds()
    {
        var scene = new AreaMapOverlayScene(
            AreaMapViewKind.LargeMap,
            new AreaUiRect(0, 0, 400, 300),
            ImmutableArray.Create(
                Placement(AreaMapOverlayMarkerKind.Boss, 0, new Vector2(50, 60)),
                Placement(AreaMapOverlayMarkerKind.Boss, 1, new Vector2(100, 120)),
                Placement(AreaMapOverlayMarkerKind.Expedition, 2, new Vector2(180, 200))));

        var commands = AreaMapOverlaySurface.BuildDrawCommands(
            new Rectangle(0, 0, 400, 300),
            scene,
            13f,
            Measure);

        Assert.Equal(["marker-0", "marker-1", "marker-2"], commands.Select(command => command.InstanceId));
        Assert.Equal(
            [new Vector2(50, 60), new Vector2(100, 120), new Vector2(180, 200)],
            commands.Select(command => command.Center));
    }

    [Theory]
    [InlineData(AreaContentKind.Abyss, AreaMapOverlayMarkerKind.Abyss, AreaMapOverlayVisualState.AbyssCompleted)]
    [InlineData(AreaContentKind.Breach, AreaMapOverlayMarkerKind.Breach, AreaMapOverlayVisualState.BreachCompleted)]
    [InlineData(AreaContentKind.Essence, AreaMapOverlayMarkerKind.Essence, AreaMapOverlayVisualState.EssenceCompleted)]
    [InlineData(AreaContentKind.Ritual, AreaMapOverlayMarkerKind.Ritual, AreaMapOverlayVisualState.RitualCompleted)]
    public void BuildDrawCommands_UsesMutedGrayscaleWithoutCheckForCompletedMechanics(
        AreaContentKind kind,
        AreaMapOverlayMarkerKind markerKind,
        AreaMapOverlayVisualState visualState)
    {
        var center = new Vector2(123, 87);
        var placement = new AreaMapOverlayPlacement(
            "completed",
            kind,
            AreaContentPhase.Completed,
            center,
            visualState,
            markerKind: markerKind);

        var command = Assert.Single(AreaMapOverlaySurface.BuildDrawCommands(
            new Rectangle(0, 0, 600, 400),
            new AreaMapOverlayScene(
                AreaMapViewKind.LargeMap,
                new AreaUiRect(0, 0, 600, 400),
                [placement]),
            13f,
            Measure));

        Assert.Equal("#8B949E", command.ColorHex);
        Assert.Equal(AtlasIconVariant.Grayscale, command.IconVariant);
        Assert.Equal(0.55f, command.Opacity);
        Assert.False(command.DrawCompletionCheck);
        Assert.Null(command.Label);
        Assert.Null(command.LabelBounds);
        Assert.Equal(center, command.Center);
        Assert.Equal(AreaMapOverlaySurface.CalculateIconRadius(400), command.Radius);
    }

    [Theory]
    [InlineData(AreaContentKind.Abyss, AreaMapOverlayMarkerKind.Abyss, AreaMapOverlayVisualState.AbyssAvailable)]
    [InlineData(AreaContentKind.Breach, AreaMapOverlayMarkerKind.Breach, AreaMapOverlayVisualState.BreachAvailable)]
    [InlineData(AreaContentKind.Essence, AreaMapOverlayMarkerKind.Essence, AreaMapOverlayVisualState.EssenceAvailable)]
    public void BuildDrawCommands_UsesOriginalOpaqueIconForAvailableMechanics(
        AreaContentKind kind,
        AreaMapOverlayMarkerKind markerKind,
        AreaMapOverlayVisualState visualState)
    {
        var command = Assert.Single(AreaMapOverlaySurface.BuildDrawCommands(
            new Rectangle(0, 0, 600, 400),
            new AreaMapOverlayScene(
                AreaMapViewKind.LargeMap,
                new AreaUiRect(0, 0, 600, 400),
                [new AreaMapOverlayPlacement(
                    "available",
                    kind,
                    AreaContentPhase.Available,
                    new Vector2(100, 100),
                    visualState,
                    markerKind: markerKind)]),
            13f,
            Measure));

        Assert.Equal(AtlasIconVariant.Original, command.IconVariant);
        Assert.Equal(1f, command.Opacity);
        Assert.False(command.DrawCompletionCheck);
    }

    [Fact]
    public void BuildDrawCommands_PreservesOriginalOpaqueBossCompletionCheck()
    {
        var command = Assert.Single(AreaMapOverlaySurface.BuildDrawCommands(
            new Rectangle(0, 0, 600, 400),
            new AreaMapOverlayScene(
                AreaMapViewKind.LargeMap,
                new AreaUiRect(0, 0, 600, 400),
                [new AreaMapOverlayPlacement(
                    "boss",
                    AreaContentKind.Boss,
                    AreaContentPhase.Completed,
                    new Vector2(100, 100),
                    AreaMapOverlayVisualState.BossCompleted)]),
            13f,
            Measure));

        Assert.Equal(AtlasIconVariant.Original, command.IconVariant);
        Assert.Equal(1f, command.Opacity);
        Assert.True(command.DrawCompletionCheck);
    }

    [Fact]
    public void BuildDrawCommands_PreservesExpeditionTagHoleCountAndValue()
    {
        var command = Assert.Single(AreaMapOverlaySurface.BuildDrawCommands(
            new Rectangle(0, 0, 600, 400),
            new AreaMapOverlayScene(
                AreaMapViewKind.LargeMap,
                new AreaUiRect(0, 0, 600, 400),
                [new AreaMapOverlayPlacement(
                    "expedition",
                    AreaContentKind.Expedition,
                    AreaContentPhase.Available,
                    new Vector2(100, 100),
                    AreaMapOverlayVisualState.ExpeditionAvailable,
                    new AreaMapExpeditionMarker(6, "6.5D"))]),
            13f,
            Measure));

        Assert.Equal(AreaMapOverlayRenderMode.ExpeditionTag, command.RenderMode);
        Assert.Equal("6孔", command.ExpeditionTag!.HoleText);
        Assert.Equal("6.5D", command.ExpeditionTag.ValueText);
    }

    [Fact]
    public void BuildDrawCommands_RejectsInvalidBoundsAndNonFiniteCenters()
    {
        var scene = new AreaMapOverlayScene(
            AreaMapViewKind.LargeMap,
            new AreaUiRect(0, 0, 400, 300),
            ImmutableArray.Create(Placement(
                AreaMapOverlayMarkerKind.Boss,
                0,
                new Vector2(float.NaN, 20))));

        Assert.Empty(AreaMapOverlaySurface.BuildDrawCommands(
            new Rectangle(0, 0, 400, 300),
            scene,
            13f,
            Measure));
        Assert.Empty(AreaMapOverlaySurface.BuildDrawCommands(
            new Rectangle(0, 0, 0, 300),
            scene,
            13f,
            Measure));
    }

    private static AreaMapOverlayPlacement Placement(
        AreaMapOverlayMarkerKind markerKind,
        int index,
        Vector2? center = null)
    {
        var (contentKind, state) = markerKind switch
        {
            AreaMapOverlayMarkerKind.Boss =>
                (AreaContentKind.Boss, AreaMapOverlayVisualState.BossAvailable),
            AreaMapOverlayMarkerKind.Expedition =>
                (AreaContentKind.Expedition, AreaMapOverlayVisualState.ExpeditionAvailable),
            AreaMapOverlayMarkerKind.Abyss =>
                (AreaContentKind.Abyss, AreaMapOverlayVisualState.AbyssAvailable),
            AreaMapOverlayMarkerKind.Ritual =>
                (AreaContentKind.Ritual, AreaMapOverlayVisualState.RitualAvailable),
            AreaMapOverlayMarkerKind.Breach =>
                (AreaContentKind.Breach, AreaMapOverlayVisualState.BreachAvailable),
            AreaMapOverlayMarkerKind.Essence =>
                (AreaContentKind.Essence, AreaMapOverlayVisualState.EssenceAvailable),
            AreaMapOverlayMarkerKind.Incursion =>
                (AreaContentKind.Incursion, AreaMapOverlayVisualState.IncursionAvailable),
            AreaMapOverlayMarkerKind.Strongbox =>
                (AreaContentKind.Strongbox, AreaMapOverlayVisualState.StrongboxAvailable),
            AreaMapOverlayMarkerKind.OmenAltar =>
                (AreaContentKind.OmenAltar, AreaMapOverlayVisualState.OmenAltarAvailable),
            AreaMapOverlayMarkerKind.RareMonster =>
                (AreaContentKind.Unknown, AreaMapOverlayVisualState.RareMonster),
            AreaMapOverlayMarkerKind.RareChest =>
                (AreaContentKind.Unknown, AreaMapOverlayVisualState.RareChest),
            AreaMapOverlayMarkerKind.UniqueChest =>
                (AreaContentKind.Unknown, AreaMapOverlayVisualState.UniqueChest),
            AreaMapOverlayMarkerKind.Pollen =>
                (AreaContentKind.Pollen, AreaMapOverlayVisualState.PollenAvailable),
            _ => throw new ArgumentOutOfRangeException(nameof(markerKind), markerKind, null)
        };
        return new AreaMapOverlayPlacement(
            $"marker-{index}",
            contentKind,
            AreaContentPhase.Available,
            center ?? new Vector2(20 + index * 40, 100),
            state,
            expeditionMarker: markerKind == AreaMapOverlayMarkerKind.Expedition
                ? new AreaMapExpeditionMarker(6, "6.5D")
                : null,
            markerKind: markerKind);
    }

    private static SizeF Measure(string text, float fontSize)
        => new(text.Length * fontSize * 0.6f, fontSize);
}
