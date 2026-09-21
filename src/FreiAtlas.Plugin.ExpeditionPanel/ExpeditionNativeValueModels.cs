using System.Collections.Immutable;
using FreiAtlas.Core.Area;

namespace FreiAtlas.Plugin.ExpeditionPanel;

public sealed record ExpeditionNativeValueRow(
    string RecipeId,
    int CatalogRow,
    AreaUiRect Bounds,
    string ValueText,
    ExpeditionPanelValueBand ValueBand);

public sealed record ExpeditionNativeValueScene(
    uint AreaHash,
    long SessionSequence,
    string InstanceId,
    AreaUiRect PanelBounds,
    AreaUiRect ListClipBounds,
    ImmutableArray<ExpeditionNativeValueRow> Rows);
