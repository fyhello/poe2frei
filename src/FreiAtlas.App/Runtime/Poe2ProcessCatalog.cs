using FreiAtlas.App.Processes;
using FreiAtlas.Host.Processes;

namespace FreiAtlas.App.Runtime;

public sealed class Poe2ProcessCatalog : IGameProcessCatalog
{
    private readonly IPoe2ProcessCandidateReader _candidateReader;

    public Poe2ProcessCatalog()
        : this(new Poe2ProcessCandidateReader())
    {
    }

    public Poe2ProcessCatalog(IPoe2ProcessCandidateReader candidateReader)
    {
        _candidateReader = candidateReader
            ?? throw new ArgumentNullException(nameof(candidateReader));
    }

    public Task<IReadOnlyList<GameProcessSnapshot>> ReadAsync(
        CancellationToken cancellationToken = default)
        => ReadCandidatesAsync(cancellationToken);

    private async Task<IReadOnlyList<GameProcessSnapshot>> ReadCandidatesAsync(
        CancellationToken cancellationToken)
    {
        var candidates = await _candidateReader
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);
        return candidates.Select(Map).ToArray();
    }

    private static GameProcessSnapshot Map(Poe2ProcessCandidate candidate)
        => candidate.State switch
        {
            Poe2ProcessCandidateState.InGame => new GameProcessSnapshot(
                candidate.ProcessId,
                candidate.CharacterName,
                GameProcessState.InGame,
                null),
            Poe2ProcessCandidateState.CharacterNotLoaded => new GameProcessSnapshot(
                candidate.ProcessId,
                null,
                GameProcessState.CharacterNotLoaded,
                "角色未载入"),
            _ => Failed(candidate.ProcessId)
        };

    private static GameProcessSnapshot Failed(int processId)
        => new(processId, null, GameProcessState.ReadFailed, "读取失败");
}
