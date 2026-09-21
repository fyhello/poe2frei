namespace FreiAtlas.Core.Atlas;

public sealed class AtlasGraph
{
    private readonly Dictionary<AtlasGridPos, AtlasNodeSnapshot> _nodes = [];
    private readonly Dictionary<(AtlasGridPos From, AtlasGridPos To), AtlasEdgeSnapshot> _edges = [];

    public IReadOnlyDictionary<AtlasGridPos, AtlasNodeSnapshot> Nodes => _nodes;
    public IReadOnlyCollection<AtlasEdgeSnapshot> Edges => _edges.Values;

    public void AddNode(AtlasNodeSnapshot node)
    {
        _nodes[node.Grid] = node;
    }

    public void AddEdge(AtlasEdgeSnapshot edge)
    {
        var ordered = Order(edge.From, edge.To);
        var normalized = edge with
        {
            From = ordered.From,
            To = ordered.To
        };
        var key = (normalized.From, normalized.To);

        if (!_edges.TryGetValue(key, out var previous)
            || normalized.State > previous.State)
        {
            _edges[key] = normalized;
        }
    }

    private static (AtlasGridPos From, AtlasGridPos To) Order(
        AtlasGridPos left,
        AtlasGridPos right)
    {
        return left.X < right.X || left.X == right.X && left.Y <= right.Y
            ? (left, right)
            : (right, left);
    }
}
