using System.Drawing;
using FreiAtlas.Plugin.ExpeditionPanel;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace FreiAtlas.Platform.Windows.Overlay;

public sealed class ExpeditionNativeValueSurface : IExpeditionNativeValueSurface
{
    private readonly LayeredOverlayWindow _window;
    private readonly Dictionary<ExpeditionPanelValueBand, ID2D1SolidColorBrush>
        _brushes = [];
    private IDWriteTextFormat? _textFormat;
    private float _textFormatSize;
    private bool _disposed;

    private ExpeditionNativeValueSurface(LayeredOverlayWindow window)
    {
        _window = window;
        try
        {
            foreach (var band in Enum.GetValues<ExpeditionPanelValueBand>())
            {
                _brushes[band] = _window.RenderTarget.CreateSolidColorBrush(
                    ToColor4(ValueArgb(band)));
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal static OverlayWindowInteraction InteractionMode
        => OverlayWindowInteraction.Passthrough;

    public static ExpeditionNativeValueSurface Create()
        => new(LayeredOverlayWindow.Create(InteractionMode));

    public bool PumpMessages()
    {
        ThrowIfDisposed();
        return _window.PumpMessages();
    }

    public void Render(
        Rectangle gameClientBounds,
        ExpeditionNativeValueScene scene)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(scene);
        if (gameClientBounds.Width <= 0 || gameClientBounds.Height <= 0)
        {
            _window.Hide();
            return;
        }

        _window.ResizeAndMove(gameClientBounds);
        var renderTarget = _window.RenderTarget;
        renderTarget.BeginDraw();
        renderTarget.Clear(new Color4(0f, 0f, 0f, 0f));
        renderTarget.TextAntialiasMode =
            Vortice.Direct2D1.TextAntialiasMode.Grayscale;

        var clientSize = gameClientBounds.Size;
        var fontSize = ExpeditionNativeValueLayoutCalculator.UniformFontSize(
            scene.Rows.Select(row => row.Bounds));
        foreach (var row in scene.Rows)
        {
            if (string.IsNullOrEmpty(row.ValueText))
            {
                continue;
            }

            var format = EnsureTextFormat(fontSize);
            var measuredWidth = MeasureTextWidth(
                _window.DWriteFactory,
                format,
                row.ValueText,
                fontSize);
            var layout = ExpeditionNativeValueLayoutCalculator.Create(
                row.Bounds,
                scene.ListClipBounds,
                clientSize,
                measuredWidth,
                fontSize);
            if (layout is not { } valueLayout)
            {
                continue;
            }

            var clip = valueLayout.ClipBounds;
            renderTarget.PushAxisAlignedClip(
                new Vortice.RawRectF(
                    clip.Left,
                    clip.Top,
                    clip.Right,
                    clip.Bottom),
                AntialiasMode.Aliased);
            var textBounds = valueLayout.TextBounds;
            renderTarget.DrawText(
                row.ValueText,
                format,
                new Rect(
                    textBounds.Left,
                    textBounds.Top,
                    textBounds.Width,
                    textBounds.Height),
                GetBrush(row.ValueBand),
                DrawTextOptions.Clip);
            renderTarget.PopAxisAlignedClip();
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
        foreach (var brush in _brushes.Values)
        {
            brush.Dispose();
        }
        _brushes.Clear();
        _window.Dispose();
    }

    internal static uint ValueArgb(ExpeditionPanelValueBand band)
        => ExpeditionValuePresentation.GetArgb(band);

    internal static void ConfigureTextFormat(IDWriteTextFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        format.TextAlignment = TextAlignment.Trailing;
        format.ParagraphAlignment = ParagraphAlignment.Center;
        format.WordWrapping = WordWrapping.NoWrap;
    }

    internal static float MeasureTextWidth(
        IDWriteFactory factory,
        IDWriteTextFormat format,
        string text,
        float fontSize)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(text);
        var maximumWidth = MathF.Max(
            fontSize,
            fontSize * Math.Max(1, text.Length) * 2f);
        using var layout = factory.CreateTextLayout(
            text,
            format,
            maximumWidth,
            fontSize * 3f);
        return layout.Metrics.WidthIncludingTrailingWhitespace;
    }

    private IDWriteTextFormat EnsureTextFormat(float fontSize)
    {
        if (_textFormat is not null
            && Math.Abs(_textFormatSize - fontSize) < 0.01f)
        {
            return _textFormat;
        }

        var replacement = _window.DWriteFactory.CreateTextFormat(
            "Microsoft YaHei UI",
            null,
            FontWeight.SemiBold,
            FontStyle.Normal,
            FontStretch.Normal,
            fontSize,
            "zh-CN");
        ConfigureTextFormat(replacement);
        _textFormat?.Dispose();
        _textFormat = replacement;
        _textFormatSize = fontSize;
        return replacement;
    }

    private ID2D1SolidColorBrush GetBrush(ExpeditionPanelValueBand band)
        => _brushes.TryGetValue(band, out var brush)
            ? brush
            : _brushes[ExpeditionPanelValueBand.Unknown];

    private static Color4 ToColor4(uint argb)
        => new(
            ((argb >> 16) & 0xff) / 255f,
            ((argb >> 8) & 0xff) / 255f,
            (argb & 0xff) / 255f,
            ((argb >> 24) & 0xff) / 255f);

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);
}
