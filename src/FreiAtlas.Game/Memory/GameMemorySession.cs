using FreiAtlas.Core.Area;
using FreiAtlas.Core.Memory;

namespace FreiAtlas.Game.Memory;

internal sealed class GameMemorySession
{
    private readonly IProcessMemory _memory;
    private readonly Poe2MemoryProfile _profile;
    private readonly GameStateLocator _locator;
    private nint _lastAreaInstance;
    private uint _lastAreaHash;

    public GameMemorySession(
        IProcessMemory memory,
        Poe2MemoryProfile profile,
        GameStateLocator locator)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
    }

    public int ProcessId => _memory.ProcessId;
    public long SessionSequence { get; private set; }

    public event Action<long>? SessionChanged;

    public bool TryRefresh(
        out GameRootState root,
        out AreaIdentity area,
        out IReadOnlyList<AreaReadDiagnostic> diagnostics)
    {
        area = default!;
        if (!_locator.TryResolve(_memory, out root, out var rootDiagnostics))
        {
            diagnostics = rootDiagnostics;
            return false;
        }

        var messages = rootDiagnostics.ToList();
        diagnostics = messages;
        var reader = new GameMemoryReader(_memory, _profile);
        if (!reader.TryReadUInt32(
                root.AreaInstance + _profile.AreaInstance.AreaHashOffset,
                out var areaHash)
            || !_memory.TryReadInt32(
                root.AreaInstance + _profile.AreaInstance.AreaLevelOffset,
                out var areaLevel)
            || areaLevel <= 0
            || !reader.TryReadPointer(
                root.AreaInstance + _profile.AreaInstance.AreaInfoOffset,
                out var areaInfo)
            || !reader.TryReadPointer(areaInfo, out var areaCodeAddress)
            || !_memory.TryReadUtf16(
                areaCodeAddress,
                64,
                out var areaCode)
            || string.IsNullOrWhiteSpace(areaCode))
        {
            messages.Add(new AreaReadDiagnostic(
                "area-identity-unavailable",
                "The resolved AreaInstance did not contain a valid area identity.",
                AreaDiagnosticSeverity.Error));
            return false;
        }

        var changed = _lastAreaInstance != root.AreaInstance
                      || _lastAreaHash != areaHash;
        if (changed)
        {
            _lastAreaInstance = root.AreaInstance;
            _lastAreaHash = areaHash;
            SessionSequence++;
        }

        area = new AreaIdentity(
            areaHash,
            areaCode,
            areaLevel,
            SessionSequence);
        if (changed)
        {
            SessionChanged?.Invoke(SessionSequence);
        }

        return true;
    }
}
