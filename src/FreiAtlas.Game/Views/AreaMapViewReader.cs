using System.Numerics;
using FreiAtlas.Core.Area;

namespace FreiAtlas.Game.Views;

internal sealed class AreaMapViewReader
{
    private readonly Dictionary<string, CandidateHistory> _history =
        new(StringComparer.Ordinal);
    private long _sessionSequence = long.MinValue;
    private long _sampleSequence;

    public void Reset()
    {
        _history.Clear();
        _sessionSequence = long.MinValue;
        _sampleSequence = 0;
    }

    public AreaMapViewsSnapshot Read(
        long sessionSequence,
        IReadOnlyList<MapUiCandidate> candidates,
        AreaUiRect clientViewport)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (_sessionSequence != sessionSequence)
        {
            _sessionSequence = sessionSequence;
            _history.Clear();
            _sampleSequence = 0;
        }

        _sampleSequence++;

        foreach (var candidate in candidates)
        {
            if (!_history.TryGetValue(candidate.Fingerprint, out var history))
            {
                history = new CandidateHistory();
                _history[candidate.Fingerprint] = history;
            }

            history.Samples++;
            history.LastObservedSample = _sampleSequence;
            history.SeenVisible |= candidate.IsVisible;
            history.SeenHidden |= !candidate.IsVisible;
            if (candidate.IsVisible)
            {
                history.VisibleSamples++;
            }

            history.Last = candidate;
        }

        var large = SelectLarge(clientViewport);
        var mini = SelectMini(clientViewport, large);
        return new AreaMapViewsSnapshot(
            ToSnapshot(
                AreaMapViewKind.LargeMap,
                large,
                large is null
                    ? AreaMapViewAvailability.Unverified
                    : AreaMapViewAvailability.Verified,
                IsLargeIdentityVerified(large)),
            ToSnapshot(
                AreaMapViewKind.MiniMap,
                mini,
                large is not null && mini is not null
                    ? AreaMapViewAvailability.Verified
                    : AreaMapViewAvailability.Unverified,
                IsMiniIdentityVerified(mini),
                suppressVisibility: large?.Last.IsVisible == true));
    }

    private CandidateHistory? SelectLarge(AreaUiRect clientViewport)
    {
        var current = _history.Values
            .Where(history => history.LastObservedSample == _sampleSequence)
            .ToArray();
        var direct = current.FirstOrDefault(history =>
            history.Last.Fingerprint == MapUiCandidateProbe.DirectLargeFingerprint);
        if (direct is not null)
        {
            return direct;
        }

        if (current.Any(IsDirectCandidate))
        {
            return null;
        }

        return current
            .Where(history => history.LastObservedSample == _sampleSequence
                              && IsLargeViewport(history.Last.Viewport, clientViewport))
            .OrderByDescending(history => Area(history.Last.Viewport))
            .FirstOrDefault();
    }

    private CandidateHistory? SelectMini(
        AreaUiRect clientViewport,
        CandidateHistory? large)
    {
        var alternatives = _history.Values
            .Where(history => history.LastObservedSample == _sampleSequence
                              && !ReferenceEquals(history, large))
            .ToArray();

        var direct = alternatives.FirstOrDefault(history =>
            history.Last.Fingerprint == MapUiCandidateProbe.DirectMiniFingerprint);
        if (direct is not null)
        {
            return direct;
        }

        if (alternatives.Any(IsDirectCandidate)
            || large?.Last.Fingerprint == MapUiCandidateProbe.DirectLargeFingerprint)
        {
            return null;
        }

        // The game's minimap can live under a container whose own visible bit is
        // cleared. Its element flag remains on, so persistent visibility is a
        // stronger identity signal than the unscaled viewport corner.
        var persistent = alternatives
            .Where(history => history.SeenVisible && !history.SeenHidden)
            .OrderByDescending(history => history.VisibleSamples)
            .ThenBy(history => Area(history.Last.Viewport))
            .FirstOrDefault();
        if (persistent is not null)
        {
            return persistent;
        }

        return alternatives
            .Where(history => !IsLargeViewport(history.Last.Viewport, clientViewport)
                              && IsCornerViewport(history.Last.Viewport, clientViewport))
            .OrderByDescending(history => history.VisibleSamples)
            .ThenByDescending(history => history.Samples)
            .FirstOrDefault();
    }

    private AreaMapViewSnapshot ToSnapshot(
        AreaMapViewKind kind,
        CandidateHistory? history,
        AreaMapViewAvailability availability,
        bool identityVerified,
        bool suppressVisibility = false)
    {
        if (history is null)
        {
            return new AreaMapViewSnapshot(
                kind,
                AreaMapViewAvailability.Unverified,
                false,
                Vector2.Zero,
                0f,
                0f,
                false,
                null,
                0f);
        }

        var candidate = history.Last;
        var confidence = identityVerified
            ? 0.85f
            : 0.35f;
        return new AreaMapViewSnapshot(
            kind,
            availability,
            candidate.IsVisible && !suppressVisibility,
            candidate.Shift,
            candidate.Zoom,
            0f,
            false,
            candidate.Viewport,
            confidence);
    }

    private static bool IsLargeIdentityVerified(CandidateHistory? history)
        => history is not null
           && (history.Last.Fingerprint == MapUiCandidateProbe.DirectLargeFingerprint
               || (history.SeenVisible && history.SeenHidden));

    private static bool IsMiniIdentityVerified(CandidateHistory? history)
        => history is not null
           && (history.Last.Fingerprint == MapUiCandidateProbe.DirectMiniFingerprint
               || (history.SeenVisible
                   && !history.SeenHidden
                   && history.VisibleSamples >= 2));

    private static bool IsDirectCandidate(CandidateHistory history)
        => history.Last.Fingerprint is MapUiCandidateProbe.DirectLargeFingerprint
            or MapUiCandidateProbe.DirectMiniFingerprint;

    private static bool IsLargeViewport(
        AreaUiRect viewport,
        AreaUiRect client)
        => Area(viewport) >= Area(client) * 0.35f;

    private static bool IsCornerViewport(
        AreaUiRect viewport,
        AreaUiRect client)
    {
        var left = viewport.X <= client.X + client.Width * 0.2f;
        var right = viewport.X + viewport.Width >= client.X + client.Width * 0.8f;
        var top = viewport.Y <= client.Y + client.Height * 0.2f;
        var bottom = viewport.Y + viewport.Height >= client.Y + client.Height * 0.8f;
        return (left || right) && (top || bottom);
    }

    private static float Area(AreaUiRect rect)
        => MathF.Max(0f, rect.Width) * MathF.Max(0f, rect.Height);

    private sealed class CandidateHistory
    {
        public int Samples { get; set; }
        public long LastObservedSample { get; set; }
        public int VisibleSamples { get; set; }
        public bool SeenVisible { get; set; }
        public bool SeenHidden { get; set; }
        public MapUiCandidate Last { get; set; } = default!;
    }
}
