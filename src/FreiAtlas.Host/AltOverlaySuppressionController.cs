using FreiAtlas.Core.Settings;

namespace FreiAtlas.Host;

internal sealed class AltOverlaySuppressionController
{
    private AltOverlayMode? _previousMode;
    private bool _previousAltDown;
    private bool _toggleCycleStarted;
    private bool _toggleSuppressed;

    public bool ShouldSuppress(
        AltOverlayMode mode,
        bool isAltDown,
        bool isGameForeground)
    {
        if (_previousMode != mode)
        {
            _previousMode = mode;
            _previousAltDown = isAltDown;
            _toggleCycleStarted = false;
            _toggleSuppressed = false;
            return mode == AltOverlayMode.HoldToHide && isAltDown;
        }

        if (mode == AltOverlayMode.HoldToHide)
        {
            _previousAltDown = isAltDown;
            return isAltDown;
        }

        if (!_previousAltDown && isAltDown)
        {
            _toggleCycleStarted = true;
        }
        else if (_previousAltDown && !isAltDown)
        {
            if (_toggleCycleStarted && isGameForeground)
            {
                _toggleSuppressed = !_toggleSuppressed;
            }

            _toggleCycleStarted = false;
        }

        _previousAltDown = isAltDown;
        return _toggleSuppressed;
    }
}
