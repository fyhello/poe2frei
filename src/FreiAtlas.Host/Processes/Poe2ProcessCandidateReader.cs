using System.Diagnostics;
using FreiAtlas.Atlas.Memory;
using FreiAtlas.Platform.Windows.Process;

namespace FreiAtlas.Host.Processes;

public enum Poe2ProcessCandidateState
{
    InGame,
    CharacterNotLoaded,
    ReadFailed
}

public sealed record Poe2ProcessCandidate(
    int ProcessId,
    string? CharacterName,
    Poe2ProcessCandidateState State);

public interface IPoe2ProcessCandidateReader
{
    Task<IReadOnlyList<Poe2ProcessCandidate>> ReadAsync(
        CancellationToken cancellationToken = default);
}

public sealed class Poe2ProcessCandidateReader : IPoe2ProcessCandidateReader
{
    private static readonly string[] SupportedProcessNames =
    [
        "PathOfExile",
        "PathOfExileSteam",
        "PathOfExile_x64",
        "PathOfExile_KG",
        "PathOfExileEGS"
    ];

    private readonly GameProcessIdentityReader _identityReader = new();

    public Task<IReadOnlyList<Poe2ProcessCandidate>> ReadAsync(
        CancellationToken cancellationToken = default)
        => Task.Run(() => Read(cancellationToken), cancellationToken);

    private IReadOnlyList<Poe2ProcessCandidate> Read(
        CancellationToken cancellationToken)
    {
        var processesById = new Dictionary<int, Process>();
        foreach (var processName in SupportedProcessNames)
        {
            foreach (var process in GetProcessesSafely(processName))
            {
                try
                {
                    if (!processesById.TryAdd(process.Id, process))
                    {
                        process.Dispose();
                    }
                }
                catch (InvalidOperationException)
                {
                    process.Dispose();
                }
            }
        }

        var processes = processesById
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value)
            .ToArray();
        var candidates = new List<Poe2ProcessCandidate>(processes.Length);
        foreach (var process in processes)
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                candidates.Add(ReadProcess(process.Id));
            }
        }

        return candidates;
    }

    private Poe2ProcessCandidate ReadProcess(int processId)
    {
        try
        {
            if (!ProcessAttachment.TryAttach(processId, out var attachment)
                || attachment is null)
            {
                return Failed(processId);
            }

            using (attachment)
            {
                var identity = _identityReader.Read(
                    attachment.Memory,
                    AtlasLayoutProfile.Default);
                return identity.State switch
                {
                    GameProcessIdentityState.InGame => new Poe2ProcessCandidate(
                        processId,
                        identity.CharacterName,
                        Poe2ProcessCandidateState.InGame),
                    GameProcessIdentityState.CharacterNotLoaded => new Poe2ProcessCandidate(
                        processId,
                        null,
                        Poe2ProcessCandidateState.CharacterNotLoaded),
                    _ => Failed(processId)
                };
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            return Failed(processId);
        }
    }

    private static IEnumerable<Process> GetProcessesSafely(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    private static Poe2ProcessCandidate Failed(int processId)
        => new(processId, null, Poe2ProcessCandidateState.ReadFailed);
}
