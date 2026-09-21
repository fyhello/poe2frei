namespace FreiAtlas.Host;

public sealed class OverlayStopController
{
    private readonly object _gate = new();
    private readonly HashSet<Action> _requestHideCallbacks = [];
    private bool _stopRequested;

    public void Register(Action requestHide)
    {
        ArgumentNullException.ThrowIfNull(requestHide);
        bool invokeImmediately;
        lock (_gate)
        {
            var added = _requestHideCallbacks.Add(requestHide);
            invokeImmediately = added && _stopRequested;
        }

        if (invokeImmediately)
        {
            requestHide();
        }
    }

    public void Unregister(Action requestHide)
    {
        ArgumentNullException.ThrowIfNull(requestHide);
        lock (_gate)
        {
            _requestHideCallbacks.Remove(requestHide);
        }
    }

    public void RequestStop()
    {
        Action[] requestHideCallbacks;
        lock (_gate)
        {
            if (_stopRequested)
            {
                return;
            }

            _stopRequested = true;
            requestHideCallbacks = [.. _requestHideCallbacks];
        }

        foreach (var requestHide in requestHideCallbacks)
        {
            requestHide();
        }
    }
}
