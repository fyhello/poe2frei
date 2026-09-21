using System.Drawing;
using System.Numerics;
using FreiAtlas.Core.Area;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace FreiAtlas.Platform.Windows.Overlay;

public enum AreaProjectionProbeVisualKind
{
    Boss,
    Expedition
}

public sealed record AreaProjectionProbeVisualMark(
    Vector2 Center,
    string Label,
    AreaProjectionProbeVisualKind Kind);

public sealed record AreaProjectionProbeFrameVisual(
    Rectangle ClientBounds,
    string ProbeFrameId,
    AreaUiRect Viewport,
    bool DrawViewportGuide,
    IReadOnlyList<AreaProjectionProbeVisualMark> Marks);

public interface IAreaProjectionProbeSurface : IDisposable
{
    bool PumpMessages();

    void Render(AreaProjectionProbeFrameVisual frame);

    void Hide();

    void RequestHide();
}

internal readonly record struct AreaProjectionProbeLine(Vector2 Start, Vector2 End);

public sealed class AreaProjectionProbeSurface : IAreaProjectionProbeSurface
{
    private const float CrossInnerGap = 3f;
    private const float CrossRadius = 9f;
    private const float StrokeWidth = 1f;

    private readonly LayeredOverlayWindow _window;
    private ID2D1SolidColorBrush? _viewportBrush;
    private ID2D1SolidColorBrush? _bossBrush;
    private ID2D1SolidColorBrush? _expeditionBrush;
    private ID2D1SolidColorBrush? _textBrush;
    private IDWriteTextFormat? _textFormat;
    private bool _disposed;

    private AreaProjectionProbeSurface(LayeredOverlayWindow window)
    {
        _window = window;
        try
        {
            _viewportBrush = window.RenderTarget.CreateSolidColorBrush(
                new Color4(0.1f, 0.9f, 1f, 0.92f));
            _bossBrush = window.RenderTarget.CreateSolidColorBrush(
                new Color4(1f, 0.12f, 0.45f, 0.98f));
            _expeditionBrush = window.RenderTarget.CreateSolidColorBrush(
                new Color4(0.45f, 1f, 0.2f, 0.98f));
            _textBrush = window.RenderTarget.CreateSolidColorBrush(
                new Color4(1f, 1f, 1f, 0.98f));
            _textFormat = window.DWriteFactory.CreateTextFormat(
                "Microsoft YaHei UI",
                null,
                FontWeight.Normal,
                FontStyle.Normal,
                FontStretch.Normal,
                11f,
                "zh-CN");
            _textFormat.TextAlignment = TextAlignment.Leading;
            _textFormat.ParagraphAlignment = ParagraphAlignment.Center;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static AreaProjectionProbeSurface Create()
        => new(LayeredOverlayWindow.Create());

    public bool PumpMessages()
    {
        ThrowIfDisposed();
        return _window.PumpMessages();
    }

    public void Render(AreaProjectionProbeFrameVisual frame)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(frame.Marks);
        if (frame.ClientBounds.Width <= 0 || frame.ClientBounds.Height <= 0)
        {
            _window.Hide();
            return;
        }

        _window.ResizeAndMove(frame.ClientBounds);
        var renderTarget = _window.RenderTarget;
        renderTarget.BeginDraw();
        renderTarget.Clear(new Color4(0f, 0f, 0f, 0f));
        renderTarget.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;

        if (frame.DrawViewportGuide
            && BuildViewportBounds(frame.Viewport) is { } viewport)
        {
            renderTarget.DrawRectangle(ToRawRect(viewport), _viewportBrush!, StrokeWidth);
            var center = new Vector2(
                viewport.Left + viewport.Width / 2f,
                viewport.Top + viewport.Height / 2f);
            DrawCross(renderTarget, center, _viewportBrush!);
        }

        foreach (var mark in frame.Marks)
        {
            var brush = mark.Kind == AreaProjectionProbeVisualKind.Boss
                ? _bossBrush!
                : _expeditionBrush!;
            DrawCross(renderTarget, mark.Center, brush);
            DrawLabel(renderTarget, mark.Label, BuildLabelBounds(mark.Center), brush);
        }

        if (!string.IsNullOrWhiteSpace(frame.ProbeFrameId)
            && BuildViewportBounds(frame.Viewport) is { } idViewport)
        {
            var idBounds = new RectangleF(
                idViewport.Left + 4f,
                idViewport.Top + 4f,
                96f,
                18f);
            DrawLabel(renderTarget, $"#{frame.ProbeFrameId}", idBounds, _textBrush!);
        }

        renderTarget.EndDraw();
        _window.Present();
    }

    public void Hide()
    {
        ThrowIfDisposed();
        _window.Hide();
    }

    public void RequestHide() => _window.RequestHide();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _textFormat?.Dispose();
        _textFormat = null;
        _textBrush?.Dispose();
        _textBrush = null;
        _expeditionBrush?.Dispose();
        _expeditionBrush = null;
        _bossBrush?.Dispose();
        _bossBrush = null;
        _viewportBrush?.Dispose();
        _viewportBrush = null;
        _window.Dispose();
    }

    internal static IReadOnlyList<AreaProjectionProbeLine> BuildCrossSegments(
        Vector2 center,
        float innerGap,
        float radius)
    {
        if (!float.IsFinite(center.X)
            || !float.IsFinite(center.Y)
            || !float.IsFinite(innerGap)
            || !float.IsFinite(radius)
            || innerGap < 0f
            || radius <= innerGap)
        {
            return Array.Empty<AreaProjectionProbeLine>();
        }

        return
        [
            new(new Vector2(center.X - radius, center.Y), new Vector2(center.X - innerGap, center.Y)),
            new(new Vector2(center.X + innerGap, center.Y), new Vector2(center.X + radius, center.Y)),
            new(new Vector2(center.X, center.Y - radius), new Vector2(center.X, center.Y - innerGap)),
            new(new Vector2(center.X, center.Y + innerGap), new Vector2(center.X, center.Y + radius))
        ];
    }

    internal static RectangleF? BuildViewportBounds(AreaUiRect viewport)
        => float.IsFinite(viewport.X)
           && float.IsFinite(viewport.Y)
           && float.IsFinite(viewport.Width)
           && float.IsFinite(viewport.Height)
           && viewport.Width > 0f
           && viewport.Height > 0f
            ? new RectangleF(viewport.X, viewport.Y, viewport.Width, viewport.Height)
            : null;

    internal static RectangleF BuildLabelBounds(Vector2 center)
        => new(center.X + 11f, center.Y - 9f, 64f, 18f);

    private static void DrawCross(
        ID2D1RenderTarget renderTarget,
        Vector2 center,
        ID2D1SolidColorBrush brush)
    {
        foreach (var line in BuildCrossSegments(center, CrossInnerGap, CrossRadius))
        {
            renderTarget.DrawLine(line.Start, line.End, brush, StrokeWidth);
        }
    }

    private void DrawLabel(
        ID2D1RenderTarget renderTarget,
        string text,
        RectangleF bounds,
        ID2D1SolidColorBrush brush)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        renderTarget.DrawText(
            text,
            _textFormat!,
            new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            brush,
            DrawTextOptions.Clip);
    }

    private static Vortice.RawRectF ToRawRect(RectangleF bounds)
        => new(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
