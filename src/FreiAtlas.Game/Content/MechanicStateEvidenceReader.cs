using FreiAtlas.Core.Area;
using FreiAtlas.Core.Memory;
using FreiAtlas.Game.Entities;

namespace FreiAtlas.Game.Content;

internal sealed record MechanicStateEvidenceResult(
    bool Matched,
    IReadOnlyList<AreaContentEvidence> Evidence);

internal sealed class MechanicStateEvidenceReader
{
    internal const string AbyssMetadata =
        "Metadata/MiscellaneousObjects/Abyss/AbyssFinalNodeBase";
    internal const string BreachMetadata =
        "Metadata/MiscellaneousObjects/Brequel/BrequelInitiator";
    internal const string EssenceMetadata =
        "Metadata/MiscellaneousObjects/Monolith";
    internal const string RitualMetadata =
        "Metadata/Terrain/Leagues/Ritual/RitualRuneObject";
    internal const string StateMachineSource = "StateMachine";
    internal const string StateKey = "State";
    internal const string MinimapIconSource = "MinimapIcon";
    internal const string CompleteKey = "IsComplete";

    private readonly IProcessMemory _memory;
    private readonly EntityComponentResolver _components;
    private readonly Poe2MemoryProfile _profile;

    public MechanicStateEvidenceReader(
        IProcessMemory memory,
        EntityComponentResolver components,
        Poe2MemoryProfile? profile = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _components = components ?? throw new ArgumentNullException(nameof(components));
        _profile = profile ?? Poe2MemoryProfile.Current;
    }

    public MechanicStateEvidenceResult Read(
        RawEntityRef rawEntity,
        AreaEntitySnapshot entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (IsMetadata(entity.MetadataPath, AbyssMetadata)
            || IsMetadata(entity.MetadataPath, EssenceMetadata))
        {
            return new MechanicStateEvidenceResult(
                true,
                [new AreaContentEvidence(
                    MinimapIconSource,
                    CompleteKey,
                    entity.IsMinimapIconComplete ? "true" : "false",
                    0.95f)]);
        }

        if (IsMetadata(entity.MetadataPath, RitualMetadata))
        {
            if (!_components.TryResolve(
                    rawEntity,
                    StateMachineSource,
                    out var ritualStateMachine)
                || ritualStateMachine == 0)
            {
                return new MechanicStateEvidenceResult(
                    true,
                    [new AreaContentEvidence(
                        StateMachineSource,
                        "Resolved",
                        "false",
                        0.2f)]);
            }

            if (!_memory.TryReadInt32(
                    ritualStateMachine + _profile.StateMachine.StateOffset,
                    out var ritualState))
            {
                return new MechanicStateEvidenceResult(
                    true,
                    [new AreaContentEvidence(
                        StateMachineSource,
                        "StateRead",
                        "false",
                        0.2f)]);
            }

            return new MechanicStateEvidenceResult(
                true,
                [new AreaContentEvidence(
                    StateMachineSource,
                    StateKey,
                    $"0x{ritualState:X8}",
                    0.95f)]);
        }

        if (!IsMetadata(entity.MetadataPath, BreachMetadata))
        {
            return new MechanicStateEvidenceResult(false, []);
        }

        if (!_components.TryResolve(
                rawEntity,
                StateMachineSource,
                out var stateMachine)
            || stateMachine == 0)
        {
            return new MechanicStateEvidenceResult(
                true,
                [new AreaContentEvidence(
                    StateMachineSource,
                    "Resolved",
                    "false",
                    0.2f)]);
        }

        if (!_memory.TryReadInt32(
                stateMachine + _profile.StateMachine.StateOffset,
                out var state))
        {
            return new MechanicStateEvidenceResult(
                true,
                [new AreaContentEvidence(
                    StateMachineSource,
                    "StateRead",
                    "false",
                    0.2f)]);
        }

        return new MechanicStateEvidenceResult(
            true,
            [new AreaContentEvidence(
                StateMachineSource,
                StateKey,
                $"0x{state:X8}",
                0.95f)]);
    }

    private static bool IsMetadata(string candidate, string expected)
        => string.Equals(
            candidate,
            expected,
            StringComparison.OrdinalIgnoreCase);
}
