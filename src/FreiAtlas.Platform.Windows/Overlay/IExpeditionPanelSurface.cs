using System.Drawing;
using FreiAtlas.Plugin.ExpeditionPanel;

namespace FreiAtlas.Platform.Windows.Overlay;

public interface IExpeditionPanelSurface : IDisposable
{
    event Action<Point>? PositionChanged;

    event Action? PanelToggleRequested;

    event Action<string>? EncounterToggleRequested;

    bool PumpMessages();

    void Render(
        Rectangle gameClientBounds,
        ExpeditionPanelScene scene,
        float panelScale = 1f);

    void Hide();

    void RequestHide();
}
