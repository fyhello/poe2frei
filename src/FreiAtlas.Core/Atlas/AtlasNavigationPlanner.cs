using System.Security.Cryptography;
using System.Text;
using FreiAtlas.Core.Settings;

namespace FreiAtlas.Core.Atlas;

public readonly record struct AtlasNavigationEdgeKey(
    AtlasGridPos First,
    AtlasGridPos Second)
{
    public static AtlasNavigationEdgeKey Create(
        AtlasGridPos left,
        AtlasGridPos right)
        => Compare(left, right) <= 0
            ? new AtlasNavigationEdgeKey(left, right)
            : new AtlasNavigationEdgeKey(right, left);

    internal static int Compare(AtlasGridPos left, AtlasGridPos right)
    {
        var x = left.X.CompareTo(right.X);
        return x != 0 ? x : left.Y.CompareTo(right.Y);
    }
}

public sealed record AtlasNavigationTarget(
    AtlasGridPos Grid,
    string DisplayName);

public sealed record AtlasNavigationPlan(
    IReadOnlySet<AtlasGridPos> HighlightTargets,
    IReadOnlyList<AtlasNavigationTarget> DirectionTargets,
    IReadOnlySet<AtlasNavigationEdgeKey> RouteEdges,
    string InputSignature);

public static class AtlasNavigationPlanner
{
    public static string CreateInputSignature(
        AtlasSnapshot snapshot,
        AtlasNavigationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settings.Rules);

        var grids = snapshot.Nodes.Select(node => node.Grid).ToHashSet();
        var edges = snapshot.Edges
            .Where(edge => grids.Contains(edge.From)
                           && grids.Contains(edge.To)
                           && edge.From != edge.To)
            .Select(edge => AtlasNavigationEdgeKey.Create(edge.From, edge.To))
            .ToHashSet();
        return CreateInputSignature(snapshot, settings, edges);
    }

    public static AtlasNavigationPlan Build(
        AtlasSnapshot snapshot,
        AtlasNavigationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settings.Rules);

        var nodesByGrid = snapshot.Nodes
            .GroupBy(node => node.Grid)
            .ToDictionary(group => group.Key, group => group.First());
        var adjacency = nodesByGrid.Keys.ToDictionary(
            grid => grid,
            _ => new List<AtlasGridPos>());
        var edges = new HashSet<AtlasNavigationEdgeKey>();
        foreach (var edge in snapshot.Edges)
        {
            if (!adjacency.ContainsKey(edge.From)
                || !adjacency.ContainsKey(edge.To)
                || edge.From == edge.To)
            {
                continue;
            }

            var key = AtlasNavigationEdgeKey.Create(edge.From, edge.To);
            if (!edges.Add(key))
            {
                continue;
            }

            adjacency[edge.From].Add(edge.To);
            adjacency[edge.To].Add(edge.From);
        }

        foreach (var neighbors in adjacency.Values)
        {
            neighbors.Sort(AtlasNavigationEdgeKey.Compare);
        }

        var distances = new Dictionary<AtlasGridPos, int>();
        var predecessors = new Dictionary<AtlasGridPos, AtlasGridPos>();
        if (snapshot.CurrentGrid is { } current && adjacency.ContainsKey(current))
        {
            RunBreadthFirstSearch(current, adjacency, distances, predecessors);
        }

        var highlightTargets = new HashSet<AtlasGridPos>();
        var directionTargets = new Dictionary<AtlasGridPos, AtlasNavigationTarget>();
        var routeEdges = new HashSet<AtlasNavigationEdgeKey>();
        foreach (var pair in NormalizeRules(settings.Rules))
        {
            var candidates = nodesByGrid.Values
                .Where(node => NamesEqual(node.DisplayName, pair.Key))
                .Where(node => !settings.HideCompletedMaps || !node.IsCompleted)
                .OrderBy(node => node.Grid.X)
                .ThenBy(node => node.Grid.Y)
                .ToArray();
            if (candidates.Length == 0)
            {
                continue;
            }

            var targets = settings.TargetMode == AtlasNavigationTargetMode.All
                ? candidates
                : [SelectNearest(candidates, snapshot.CurrentGrid, distances)];
            foreach (var target in targets)
            {
                if (pair.Value.Highlight)
                {
                    highlightTargets.Add(target.Grid);
                }

                if (pair.Value.Direction)
                {
                    directionTargets[target.Grid] = new AtlasNavigationTarget(
                        target.Grid,
                        target.DisplayName!.Trim());
                }

                if (pair.Value.Route
                    && snapshot.CurrentGrid is { } routeStart
                    && distances.ContainsKey(target.Grid))
                {
                    AddPath(routeStart, target.Grid, predecessors, routeEdges);
                }
            }
        }

        return new AtlasNavigationPlan(
            highlightTargets,
            directionTargets.Values
                .OrderBy(target => target.Grid.X)
                .ThenBy(target => target.Grid.Y)
                .ToArray(),
            routeEdges,
            CreateInputSignature(snapshot, settings, edges));
    }

    private static void RunBreadthFirstSearch(
        AtlasGridPos start,
        IReadOnlyDictionary<AtlasGridPos, List<AtlasGridPos>> adjacency,
        IDictionary<AtlasGridPos, int> distances,
        IDictionary<AtlasGridPos, AtlasGridPos> predecessors)
    {
        var queue = new Queue<AtlasGridPos>();
        distances[start] = 0;
        queue.Enqueue(start);
        while (queue.TryDequeue(out var current))
        {
            foreach (var neighbor in adjacency[current])
            {
                if (distances.ContainsKey(neighbor))
                {
                    continue;
                }

                distances[neighbor] = distances[current] + 1;
                predecessors[neighbor] = current;
                queue.Enqueue(neighbor);
            }
        }
    }

    private static AtlasNodeSnapshot SelectNearest(
        IReadOnlyList<AtlasNodeSnapshot> candidates,
        AtlasGridPos? current,
        IReadOnlyDictionary<AtlasGridPos, int> distances)
    {
        var reachable = candidates
            .Where(node => distances.ContainsKey(node.Grid))
            .OrderBy(node => distances[node.Grid])
            .ThenBy(node => node.Grid.X)
            .ThenBy(node => node.Grid.Y)
            .FirstOrDefault();
        if (reachable is not null)
        {
            return reachable;
        }

        return current is { } origin
            ? candidates
                .OrderBy(node => SquaredDistance(origin, node.Grid))
                .ThenBy(node => node.Grid.X)
                .ThenBy(node => node.Grid.Y)
                .First()
            : candidates
                .OrderBy(node => node.Grid.X)
                .ThenBy(node => node.Grid.Y)
                .First();
    }

    private static long SquaredDistance(AtlasGridPos left, AtlasGridPos right)
    {
        var x = (long)left.X - right.X;
        var y = (long)left.Y - right.Y;
        return (x * x) + (y * y);
    }

    private static void AddPath(
        AtlasGridPos start,
        AtlasGridPos target,
        IReadOnlyDictionary<AtlasGridPos, AtlasGridPos> predecessors,
        ISet<AtlasNavigationEdgeKey> output)
    {
        var current = target;
        while (current != start)
        {
            if (!predecessors.TryGetValue(current, out var previous))
            {
                return;
            }

            output.Add(AtlasNavigationEdgeKey.Create(previous, current));
            current = previous;
        }
    }

    private static IReadOnlyDictionary<string, AtlasNavigationRule> NormalizeRules(
        IReadOnlyDictionary<string, AtlasNavigationRule> rules)
    {
        var normalized = new Dictionary<string, AtlasNavigationRule>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var pair in rules)
        {
            if (string.IsNullOrWhiteSpace(pair.Key)
                || pair.Value is null
                || !pair.Value.IsActive)
            {
                continue;
            }

            var name = pair.Key.Trim();
            if (normalized.TryGetValue(name, out var existing))
            {
                normalized[name] = new AtlasNavigationRule(
                    existing.Highlight || pair.Value.Highlight,
                    existing.Route || pair.Value.Route,
                    existing.Direction || pair.Value.Direction);
            }
            else
            {
                normalized.Add(name, pair.Value);
            }
        }

        return normalized;
    }

    private static bool NamesEqual(string? displayName, string normalizedName)
        => !string.IsNullOrWhiteSpace(displayName)
           && string.Equals(
               displayName.Trim(),
               normalizedName,
               StringComparison.OrdinalIgnoreCase);

    private static string CreateInputSignature(
        AtlasSnapshot snapshot,
        AtlasNavigationSettings settings,
        IEnumerable<AtlasNavigationEdgeKey> edges)
    {
        var value = new StringBuilder();
        foreach (var node in snapshot.Nodes
                     .Where(node => !string.IsNullOrWhiteSpace(node.DisplayName))
                     .OrderBy(node => node.Grid.X)
                     .ThenBy(node => node.Grid.Y))
        {
            value.Append(node.Grid.X).Append(',').Append(node.Grid.Y).Append(':')
                .Append(node.DisplayName!.Trim().ToUpperInvariant());
            if (settings.HideCompletedMaps)
            {
                value.Append(':').Append(node.IsCompleted ? '1' : '0');
            }

            value.Append(';');
        }

        value.Append('|');
        foreach (var edge in edges
                     .OrderBy(edge => edge.First.X)
                     .ThenBy(edge => edge.First.Y)
                     .ThenBy(edge => edge.Second.X)
                     .ThenBy(edge => edge.Second.Y))
        {
            value.Append(edge.First.X).Append(',').Append(edge.First.Y).Append('>')
                .Append(edge.Second.X).Append(',').Append(edge.Second.Y).Append(';');
        }

        value.Append('|')
            .Append(snapshot.CurrentGrid?.X).Append(',')
            .Append(snapshot.CurrentGrid?.Y).Append('|')
            .Append((int)settings.TargetMode).Append('|')
            .Append(settings.HideCompletedMaps ? '1' : '0').Append('|');
        foreach (var pair in NormalizeRules(settings.Rules)
                     .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            value.Append(pair.Key.ToUpperInvariant()).Append(':')
                .Append(pair.Value.Highlight ? '1' : '0')
                .Append(pair.Value.Route ? '1' : '0')
                .Append(pair.Value.Direction ? '1' : '0')
                .Append(';');
        }

        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(value.ToString())));
    }
}
