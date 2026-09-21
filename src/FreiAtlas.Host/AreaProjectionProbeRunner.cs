using System.Diagnostics;
using System.Globalization;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Contracts;
using FreiAtlas.Platform.Windows.Overlay;
using FreiAtlas.Platform.Windows.Windows;

namespace FreiAtlas.Host;

internal sealed class AreaProjectionProbeRunner
{
    private readonly TextWriter _output;

    public AreaProjectionProbeRunner(TextWriter output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public int Run(
        IAreaMapApi areaMapApi,
        Func<GameWindowSnapshot?> windowProvider,
        IAreaProjectionProbeSurface surface,
        IAreaProjectionProbeTraceSink trace,
        AreaProjectionProbeMode mode,
        AreaProjectionProbeCalibration calibration,
        CancellationToken cancellationToken,
        TimeSpan frameInterval)
    {
        ArgumentNullException.ThrowIfNull(areaMapApi);
        ArgumentNullException.ThrowIfNull(windowProvider);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(calibration);

        try
        {
            while (!cancellationToken.IsCancellationRequested
                   && surface.PumpMessages())
            {
                var startedAt = Stopwatch.GetTimestamp();
                var snapshot = areaMapApi.Current;
                var window = windowProvider();
                if (window is null)
                {
                    return 0;
                }

                var frame = AreaProjectionProbeCoordinator.Build(
                    window,
                    snapshot,
                    mode,
                    calibration,
                    CreateProbeFrameId(snapshot));
                if (frame is null)
                {
                    surface.Hide();
                }
                else
                {
                    try
                    {
                        trace.Write(frame.Trace);
                    }
                    catch (IOException)
                    {
                        surface.Hide();
                        _output.WriteLine("reason=area-projection-log-write-failed");
                        _output.WriteLine("error=IOException");
                        return 2;
                    }

                    surface.Render(ToVisual(frame));
                }

                SleepRemaining(startedAt, frameInterval, cancellationToken);
            }

            return 0;
        }
        finally
        {
            surface.Hide();
        }
    }

    internal static string CreateProbeFrameId(AreaMapSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{snapshot.Area.SessionSequence}-{snapshot.CapturedAt.UtcTicks:X16}");
    }

    private static AreaProjectionProbeFrameVisual ToVisual(
        AreaProjectionProbeFrame frame)
        => new(
            frame.ClientBounds,
            frame.ProbeFrameId,
            frame.Viewport,
            frame.DrawViewportGuide,
            frame.Marks.Select(mark => new AreaProjectionProbeVisualMark(
                mark.Center,
                mark.Label,
                mark.ContentKind == AreaContentKind.Expedition
                    ? AreaProjectionProbeVisualKind.Expedition
                    : AreaProjectionProbeVisualKind.Boss)).ToArray());

    private static void SleepRemaining(
        long startedAt,
        TimeSpan frameInterval,
        CancellationToken cancellationToken)
    {
        var remaining = frameInterval - Stopwatch.GetElapsedTime(startedAt);
        if (remaining > TimeSpan.FromMilliseconds(1))
        {
            cancellationToken.WaitHandle.WaitOne(remaining);
        }
    }
}
