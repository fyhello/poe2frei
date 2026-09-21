using System.Drawing;
using FreiAtlas.Plugin.ExpeditionPanel;

namespace FreiAtlas.Platform.Windows.Overlay;

public interface IExpeditionNativeValueSurface : IDisposable
{
    bool PumpMessages();

    void Render(
        Rectangle gameClientBounds,
        ExpeditionNativeValueScene scene);

    void Hide();

    void RequestHide();
}
