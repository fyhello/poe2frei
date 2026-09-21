using System.Reflection;
using System.Text.Json;

namespace FreiAtlas.Game.Entities;

internal sealed class EntityNameCatalog
{
    private const string DntPrefix = "[DNT-UNUSED] ";
    private readonly Dictionary<string, string> _names;

    private EntityNameCatalog(
        Dictionary<string, string> names,
        bool isAvailable)
    {
        _names = names;
        IsAvailable = isAvailable;
    }

    public bool IsAvailable { get; }
    public int Count => _names.Count;

    public static EntityNameCatalog LoadEmbedded()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith(
                    ".Data.entity-names.json",
                    StringComparison.OrdinalIgnoreCase));
            if (resourceName is null)
            {
                return Unavailable();
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return Unavailable();
            }

            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(
                stream);
            return entries is null
                ? Unavailable()
                : FromEntries(entries);
        }
        catch (JsonException)
        {
            return Unavailable();
        }
        catch (IOException)
        {
            return Unavailable();
        }
    }

    public static EntityNameCatalog FromEntries(
        IEnumerable<KeyValuePair<string, string>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var names = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            names[entry.Key] = entry.Value;
        }

        return new EntityNameCatalog(names, true);
    }

    public static EntityNameCatalog Unavailable()
        => new(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            false);

    public string ResolveOrShorten(string metadataPath)
    {
        var normalized = StripRuntimeSuffix(metadataPath);
        var resolved = Resolve(normalized);
        if (resolved is not null)
        {
            return resolved.StartsWith(DntPrefix, StringComparison.Ordinal)
                ? resolved[DntPrefix.Length..]
                : resolved;
        }

        var separator = normalized.LastIndexOf('/');
        return separator >= 0
            ? normalized[(separator + 1)..]
            : normalized;
    }

    public string? Resolve(string metadataPath)
    {
        if (_names.TryGetValue(metadataPath, out var name))
        {
            return name;
        }

        var probe = metadataPath;
        while (true)
        {
            var separator = probe.LastIndexOf('/');
            if (separator <= 0)
            {
                return null;
            }

            probe = probe[..separator];
            if (_names.TryGetValue(probe, out name))
            {
                return name;
            }
        }
    }

    private static string StripRuntimeSuffix(string metadataPath)
    {
        var marker = metadataPath.IndexOf('@');
        return marker >= 0 ? metadataPath[..marker] : metadataPath;
    }
}
