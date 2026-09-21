using FreiAtlas.Core.Atlas;
using FreiAtlas.Core.Contracts;
using FreiAtlas.Core.Settings;

namespace FreiAtlas.Host;

internal sealed class AtlasNavigationRenderState
{
    private AtlasNavigationPlan? _plan;
    private string? _planInputSignature;
    private AtlasSnapshot? _lastSnapshotInput;
    private AtlasNavigationSettings? _lastSettingsInput;
    private AtlasLiveRenderGeometry? _lastStableGeometry;
    private int _failedGeometryFrames;

    public AtlasNavigationPlan ResolvePlan(
        AtlasSnapshot snapshot,
        AtlasNavigationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);

        if (_plan is not null
            && ReferenceEquals(_lastSnapshotInput, snapshot)
            && ReferenceEquals(_lastSettingsInput, settings))
        {
            return _plan;
        }

        var inputSignature = AtlasNavigationPlanner.CreateInputSignature(
            snapshot,
            settings);
        if (_plan is not null
            && string.Equals(
                _planInputSignature,
                inputSignature,
                StringComparison.Ordinal))
        {
            _lastSnapshotInput = snapshot;
            _lastSettingsInput = settings;
            return _plan;
        }

        _plan = AtlasNavigationPlanner.Build(snapshot, settings);
        _planInputSignature = _plan.InputSignature;
        _lastSnapshotInput = snapshot;
        _lastSettingsInput = settings;
        return _plan;
    }

    public AtlasLiveRenderGeometry? ObserveGeometry(
        bool atlasActive,
        AtlasLiveRenderGeometry? geometry)
    {
        if (!atlasActive)
        {
            Clear();
            return null;
        }

        if (geometry is { IsStable: true } stable
            && stable.NodePositions is not null)
        {
            _lastStableGeometry = stable;
            _failedGeometryFrames = 0;
            return stable;
        }

        _failedGeometryFrames++;
        return _failedGeometryFrames <= 15
            ? _lastStableGeometry
            : null;
    }

    public void Clear()
    {
        _plan = null;
        _planInputSignature = null;
        _lastSnapshotInput = null;
        _lastSettingsInput = null;
        _lastStableGeometry = null;
        _failedGeometryFrames = 0;
    }
}
