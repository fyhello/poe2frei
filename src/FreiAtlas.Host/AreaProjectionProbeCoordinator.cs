using System.Drawing;
using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Platform.Windows.Windows;

namespace FreiAtlas.Host;

public enum AreaProjectionProbeMode
{
    Viewport,
    Targets
}

public sealed record AreaProjectionProbeCalibration(
    AreaMapProjectionParameters LargeMap,
    AreaMapProjectionParameters MiniMap)
{
    public float MiniMapVisibleCenterSafeInsetRatio { get; init; } =
        AreaMapProjectionProfile.Verified.MiniMapVisibleCenterSafeInsetRatio;

    public static AreaProjectionProbeCalibration CreateReferenceInitial()
    {
        var profile = AreaMapProjectionProfile.Verified;
        return new AreaProjectionProbeCalibration(
            profile.LargeMap,
            profile.MiniMap)
        {
            MiniMapVisibleCenterSafeInsetRatio =
                profile.MiniMapVisibleCenterSafeInsetRatio
        };
    }

    public AreaMapProjectionParameters For(AreaMapViewKind kind)
        => kind switch
        {
            AreaMapViewKind.LargeMap => LargeMap,
            AreaMapViewKind.MiniMap => MiniMap,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
}

public sealed record AreaProjectionProbeMark(
    string TargetId,
    AreaContentKind ContentKind,
    AreaContentPhase Phase,
    Vector2 TargetGrid,
    Vector2 Center,
    string Label);

public sealed record AreaProjectionProbeTargetTrace(
    string TargetId,
    AreaContentKind ContentKind,
    AreaContentPhase Phase,
    Vector2 TargetGrid,
    Vector2 PredictedClientPoint);

public sealed record AreaProjectionProbeTraceFrame(
    string ProbeFrameId,
    DateTimeOffset CapturedAt,
    uint AreaHash,
    long SessionSequence,
    Rectangle ClientBounds,
    AreaMapViewSnapshot View,
    Vector2 PlayerGrid,
    AreaMapProjectionParameters Parameters,
    IReadOnlyList<AreaProjectionProbeTargetTrace> Targets);

public sealed record AreaProjectionProbeFrame(
    Rectangle ClientBounds,
    string ProbeFrameId,
    AreaMapViewKind ViewKind,
    AreaUiRect Viewport,
    bool DrawViewportGuide,
    IReadOnlyList<AreaProjectionProbeMark> Marks,
    AreaProjectionProbeTraceFrame Trace);

public static class AreaProjectionProbeCoordinator
{
    public static AreaProjectionProbeFrame? Build(
        GameWindowSnapshot? window,
        AreaMapSnapshot? snapshot,
        AreaProjectionProbeMode mode,
        AreaProjectionProbeCalibration calibration,
        string probeFrameId)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        if (window is null
            || !window.IsForeground
            || window.IsMinimized
            || window.ClientBounds.Width <= 0
            || window.ClientBounds.Height <= 0
            || snapshot is null
            || snapshot.Status is not (AreaMapSnapshotStatus.Stable
                or AreaMapSnapshotStatus.Degraded)
            || snapshot.Area.AreaHash == 0
            || snapshot.Player is not { } player
            || !IsFinite(player.GridPosition)
            || string.IsNullOrWhiteSpace(probeFrameId)
            || !Enum.IsDefined(mode))
        {
            return null;
        }

        var visibleViews = new[]
        {
            snapshot.MapViews.LargeMap,
            snapshot.MapViews.MiniMap
        }
            .Where(view => view.Availability == AreaMapViewAvailability.Verified
                           && view.IsVisible)
            .ToArray();
        if (visibleViews.Length != 1)
        {
            return null;
        }

        var view = visibleViews[0];
        if (!IsValid(view))
        {
            return null;
        }

        if (view.Kind is not (AreaMapViewKind.LargeMap or AreaMapViewKind.MiniMap))
        {
            return null;
        }

        var parameters = calibration.For(view.Kind);
        if (parameters.Kind != view.Kind)
        {
            return null;
        }

        var marks = mode == AreaProjectionProbeMode.Targets
            ? BuildMarks(
                snapshot.Contents,
                player.GridPosition,
                view,
                parameters,
                calibration.MiniMapVisibleCenterSafeInsetRatio)
            : Array.Empty<AreaProjectionProbeMark>();
        var targetTrace = marks
            .Select(mark => new AreaProjectionProbeTargetTrace(
                mark.TargetId,
                mark.ContentKind,
                mark.Phase,
                mark.TargetGrid,
                mark.Center))
            .ToArray();
        var trace = new AreaProjectionProbeTraceFrame(
            probeFrameId,
            snapshot.CapturedAt,
            snapshot.Area.AreaHash,
            snapshot.Area.SessionSequence,
            window.ClientBounds,
            view,
            player.GridPosition,
            parameters,
            targetTrace);
        return new AreaProjectionProbeFrame(
            window.ClientBounds,
            probeFrameId,
            view.Kind,
            view.Viewport!,
            mode == AreaProjectionProbeMode.Viewport,
            marks,
            trace);
    }

    private static IReadOnlyList<AreaProjectionProbeMark> BuildMarks(
        IReadOnlyList<AreaContentSnapshot> contents,
        Vector2 playerGrid,
        AreaMapViewSnapshot view,
        AreaMapProjectionParameters parameters,
        float miniMapVisibleCenterSafeInsetRatio)
    {
        var marks = new List<AreaProjectionProbeMark>();
        var ordinal = 0;
        foreach (var content in contents)
        {
            if (content.Kind is not (AreaContentKind.Boss
                or AreaContentKind.BossCandidate
                or AreaContentKind.Expedition))
            {
                continue;
            }

            ordinal++;
            if (!AreaMapProjection.TryProject(
                    playerGrid,
                    content.GridPosition,
                    view,
                    parameters,
                    out var center)
                || !Contains(view.Viewport!, center))
            {
                continue;
            }

            if (view.Kind == AreaMapViewKind.MiniMap)
            {
                var viewport = view.Viewport!;
                var inset = viewport.Height * miniMapVisibleCenterSafeInsetRatio;
                center = new Vector2(
                    Math.Clamp(
                        center.X,
                        viewport.X + inset,
                        viewport.X + viewport.Width - inset),
                    Math.Clamp(
                        center.Y,
                        viewport.Y + inset,
                        viewport.Y + viewport.Height - inset));
            }

            marks.Add(new AreaProjectionProbeMark(
                content.InstanceId,
                content.Kind,
                content.Phase,
                content.GridPosition,
                center,
                CreateLabel(view.Kind, content.Kind, ordinal)));
        }

        return marks;
    }

    private static string CreateLabel(
        AreaMapViewKind viewKind,
        AreaContentKind contentKind,
        int ordinal)
    {
        var viewCode = viewKind == AreaMapViewKind.LargeMap ? 'L' : 'M';
        var contentCode = contentKind switch
        {
            AreaContentKind.Boss => 'B',
            AreaContentKind.BossCandidate => 'C',
            AreaContentKind.Expedition => 'E',
            _ => throw new ArgumentOutOfRangeException(nameof(contentKind), contentKind, null)
        };
        return $"{viewCode}-{contentCode}{ordinal:00}";
    }

    private static bool Contains(AreaUiRect viewport, Vector2 point)
        => point.X >= viewport.X
           && point.X <= viewport.X + viewport.Width
           && point.Y >= viewport.Y
           && point.Y <= viewport.Y + viewport.Height;

    private static bool IsValid(AreaMapViewSnapshot view)
        => view.Viewport is { } viewport
           && IsFinite(view.Shift)
           && float.IsFinite(view.Zoom)
           && view.Zoom > 0f
           && float.IsFinite(view.RotationRadians)
           && view.RotationRadians == 0f
           && !view.RotatesWithPlayer
           && float.IsFinite(viewport.X)
           && float.IsFinite(viewport.Y)
           && float.IsFinite(viewport.Width)
           && float.IsFinite(viewport.Height)
           && viewport.Width > 0f
           && viewport.Height > 0f;

    private static bool IsFinite(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y);
}
