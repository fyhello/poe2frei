using System.Drawing;
using FreiAtlas.Platform.Windows.Native;

namespace FreiAtlas.Platform.Windows.Windows;

public sealed record GameWindowSnapshot(
    nint Handle,
    int ProcessId,
    Rectangle Bounds,
    bool IsForeground,
    bool IsMinimized)
{
    public Rectangle ClientBounds { get; init; } = Bounds;
}

public sealed record WindowInfo(
    nint Handle,
    int ProcessId,
    Rectangle Bounds,
    bool IsForeground,
    bool IsMinimized,
    bool IsVisible = true)
{
    public Rectangle ClientBounds { get; init; } = Bounds;
}

public interface IWindowInspector
{
    IReadOnlyList<WindowInfo> EnumerateTopLevelWindows();
}

public sealed class GameWindowTracker
{
    private readonly IWindowInspector _inspector;

    public GameWindowTracker()
        : this(new NativeWindowInspector())
    {
    }

    public GameWindowTracker(IWindowInspector inspector)
    {
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
    }

    public GameWindowSnapshot? Track(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        var window = _inspector
            .EnumerateTopLevelWindows()
            .Where(item => item.ProcessId == processId && item.IsVisible)
            .OrderByDescending(item => item.IsForeground)
            .ThenBy(item => item.IsMinimized)
            .ThenByDescending(item => item.Bounds.Width * (long)item.Bounds.Height)
            .FirstOrDefault();

        return window is null
            ? null
            : new GameWindowSnapshot(
                window.Handle,
                window.ProcessId,
                window.Bounds,
                window.IsForeground,
                window.IsMinimized)
            {
                ClientBounds = window.ClientBounds
            };
    }
}

internal sealed class NativeWindowInspector : IWindowInspector
{
    public IReadOnlyList<WindowInfo> EnumerateTopLevelWindows()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var windows = new List<WindowInfo>();

        NativeMethods.EnumWindows((windowHandle, _) =>
        {
            if (!NativeMethods.IsWindowVisible(windowHandle)
                || !NativeMethods.GetWindowRect(windowHandle, out var nativeRect))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
            if (processId == 0)
            {
                return true;
            }

            var bounds = Rectangle.FromLTRB(
                nativeRect.Left,
                nativeRect.Top,
                nativeRect.Right,
                nativeRect.Bottom);
            var clientBounds = ReadClientBounds(windowHandle, bounds);

            windows.Add(new WindowInfo(
                windowHandle,
                checked((int)processId),
                bounds,
                windowHandle == foreground,
                NativeMethods.IsIconic(windowHandle))
            {
                ClientBounds = clientBounds
            });
            return true;
        }, 0);

        return windows;
    }

    private static Rectangle ReadClientBounds(
        nint windowHandle,
        Rectangle fallback)
    {
        if (!NativeMethods.GetClientRect(windowHandle, out var clientRect))
        {
            return fallback;
        }

        var origin = new NativeMethods.NativePoint
        {
            X = 0,
            Y = 0
        };
        if (!NativeMethods.ClientToScreen(windowHandle, ref origin))
        {
            return fallback;
        }

        var width = clientRect.Right - clientRect.Left;
        var height = clientRect.Bottom - clientRect.Top;
        return width > 0 && height > 0
            ? new Rectangle(origin.X, origin.Y, width, height)
            : fallback;
    }
}
