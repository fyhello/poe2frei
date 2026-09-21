namespace FreiAtlas.Core.Atlas;

public sealed class AtlasSnapshotStabilizer
{
    private readonly int _requiredStableSamples;
    private string? _lastSignature;
    private int _sameSignatureCount;
    private AtlasSnapshot? _current;

    public AtlasSnapshotStabilizer(int requiredStableSamples)
    {
        if (requiredStableSamples < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requiredStableSamples),
                "The required sample count must be positive.");
        }

        _requiredStableSamples = requiredStableSamples;
    }

    public AtlasSnapshot? Current => _current;

    public AtlasSnapshot? ReplaceCurrent(AtlasSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (_current is null)
        {
            return null;
        }

        _current = snapshot with { Status = AtlasSnapshotStatus.Stable };
        return _current;
    }

    public void Reset()
    {
        _lastSignature = null;
        _sameSignatureCount = 0;
        _current = null;
    }

    public AtlasSnapshotStatus Push(AtlasSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!string.Equals(_lastSignature, snapshot.Signature, StringComparison.Ordinal))
        {
            _lastSignature = snapshot.Signature;
            _sameSignatureCount = 1;
            return _current is null
                ? AtlasSnapshotStatus.Loading
                : AtlasSnapshotStatus.Rebuilding;
        }

        _sameSignatureCount++;
        if (_sameSignatureCount < _requiredStableSamples)
        {
            return _current is null
                ? AtlasSnapshotStatus.Loading
                : AtlasSnapshotStatus.Rebuilding;
        }

        _current = snapshot with { Status = AtlasSnapshotStatus.Stable };
        return AtlasSnapshotStatus.Stable;
    }
}
