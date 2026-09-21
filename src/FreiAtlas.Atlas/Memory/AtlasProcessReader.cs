using FreiAtlas.Core.Memory;
using FreiAtlas.Core.Atlas;

namespace FreiAtlas.Atlas.Memory;

public interface IAtlasMemoryProbe
{
    AtlasUiTreeSnapshot Read(IProcessMemory memory, AtlasLayoutProfile profile);
    IReadOnlyList<string> Diagnostics => [];
}

public interface IAtlasTransformProbe
{
    bool TryReadTransform(
        IProcessMemory memory,
        AtlasLayoutProfile profile,
        out AtlasUiTransform transform);
}

public interface IAtlasLiveGeometryProbe
{
    bool TryReadLiveGeometry(
        IProcessMemory memory,
        AtlasLayoutProfile profile,
        out AtlasUiLiveGeometry geometry);
}

public interface IAtlasSelectiveLiveGeometryProbe
    : IAtlasLiveGeometryProbe
{
    bool TryReadLiveGeometry(
        IProcessMemory memory,
        AtlasLayoutProfile profile,
        IReadOnlySet<AtlasGridPos> grids,
        out AtlasUiLiveGeometry geometry);
}

public interface IAtlasLiveRenderGeometryProbe
{
    bool TryReadLiveRenderGeometry(
        IProcessMemory memory,
        AtlasLayoutProfile profile,
        IReadOnlySet<AtlasGridPos> grids,
        out AtlasUiLiveGeometry geometry);
}

public sealed class AtlasProcessReader
    : IAtlasUiSource,
      IAtlasUiTransformSource,
      IAtlasUiLiveGeometrySource,
      IAtlasUiSelectiveLiveGeometrySource,
      IAtlasUiLiveRenderGeometrySource
{
    private readonly IProcessMemory _memory;
    private readonly AtlasLayoutProfile _profile;
    private readonly IAtlasMemoryProbe _probe;

    public AtlasProcessReader(
        IProcessMemory memory,
        AtlasLayoutProfile profile,
        IAtlasMemoryProbe probe)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public int ProcessId => _memory.ProcessId;
    public IReadOnlyList<string> Diagnostics => _probe.Diagnostics;

    public AtlasUiTreeSnapshot Read()
        => _probe.Read(_memory, _profile);

    public bool TryReadTransform(out AtlasUiTransform transform)
    {
        if (_probe is IAtlasTransformProbe transformProbe)
        {
            return transformProbe.TryReadTransform(
                _memory,
                _profile,
                out transform);
        }

        transform = default;
        return false;
    }

    public bool TryReadLiveGeometry(out AtlasUiLiveGeometry geometry)
    {
        if (_probe is IAtlasLiveGeometryProbe geometryProbe
            && geometryProbe.TryReadLiveGeometry(
                _memory,
                _profile,
                out geometry))
        {
            return true;
        }

        geometry = default;
        return false;
    }

    public bool TryReadLiveGeometry(
        IReadOnlySet<AtlasGridPos> grids,
        out AtlasUiLiveGeometry geometry)
    {
        if (_probe is IAtlasSelectiveLiveGeometryProbe selectiveProbe
            && selectiveProbe.TryReadLiveGeometry(
                _memory,
                _profile,
                grids,
                out geometry))
        {
            return true;
        }

        return TryReadLiveGeometry(out geometry);
    }

    public bool TryReadLiveRenderGeometry(
        IReadOnlySet<AtlasGridPos> grids,
        out AtlasUiLiveGeometry geometry)
    {
        if (_probe is IAtlasLiveRenderGeometryProbe renderProbe
            && renderProbe.TryReadLiveRenderGeometry(
                _memory,
                _profile,
                grids,
                out geometry))
        {
            return true;
        }

        return TryReadLiveGeometry(grids, out geometry);
    }
}
