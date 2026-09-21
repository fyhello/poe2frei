using System.Runtime.InteropServices;

namespace FreiAtlas.Platform.Windows.Windows;

public static class DpiAwareness
{
    private static readonly nint PerMonitorV2Context = new(-4);

    public static bool TryEnablePerMonitorV2()
    {
        return OperatingSystem.IsWindows()
            && SetProcessDpiAwarenessContext(PerMonitorV2Context);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(
        nint dpiAwarenessContext);
}
