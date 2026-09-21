using System.Drawing;
using FreiAtlas.Core.Settings;
using FreiAtlas.Plugin.AreaMap;

namespace FreiAtlas.Platform.Windows.Overlay;

public interface IAreaMapOverlaySurface : IDisposable
{
    bool PumpMessages();

    void Render(
        Rectangle clientBounds,
        AreaMapOverlayScene scene,
        AreaMapDisplaySettings settings);

    void Hide();

    void RequestHide();
}
