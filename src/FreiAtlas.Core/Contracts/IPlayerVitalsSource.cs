using FreiAtlas.Core.Player;

namespace FreiAtlas.Core.Contracts;

public interface IPlayerVitalsSource
{
    PlayerVitalsSnapshot Current { get; }
}
