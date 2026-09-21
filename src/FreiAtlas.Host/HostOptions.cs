namespace FreiAtlas.Host;

public sealed record HostOptions(
    int? ProcessId,
    bool AtlasProbe,
    bool AtlasOverlay,
    string? ReplayPath)
{
    public bool AreaProbe { get; init; }

    public bool AreaWatch { get; init; }

    public bool AreaExpeditionProbe { get; init; }

    public string? AreaRecordPath { get; init; }

    public string? AreaReplayPath { get; init; }

    public AreaProjectionProbeMode? AreaProjectionProbeMode { get; init; }

    public string? ProjectionLogPath { get; init; }

    public bool RequiresCancellationHandling
        => AreaWatch
           || AreaRecordPath is not null
           || AreaProjectionProbeMode is not null;

    public static HostOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        int? processId = null;
        string? replayPath = null;
        string? areaRecordPath = null;
        string? areaReplayPath = null;
        string? projectionLogPath = null;
        AreaProjectionProbeMode? areaProjectionProbeMode = null;
        var atlasProbe = false;
        var atlasOverlay = false;
        var areaProbe = false;
        var areaWatch = false;
        var areaExpeditionProbe = false;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--pid":
                    processId = ParsePid(RequireValue(args, ref index, "--pid"));
                    break;
                case "--replay":
                    replayPath = RequireValue(args, ref index, "--replay");
                    break;
                case "--atlas-probe":
                    atlasProbe = true;
                    break;
                case "--atlas-overlay":
                    atlasOverlay = true;
                    break;
                case "--area-probe":
                    areaProbe = true;
                    break;
                case "--area-watch":
                    areaWatch = true;
                    break;
                case "--area-expedition-probe":
                    areaExpeditionProbe = true;
                    break;
                case "--area-record":
                    areaRecordPath = RequireValue(args, ref index, "--area-record");
                    break;
                case "--area-replay":
                    areaReplayPath = RequireValue(args, ref index, "--area-replay");
                    break;
                case "--area-projection-probe":
                    areaProjectionProbeMode = ParseProjectionProbeMode(
                        RequireValue(args, ref index, "--area-projection-probe"));
                    break;
                case "--projection-log":
                    projectionLogPath = RequireValue(args, ref index, "--projection-log");
                    break;
                case "--help":
                case "-h":
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown command line option: {args[index]}",
                        nameof(args));
            }
        }

        var hasProcessMode = atlasProbe
                             || atlasOverlay
                             || areaProbe
                             || areaWatch
                             || areaExpeditionProbe
                             || areaRecordPath is not null
                             || areaProjectionProbeMode is not null;
        if (hasProcessMode && processId is null)
        {
            throw new ArgumentException(
                "A live probe or recording mode requires --pid <pid>.",
                nameof(args));
        }

        var modeCount =
            (atlasProbe ? 1 : 0)
            + (atlasOverlay ? 1 : 0)
            + (areaProbe ? 1 : 0)
            + (areaWatch ? 1 : 0)
            + (areaExpeditionProbe ? 1 : 0)
            + (areaRecordPath is not null ? 1 : 0)
            + (areaReplayPath is not null ? 1 : 0)
            + (replayPath is not null ? 1 : 0)
            + (areaProjectionProbeMode is not null ? 1 : 0);
        if (modeCount > 1)
        {
            throw new ArgumentException(
                "Only one execution mode can be selected.",
                nameof(args));
        }

        if (replayPath is not null && processId is not null)
        {
            throw new ArgumentException(
                "--replay cannot be combined with --pid.",
                nameof(args));
        }

        if (areaReplayPath is not null && processId is not null)
        {
            throw new ArgumentException(
                "--area-replay cannot be combined with --pid.",
                nameof(args));
        }

        if (processId is not null && !hasProcessMode)
        {
            throw new ArgumentException(
                "--pid must be combined with an explicit execution mode.",
                nameof(args));
        }

        if ((areaProjectionProbeMode is null) != (projectionLogPath is null))
        {
            throw new ArgumentException(
                "--area-projection-probe and --projection-log must be provided together.",
                nameof(args));
        }

        return new HostOptions(
            processId,
            atlasProbe,
            atlasOverlay,
            replayPath)
        {
            AreaProbe = areaProbe,
            AreaWatch = areaWatch,
            AreaExpeditionProbe = areaExpeditionProbe,
            AreaRecordPath = areaRecordPath,
            AreaReplayPath = areaReplayPath,
            AreaProjectionProbeMode = areaProjectionProbeMode,
            ProjectionLogPath = projectionLogPath
        };
    }

    private static string RequireValue(
        IReadOnlyList<string> args,
        ref int index,
        string option)
    {
        if (index + 1 >= args.Count
            || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new ArgumentException(
                $"{option} requires a value.",
                nameof(args));
        }

        index++;
        return args[index];
    }

    private static int ParsePid(string value)
        => int.TryParse(value, out var processId) && processId > 0
            ? processId
            : throw new ArgumentException(
                $"Invalid process id: {value}",
                nameof(value));

    private static AreaProjectionProbeMode ParseProjectionProbeMode(string value)
        => value switch
        {
            "viewport" => global::FreiAtlas.Host.AreaProjectionProbeMode.Viewport,
            "targets" => global::FreiAtlas.Host.AreaProjectionProbeMode.Targets,
            _ => throw new ArgumentException(
                $"Invalid area projection probe mode: {value}",
                nameof(value))
        };
}
