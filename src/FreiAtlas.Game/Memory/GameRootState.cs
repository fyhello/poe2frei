namespace FreiAtlas.Game.Memory;

internal readonly record struct GameRootState(
    nint GameStateSlot,
    nint GameState,
    nint InGameState,
    nint AreaInstance,
    nint LocalPlayer);
