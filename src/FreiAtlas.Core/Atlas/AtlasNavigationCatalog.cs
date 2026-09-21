using System.Security.Cryptography;
using System.Text;

namespace FreiAtlas.Core.Atlas;

public sealed record AtlasNavigationCatalogEntry(
    string DisplayName,
    int NodeCount,
    int IncompleteNodeCount);

public sealed record AtlasNavigationCatalog(
    IReadOnlyList<AtlasNavigationCatalogEntry> Entries,
    string Signature);

public static class AtlasNavigationCatalogBuilder
{
    public static AtlasNavigationCatalog Build(
        IReadOnlyList<AtlasNodeSnapshot> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var entries = nodes
            .Where(node => !string.IsNullOrWhiteSpace(node.DisplayName))
            .Select(node => new NamedNode(
                node.Grid,
                node.DisplayName!.Trim(),
                node.IsCompleted))
            .GroupBy(node => node.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AtlasNavigationCatalogEntry(
                group
                    .OrderBy(node => node.Grid.X)
                    .ThenBy(node => node.Grid.Y)
                    .ThenBy(node => node.DisplayName, StringComparer.Ordinal)
                    .First()
                    .DisplayName,
                group.Count(),
                group.Count(node => !node.IsCompleted)))
            .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.DisplayName, StringComparer.Ordinal)
            .ToArray();

        return new AtlasNavigationCatalog(
            Array.AsReadOnly(entries),
            CreateSignature(entries));
    }

    private static string CreateSignature(
        IReadOnlyList<AtlasNavigationCatalogEntry> entries)
    {
        var canonical = new StringBuilder();
        foreach (var entry in entries)
        {
            canonical
                .Append(entry.DisplayName.Length)
                .Append(':')
                .Append(entry.DisplayName)
                .Append(':')
                .Append(entry.NodeCount)
                .Append(':')
                .Append(entry.IncompleteNodeCount)
                .Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return Convert.ToHexString(hash);
    }

    private readonly record struct NamedNode(
        AtlasGridPos Grid,
        string DisplayName,
        bool IsCompleted);
}
