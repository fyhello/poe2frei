using System.Globalization;
using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Contracts;
using FreiAtlas.Game;
using FreiAtlas.Game.Replay;
using FreiAtlas.Platform.Windows.Overlay;
using FreiAtlas.Platform.Windows.Process;
using FreiAtlas.Platform.Windows.Windows;

namespace FreiAtlas.Host;

internal interface IAreaSamplingApi : IAreaMapApi
{
    AreaMapSnapshot SampleWorld();

    AreaMapSnapshot SampleRealtime();

    Task StartAsync(CancellationToken cancellationToken = default);
}

internal sealed record AreaSamplingProviderRequest(
    int ProcessId,
    bool IncludeExpeditionResearchEvidence);

internal interface IAreaSamplingProviderFactory
{
    bool TryCreate(
        AreaSamplingProviderRequest request,
        out IAreaSamplingApi? provider,
        out IDisposable? lifetime,
        out string reason);
}

public sealed class AreaCommandRunner
{
    private static readonly TimeSpan ProjectionFrameInterval =
        TimeSpan.FromMilliseconds(1000d / 30d);

    private readonly TextWriter _output;
    private readonly IAreaSamplingProviderFactory _providerFactory;

    public AreaCommandRunner()
        : this(Console.Out, new LiveAreaSamplingProviderFactory())
    {
    }

    internal AreaCommandRunner(TextWriter output)
        : this(output, new LiveAreaSamplingProviderFactory())
    {
    }

    internal AreaCommandRunner(
        TextWriter output,
        IAreaSamplingProviderFactory providerFactory)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _providerFactory = providerFactory
            ?? throw new ArgumentNullException(nameof(providerFactory));
    }

    public int RunProbe(int processId)
    {
        if (!TryCreateProvider(
                processId,
                includeExpeditionResearchEvidence: false,
                out var provider,
                out var lifetime,
                out var reason))
        {
            _output.WriteLine($"pid={processId}");
            _output.WriteLine("area=unavailable");
            _output.WriteLine($"reason={reason}");
            return 2;
        }

        using (lifetime)
        {
            provider!.SampleWorld();
            provider.SampleRealtime();
            var snapshot = provider.Current;
            foreach (var line in AreaCommandFormatter.FormatSnapshot(snapshot))
            {
                _output.WriteLine(line);
            }

            return snapshot.Status == AreaMapSnapshotStatus.Detached ? 2 : 0;
        }
    }

    public int RunExpeditionProbe(int processId)
    {
        if (!TryCreateProvider(
                processId,
                includeExpeditionResearchEvidence: true,
                out var provider,
                out var lifetime,
                out var reason))
        {
            _output.WriteLine($"pid={processId}");
            _output.WriteLine("area=unavailable");
            _output.WriteLine($"reason={reason}");
            return 2;
        }

        using (lifetime)
        {
            provider!.SampleWorld();
            provider.SampleRealtime();
            var snapshot = provider.Current;
            if (snapshot.Status is not (AreaMapSnapshotStatus.Stable
                    or AreaMapSnapshotStatus.Degraded)
                || snapshot.Area.AreaHash == 0)
            {
                _output.WriteLine($"pid={processId}");
                _output.WriteLine("area=unavailable");
                _output.WriteLine("reason=expedition-research-snapshot-not-ready");
                return 2;
            }

            foreach (var line in AreaCommandFormatter.FormatExpeditionResearch(snapshot))
            {
                _output.WriteLine(line);
            }

            return 0;
        }
    }

    public int RunWatch(int processId, CancellationToken cancellationToken = default)
    {
        if (!TryCreateProvider(
                processId,
                includeExpeditionResearchEvidence: false,
                out var provider,
                out var lifetime,
                out var reason))
        {
            _output.WriteLine($"pid={processId}");
            _output.WriteLine("area=unavailable");
            _output.WriteLine($"reason={reason}");
            return 2;
        }

        using (lifetime)
        {
            AreaMapSnapshot? last = null;
            provider!.SnapshotChanged += snapshot =>
            {
                if (last is null || !AreaCommandFormatter.HasSameWatchState(last, snapshot))
                {
                    foreach (var line in AreaCommandFormatter.FormatSnapshot(snapshot))
                    {
                        _output.WriteLine(line);
                    }

                    last = snapshot;
                }
            };

            provider.SampleWorld();
            provider.SampleRealtime();
            try
            {
                provider.StartAsync(cancellationToken).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }

            return 0;
        }
    }

    public int RunRecord(
        int processId,
        string directory,
        CancellationToken cancellationToken = default)
    {
        if (!TryCreateProvider(
                processId,
                includeExpeditionResearchEvidence: false,
                out var provider,
                out var lifetime,
                out var reason))
        {
            _output.WriteLine($"pid={processId}");
            _output.WriteLine("area=unavailable");
            _output.WriteLine($"reason={reason}");
            return 2;
        }

        using (lifetime)
        {
            return RunRecord(provider!, directory, cancellationToken);
        }
    }

    public int RunProjectionProbe(
        int processId,
        AreaProjectionProbeMode mode,
        string directory,
        CancellationToken cancellationToken = default)
    {
        if (!TryCreateProvider(
                processId,
                includeExpeditionResearchEvidence: false,
                out var provider,
                out var lifetime,
                out var reason))
        {
            _output.WriteLine($"pid={processId}");
            _output.WriteLine("area=unavailable");
            _output.WriteLine($"reason={reason}");
            return 2;
        }

        using (lifetime)
        {
            try
            {
                using var trace = new AreaProjectionProbeTraceWriter(directory);
                using var surface = AreaProjectionProbeSurface.Create();
                var windowTracker = new GameWindowTracker();
                return RunProjectionProbe(
                    provider!,
                    () => windowTracker.Track(processId),
                    surface,
                    trace,
                    mode,
                    cancellationToken,
                    ProjectionFrameInterval);
            }
            catch (IOException)
            {
                _output.WriteLine("reason=area-projection-log-write-failed");
                _output.WriteLine("error=IOException");
                return 2;
            }
        }
    }

    internal int RunProjectionProbe(
        IAreaSamplingApi provider,
        Func<GameWindowSnapshot?> windowProvider,
        IAreaProjectionProbeSurface surface,
        IAreaProjectionProbeTraceSink trace,
        AreaProjectionProbeMode mode,
        CancellationToken cancellationToken,
        TimeSpan frameInterval)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(windowProvider);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(trace);

        provider.SampleWorld();
        provider.SampleRealtime();
        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var samplingTask = provider.StartAsync(linkedCancellation.Token);
        try
        {
            return new AreaProjectionProbeRunner(_output).Run(
                provider,
                windowProvider,
                surface,
                trace,
                mode,
                AreaProjectionProbeCalibration.CreateReferenceInitial(),
                cancellationToken,
                frameInterval);
        }
        finally
        {
            linkedCancellation.Cancel();
            try
            {
                samplingTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
                when (linkedCancellation.IsCancellationRequested)
            {
            }
        }
    }

    internal int RunRecord(
        IAreaSamplingApi provider,
        string directory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        AreaSnapshotRecorder recorder;
        try
        {
            recorder = new AreaSnapshotRecorder(directory);
        }
        catch (Exception exception)
        {
            return ReportRecordFailure(exception);
        }

        Exception? recordingFailure = null;
        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void RecordSnapshot(AreaMapSnapshot snapshot)
        {
            if (Volatile.Read(ref recordingFailure) is not null)
            {
                return;
            }

            try
            {
                recorder.Record(snapshot);
            }
            catch (Exception exception)
            {
                if (Interlocked.CompareExchange(
                        ref recordingFailure,
                        exception,
                        null) is null)
                {
                    linkedCancellation.Cancel();
                }
            }
        }

        provider.SnapshotChanged += RecordSnapshot;
        try
        {
            provider.SampleWorld();
            if (Volatile.Read(ref recordingFailure) is null)
            {
                provider.SampleRealtime();
            }

            if (Volatile.Read(ref recordingFailure) is null)
            {
                provider.StartAsync(linkedCancellation.Token).GetAwaiter().GetResult();
            }
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            provider.SnapshotChanged -= RecordSnapshot;
            try
            {
                recorder.Dispose();
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref recordingFailure, exception, null);
            }
        }

        if (Volatile.Read(ref recordingFailure) is { } failure)
        {
            return ReportRecordFailure(failure);
        }

        _output.WriteLine($"areaRecord={recorder.OutputDirectory}");
        _output.WriteLine($"frames={recorder.FrameCount}");
        return 0;
    }

    private int ReportRecordFailure(Exception exception)
    {
        _output.WriteLine("area=unavailable");
        _output.WriteLine("reason=area-record-write-failed");
        _output.WriteLine($"error={exception.GetType().Name}");
        return 2;
    }

    public int RunReplay(string directory)
    {
        using var provider = ReplayAreaMapProvider.Load(directory);
        _output.WriteLine($"areaReplay={Path.GetFullPath(directory)}");
        _output.WriteLine($"frames={provider.FrameCount}");
        var replayed = 0;
        while (provider.ReadNext())
        {
            replayed++;
            var snapshot = provider.Current;
            _output.WriteLine(
                $"frame={replayed};capturedAt={snapshot.CapturedAt:O}"
                + $";status={snapshot.Status};area={snapshot.Area.AreaCode}"
                + $";bossPhase={FormatPhases(snapshot, AreaContentKind.Boss)}"
                + $";expeditionPhase={FormatPhases(snapshot, AreaContentKind.Expedition)}");
        }

        _output.WriteLine($"replayed={replayed}");
        return 0;
    }

    private bool TryCreateProvider(
        int processId,
        bool includeExpeditionResearchEvidence,
        out IAreaSamplingApi? provider,
        out IDisposable? lifetime,
        out string reason)
    {
        if (_providerFactory.TryCreate(
                new AreaSamplingProviderRequest(
                    processId,
                    includeExpeditionResearchEvidence),
                out provider,
                out lifetime,
                out reason)
            && provider is not null
            && lifetime is not null)
        {
            return true;
        }

        lifetime?.Dispose();
        provider = null;
        lifetime = null;
        reason = string.IsNullOrWhiteSpace(reason)
            ? "area-provider-factory-invalid"
            : reason;
        return false;
    }

    private sealed class LiveAreaSamplingProviderFactory : IAreaSamplingProviderFactory
    {
        public bool TryCreate(
            AreaSamplingProviderRequest request,
            out IAreaSamplingApi? provider,
            out IDisposable? lifetime,
            out string reason)
        {
            provider = null;
            lifetime = null;
            reason = "read-only-process-attach-failed";
            if (!ProcessAttachment.TryAttach(request.ProcessId, out var attachment)
                || attachment is null)
            {
                return false;
            }

            lifetime = attachment;
            var window = new GameWindowTracker().Track(request.ProcessId);
            if (window is not { ClientBounds.Width: > 0, ClientBounds.Height: > 0 })
            {
                attachment.Dispose();
                lifetime = null;
                reason = "client-viewport-unavailable";
                return false;
            }

            provider = new LiveAreaSamplingApi(new AreaMapProviderService(
                attachment.Memory,
                new AreaMapProviderOptions
                {
                    ClientViewport = new AreaUiRect(
                        0f,
                        0f,
                        window.ClientBounds.Width,
                        window.ClientBounds.Height),
                    IncludeExpeditionResearchEvidence =
                        request.IncludeExpeditionResearchEvidence
                }));
            return true;
        }
    }

    private static string FormatPhases(
        AreaMapSnapshot snapshot,
        AreaContentKind kind)
    {
        var phases = snapshot.Contents
            .Where(content => content.Kind == kind)
            .Select(content => content.Phase)
            .Distinct()
            .Order()
            .ToArray();
        return phases.Length == 0 ? "none" : string.Join(',', phases);
    }

    private sealed class LiveAreaSamplingApi : IAreaSamplingApi
    {
        private readonly AreaMapProviderService _provider;

        public LiveAreaSamplingApi(AreaMapProviderService provider)
        {
            _provider = provider;
        }

        public AreaMapSnapshot Current => _provider.Current;

        public event Action<AreaMapSnapshot>? SnapshotChanged
        {
            add => _provider.SnapshotChanged += value;
            remove => _provider.SnapshotChanged -= value;
        }

        public AreaMapSnapshot SampleWorld() => _provider.SampleWorld();

        public AreaMapSnapshot SampleRealtime() => _provider.SampleRealtime();

        public Task StartAsync(CancellationToken cancellationToken = default)
            => _provider.StartAsync(cancellationToken);
    }
}

public static class AreaCommandFormatter
{
    public static IReadOnlyList<string> FormatSnapshot(AreaMapSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var lines = new List<string>
        {
            $"pid={snapshot.ProcessId}",
            $"profile={snapshot.ProfileId}",
            $"status={snapshot.Status}",
            $"area={snapshot.Area.AreaCode}",
            $"areaHash=0x{snapshot.Area.AreaHash:X8}",
            $"areaLevel={snapshot.Area.AreaLevel}",
            $"session={snapshot.Area.SessionSequence}",
            $"character={snapshot.Player?.CharacterName ?? "unknown"}",
            $"playerLevel={snapshot.Player?.Level ?? 0}",
            $"playerWorld={FormatVector(snapshot.Player?.WorldPosition)}",
            $"playerGrid={FormatVector(snapshot.Player?.GridPosition)}",
            $"entities={snapshot.Entities.Count}",
            $"contents={snapshot.Contents.Count}",
            $"pollen={snapshot.Contents.Count(item => item.Kind == AreaContentKind.Pollen)}",
            $"pollenWild={CountPollen(snapshot, AreaPollenKind.Wild)}",
            $"pollenSoul={CountPollen(snapshot, AreaPollenKind.Soul)}",
            $"pollenPrimal={CountPollen(snapshot, AreaPollenKind.Primal)}",
            $"pollenSacred={CountPollen(snapshot, AreaPollenKind.Sacred)}",
            $"pollenUnknown={CountPollen(snapshot, AreaPollenKind.Unknown)}",
            $"pollenModelLayoutSource={FormatPollenEvidence(snapshot, "ModelLayoutSource")}",
            $"pollenModelLayout={FormatPollenEvidence(snapshot, "ModelLayout")}",
            $"omenAltar={snapshot.Contents.Count(item => item.Kind == AreaContentKind.OmenAltar)}",
            $"boss={snapshot.Contents.Count(item => item.Kind == AreaContentKind.Boss)}",
            $"bossCandidate={snapshot.Contents.Count(item => item.Kind == AreaContentKind.BossCandidate)}",
            $"expedition={snapshot.Contents.Count(item => item.Kind == AreaContentKind.Expedition)}",
            $"unknown={snapshot.Contents.Count(item => item.Kind == AreaContentKind.Unknown)}",
            $"terrain={(snapshot.Terrain is null ? "none" : $"{snapshot.Terrain.Width}x{snapshot.Terrain.Height}")}",
            $"landmarks={snapshot.Landmarks.Count}",
            $"largeMap={FormatView(snapshot.MapViews.LargeMap)}",
            $"miniMap={FormatView(snapshot.MapViews.MiniMap)}",
            $"diagnostics={string.Join(',', snapshot.Diagnostics.Select(item => item.Code).Distinct(StringComparer.Ordinal))}"
        };
        foreach (var category in Enum.GetValues<AreaEntityCategory>())
        {
            lines.Add(
                $"entity{category}={snapshot.Entities.Count(entity => entity.Category == category)}");
        }

        return lines;
    }

    public static IReadOnlyList<string> FormatExpeditionResearch(
        AreaMapSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var lines = new List<string>
        {
            $"pid={snapshot.ProcessId}",
            $"profile={snapshot.ProfileId}",
            $"status={snapshot.Status}",
            $"area={snapshot.Area.AreaCode}"
        };
        var expeditions = snapshot.Contents
            .Where(content => content.Kind == AreaContentKind.Expedition)
            .OrderBy(content => content.SourceEntityId ?? uint.MaxValue)
            .ThenBy(content => content.InstanceId, StringComparer.Ordinal)
            .ToArray();
        if (expeditions.Length == 0)
        {
            lines.Add("expeditionResearch=none");
            return lines;
        }

        lines.Add($"expeditionResearch=count:{expeditions.Length}");
        var entities = snapshot.Entities.ToDictionary(entity => entity.EntityId);
        foreach (var expedition in expeditions)
        {
            var entityId = expedition.SourceEntityId;
            var completed = entityId is { } id
                            && entities.TryGetValue(id, out var entity)
                ? entity.IsMinimapIconComplete.ToString().ToLowerInvariant()
                : "unknown";
            lines.Add(
                $"expedition=entity:{entityId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}"
                + $";instance:{expedition.InstanceId}"
                + $";world:{FormatVector(expedition.WorldPosition)}"
                + $";grid:{FormatVector(expedition.GridPosition)}"
                + $";phase:{expedition.Phase}"
                + $";completed:{completed}");

            foreach (var evidence in expedition.Evidence
                         .Where(item => item.Key.StartsWith(
                             "Research.",
                             StringComparison.Ordinal))
                         .OrderBy(item => item.Source, StringComparer.Ordinal)
                         .ThenBy(item => item.Key, StringComparer.Ordinal)
                         .ThenBy(item => item.Value, StringComparer.Ordinal))
            {
                lines.Add(
                    $"research=entity:{entityId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}"
                    + $";source:{evidence.Source}"
                    + $";key:{evidence.Key}"
                    + $";value:{evidence.Value}"
                    + $";confidence:{evidence.Confidence.ToString("0.###", CultureInfo.InvariantCulture)}");
            }
        }

        return lines;
    }

    internal static bool HasSameWatchState(
        AreaMapSnapshot left,
        AreaMapSnapshot right)
        => left.Status == right.Status
           && left.Area == right.Area
           && left.Entities.Select(entity => entity.EntityId).Order().SequenceEqual(
               right.Entities.Select(entity => entity.EntityId).Order())
           && left.Contents.Select(content => (content.InstanceId, content.Phase)).SequenceEqual(
               right.Contents.Select(content => (content.InstanceId, content.Phase)))
           && left.MapViews == right.MapViews;

    private static int CountPollen(AreaMapSnapshot snapshot, AreaPollenKind kind)
        => snapshot.Contents.Count(content => content.Kind == AreaContentKind.Pollen
                                              && (content.PollenDetails?.Kind
                                                  ?? AreaPollenKind.Unknown) == kind);

    private static string FormatPollenEvidence(
        AreaMapSnapshot snapshot,
        string key)
    {
        var values = snapshot.Contents
            .Where(content => content.Kind == AreaContentKind.Pollen)
            .SelectMany(content => content.Evidence)
            .Where(evidence => evidence.Source == "Pollen" && evidence.Key == key)
            .Select(evidence => evidence.Value)
            .GroupBy(value => value, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}:{group.Count()}")
            .ToArray();
        return values.Length == 0 ? "none" : string.Join(',', values);
    }

    private static string FormatView(AreaMapViewSnapshot view)
        => $"{view.Availability}/{(view.IsVisible ? "visible" : "hidden")}/"
           + $"shift:{view.Shift.X:0.###},{view.Shift.Y:0.###}/zoom:{view.Zoom:0.###}/"
           + $"viewport:{FormatViewport(view.Viewport)}";

    private static string FormatViewport(AreaUiRect? viewport)
        => viewport is { } rect
            ? $"{rect.X:0.###},{rect.Y:0.###},{rect.Width:0.###},{rect.Height:0.###}"
            : "none";

    private static string FormatVector(Vector3? vector)
        => vector is { } value
            ? FormattableString.Invariant($"{value.X:0.###},{value.Y:0.###},{value.Z:0.###}")
            : "unknown";

    private static string FormatVector(Vector2? vector)
        => vector is { } value
            ? FormattableString.Invariant($"{value.X:0.###},{value.Y:0.###}")
            : "unknown";
}
