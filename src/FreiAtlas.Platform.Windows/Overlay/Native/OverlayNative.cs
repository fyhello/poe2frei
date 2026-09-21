using System.Runtime.InteropServices;

namespace FreiAtlas.Platform.Windows.Overlay.Native;

internal static class OverlayNative
{
    internal const uint WS_POPUP = 0x80000000;
    internal const uint WS_VISIBLE = 0x10000000;

    internal const uint WS_EX_TOPMOST = 0x00000008;
    internal const uint WS_EX_TRANSPARENT = 0x00000020;
    internal const uint WS_EX_LAYERED = 0x00080000;
    internal const uint WS_EX_NOACTIVATE = 0x08000000;
    internal const uint WS_EX_TOOLWINDOW = 0x00000080;

    internal const int SW_SHOW = 5;
    internal const int SW_HIDE = 0;

    internal const uint PM_REMOVE = 0x0001;
    internal const uint WM_DESTROY = 0x0002;
    internal const uint WM_MOUSEACTIVATE = 0x0021;
    internal const uint WM_NCHITTEST = 0x0084;
    internal const uint WM_MOUSEMOVE = 0x0200;
    internal const uint WM_LBUTTONDOWN = 0x0201;
    internal const uint WM_LBUTTONUP = 0x0202;
    internal const uint WM_MOUSEWHEEL = 0x020A;
    internal const uint WM_QUIT = 0x0012;

    internal const nint HTTRANSPARENT = -1;
    internal const nint HTCLIENT = 1;
    internal const nint MA_NOACTIVATE = 3;

    internal const nuint MK_LBUTTON = 0x0001;
    internal const nuint MK_CONTROL = 0x0008;

    internal const int VK_LBUTTON = 0x01;
    internal const int VK_CONTROL = 0x11;

    internal const uint CS_HREDRAW = 0x0002;
    internal const uint CS_VREDRAW = 0x0001;

    internal const byte AC_SRC_OVER = 0x00;
    internal const byte AC_SRC_ALPHA = 0x01;
    internal const uint ULW_ALPHA = 0x00000002;
    internal const uint BI_RGB = 0;
    internal const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEXW
    {
        internal uint cbSize;
        internal uint style;
        internal nint lpfnWndProc;
        internal int cbClsExtra;
        internal int cbWndExtra;
        internal nint hInstance;
        internal nint hIcon;
        internal nint hCursor;
        internal nint hbrBackground;
        internal nint lpszMenuName;
        internal nint lpszClassName;
        internal nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        internal nint hwnd;
        internal uint message;
        internal nuint wParam;
        internal nint lParam;
        internal uint time;
        internal POINT pt;
        internal uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE
    {
        internal int cx;
        internal int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BLENDFUNCTION
    {
        internal byte BlendOp;
        internal byte BlendFlags;
        internal byte SourceConstantAlpha;
        internal byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        internal uint biSize;
        internal int biWidth;
        internal int biHeight;
        internal ushort biPlanes;
        internal ushort biBitCount;
        internal uint biCompression;
        internal uint biSizeImage;
        internal int biXPelsPerMeter;
        internal int biYPelsPerMeter;
        internal uint biClrUsed;
        internal uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFO
    {
        internal BITMAPINFOHEADER bmiHeader;
        internal uint bmiColorsPad;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint WndProc(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW")]
    internal static extern unsafe ushort RegisterClassExW(WNDCLASSEXW* windowClass);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateWindowExW(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parentWindow,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", EntryPoint = "DestroyWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    internal static extern nint DefWindowProcW(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll", EntryPoint = "ShowWindowAsync")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindowAsync(nint hwnd, int command);

    [DllImport("user32.dll", EntryPoint = "SetCapture")]
    internal static extern nint SetCapture(nint hwnd);

    [DllImport("user32.dll", EntryPoint = "ReleaseCapture")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")]
    internal static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PeekMessageW(out MSG message, nint hwnd, uint filterMin, uint filterMax, uint removeMessage);

    [DllImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    internal static extern nint DispatchMessageW(ref MSG message);

    [DllImport("user32.dll", EntryPoint = "PostQuitMessage")]
    internal static extern void PostQuitMessage(int exitCode);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    internal static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", EntryPoint = "GetDC")]
    internal static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll", EntryPoint = "ReleaseDC")]
    internal static extern int ReleaseDC(nint hwnd, nint deviceContext);

    [DllImport("gdi32.dll", EntryPoint = "CreateCompatibleDC")]
    internal static extern nint CreateCompatibleDC(nint deviceContext);

    [DllImport("gdi32.dll", EntryPoint = "DeleteDC")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(nint deviceContext);

    [DllImport("gdi32.dll", EntryPoint = "SelectObject")]
    internal static extern nint SelectObject(nint deviceContext, nint gdiObject);

    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(nint gdiObject);

    [DllImport("gdi32.dll", EntryPoint = "GdiFlush")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GdiFlush();

    [DllImport("gdi32.dll", EntryPoint = "CreateDIBSection")]
    internal static extern nint CreateDIBSection(
        nint deviceContext,
        ref BITMAPINFO bitmapInfo,
        uint usage,
        out nint bits,
        nint section,
        uint offset);

    [DllImport("user32.dll", EntryPoint = "UpdateLayeredWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateLayeredWindow(
        nint hwnd,
        nint destinationDeviceContext,
        ref POINT destinationPosition,
        ref SIZE size,
        nint sourceDeviceContext,
        ref POINT sourcePosition,
        uint colorKey,
        ref BLENDFUNCTION blend,
        uint flags);
}
