using FreiAtlas.Core.Recovery;

namespace FreiAtlas.Core.Contracts;

public enum RecoveryInputState { Sent, Paused, Failed }

public sealed record RecoveryInputResult(RecoveryInputState State, string Message);

public interface IRecoveryInput
{
    bool IsTargetForeground(int processId);
    RecoveryInputResult Send(int processId, RecoveryKey key, DateTimeOffset capturedAt);
}
