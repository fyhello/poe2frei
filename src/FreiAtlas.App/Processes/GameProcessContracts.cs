namespace FreiAtlas.App.Processes;

public enum GameProcessState
{
    InGame,
    CharacterNotLoaded,
    ReadFailed,
    Exited
}

public sealed record GameProcessSnapshot(
    int ProcessId,
    string? CharacterName,
    GameProcessState State,
    string? StatusMessage);

public interface IGameProcessCatalog
{
    Task<IReadOnlyList<GameProcessSnapshot>> ReadAsync(
        CancellationToken cancellationToken = default);
}
