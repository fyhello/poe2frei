using FreiAtlas.Core.Area;

namespace FreiAtlas.Core.Contracts;

public interface IAreaMapApi
{
    AreaMapSnapshot Current { get; }

    event Action<AreaMapSnapshot>? SnapshotChanged;
}
