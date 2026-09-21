namespace FreiAtlas.Core.Atlas;

public static class AtlasEdgeClassifier
{
    public static AtlasEdgeSnapshot Classify(
        AtlasEdgeSnapshot edge,
        bool fromAccessible,
        bool toAccessible)
    {
        var isReachable = fromAccessible || toAccessible;
        return edge with
        {
            State = isReachable ? AtlasEdgeState.Reachable : AtlasEdgeState.Locked,
            RenderColor = isReachable ? AtlasEdgeColor.Green : AtlasEdgeColor.Red
        };
    }
}
