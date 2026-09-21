using System.Drawing;
using System.Runtime.InteropServices;

namespace FreiAtlas.Platform.Windows.Windows;

public static class CursorPositionReader
{
    public static bool TryGetScreenPosition(out Point position)
    {
        if (GetCursorPos(out var nativePoint))
        {
            position = new Point(nativePoint.X, nativePoint.Y);
            return true;
        }

        position = default;
        return false;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }
}
