using System.Drawing;
using FreiAtlas.Platform.Windows.Windows;
using FreiAtlas.Plugin.AreaMap;

namespace FreiAtlas.Host;

public sealed record AreaMapOverlayFrame(
    Rectangle ClientBounds,
    AreaMapOverlayScene Scene);

public static class AreaMapOverlayCoordinator
{
    public static AreaMapOverlayFrame? Build(
        GameWindowSnapshot? window,
        AreaMapOverlayScene? scene)
    {
        if (window is null
            || window.Handle == 0
            || !window.IsForeground
            || window.IsMinimized
            || window.ClientBounds.Width <= 0
            || window.ClientBounds.Height <= 0
            || scene is null
            || scene.Placements.IsDefaultOrEmpty)
        {
            return null;
        }

        return new AreaMapOverlayFrame(window.ClientBounds, scene);
    }
}
