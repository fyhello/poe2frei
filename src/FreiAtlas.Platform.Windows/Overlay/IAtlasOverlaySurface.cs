using System.Drawing;
using FreiAtlas.Core.Atlas;
using FreiAtlas.Core.Settings;

namespace FreiAtlas.Platform.Windows.Overlay;

public interface IAtlasOverlaySurface : IDisposable
{
    bool PumpMessages();

    void Render(
        Rectangle clientBounds,
        IReadOnlyList<AtlasEdgePlacement> edges,
        IReadOnlyList<AtlasEdgePlacement> navigationRouteEdges,
        IReadOnlyList<AtlasLabelPlacement> labels,
        IReadOnlyList<AtlasContentIconPlacement> contentIcons,
        IReadOnlyList<AtlasDirectionPlacement> directions,
        AtlasDisplaySettings settings);

    void Hide();

    void RequestHide();
}
