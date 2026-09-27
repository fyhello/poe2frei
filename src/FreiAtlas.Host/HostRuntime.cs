using FreiAtlas.Atlas;
using FreiAtlas.Atlas.Memory;
using FreiAtlas.Atlas.Replay;
using FreiAtlas.Core.Atlas;
using FreiAtlas.Game.Content;
using FreiAtlas.Platform.Windows.Process;
using FreiAtlas.Platform.Windows.Windows;

namespace FreiAtlas.Host;

public sealed class HostRuntime
{
    internal const string Usage =
        "usage=--pid <pid> --atlas-overlay | --pid <pid> --atlas-probe | "
        + "--pid <pid> --area-probe | --pid <pid> --area-watch | "
        + "--pid <pid> --area-expedition-probe | --pid <pid> --pollen-probe | "
        + "--pid <pid> --area-record <directory> | --area-replay <directory> | "
        + "--pid <pid> --area-projection-probe <viewport|targets> "
        + "--projection-log <directory> | --replay <path>";

    private readonly AreaCommandRunner _areaRunner;
    private readonly Func<
        int,
        AreaProjectionProbeMode,
        string,
        CancellationToken,
        int> _runAreaProjectionProbe;

    public HostRuntime()
        : this(new AreaCommandRunner())
    {
    }

    internal HostRuntime(AreaCommandRunner areaRunner)
        : this(areaRunner, CreateProjectionProbeDelegate(areaRunner))
    {
    }

    internal HostRuntime(
        AreaCommandRunner areaRunner,
        Func<int, AreaProjectionProbeMode, string, CancellationToken, int>
            runAreaProjectionProbe)
    {
        _areaRunner = areaRunner ?? throw new ArgumentNullException(nameof(areaRunner));
        _runAreaProjectionProbe = runAreaProjectionProbe
            ?? throw new ArgumentNullException(nameof(runAreaProjectionProbe));
    }

    public int Run(
        HostOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.ReplayPath is not null)
        {
            return RunReplay(options.ReplayPath);
        }

        if (options.AreaReplayPath is not null)
        {
            return _areaRunner.RunReplay(options.AreaReplayPath);
        }

        if (options.AreaProjectionProbeMode is { } projectionMode)
        {
            if (options.ProcessId is null
                || string.IsNullOrWhiteSpace(options.ProjectionLogPath))
            {
                Console.WriteLine("pid=required");
                Console.WriteLine("area=unavailable");
                Console.WriteLine("reason=area-projection-options-invalid");
                return 2;
            }

            return _runAreaProjectionProbe(
                options.ProcessId.Value,
                projectionMode,
                options.ProjectionLogPath,
                cancellationToken);
        }

        if (options.PollenProbe)
        {
            if (options.ProcessId is null)
            {
                Console.WriteLine("pid=required");
                Console.WriteLine("pollen=unavailable");
                Console.WriteLine("reason=--pid-is-required");
                return 2;
            }

            return RunPollenProbe(options.ProcessId.Value);
        }

        if (options.AreaProbe
            || options.AreaWatch
            || options.AreaExpeditionProbe
            || options.AreaRecordPath is not null)
        {
            if (options.ProcessId is null)
            {
                Console.WriteLine("pid=required");
                Console.WriteLine("area=unavailable");
                Console.WriteLine("reason=--pid-is-required");
                return 2;
            }

            if (options.AreaRecordPath is not null)
            {
                return _areaRunner.RunRecord(
                    options.ProcessId.Value,
                    options.AreaRecordPath,
                    cancellationToken);
            }

            if (options.AreaExpeditionProbe)
            {
                return _areaRunner.RunExpeditionProbe(options.ProcessId.Value);
            }

            return options.AreaWatch
                ? _areaRunner.RunWatch(options.ProcessId.Value, cancellationToken)
                : _areaRunner.RunProbe(options.ProcessId.Value);
        }

        if (options.AtlasOverlay && options.ProcessId is null)
        {
            Console.WriteLine("pid=required");
            Console.WriteLine("overlay=unavailable");
            Console.WriteLine("reason=--pid-is-required");
            return 2;
        }

        if (options.AtlasOverlay)
        {
            return new AtlasOverlayRunner().Run(options.ProcessId!.Value);
        }

        if (options.AtlasProbe && options.ProcessId is null)
        {
            Console.WriteLine("pid=required");
            Console.WriteLine("atlas=unavailable");
            Console.WriteLine("reason=--pid-is-required");
            return 2;
        }

        if (options.AtlasProbe)
        {
            return RunAtlasProbe(options.ProcessId!.Value);
        }

        Console.WriteLine(Usage);
        return 0;
    }

    private static int RunReplay(string path)
    {
        var provider = ReplayAtlasProvider.Load(path);
        var snapshot = provider.Read();
        var replayName = Path.GetFileNameWithoutExtension(path);

        Console.WriteLine($"replay={replayName}");
        Console.WriteLine($"snapshot={snapshot.Status}");
        Console.WriteLine($"nodes={snapshot.NodeCount}");
        Console.WriteLine($"edges={snapshot.EdgeCount}");
        Console.WriteLine($"invalidEdges={CountInvalidEdges(snapshot)}");
        return 0;
    }

    private static Func<
        int,
        AreaProjectionProbeMode,
        string,
        CancellationToken,
        int> CreateProjectionProbeDelegate(AreaCommandRunner areaRunner)
    {
        ArgumentNullException.ThrowIfNull(areaRunner);
        return areaRunner.RunProjectionProbe;
    }

    private static int RunPollenProbe(int processId)
    {
        if (!ProcessAttachment.TryAttach(processId, out var attachment)
            || attachment is null)
        {
            Console.WriteLine($"pid={processId}");
            Console.WriteLine("pollen=unavailable");
            Console.WriteLine("reason=read-only-process-attach-failed");
            return 2;
        }

        using (attachment)
        {
            var result = new PollenResearchProbe(attachment.Memory).Capture();
            Console.WriteLine($"pid={result.ProcessId}");
            Console.WriteLine($"profile={result.ProfileId}");
            Console.WriteLine($"area={result.Area?.AreaCode ?? "unavailable"}");
            Console.WriteLine($"pollen=count:{result.Entities.Count}");
            foreach (var entity in result.Entities)
            {
                Console.WriteLine($"pollen={entity.FormatSummary()}");
                Console.WriteLine($"metadata={entity.MetadataPath}");
                Console.WriteLine($"components={string.Join(',', entity.Components)}");
                Console.WriteLine($"animatedModelPath={entity.AnimatedModelPath ?? "unavailable"}");
                Console.WriteLine($"renderPaths={string.Join('|', entity.RenderPaths)}");
                Console.WriteLine($"signaturePaths={string.Join('|', entity.SignaturePaths)}");
                Console.WriteLine($"limitedLifespanHex={entity.LimitedLifespanHex ?? "unavailable"}");
            }

            foreach (var diagnostic in result.Diagnostics)
            {
                Console.WriteLine(
                    $"diagnostic={diagnostic.Code};severity:{diagnostic.Severity};"
                    + $"message:{diagnostic.Message}");
            }

            return result.Area is null ? 2 : 0;
        }
    }

    private static int RunAtlasProbe(int processId)
    {
        if (!ProcessAttachment.TryAttach(processId, out var attachment)
            || attachment is null)
        {
            Console.WriteLine($"pid={processId}");
            Console.WriteLine("atlas=unavailable");
            Console.WriteLine("reason=read-only-process-attach-failed");
            return 2;
        }

        using (attachment)
        {
            var windowTracker = new GameWindowTracker();
            var window = windowTracker.Track(processId);
            Console.WriteLine($"pid={processId}");
            Console.WriteLine(window is null
                ? "window=not-found"
                : $"window={window.Bounds.Width}x{window.Bounds.Height}");
            if (window is not null)
            {
                Console.WriteLine(
                    $"client={window.ClientBounds.Width}x{window.ClientBounds.Height}"
                    + $"@{window.ClientBounds.Left},{window.ClientBounds.Top}");
            }

            var profile = AtlasLayoutProfile.Default;
            var probe = new LiveAtlasMemoryProbe();
            var source = new AtlasProcessReader(
                attachment.Memory,
                profile,
                probe);
            var provider = new AtlasUiTreeProvider(
                source,
                profile,
                () => GetViewport(windowTracker, processId));
            var service = new AtlasProviderService(
                provider,
                profile.RequiredStableSamples);

            var latest = AtlasWarmup.WaitUntilStable(
                service,
                maxSamples: 12,
                sampleInterval: TimeSpan.FromMilliseconds(120))
                ?? service.ReadLatest();
            Console.WriteLine("atlas=live");
            Console.WriteLine($"status={latest?.Status.ToString() ?? "Unavailable"}");
            Console.WriteLine($"nodes={latest?.NodeCount ?? 0}");
            Console.WriteLine($"edges={latest?.EdgeCount ?? 0}");
            Console.WriteLine(
                $"invalidEdges={(latest is null ? 0 : CountInvalidEdges(latest))}");
            Console.WriteLine(
                $"accessible={latest?.Nodes.Count(node => node.IsAccessible) ?? 0}");
            Console.WriteLine(
                $"completed={latest?.Nodes.Count(node => node.IsCompleted) ?? 0}");
            Console.WriteLine(
                $"visible={latest?.Nodes.Count(node => node.IsVisible) ?? 0}");
            Console.WriteLine(
                $"named={latest?.Nodes.Count(node => !string.IsNullOrWhiteSpace(node.DisplayName)) ?? 0}");
            Console.WriteLine(
                $"tagged={latest?.Nodes.Count(node => node.Contents.Count > 0) ?? 0}");
            Console.WriteLine(
                $"greenEdges={latest?.Edges.Count(edge => edge.RenderColor == AtlasEdgeColor.Green) ?? 0}");
            Console.WriteLine(
                $"redEdges={latest?.Edges.Count(edge => edge.RenderColor == AtlasEdgeColor.Red) ?? 0}");
            if (latest?.CurrentGrid is { } current)
            {
                Console.WriteLine($"current={current.X},{current.Y}");
                var graph = AnalyzeCurrentGraph(latest, current);
                Console.WriteLine(
                    $"current-graph=present:{graph.IsPresent};"
                    + $"degree:{graph.Degree};reachable:{graph.ReachableNodes}");
            }

            if (latest is { } projectionSnapshot)
            {
                Console.WriteLine(
                    $"projection=scale:{projectionSnapshot.Projection.ScaleX:0.####}"
                    + $",offset:{projectionSnapshot.Projection.OffsetX:0.##},"
                    + $"{projectionSnapshot.Projection.OffsetY:0.##}"
                    + $",uiScale:{projectionSnapshot.Projection.UiScale:0.####}"
                    + $",zoom:{projectionSnapshot.Projection.AtlasZoom:0.####}");
            }

            var rawTree = source.Read();
            Console.WriteLine(
                $"canvas-transform=relative:{rawTree.CanvasRelativePosition.X:0.##},"
                + $"{rawTree.CanvasRelativePosition.Y:0.##}"
                + $",scale:{rawTree.CanvasScale:0.####}"
                + $",nodeSize:{rawTree.NodeSize.X:0.##}x{rawTree.NodeSize.Y:0.##}");
            if (rawTree.CurrentGrid is { } rawCurrent)
            {
                var currentNode = rawTree.Nodes
                    .FirstOrDefault(node => node.Grid == rawCurrent);
                if (currentNode is not null)
                {
                    Console.WriteLine(
                        $"current-geometry=grid:{rawCurrent.X},{rawCurrent.Y}"
                        + $",relative:{currentNode.RelativeX:0.##},{currentNode.RelativeY:0.##}"
                        + $",visible:{currentNode.IsVisible}");
                }
            }

            if (latest is { NodeCount: > 0 } rangeSnapshot)
            {
                var minX = rangeSnapshot.Nodes.Min(node => node.RelativeX);
                var maxX = rangeSnapshot.Nodes.Max(node => node.RelativeX);
                var minY = rangeSnapshot.Nodes.Min(node => node.RelativeY);
                var maxY = rangeSnapshot.Nodes.Max(node => node.RelativeY);
                var medianX = rangeSnapshot.Nodes
                    .Select(node => node.RelativeX)
                    .Order()
                    .ElementAt(rangeSnapshot.Nodes.Count / 2);
                var medianY = rangeSnapshot.Nodes
                    .Select(node => node.RelativeY)
                    .Order()
                    .ElementAt(rangeSnapshot.Nodes.Count / 2);
                Console.WriteLine(
                    $"relative-range=x:{minX:0.##}..{maxX:0.##},"
                    + $"y:{minY:0.##}..{maxY:0.##},"
                    + $"median:{medianX:0.##},{medianY:0.##}");
                PrintGeometryBucket("all", rangeSnapshot.Nodes);
                PrintGeometryBucket(
                    "uiHidden",
                    rangeSnapshot.Nodes.Where(node => !node.IsVisible));
                PrintGeometryBucket(
                    "undiscovered",
                    rangeSnapshot.Nodes.Where(node => node.IsDiscovered is false));
                PrintGeometryBucket(
                    "visible",
                    rangeSnapshot.Nodes.Where(node => node.IsVisible));
            }

            if (latest is not null)
            {
                var rawNodes = latest.Nodes
                    .Where(node =>
                        node.RawContentValue != 0
                        || node.ContentVectorValues.Count > 0
                        || node.IconType != 0
                        || node.ContentBadges.Count > 0)
                    .ToArray();
                Console.WriteLine(
                    $"raw310Nodes={latest.Nodes.Count(node => node.RawContentValue != 0)}");
                Console.WriteLine(
                    $"contentVecNodes={latest.Nodes.Count(node => node.ContentVectorValues.Count > 0)}");
                Console.WriteLine(
                    $"iconTypeNodes={latest.Nodes.Count(node => node.IconType != 0)}");
                Console.WriteLine(
                    $"badgeNodes={latest.Nodes.Count(node => node.ContentBadges.Count > 0)}");

                foreach (var node in rawNodes.Take(20))
                {
                    Console.WriteLine(
                        $"raw-node={node.Grid.X},{node.Grid.Y}"
                        + $";map={node.DisplayName}"
                        + $";raw310={node.RawContentValue}"
                        + $";raw310Hex=0x{node.RawContentValue:X8}"
                        + $";vec=[{string.Join(",", node.ContentVectorValues)}]"
                        + $";iconType={node.IconType}"
                        + $";badges=[{string.Join("|", node.ContentBadges)}]"
                        + $";codes=[{string.Join("|", node.RawContent.Select(tag => tag.RawCode))}]"
                        + $";normalized=[{string.Join(",", node.Contents.Select(content => content.ContentId))}]");
                }
            }

            foreach (var diagnostic in provider.Diagnostics.Take(24))
            {
                Console.WriteLine($"diagnostic={diagnostic}");
            }

            return 0;
        }
    }

    private static void PrintGeometryBucket(
        string name,
        IEnumerable<AtlasNodeSnapshot> nodes)
    {
        var materialized = nodes.ToArray();
        if (materialized.Length == 0)
        {
            Console.WriteLine($"geometry-{name}=count:0");
            return;
        }

        var worldish = materialized.Count(node =>
            MathF.Abs(node.RelativeX) <= 5_000f
            && MathF.Abs(node.RelativeY) <= 5_000f);
        var implausible = materialized.Count(node =>
            MathF.Abs(node.RelativeX) > 50_000f
            || MathF.Abs(node.RelativeY) > 50_000f);
        var projected = materialized.Count(node =>
            node.RelativeX * 0.55f >= -64f
            && node.RelativeX * 0.55f <= 1_800f
            && node.RelativeY * 0.55f >= -64f
            && node.RelativeY * 0.55f <= 1_100f);
        Console.WriteLine(
            $"geometry-{name}=count:{materialized.Length};"
            + $"worldish:{worldish};implausible:{implausible};"
            + $"approxViewport:{projected}");
    }

    private static int CountInvalidEdges(AtlasSnapshot snapshot)
    {
        var grids = snapshot.Nodes.Select(node => node.Grid).ToHashSet();
        return snapshot.Edges.Count(edge =>
            edge.From == edge.To
            || !grids.Contains(edge.From)
            || !grids.Contains(edge.To));
    }

    private static (bool IsPresent, int Degree, int ReachableNodes) AnalyzeCurrentGraph(
        AtlasSnapshot snapshot,
        AtlasGridPos current)
    {
        var adjacency = snapshot.Nodes
            .Select(node => node.Grid)
            .Distinct()
            .ToDictionary(grid => grid, _ => new HashSet<AtlasGridPos>());
        foreach (var edge in snapshot.Edges)
        {
            if (edge.From == edge.To
                || !adjacency.ContainsKey(edge.From)
                || !adjacency.ContainsKey(edge.To))
            {
                continue;
            }

            adjacency[edge.From].Add(edge.To);
            adjacency[edge.To].Add(edge.From);
        }

        if (!adjacency.TryGetValue(current, out var neighbors))
        {
            return (false, 0, 0);
        }

        var visited = new HashSet<AtlasGridPos> { current };
        var queue = new Queue<AtlasGridPos>();
        queue.Enqueue(current);
        while (queue.TryDequeue(out var grid))
        {
            foreach (var neighbor in adjacency[grid])
            {
                if (visited.Add(neighbor))
                {
                    queue.Enqueue(neighbor);
                }
            }
        }

        return (true, neighbors.Count, visited.Count);
    }

    private static AtlasViewport? GetViewport(
        GameWindowTracker tracker,
        int processId)
    {
        var clientBounds = tracker.Track(processId)?.ClientBounds ?? default;
        return clientBounds.Width > 0 && clientBounds.Height > 0
            ? new AtlasViewport(clientBounds.Width, clientBounds.Height)
            : null;
    }
}
