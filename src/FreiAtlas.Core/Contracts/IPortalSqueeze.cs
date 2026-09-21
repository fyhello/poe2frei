using FreiAtlas.Core.PortalSqueeze;

namespace FreiAtlas.Core.Contracts;

public interface IPortalCandidateSource : IDisposable
{
    PortalCandidateScan Scan(float maxDistanceGrid);
}

public interface IPortalInteraction
{
    PortalInteractionResult Interact(PortalCandidateScan scan, PortalCandidate target, TimeSpan timeout);
}
