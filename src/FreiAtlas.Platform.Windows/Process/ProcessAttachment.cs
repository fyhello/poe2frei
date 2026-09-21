namespace FreiAtlas.Platform.Windows.Process;

public sealed class ProcessAttachment : IDisposable
{
    private NativeProcessMemory? _memory;

    private ProcessAttachment(NativeProcessMemory memory)
    {
        _memory = memory;
    }

    public NativeProcessMemory Memory =>
        _memory ?? throw new ObjectDisposedException(nameof(ProcessAttachment));

    public static bool TryAttach(int processId, out ProcessAttachment? attachment)
    {
        if (!NativeProcessMemory.TryOpen(processId, out var memory)
            || memory is null)
        {
            attachment = null;
            return false;
        }

        attachment = new ProcessAttachment(memory);
        return true;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _memory, null)?.Dispose();
    }
}
