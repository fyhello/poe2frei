using System.Drawing;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using FreiAtlas.Platform.Windows.Overlay.Native;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace FreiAtlas.Platform.Windows.Overlay;

public enum OverlayWindowInteraction
{
    Passthrough,
    Selective
}

public readonly record struct LayeredOverlayMessage(
    uint Message,
    nuint WParam,
    nint LParam);

public sealed class LayeredOverlayWindow : IDisposable
{
    private const string WindowClassName = "FreiAtlasNameOverlay";
    private static readonly OverlayNative.WndProc WindowProcedureDelegate = WindowProcedure;
    private static readonly ConcurrentDictionary<nint, LayeredOverlayWindow> Windows = [];

    private readonly object _windowGate = new();
    private readonly OverlayWindowInteraction _interaction;
    private nint _hwnd;
    private nint _instance;
    private ID2D1Factory? _d2dFactory;
    private IDWriteFactory? _dwriteFactory;
    private ID2D1DCRenderTarget? _renderTarget;
    private nint _memoryDc;
    private nint _dibSection;
    private nint _previousBitmap;
    private int _width;
    private int _height;
    private int _originX;
    private int _originY;
    private bool _visible;
    private bool _hideRequested;

    private LayeredOverlayWindow(OverlayWindowInteraction interaction)
    {
        _interaction = interaction;
    }

    public event Action<LayeredOverlayMessage>? MessageReceived;

    public ID2D1RenderTarget RenderTarget =>
        _renderTarget ?? throw new ObjectDisposedException(nameof(LayeredOverlayWindow));

    public IDWriteFactory DWriteFactory =>
        _dwriteFactory ?? throw new ObjectDisposedException(nameof(LayeredOverlayWindow));

    public bool IsValid
    {
        get
        {
            lock (_windowGate)
            {
                return IsValidCore();
            }
        }
    }

    public static LayeredOverlayWindow Create(
        OverlayWindowInteraction interaction = OverlayWindowInteraction.Passthrough)
    {
        var window = new LayeredOverlayWindow(interaction);
        try
        {
            window.Initialize();
            return window;
        }
        catch
        {
            window.Dispose();
            throw;
        }
    }

    public bool PumpMessages()
    {
        while (OverlayNative.PeekMessageW(out var message, 0, 0, 0, OverlayNative.PM_REMOVE))
        {
            if (message.message == OverlayNative.WM_QUIT)
            {
                return false;
            }

            OverlayNative.TranslateMessage(ref message);
            OverlayNative.DispatchMessageW(ref message);
        }

        return true;
    }

    public void ResizeAndMove(Rectangle clientBounds)
    {
        lock (_windowGate)
        {
            if (_hwnd == 0 || _hideRequested)
            {
                return;
            }
        }

        if (clientBounds.Width <= 0 || clientBounds.Height <= 0)
        {
            Hide();
            return;
        }

        if (clientBounds.Width != _width || clientBounds.Height != _height)
        {
            AllocateBackingBitmap(clientBounds.Width, clientBounds.Height);
        }

        lock (_windowGate)
        {
            if (_hwnd == 0 || _hideRequested)
            {
                return;
            }

            _originX = clientBounds.Left;
            _originY = clientBounds.Top;
            if (!_visible)
            {
                OverlayNative.ShowWindow(_hwnd, OverlayNative.SW_SHOW);
                _visible = true;
            }
        }
    }

    public void Present()
    {
        lock (_windowGate)
        {
            if (_hideRequested || !IsValidCore())
            {
                return;
            }

            OverlayNative.GdiFlush();
            var screenDc = OverlayNative.GetDC(0);
            if (screenDc == 0)
            {
                return;
            }

            try
            {
                var destination = new OverlayNative.POINT { X = _originX, Y = _originY };
                var size = new OverlayNative.SIZE { cx = _width, cy = _height };
                var source = new OverlayNative.POINT { X = 0, Y = 0 };
                var blend = new OverlayNative.BLENDFUNCTION
                {
                    BlendOp = OverlayNative.AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = OverlayNative.AC_SRC_ALPHA,
                };

                OverlayNative.UpdateLayeredWindow(
                    _hwnd,
                    screenDc,
                    ref destination,
                    ref size,
                    _memoryDc,
                    ref source,
                    0,
                    ref blend,
                    OverlayNative.ULW_ALPHA);
            }
            finally
            {
                OverlayNative.ReleaseDC(0, screenDc);
            }
        }
    }

    public void Hide()
    {
        lock (_windowGate)
        {
            if (_hwnd == 0 || !_visible)
            {
                return;
            }

            OverlayNative.ShowWindow(_hwnd, OverlayNative.SW_HIDE);
            _visible = false;
        }
    }

    public void RequestHide()
    {
        lock (_windowGate)
        {
            _hideRequested = true;
            if (_hwnd == 0)
            {
                return;
            }

            OverlayNative.ShowWindowAsync(_hwnd, OverlayNative.SW_HIDE);
        }
    }

    public void CaptureMouse()
    {
        lock (_windowGate)
        {
            if (_hwnd != 0)
            {
                OverlayNative.SetCapture(_hwnd);
            }
        }
    }

    public void ReleaseMouseCapture()
        => OverlayNative.ReleaseCapture();

    public void Dispose()
    {
        _renderTarget?.Dispose();
        _renderTarget = null;
        _dwriteFactory?.Dispose();
        _dwriteFactory = null;
        _d2dFactory?.Dispose();
        _d2dFactory = null;

        FreeBackingBitmap();

        lock (_windowGate)
        {
            _hideRequested = true;
            if (_hwnd != 0)
            {
                Windows.TryRemove(_hwnd, out _);
                OverlayNative.DestroyWindow(_hwnd);
                _hwnd = 0;
            }

            _visible = false;
        }
    }

    private void Initialize()
    {
        _instance = OverlayNative.GetModuleHandleW(null);
        RegisterWindowClass();
        CreateWindow();

        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory>(Vortice.Direct2D1.FactoryType.SingleThreaded);
        _dwriteFactory = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);

        var properties = new RenderTargetProperties(
            RenderTargetType.Default,
            new PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            96f,
            96f,
            RenderTargetUsage.None,
            FeatureLevel.Default);

        _renderTarget = _d2dFactory.CreateDCRenderTarget(properties);
        AllocateBackingBitmap(1, 1);
    }

    private unsafe void RegisterWindowClass()
    {
        fixed (char* className = WindowClassName)
        {
            var windowClass = new OverlayNative.WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<OverlayNative.WNDCLASSEXW>(),
                style = OverlayNative.CS_HREDRAW | OverlayNative.CS_VREDRAW,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WindowProcedureDelegate),
                hInstance = _instance,
                lpszClassName = (nint)className,
            };

            OverlayNative.RegisterClassExW(&windowClass);
        }
    }

    private void CreateWindow()
    {
        var extendedStyle = BuildExtendedStyle(_interaction);

        _hwnd = OverlayNative.CreateWindowExW(
            extendedStyle,
            WindowClassName,
            WindowClassName,
            OverlayNative.WS_POPUP | OverlayNative.WS_VISIBLE,
            0,
            0,
            1,
            1,
            0,
            0,
            _instance,
            0);

        if (_hwnd == 0)
        {
            throw new InvalidOperationException("CreateWindowExW failed.");
        }

        Windows[_hwnd] = this;
        OverlayNative.ShowWindow(_hwnd, OverlayNative.SW_SHOW);
        _visible = true;
    }

    private void AllocateBackingBitmap(int width, int height)
    {
        FreeBackingBitmap();

        var screenDc = OverlayNative.GetDC(0);
        if (screenDc == 0)
        {
            throw new InvalidOperationException("GetDC failed.");
        }

        try
        {
            _memoryDc = OverlayNative.CreateCompatibleDC(screenDc);
            if (_memoryDc == 0)
            {
                throw new InvalidOperationException("CreateCompatibleDC failed.");
            }

            var bitmapInfo = new OverlayNative.BITMAPINFO
            {
                bmiHeader = new OverlayNative.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<OverlayNative.BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = OverlayNative.BI_RGB,
                },
            };

            _dibSection = OverlayNative.CreateDIBSection(
                _memoryDc,
                ref bitmapInfo,
                OverlayNative.DIB_RGB_COLORS,
                out _,
                0,
                0);

            if (_dibSection == 0)
            {
                throw new InvalidOperationException("CreateDIBSection failed.");
            }

            _previousBitmap = OverlayNative.SelectObject(_memoryDc, _dibSection);
            if (_previousBitmap == 0)
            {
                throw new InvalidOperationException("SelectObject failed.");
            }

            _renderTarget!.BindDC(_memoryDc, new Vortice.RawRect(0, 0, width, height));
            _width = width;
            _height = height;
        }
        catch
        {
            FreeBackingBitmap();
            throw;
        }
        finally
        {
            OverlayNative.ReleaseDC(0, screenDc);
        }
    }

    private void FreeBackingBitmap()
    {
        if (_memoryDc != 0 && _previousBitmap != 0)
        {
            OverlayNative.SelectObject(_memoryDc, _previousBitmap);
            _previousBitmap = 0;
        }

        if (_dibSection != 0)
        {
            OverlayNative.DeleteObject(_dibSection);
            _dibSection = 0;
        }

        if (_memoryDc != 0)
        {
            OverlayNative.DeleteDC(_memoryDc);
            _memoryDc = 0;
        }

        _width = 0;
        _height = 0;
    }

    private bool IsValidCore()
        => _hwnd != 0
           && _renderTarget is not null
           && _memoryDc != 0
           && _dibSection != 0;

    internal static uint BuildExtendedStyle(OverlayWindowInteraction interaction)
    {
        var style = OverlayNative.WS_EX_TOPMOST
                    | OverlayNative.WS_EX_LAYERED
                    | OverlayNative.WS_EX_NOACTIVATE
                    | OverlayNative.WS_EX_TOOLWINDOW;
        return interaction == OverlayWindowInteraction.Passthrough
            ? style | OverlayNative.WS_EX_TRANSPARENT
            : style;
    }

    internal static nint HitTest(
        OverlayWindowInteraction interaction,
        int screenX,
        int screenY,
        int originX,
        int originY,
        int width,
        int height)
    {
        if (interaction == OverlayWindowInteraction.Passthrough)
        {
            return OverlayNative.HTTRANSPARENT;
        }

        return screenX >= originX
               && screenY >= originY
               && screenX < originX + width
               && screenY < originY + height
            ? OverlayNative.HTCLIENT
            : OverlayNative.HTTRANSPARENT;
    }

    internal static nint MouseActivateResult(uint message)
        => message == OverlayNative.WM_MOUSEACTIVATE
            ? OverlayNative.MA_NOACTIVATE
            : 0;

    private nint HandleWindowMessage(uint message, nuint wParam, nint lParam)
    {
        var activationResult = MouseActivateResult(message);
        if (activationResult != 0)
        {
            return activationResult;
        }

        if (message == OverlayNative.WM_NCHITTEST)
        {
            var value = lParam.ToInt64();
            var screenX = (short)(value & 0xFFFF);
            var screenY = (short)((value >> 16) & 0xFFFF);
            return HitTest(
                _interaction,
                screenX,
                screenY,
                _originX,
                _originY,
                _width,
                _height);
        }

        if (message is OverlayNative.WM_MOUSEMOVE
            or OverlayNative.WM_LBUTTONDOWN
            or OverlayNative.WM_LBUTTONUP
            or OverlayNative.WM_MOUSEWHEEL)
        {
            MessageReceived?.Invoke(new LayeredOverlayMessage(message, wParam, lParam));
        }

        return OverlayNative.DefWindowProcW(_hwnd, message, wParam, lParam);
    }

    private static nint WindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (Windows.TryGetValue(hwnd, out var window))
        {
            return window.HandleWindowMessage(message, wParam, lParam);
        }

        if (message == OverlayNative.WM_DESTROY)
        {
            OverlayNative.PostQuitMessage(0);
            return 0;
        }

        return OverlayNative.DefWindowProcW(hwnd, message, wParam, lParam);
    }
}
