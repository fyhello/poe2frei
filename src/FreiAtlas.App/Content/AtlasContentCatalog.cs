using System.Reflection;
using System.Text.Json;

namespace FreiAtlas.App.Content;

public sealed record AtlasContentCatalogEntry(
    string Id,
    string DisplayName,
    string IconPath);

public sealed class AtlasContentCatalog
{
    private const string ResourceName =
        "FreiAtlas.App.Content.content-catalog.json";

    private static readonly Lazy<AtlasContentCatalog> EmbeddedCatalog =
        new(LoadEmbedded);

    private AtlasContentCatalog(IReadOnlyList<AtlasContentCatalogEntry> entries)
    {
        Entries = entries;
        ContentIds = entries.Select(entry => entry.Id).ToArray();
    }

    public static AtlasContentCatalog Embedded => EmbeddedCatalog.Value;

    public IReadOnlyList<AtlasContentCatalogEntry> Entries { get; }

    public IReadOnlyList<string> ContentIds { get; }

    private static AtlasContentCatalog LoadEmbedded()
    {
        using var stream = typeof(AtlasContentCatalog).Assembly
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded content catalog '{ResourceName}' was not found.");
        var entries = JsonSerializer.Deserialize<List<AtlasContentCatalogEntry>>(
                stream,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                })
            ?? throw new InvalidDataException("The content catalog is empty.");

        if (entries.Count != 66
            || entries.Any(entry =>
                string.IsNullOrWhiteSpace(entry.Id)
                || string.IsNullOrWhiteSpace(entry.DisplayName)
                || string.IsNullOrWhiteSpace(entry.IconPath))
            || entries.Select(entry => entry.Id)
                .Distinct(StringComparer.Ordinal)
                .Count() != entries.Count)
        {
            throw new InvalidDataException(
                "The content catalog must contain 66 complete, unique entries.");
        }

        return new AtlasContentCatalog(entries.AsReadOnly());
    }
}
