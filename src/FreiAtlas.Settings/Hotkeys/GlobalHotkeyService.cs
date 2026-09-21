using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using FreiAtlas.App.Settings;
using FreiAtlas.Core.Recovery;

namespace FreiAtlas.Settings.Hotkeys;

internal interface IGlobalHotkeyNative
{
    bool RegisterHotKey(
        nint window,
        int id,
        uint modifiers,
        uint virtualKey);

    bool UnregisterHotKey(nint window, int id);
}

internal sealed class GlobalHotkeyRegistration : IDisposable
{
    private const int FirstRegistrationId = 0x4A01;
    private const int SecondRegistrationId = 0x4A02;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;

    private readonly nint _window;
    private readonly IGlobalHotkeyNative _native;
    private readonly int _firstRegistrationId;
    private readonly int _secondRegistrationId;
    private bool _disposed;

    public GlobalHotkeyRegistration(
        nint window,
        IGlobalHotkeyNative native)
        : this(window, native, FirstRegistrationId, SecondRegistrationId)
    {
    }

    public GlobalHotkeyRegistration(
        nint window,
        IGlobalHotkeyNative native,
        int firstRegistrationId,
        int secondRegistrationId)
    {
        if (window == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }

        _window = window;
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _firstRegistrationId = firstRegistrationId;
        _secondRegistrationId = secondRegistrationId;
    }

    public int ActiveId { get; private set; }

    public AtlasHotkey? Current { get; private set; }

    public bool TryRegister(AtlasHotkey hotkey, out string? error)
    {
        ArgumentNullException.ThrowIfNull(hotkey);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (hotkey == Current && ActiveId != 0)
        {
            error = null;
            return true;
        }

        var candidateId = ActiveId == _firstRegistrationId
            ? _secondRegistrationId
            : _firstRegistrationId;
        var modifiers = ToNativeModifiers(hotkey.Modifiers) | ModNoRepeat;
        var virtualKey = ToVirtualKey(hotkey.Key);
        if (!_native.RegisterHotKey(
                _window,
                candidateId,
                modifiers,
                virtualKey))
        {
            error = "快捷键已被其他程序占用";
            return false;
        }

        var previousId = ActiveId;
        ActiveId = candidateId;
        Current = hotkey;
        if (previousId != 0)
        {
            _native.UnregisterHotKey(_window, previousId);
        }

        error = null;
        return true;
    }

    public bool HandlesMessage(int registrationId)
        => !_disposed
           && ActiveId != 0
           && ActiveId == registrationId;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (ActiveId != 0)
        {
            _native.UnregisterHotKey(_window, ActiveId);
            ActiveId = 0;
        }

        Current = null;
    }

    internal void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ActiveId != 0)
        {
            _native.UnregisterHotKey(_window, ActiveId);
            ActiveId = 0;
        }

        Current = null;
    }

    internal bool TryRegister(RecoveryKey hotkey, out string? error)
    {
        ArgumentNullException.ThrowIfNull(hotkey);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Current is not null
            && Current.Gesture.Equals(hotkey.Gesture, StringComparison.OrdinalIgnoreCase)
            && ActiveId != 0)
        {
            error = null;
            return true;
        }

        var candidateId = ActiveId == _firstRegistrationId
            ? _secondRegistrationId
            : _firstRegistrationId;
        var modifiers = ToNativeModifiers(hotkey.Modifiers) | ModNoRepeat;
        if (!_native.RegisterHotKey(_window, candidateId, modifiers, hotkey.VirtualKey))
        {
            error = "快捷键已被其他程序占用";
            return false;
        }

        var previousId = ActiveId;
        ActiveId = candidateId;
        Current = AtlasHotkey.TryParse(hotkey.Gesture, out var parsed)
            ? parsed
            : null;
        if (previousId != 0)
        {
            _native.UnregisterHotKey(_window, previousId);
        }

        error = null;
        return true;
    }

    private static uint ToNativeModifiers(AtlasHotkeyModifiers modifiers)
    {
        var native = 0u;
        if ((modifiers & AtlasHotkeyModifiers.Control) != 0)
        {
            native |= ModControl;
        }

        if ((modifiers & AtlasHotkeyModifiers.Alt) != 0)
        {
            native |= ModAlt;
        }

        if ((modifiers & AtlasHotkeyModifiers.Shift) != 0)
        {
            native |= ModShift;
        }

        return native;
    }

    private static uint ToNativeModifiers(IReadOnlyList<ushort> modifiers)
    {
        var native = 0u;
        foreach (var modifier in modifiers)
        {
            native |= modifier switch
            {
                0x11 => ModControl,
                0x12 => ModAlt,
                0x10 => ModShift,
                _ => 0u
            };
        }

        return native;
    }

    private static uint ToVirtualKey(string key)
    {
        if (key.Length == 1)
        {
            return key[0];
        }

        if (key[0] == 'F'
            && int.TryParse(key[1..], out var number)
            && number is >= 1 and <= 12)
        {
            return (uint)(0x70 + number - 1);
        }

        throw new ArgumentException("The hotkey key is invalid.", nameof(key));
    }
}

internal sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;

    private readonly HwndSource _source;
    private readonly GlobalHotkeyRegistration _overlayRegistration;
    private readonly GlobalHotkeyRegistration _portalSqueezeRegistration;
    private bool _disposed;

    public GlobalHotkeyService(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle)
            ?? throw new InvalidOperationException(
                "The settings window message source is unavailable.");
        _overlayRegistration = new GlobalHotkeyRegistration(
            handle,
            User32GlobalHotkeyNative.Instance);
        _portalSqueezeRegistration = new GlobalHotkeyRegistration(
            handle,
            User32GlobalHotkeyNative.Instance,
            0x4A03,
            0x4A04);
        _source.AddHook(WindowProcedure);
    }

    public event Action? Pressed;

    public event Action? PortalSqueezePressed;

    public bool TryRegister(AtlasHotkey hotkey, out string? error)
        => _overlayRegistration.TryRegister(hotkey, out error);

    public bool TryRegisterPortalSqueeze(RecoveryKey hotkey, out string? error)
        => _portalSqueezeRegistration.TryRegister(hotkey, out error);

    public void ClearPortalSqueeze() => _portalSqueezeRegistration.Clear();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.RemoveHook(WindowProcedure);
        _overlayRegistration.Dispose();
        _portalSqueezeRegistration.Dispose();
    }

    private nint WindowProcedure(
        nint hwnd,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message != WmHotkey)
        {
            return 0;
        }

        var registrationId = unchecked((int)wParam);
        if (_overlayRegistration.HandlesMessage(registrationId))
        {
            handled = true;
            Pressed?.Invoke();
        }
        else if (_portalSqueezeRegistration.HandlesMessage(registrationId))
        {
            handled = true;
            PortalSqueezePressed?.Invoke();
        }

        return 0;
    }
}

internal sealed class User32GlobalHotkeyNative : IGlobalHotkeyNative
{
    public static User32GlobalHotkeyNative Instance { get; } = new();

    public bool RegisterHotKey(
        nint window,
        int id,
        uint modifiers,
        uint virtualKey)
        => RegisterHotKeyNative(window, id, modifiers, virtualKey);

    public bool UnregisterHotKey(nint window, int id)
        => UnregisterHotKeyNative(window, id);

    [DllImport("user32.dll", EntryPoint = "RegisterHotKey", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKeyNative(
        nint window,
        int id,
        uint modifiers,
        uint virtualKey);

    [DllImport("user32.dll", EntryPoint = "UnregisterHotKey", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKeyNative(nint window, int id);
}
