using FreiAtlas.Core.Memory;
using FreiAtlas.Game.Memory;

namespace FreiAtlas.Game.Entities;

internal sealed class EntityComponentResolver
{
    private const int MaximumComponents = 256;
    private const int MaximumComponentNameBytes = 32;

    private readonly Poe2MemoryProfile _profile;
    private readonly GameMemoryReader _reader;
    private readonly Dictionary<ComponentCacheKey, nint> _cache = [];
    private readonly Dictionary<nint, uint> _entityIdsByAddress = [];
    private long _sessionSequence = long.MinValue;

    public EntityComponentResolver(
        IProcessMemory memory,
        Poe2MemoryProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(memory);
        _profile = profile ?? Poe2MemoryProfile.Current;
        _reader = new GameMemoryReader(memory, _profile);
    }

    public void BeginSample(long sessionSequence)
    {
        if (_sessionSequence != sessionSequence)
        {
            _sessionSequence = sessionSequence;
            _cache.Clear();
            _entityIdsByAddress.Clear();
            return;
        }

        foreach (var key in _cache
                     .Where(entry => entry.Value == 0)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            _cache.Remove(key);
        }
    }

    public void Reset()
    {
        _sessionSequence = long.MinValue;
        _cache.Clear();
        _entityIdsByAddress.Clear();
    }

    public bool TryResolve(
        RawEntityRef entity,
        string componentName,
        out nint componentAddress)
    {
        componentAddress = 0;
        if (string.IsNullOrWhiteSpace(componentName))
        {
            return false;
        }

        ObserveEntityIdentity(entity);
        var key = new ComponentCacheKey(
            _sessionSequence,
            entity.EntityId,
            entity.EntityAddress,
            componentName);
        if (_cache.TryGetValue(key, out componentAddress))
        {
            return true;
        }

        if (!_reader.TryReadPointer(
                entity.EntityAddress + _profile.Entity.DetailsOffset,
                out var details)
            || !_reader.TryReadPointer(
                details + _profile.EntityDetails.ComponentLookupOffset,
                out var lookup)
            || !_reader.TryReadStdVector(
                entity.EntityAddress + _profile.Entity.ComponentsOffset,
                IntPtr.Size,
                MaximumComponents,
                out var components)
            || !_reader.TryReadStdVector(
                lookup + _profile.ComponentLookup.BucketOffset,
                _profile.ComponentLookup.EntryStride,
                MaximumComponents,
                out var entries))
        {
            return false;
        }

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries.First
                        + (index * _profile.ComponentLookup.EntryStride);
            if (!_reader.TryReadPointer(entry, out var nameAddress)
                || !_reader.TryReadUInt32(entry + IntPtr.Size, out var rawIndex)
                || !_reader.TryReadUtf8(
                    nameAddress,
                    MaximumComponentNameBytes,
                    out var candidateName))
            {
                return false;
            }

            var componentIndex = unchecked((int)rawIndex);
            if (componentIndex < 0 || componentIndex >= components.Count)
            {
                continue;
            }

            if (!string.Equals(
                    candidateName,
                    componentName,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (!_reader.TryReadPointer(
                    components.First + (componentIndex * IntPtr.Size),
                    out componentAddress))
            {
                return false;
            }

            _cache[key] = componentAddress;
            return true;
        }

        componentAddress = 0;
        _cache[key] = 0;
        return true;
    }

    private void ObserveEntityIdentity(RawEntityRef entity)
    {
        if (_entityIdsByAddress.TryGetValue(
                entity.EntityAddress,
                out var previousId)
            && previousId != entity.EntityId)
        {
            foreach (var key in _cache.Keys
                         .Where(key => key.EntityAddress == entity.EntityAddress)
                         .ToArray())
            {
                _cache.Remove(key);
            }
        }

        _entityIdsByAddress[entity.EntityAddress] = entity.EntityId;
    }

    private readonly record struct ComponentCacheKey(
        long SessionSequence,
        uint EntityId,
        nint EntityAddress,
        string ComponentName);
}
