using System.Drawing;
using System.Numerics;
using FreiAtlas.Core.Atlas;
using FreiAtlas.Core.Settings;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace FreiAtlas.Platform.Windows.Overlay;

public sealed class AtlasNameOverlaySurface : IAtlasOverlaySurface
{
    private const float ContentIconSize = 20f;
    private const float ContentIconOffsetY = -15f;
    private const float ContentIconSpacing = 22f;

    private readonly LayeredOverlayWindow _window;
    private AtlasIconCache? _atlasIconCache;
    private ID2D1SolidColorBrush? _textBrush;
    private ID2D1SolidColorBrush? _panelBrush;
    private ID2D1SolidColorBrush? _navigationPanelBrush;
    private ID2D1SolidColorBrush? _redEdgeBrush;
    private ID2D1SolidColorBrush? _greenEdgeBrush;
    private ID2D1SolidColorBrush? _grayEdgeBrush;
    private ID2D1SolidColorBrush? _highlightedEdgeBrush;
    private ID2D1SolidColorBrush? _bossIconBrush;
    private ID2D1SolidColorBrush? _expeditionIconBrush;
    private ID2D1SolidColorBrush? _iconOutlineBrush;
    private IDWriteTextFormat? _textFormat;
    private AtlasOverlayStyle? _style;
    private bool _disposed;

    private AtlasNameOverlaySurface(LayeredOverlayWindow window)
    {
        _window = window;

        try
        {
            _atlasIconCache = new AtlasIconCache();
            _grayEdgeBrush = _window.RenderTarget.CreateSolidColorBrush(
                new Color4(0.62f, 0.65f, 0.7f, 0.75f));
            _bossIconBrush = _window.RenderTarget.CreateSolidColorBrush(
                new Color4(0.89f, 0.22f, 0.28f, 0.98f));
            _expeditionIconBrush = _window.RenderTarget.CreateSolidColorBrush(
                new Color4(0.84f, 0.70f, 0.28f, 0.98f));
            _iconOutlineBrush = _window.RenderTarget.CreateSolidColorBrush(
                new Color4(0.05f, 0.07f, 0.09f, 0.95f));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static AtlasNameOverlaySurface Create()
        => new(LayeredOverlayWindow.Create());

    public bool PumpMessages()
    {
        ThrowIfDisposed();
        return _window.PumpMessages();
    }

    public void Render(
        Rectangle clientBounds,
        IReadOnlyList<AtlasEdgePlacement> edges,
        IReadOnlyList<AtlasEdgePlacement> navigationRouteEdges,
        IReadOnlyList<AtlasLabelPlacement> labels,
        IReadOnlyList<AtlasContentIconPlacement> contentIcons,
        IReadOnlyList<AtlasDirectionPlacement> directions,
        AtlasDisplaySettings settings)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(navigationRouteEdges);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(contentIcons);
        ArgumentNullException.ThrowIfNull(directions);
        ArgumentNullException.ThrowIfNull(settings);

        if (clientBounds.Width <= 0 || clientBounds.Height <= 0)
        {
            _window.Hide();
            return;
        }

        _window.ResizeAndMove(clientBounds);
        var style = EnsureStyleResources(settings);

        var renderTarget = _window.RenderTarget;
        renderTarget.BeginDraw();
        renderTarget.Clear(new Color4(0f, 0f, 0f, 0f));
        renderTarget.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;

        foreach (var edge in edges)
        {
            var start = ApplyEdgeAnchorOffset(edge.Start);
            var end = ApplyEdgeAnchorOffset(edge.End);
            renderTarget.DrawLine(
                start,
                end,
                GetEdgeBrush(edge.Color),
                style.EdgeWidth);
        }

        foreach (var edge in edges.Where(edge => edge.IsHighlighted))
        {
            renderTarget.DrawLine(
                ApplyEdgeAnchorOffset(edge.Start),
                ApplyEdgeAnchorOffset(edge.End),
                _highlightedEdgeBrush!,
                style.HighlightWidth);
        }

        foreach (var edge in navigationRouteEdges)
        {
            renderTarget.DrawLine(
                ApplyEdgeAnchorOffset(edge.Start),
                ApplyEdgeAnchorOffset(edge.End),
                _highlightedEdgeBrush!,
                style.HighlightWidth);
        }

        foreach (var label in labels)
        {
            var bounds = CalculateLabelPanel(label, style.FontSize);
            var panel = new Vortice.RawRectF(
                bounds.Left,
                bounds.Top,
                bounds.Right,
                bounds.Bottom);

            if (label.IsNavigationHighlighted)
            {
                renderTarget.FillRectangle(panel, _navigationPanelBrush!);
                renderTarget.DrawRectangle(
                    panel,
                    _highlightedEdgeBrush!,
                    style.HighlightWidth);
            }
            else if (style.ShowLabelBackground)
            {
                renderTarget.FillRectangle(panel, _panelBrush!);
            }
            renderTarget.DrawText(
                label.Text,
                _textFormat!,
                CreateTextLayoutRect(panel),
                _textBrush!,
                DrawTextOptions.Clip);
        }

        DrawContentIcons(renderTarget, contentIcons);
        DrawDirections(renderTarget, directions, style);

        renderTarget.EndDraw();
        _window.Present();
    }

    private void DrawDirections(
        ID2D1RenderTarget renderTarget,
        IReadOnlyList<AtlasDirectionPlacement> directions,
        AtlasOverlayStyle style)
    {
        foreach (var direction in directions)
        {
            var arrow = CalculateDirectionArrow(
                direction.ArrowTip,
                direction.UnitDirection);
            renderTarget.DrawLine(
                arrow.Tip,
                arrow.Left,
                _highlightedEdgeBrush!,
                style.HighlightWidth);
            renderTarget.DrawLine(
                arrow.Tip,
                arrow.Right,
                _highlightedEdgeBrush!,
                style.HighlightWidth);
            renderTarget.DrawLine(
                arrow.Left,
                arrow.Right,
                _highlightedEdgeBrush!,
                style.HighlightWidth);

            var panel = new Vortice.RawRectF(
                direction.LabelBounds.Left,
                direction.LabelBounds.Top,
                direction.LabelBounds.Right,
                direction.LabelBounds.Bottom);
            renderTarget.FillRectangle(panel, _navigationPanelBrush!);
            renderTarget.DrawRectangle(
                panel,
                _highlightedEdgeBrush!,
                style.HighlightWidth);
            renderTarget.DrawText(
                direction.Text,
                _textFormat!,
                CreateTextLayoutRect(panel),
                _textBrush!,
                DrawTextOptions.Clip);
        }
    }

    private void DrawContentIcons(
        ID2D1RenderTarget renderTarget,
        IReadOnlyList<AtlasContentIconPlacement> contentIcons)
    {
        foreach (var group in contentIcons.GroupBy(icon => icon.Grid))
        {
            var icons = group.ToArray();
            var anchor = ApplyLabelAnchorOffset(icons[0].Anchor);
            var centers = CalculateContentIconCenters(anchor, icons.Length);
            for (var index = 0; index < icons.Length; index++)
            {
                var contentIcon = icons[index];
                var bitmap = _atlasIconCache?.Get(
                    renderTarget,
                    contentIcon.ReferenceIconId);
                if (bitmap is not null)
                {
                    renderTarget.DrawBitmap(
                        bitmap,
                        CalculateContentIconBounds(centers[index]),
                        1f,
                        BitmapInterpolationMode.Linear,
                        (Vortice.RawRectF?)null);
                    continue;
                }

                var ellipse = new Ellipse(centers[index], 5f, 5f);
                var brush = contentIcon.ContentId.Equals(
                    "expedition",
                    StringComparison.OrdinalIgnoreCase)
                    ? _expeditionIconBrush!
                    : _bossIconBrush!;

                renderTarget.FillEllipse(ellipse, brush);
                renderTarget.DrawEllipse(ellipse, _iconOutlineBrush!, 1.25f);
            }
        }
    }

    internal static float CalculateLabelWidth(string text)
        => AtlasLabelPanelLayout.GetWidth(text);

    internal static float CalculateLabelTop(float adjustedAnchorY)
        => AtlasLabelPanelLayout.GetTop(adjustedAnchorY);

    internal static RectangleF CalculateLabelPanel(AtlasLabelPlacement label)
        => AtlasLabelPanelLayout.GetPanelBounds(label);

    internal static RectangleF CalculateLabelPanel(
        AtlasLabelPlacement label,
        float fontSize)
        => AtlasLabelPanelLayout.GetPanelBounds(label, fontSize);

    internal static IReadOnlyList<Vector2> CalculateContentIconCenters(
        Vector2 adjustedAnchor,
        int count)
    {
        if (count <= 0)
        {
            return Array.Empty<Vector2>();
        }

        var firstX = adjustedAnchor.X - ((count - 1) * ContentIconSpacing / 2f);
        return Enumerable.Range(0, count)
            .Select(index => new Vector2(
                firstX + (index * ContentIconSpacing),
                adjustedAnchor.Y + ContentIconOffsetY))
            .ToArray();
    }

    internal static Vortice.RawRectF CalculateContentIconBounds(Vector2 center)
    {
        var halfSize = ContentIconSize / 2f;
        return new Vortice.RawRectF(
            center.X - halfSize,
            center.Y - halfSize,
            center.X + halfSize,
            center.Y + halfSize);
    }

    internal static Rect CreateTextLayoutRect(Vortice.RawRectF panel)
        => new(
            panel.Left,
            panel.Top,
            panel.Right - panel.Left,
            panel.Bottom - panel.Top);

    internal static Vector2 ApplyLabelAnchorOffset(Vector2 anchor)
        => AtlasLabelPanelLayout.AdjustAnchor(anchor);

    internal static Vector2 ApplyEdgeAnchorOffset(Vector2 endpoint)
        => ApplyLabelAnchorOffset(endpoint);

    internal static (Vector2 Tip, Vector2 Left, Vector2 Right)
        CalculateDirectionArrow(
            Vector2 tip,
            Vector2 unitDirection,
            float length = 18f,
            float halfWidth = 7f)
    {
        var direction = unitDirection.LengthSquared() > 0f
            ? Vector2.Normalize(unitDirection)
            : Vector2.UnitX;
        var baseCenter = tip - (direction * length);
        var perpendicular = new Vector2(-direction.Y, direction.X) * halfWidth;
        return (tip, baseCenter + perpendicular, baseCenter - perpendicular);
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
        _panelBrush?.Dispose();
        _panelBrush = null;
        _navigationPanelBrush?.Dispose();
        _navigationPanelBrush = null;
        _redEdgeBrush?.Dispose();
        _redEdgeBrush = null;
        _greenEdgeBrush?.Dispose();
        _greenEdgeBrush = null;
        _grayEdgeBrush?.Dispose();
        _grayEdgeBrush = null;
        _highlightedEdgeBrush?.Dispose();
        _highlightedEdgeBrush = null;
        _bossIconBrush?.Dispose();
        _bossIconBrush = null;
        _expeditionIconBrush?.Dispose();
        _expeditionIconBrush = null;
        _iconOutlineBrush?.Dispose();
        _iconOutlineBrush = null;
        _atlasIconCache?.Dispose();
        _atlasIconCache = null;
        _textBrush?.Dispose();
        _textBrush = null;
        _textFormat?.Dispose();
        _textFormat = null;
        _window.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private ID2D1SolidColorBrush GetEdgeBrush(AtlasEdgeColor color)
        => color switch
        {
            AtlasEdgeColor.Red => _redEdgeBrush!,
            AtlasEdgeColor.Green => _greenEdgeBrush!,
            _ => _grayEdgeBrush!
        };

    private AtlasOverlayStyle EnsureStyleResources(AtlasDisplaySettings settings)
    {
        var next = AtlasOverlayStyleResolver.Resolve(settings);
        var changes = AtlasOverlayStyleResolver.GetResourceChanges(_style, next);

        if (changes.HasFlag(AtlasOverlayResourceChanges.EdgeBrushes))
        {
            ReplaceBrush(ref _greenEdgeBrush, next.ReachableEdgeColor);
            ReplaceBrush(ref _redEdgeBrush, next.LockedEdgeColor);
        }

        if (changes.HasFlag(AtlasOverlayResourceChanges.HighlightBrush))
        {
            ReplaceBrush(ref _highlightedEdgeBrush, next.HighlightColor);
        }

        if (changes.HasFlag(AtlasOverlayResourceChanges.TextBrush))
        {
            ReplaceBrush(ref _textBrush, next.TextColor);
        }

        if (changes.HasFlag(AtlasOverlayResourceChanges.PanelBrush))
        {
            ReplaceBrush(ref _panelBrush, next.PanelColor);
            ReplaceBrush(
                ref _navigationPanelBrush,
                next.NavigationPanelColor);
        }

        if (changes.HasFlag(AtlasOverlayResourceChanges.TextFormat))
        {
            var replacement = _window.DWriteFactory.CreateTextFormat(
                "Microsoft YaHei UI",
                null,
                FontWeight.Normal,
                FontStyle.Normal,
                FontStretch.Normal,
                next.FontSize,
                "zh-CN");
            replacement.TextAlignment = TextAlignment.Center;
            replacement.ParagraphAlignment = ParagraphAlignment.Center;
            _textFormat?.Dispose();
            _textFormat = replacement;
        }

        _style = next;
        return next;
    }

    private void ReplaceBrush(
        ref ID2D1SolidColorBrush? brush,
        Color4 color)
    {
        var replacement = _window.RenderTarget.CreateSolidColorBrush(color);
        brush?.Dispose();
        brush = replacement;
    }
}
