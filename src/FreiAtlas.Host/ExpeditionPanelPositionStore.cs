using System.Drawing;
using System.Text.Json;

namespace FreiAtlas.Host;

internal static class ExpeditionPanelPositionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static Point? TryLoad(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var document = JsonSerializer.Deserialize<PositionDocument>(
                File.ReadAllText(path),
                JsonOptions);
            return document is null
                ? null
                : new Point(document.X, document.Y);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(string path, Point position)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(
                new PositionDocument { X = position.X, Y = position.Y },
                JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private sealed class PositionDocument
    {
        public int X { get; set; }

        public int Y { get; set; }
    }
}
