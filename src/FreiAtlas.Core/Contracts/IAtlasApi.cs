using FreiAtlas.Core.Atlas;
using System.Numerics;

namespace FreiAtlas.Core.Contracts;

public interface IAtlasApi
{
    AtlasSnapshot? Current { get; }
    event Action<AtlasSnapshot>? SnapshotChanged;
}

public readonly record struct AtlasLiveRenderGeometry(
    IReadOnlyDictionary<AtlasGridPos, Vector2> NodePositions,
    AtlasProjection Projection,
    bool IsStable = true);

public interface IAtlasLiveGeometryApi
{
    bool TryReadLiveGeometry(
        IReadOnlySet<AtlasGridPos> grids,
        out AtlasLiveRenderGeometry geometry);
}
