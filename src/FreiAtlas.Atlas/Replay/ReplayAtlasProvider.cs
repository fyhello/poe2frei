using System.Text.Json;
using System.Text.Json.Serialization;
using FreiAtlas.Core.Atlas;
using FreiAtlas.Core.Contracts;

namespace FreiAtlas.Atlas.Replay;

public sealed record ReplayWindow(int Width, int Height);

public sealed class ReplayAtlasProvider : IAtlasApi
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly AtlasSnapshot _snapshot;

    private ReplayAtlasProvider(
        AtlasSnapshot snapshot,
        int processId,
        ReplayWindow window,
        string canvasToken)
    {
        _snapshot = snapshot;
        ProcessId = processId;
        Window = window;
        CanvasToken = canvasToken;
    }

    public int ProcessId { get; }
    public ReplayWindow Window { get; }
    public string CanvasToken { get; }
    public AtlasSnapshot? Current => _snapshot;

    public event Action<AtlasSnapshot>? SnapshotChanged
    {
        add { }
        remove { }
    }

    public static ReplayAtlasProvider Load(string path)
    {
        var resolvedPath = ResolvePath(path);
        var json = File.ReadAllText(resolvedPath);
        var document = JsonSerializer.Deserialize<ReplayAtlasDocument>(json, JsonOptions)
            ?? throw new InvalidDataException("Atlas replay is empty.");

        Validate(document, resolvedPath);

        var snapshot = new AtlasSnapshot(
            document.CapturedAt,
            document.Status,
            document.NodeCount,
            document.EdgeCount,
            document.Nodes,
            document.Edges,
            document.CurrentGrid,
            document.Projection,
            document.Signature,
            document.IsAtlasOpen);

        return new ReplayAtlasProvider(
            snapshot,
            document.ProcessId,
            document.Window,
            document.CanvasToken);
    }

    public AtlasSnapshot Read() => _snapshot;

    private static void Validate(ReplayAtlasDocument document, string path)
    {
        if (document.ProcessId <= 0)
        {
            throw new InvalidDataException($"{path}: processId must be positive.");
        }

        if (document.Window.Width <= 0 || document.Window.Height <= 0)
        {
            throw new InvalidDataException($"{path}: window dimensions must be positive.");
        }

        if (string.IsNullOrWhiteSpace(document.CanvasToken)
            || string.IsNullOrWhiteSpace(document.Signature))
        {
            throw new InvalidDataException($"{path}: canvasToken and signature are required.");
        }

        if (document.NodeCount != document.Nodes.Count
            || document.EdgeCount != document.Edges.Count)
        {
            throw new InvalidDataException($"{path}: metadata cardinality does not match payload.");
        }

        var grids = document.Nodes
            .Select(node => node.Grid)
            .ToHashSet();
        if (grids.Count != document.Nodes.Count)
        {
            throw new InvalidDataException($"{path}: node grid positions must be unique.");
        }

        foreach (var edge in document.Edges)
        {
            if (edge.From == edge.To
                || !grids.Contains(edge.From)
                || !grids.Contains(edge.To))
            {
                throw new InvalidDataException(
                    $"{path}: edge {edge.From} -> {edge.To} references an invalid node.");
            }
        }
    }

    private static string ResolvePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, path);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return Path.GetFullPath(path);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed class ReplayAtlasDocument
    {
        public int ProcessId { get; init; }
        public ReplayWindow Window { get; init; } = new(0, 0);
        public string CanvasToken { get; init; } = string.Empty;
        public int NodeCount { get; init; }
        public int EdgeCount { get; init; }
        public DateTimeOffset CapturedAt { get; init; }
        public AtlasSnapshotStatus Status { get; init; }
        public List<AtlasNodeSnapshot> Nodes { get; init; } = [];
        public List<AtlasEdgeSnapshot> Edges { get; init; } = [];
        public AtlasGridPos? CurrentGrid { get; init; }
        public AtlasProjection Projection { get; init; } = AtlasProjection.Identity;
        public string Signature { get; init; } = string.Empty;
        public bool IsAtlasOpen { get; init; } = true;
    }
}
