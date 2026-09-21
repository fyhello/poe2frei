using FreiAtlas.Atlas.Memory;
using FreiAtlas.Core.Atlas;

namespace FreiAtlas.Atlas.Graph;

public sealed class AtlasGraphProvider
{
    private readonly List<string> _diagnostics = [];

    public IReadOnlyList<string> Diagnostics => _diagnostics;

    public AtlasGraph Build(
        IEnumerable<AtlasNodeSnapshot> nodes,
        IEnumerable<AtlasUiEdgeData> rawEdges)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(rawEdges);

        _diagnostics.Clear();
        var graph = new AtlasGraph();
        foreach (var node in nodes)
        {
            graph.AddNode(node);
        }

        foreach (var rawEdge in rawEdges)
        {
            if (!graph.Nodes.ContainsKey(rawEdge.From)
                || !graph.Nodes.ContainsKey(rawEdge.To)
                || rawEdge.From == rawEdge.To)
            {
                _diagnostics.Add(
                    $"Invalid edge: {rawEdge.From} -> {rawEdge.To}.");
                continue;
            }

            graph.AddEdge(new AtlasEdgeSnapshot(
                rawEdge.From,
                rawEdge.To,
                AtlasEdgeState.Known,
                AtlasEdgeColor.Red));
        }

        return graph;
    }
}
