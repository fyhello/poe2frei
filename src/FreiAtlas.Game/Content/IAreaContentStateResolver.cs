using FreiAtlas.Core.Area;

namespace FreiAtlas.Game.Content;

internal sealed record AreaContentContext(
    AreaIdentity Area,
    AreaEntitySnapshot Entity,
    IReadOnlyList<AreaLandmarkSnapshot> Landmarks,
    IReadOnlyList<AreaContentEvidence> Evidence);

internal interface IAreaContentStateResolver
{
    bool TryResolve(
        AreaContentContext context,
        out AreaContentSnapshot content);
}
