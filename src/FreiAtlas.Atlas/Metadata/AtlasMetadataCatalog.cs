using System.Reflection;
using System.Text.Json;

namespace FreiAtlas.Atlas.Metadata;

public readonly record struct AtlasMapMetadata(
    string Name,
    string Type,
    string Group,
    IReadOnlyList<string> Tags);

public readonly record struct AtlasContentMetadata(
    string ContentId,
    string DisplayName,
    string IconId,
    string Description);

public sealed class AtlasMetadataCatalog
{
    private readonly Dictionary<string, AtlasMapMetadata> _maps =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AtlasContentMetadata> _contentsByName =
        new(StringComparer.OrdinalIgnoreCase);

    public static AtlasMetadataCatalog Embedded { get; } = LoadEmbedded();

    public int MapCount => _maps.Count;
    public int ContentCount => _contentsByName.Count;

    public bool TryGetMap(
        string? mapId,
        out AtlasMapMetadata metadata)
    {
        if (!string.IsNullOrWhiteSpace(mapId)
            && _maps.TryGetValue(mapId, out metadata))
        {
            return true;
        }

        metadata = default;
        return false;
    }

    public bool TryGetContentByDisplayName(
        string? displayName,
        out AtlasContentMetadata metadata)
    {
        if (!string.IsNullOrWhiteSpace(displayName)
            && _contentsByName.TryGetValue(displayName, out metadata))
        {
            return true;
        }

        metadata = default;
        return false;
    }

    private static AtlasMetadataCatalog LoadEmbedded()
    {
        var catalog = new AtlasMetadataCatalog();
        var assembly = typeof(AtlasMetadataCatalog).Assembly;

        using (var stream = OpenResource(assembly, "atlas_maps.json"))
        {
            if (stream is not null)
            {
                using var document = JsonDocument.Parse(stream);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    var value = property.Value;
                    var tags = value.TryGetProperty("tags", out var tagsElement)
                        && tagsElement.ValueKind == JsonValueKind.Array
                        ? tagsElement.EnumerateArray()
                            .Where(item => item.ValueKind == JsonValueKind.String)
                            .Select(item => item.GetString())
                            .Where(item => !string.IsNullOrWhiteSpace(item))
                            .Select(item => item!)
                            .ToArray()
                        : Array.Empty<string>();

                    catalog._maps[property.Name] = new AtlasMapMetadata(
                        ReadString(value, "name"),
                        ReadString(value, "type"),
                        ReadString(value, "group"),
                        tags);
                }
            }
        }

        using (var stream = OpenResource(assembly, "atlas_content.json"))
        {
            if (stream is not null)
            {
                using var document = JsonDocument.Parse(stream);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    var value = property.Value;
                    var displayName = ReadString(value, "name");
                    if (displayName.Length == 0)
                    {
                        continue;
                    }

                    catalog._contentsByName[displayName] =
                        new AtlasContentMetadata(
                            property.Name,
                            displayName,
                            ReadString(value, "icon"),
                            ReadString(value, "desc"));
                }
            }
        }

        return catalog;
    }

    private static Stream? OpenResource(
        Assembly assembly,
        string suffix)
    {
        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(
                suffix,
                StringComparison.OrdinalIgnoreCase));
        return resourceName is null
            ? null
            : assembly.GetManifestResourceStream(resourceName);
    }

    private static string ReadString(
        JsonElement value,
        string propertyName)
        => value.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
}
