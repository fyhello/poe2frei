namespace FreiAtlas.App.Processes;

public sealed class ProcessSelectionController
{
    private bool _firstScanApplied;
    private List<GameProcessSnapshot> _processes = [];

    public IReadOnlyList<GameProcessSnapshot> Processes => _processes;

    public int? SelectedProcessId { get; private set; }

    public bool IsOverlayRunning { get; private set; }

    public void ApplyScan(IEnumerable<GameProcessSnapshot> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);

        var current = processes
            .Where(process => process.State != GameProcessState.Exited)
            .GroupBy(process => process.ProcessId)
            .Select(group => group.Last())
            .OrderBy(process => process.ProcessId)
            .ToList();

        if (!_firstScanApplied)
        {
            _firstScanApplied = true;
            if (current.Count == 1)
            {
                SelectedProcessId = current[0].ProcessId;
            }
        }

        if (SelectedProcessId is { } selected
            && current.All(process => process.ProcessId != selected))
        {
            IsOverlayRunning = false;
            current.Add(new GameProcessSnapshot(
                selected,
                null,
                GameProcessState.Exited,
                "进程已退出"));
            current.Sort((left, right) => left.ProcessId.CompareTo(right.ProcessId));
        }

        _processes = current;
    }

    public bool TrySelect(int processId)
    {
        if (IsOverlayRunning && SelectedProcessId != processId)
        {
            return false;
        }

        if (_processes.All(process =>
                process.ProcessId != processId
                || process.State == GameProcessState.Exited))
        {
            return false;
        }

        SelectedProcessId = processId;
        return true;
    }

    public void MarkOverlayStarted()
    {
        if (SelectedProcessId is not { } selected
            || _processes.All(process =>
                process.ProcessId != selected
                || process.State == GameProcessState.Exited))
        {
            throw new InvalidOperationException(
                "An overlay cannot start without a current selected process.");
        }

        IsOverlayRunning = true;
    }

    public void MarkOverlayStopped() => IsOverlayRunning = false;
}
