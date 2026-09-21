using System.Collections.Immutable;
using System.Drawing;
using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Host;
using FreiAtlas.Platform.Windows.Windows;
using FreiAtlas.Plugin.AreaMap;

namespace FreiAtlas.Host.Tests;

public sealed class AreaMapOverlayCoordinatorTests
{
    [Fact]
    public void Build_ReturnsFrameForForegroundUsableWindowAndNonEmptyScene()
    {
        var scene = Scene();

        var frame = AreaMapOverlayCoordinator.Build(Window(), scene);

        Assert.NotNull(frame);
        Assert.Equal(new Rectangle(100, 200, 908, 600), frame!.ClientBounds);
        Assert.Same(scene, frame.Scene);
    }

    [Fact]
    public void Build_RejectsMissingBackgroundMinimizedOrInvalidWindow()
    {
        GameWindowSnapshot?[] invalid =
        [
            null,
            Window() with { IsForeground = false },
            Window() with { IsMinimized = true },
            Window() with { Handle = 0 },
            Window() with { ClientBounds = new Rectangle(0, 0, 0, 600) },
            Window() with { ClientBounds = new Rectangle(0, 0, 908, 0) }
        ];

        Assert.All(
            invalid,
            window => Assert.Null(AreaMapOverlayCoordinator.Build(window, Scene())));
    }

    [Fact]
    public void Build_RejectsMissingOrEmptyScene()
    {
        Assert.Null(AreaMapOverlayCoordinator.Build(Window(), null));

        var empty = new AreaMapOverlayScene(
            AreaMapViewKind.MiniMap,
            new AreaUiRect(600, 20, 280, 220),
            []);
        Assert.Null(AreaMapOverlayCoordinator.Build(Window(), empty));
    }

    private static GameWindowSnapshot Window()
        => new(
            (nint)42,
            1234,
            new Rectangle(90, 170, 928, 650),
            true,
            false)
        {
            ClientBounds = new Rectangle(100, 200, 908, 600)
        };

    private static AreaMapOverlayScene Scene()
        => new(
            AreaMapViewKind.MiniMap,
            new AreaUiRect(600, 20, 280, 220),
            ImmutableArray.Create(new AreaMapOverlayPlacement(
                "boss",
                AreaContentKind.Boss,
                AreaContentPhase.Available,
                new Vector2(700, 100),
                AreaMapOverlayVisualState.BossAvailable)));
}
