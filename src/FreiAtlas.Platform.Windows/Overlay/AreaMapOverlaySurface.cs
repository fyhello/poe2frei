using System.Collections.Immutable;
using System.Drawing;
using System.Globalization;
using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Settings;
using FreiAtlas.Plugin.AreaMap;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace FreiAtlas.Platform.Windows.Overlay;

internal enum AreaMapOverlayGlyph
{
    Boss,
    Expedition,
    Abyss,
    Ritual,
    Breach,
    Essence,
    Incursion,
    Strongbox,
    OmenAltar,
    RareMonster,
    RareChest,
    UniqueChest,
    Pollen
}

internal enum AreaMapOverlayRenderMode
{
    Bitmap,
    ExpeditionTag,
    Vector
}

internal sealed record AreaMapOverlayVisualStyle(
    string ColorHex,
    string? Label,
    AtlasIconVariant IconVariant,
    float Opacity,
    bool DrawCompletionCheck);

internal sealed record AreaMapOverlayDrawCommand(
    string InstanceId,
    AreaContentKind Kind,
    AreaMapOverlayMarkerKind MarkerKind,
    Vector2 Center,
    float Radius,
    AreaMapOverlayGlyph Glyph,
    AreaMapOverlayRenderMode RenderMode,
    string? ReferenceIconId,
    string ColorHex,
    AtlasIconVariant IconVariant,
    float Opacity,
    bool DrawCompletionCheck,
    string? Label,
    RectangleF? LabelBounds,
    AreaMapExpeditionTagLayout? ExpeditionTag);

public sealed class AreaMapOverlaySurface : IAreaMapOverlaySurface
{
    private const float IconRadiusScale = 0.014f;
    private const float MinimumIconRadius = 7f;
    private const float MaximumIconRadius = 13f;

    // 灵火是高密度点位，用独立的小圆点半径，避免密集区糊成一片。
    private const float PollenDotScale = 0.24f;
    private const float MinimumPollenDotRadius = 1.8f;
    private const float MaximumPollenDotRadius = 3.2f;

    private static readonly IReadOnlyDictionary<AreaMapOverlayVisualState, AreaMapOverlayVisualStyle>
        VisualStyles = new Dictionary<AreaMapOverlayVisualState, AreaMapOverlayVisualStyle>
        {
            [AreaMapOverlayVisualState.BossInactive] = Original("#FF9F43"),
            [AreaMapOverlayVisualState.BossAvailable] = Original("#FF4D5E"),
            [AreaMapOverlayVisualState.BossCompleted] = Original("#8B949E", true),
            [AreaMapOverlayVisualState.ExpeditionAvailable] = Original("#FF4D5E"),
            [AreaMapOverlayVisualState.ExpeditionSelected] = Original("#33E661"),
            [AreaMapOverlayVisualState.ExpeditionCompleted] = Original("#8B949E", true),
            [AreaMapOverlayVisualState.AbyssAvailable] = Original("#31C7C9"),
            [AreaMapOverlayVisualState.AbyssCompleted] = CompletedMechanic(),
            [AreaMapOverlayVisualState.RitualAvailable] = Original("#E14C5A"),
            [AreaMapOverlayVisualState.RitualCompleted] = CompletedMechanic(),
            [AreaMapOverlayVisualState.BreachAvailable] = Original("#A77BF3"),
            [AreaMapOverlayVisualState.BreachCompleted] = CompletedMechanic(),
            [AreaMapOverlayVisualState.EssenceAvailable] = Original("#59D18C"),
            [AreaMapOverlayVisualState.EssenceCompleted] = CompletedMechanic(),
            [AreaMapOverlayVisualState.IncursionAvailable] = Original("#F2D45C"),
            [AreaMapOverlayVisualState.StrongboxAvailable] = Original("#F29A4A"),
            [AreaMapOverlayVisualState.OmenAltarAvailable] = Original("#F2C14E"),
            [AreaMapOverlayVisualState.RareMonster] = Original("#F2D45C"),
            [AreaMapOverlayVisualState.RareChest] = Original("#E8C44F"),
            [AreaMapOverlayVisualState.UniqueChest] = Original("#D9822B"),
            [AreaMapOverlayVisualState.PollenWild] = Original("#E1C14C"),
            [AreaMapOverlayVisualState.PollenSoul] = Original("#B14CE1"),
            [AreaMapOverlayVisualState.PollenPrimal] = Original("#35C9C9"),
            [AreaMapOverlayVisualState.PollenSacred] = Original("#E17C3D"),
            [AreaMapOverlayVisualState.PollenAvailable] = Original("#D8D8D8")
        };

    private readonly LayeredOverlayWindow _window;
    private readonly Dictionary<(string ColorHex, float Opacity), ID2D1SolidColorBrush> _stateBrushes =
        [];
    private readonly Dictionary<string, ID2D1SolidColorBrush> _expeditionBorderBrushes =
        new(StringComparer.Ordinal);
    private AtlasIconCache? _atlasIconCache;
    private ID2D1SolidColorBrush? _completionBrush;
    private ID2D1SolidColorBrush? _expeditionTextBrush;
    private ID2D1SolidColorBrush? _expeditionPanelBrush;
    private IDWriteTextFormat? _expeditionTextFormat;
    private AreaMapExpeditionTagStyle? _expeditionTagStyle;
    private float _expeditionTextFormatSize;
    private bool _disposed;

    private AreaMapOverlaySurface(LayeredOverlayWindow window)
    {
        _window = window;
        try
        {
            _atlasIconCache = new AtlasIconCache();
            foreach (var style in VisualStyles.Values
                         .DistinctBy(style => (style.ColorHex, style.Opacity)))
            {
                _stateBrushes.Add(
                    (style.ColorHex, style.Opacity),
                    _window.RenderTarget.CreateSolidColorBrush(ToColor4(
                        style.ColorHex,
                        style.Opacity == 1f ? 0.98f : style.Opacity)));
            }

            _completionBrush = _window.RenderTarget.CreateSolidColorBrush(
                new Color4(1f, 1f, 1f, 1f));
            _expeditionTextBrush = _window.RenderTarget.CreateSolidColorBrush(
                new Color4(1f, 1f, 1f, 1f));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static AreaMapOverlaySurface Create()
        => new(LayeredOverlayWindow.Create());

    public bool PumpMessages()
    {
        ThrowIfDisposed();
        return _window.PumpMessages();
    }

    public void Render(
        Rectangle clientBounds,
        AreaMapOverlayScene scene,
        AreaMapDisplaySettings settings)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(settings);

        if (clientBounds.Width <= 0 || clientBounds.Height <= 0)
        {
            _window.Hide();
            return;
        }

        _window.ResizeAndMove(clientBounds);
        EnsureExpeditionBrushes(settings.ExpeditionTag);
        var commands = BuildDrawCommands(
            clientBounds,
            scene,
            settings.LargeMapLabelFontSize,
            MeasureExpeditionText);
        var renderTarget = _window.RenderTarget;
        renderTarget.BeginDraw();
        renderTarget.Clear(new Color4(0f, 0f, 0f, 0f));
        renderTarget.TextAntialiasMode =
            Vortice.Direct2D1.TextAntialiasMode.Grayscale;

        foreach (var command in commands)
        {
            switch (command.RenderMode)
            {
                case AreaMapOverlayRenderMode.ExpeditionTag:
                    DrawExpeditionTag(renderTarget, command);
                    break;
                case AreaMapOverlayRenderMode.Bitmap:
                    DrawBitmapOrFallback(renderTarget, command);
                    break;
                case AreaMapOverlayRenderMode.Vector:
                    DrawIcon(renderTarget, command);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(command),
                        command.RenderMode,
                        null);
            }
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
        foreach (var brush in _stateBrushes.Values)
        {
            brush.Dispose();
        }

        _stateBrushes.Clear();
        DisposeExpeditionBrushes();
        _atlasIconCache?.Dispose();
        _atlasIconCache = null;
        _completionBrush?.Dispose();
        _completionBrush = null;
        _expeditionTextBrush?.Dispose();
        _expeditionTextBrush = null;
        _expeditionTextFormat?.Dispose();
        _expeditionTextFormat = null;
        _window.Dispose();
    }

    internal static AreaMapOverlayVisualStyle ResolveVisualStyle(
        AreaMapOverlayVisualState state)
        => VisualStyles.TryGetValue(state, out var style)
            ? style
            : throw new ArgumentOutOfRangeException(nameof(state), state, null);

    private static AreaMapOverlayVisualStyle Original(
        string colorHex,
        bool drawCompletionCheck = false)
        => new(
            colorHex,
            null,
            AtlasIconVariant.Original,
            1f,
            drawCompletionCheck);

    private static AreaMapOverlayVisualStyle CompletedMechanic()
        => new(
            "#8B949E",
            null,
            AtlasIconVariant.Grayscale,
            0.55f,
            false);

    internal static AreaMapOverlayGlyph ResolveGlyph(AreaMapOverlayMarkerKind kind)
        => kind switch
        {
            AreaMapOverlayMarkerKind.Boss => AreaMapOverlayGlyph.Boss,
            AreaMapOverlayMarkerKind.Expedition => AreaMapOverlayGlyph.Expedition,
            AreaMapOverlayMarkerKind.Abyss => AreaMapOverlayGlyph.Abyss,
            AreaMapOverlayMarkerKind.Ritual => AreaMapOverlayGlyph.Ritual,
            AreaMapOverlayMarkerKind.Breach => AreaMapOverlayGlyph.Breach,
            AreaMapOverlayMarkerKind.Essence => AreaMapOverlayGlyph.Essence,
            AreaMapOverlayMarkerKind.Incursion => AreaMapOverlayGlyph.Incursion,
            AreaMapOverlayMarkerKind.Strongbox => AreaMapOverlayGlyph.Strongbox,
            AreaMapOverlayMarkerKind.OmenAltar => AreaMapOverlayGlyph.OmenAltar,
            AreaMapOverlayMarkerKind.RareMonster => AreaMapOverlayGlyph.RareMonster,
            AreaMapOverlayMarkerKind.RareChest => AreaMapOverlayGlyph.RareChest,
            AreaMapOverlayMarkerKind.UniqueChest => AreaMapOverlayGlyph.UniqueChest,
            AreaMapOverlayMarkerKind.Pollen => AreaMapOverlayGlyph.Pollen,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    internal static string? ResolveReferenceIconId(AreaMapOverlayMarkerKind kind)
        => kind switch
        {
            AreaMapOverlayMarkerKind.Boss => "AtlasIconContentMapBoss",
            AreaMapOverlayMarkerKind.Abyss => "AtlasIconContentAbyss",
            AreaMapOverlayMarkerKind.Ritual => "AtlasIconContentRitual",
            AreaMapOverlayMarkerKind.Breach => "AtlasIconContentBreach",
            AreaMapOverlayMarkerKind.Essence => "AtlasIconContentEssence",
            AreaMapOverlayMarkerKind.Incursion => "AtlasIconContentIncursion",
            AreaMapOverlayMarkerKind.Strongbox => "AtlasIconContentStrongBox",
            AreaMapOverlayMarkerKind.Expedition
                or AreaMapOverlayMarkerKind.OmenAltar
                or AreaMapOverlayMarkerKind.RareMonster
                or AreaMapOverlayMarkerKind.RareChest
                or AreaMapOverlayMarkerKind.UniqueChest
                or AreaMapOverlayMarkerKind.Pollen => null,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    internal static AreaMapOverlayRenderMode ResolveRenderMode(
        AreaMapOverlayMarkerKind kind)
        => kind == AreaMapOverlayMarkerKind.Expedition
            ? AreaMapOverlayRenderMode.ExpeditionTag
            : ResolveReferenceIconId(kind) is not null
                ? AreaMapOverlayRenderMode.Bitmap
                : AreaMapOverlayRenderMode.Vector;

    internal static float CalculateIconRadius(float viewportHeight)
        => float.IsFinite(viewportHeight)
            ? Math.Clamp(
                viewportHeight * IconRadiusScale,
                MinimumIconRadius,
                MaximumIconRadius)
            : MinimumIconRadius;

    internal static Vortice.RawRectF CalculateBitmapBounds(
        Vector2 center,
        float radius)
        => new(
            center.X - radius,
            center.Y - radius,
            center.X + radius,
            center.Y + radius);

    internal static ImmutableArray<AreaMapOverlayDrawCommand> BuildDrawCommands(
        Rectangle clientBounds,
        AreaMapOverlayScene scene,
        float configuredFontSize,
        Func<string, float, SizeF> measureText)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(measureText);
        if (clientBounds.Width <= 0
            || clientBounds.Height <= 0
            || !IsValidViewport(scene.Viewport))
        {
            return [];
        }

        var viewportBounds = RectangleF.Intersect(
            new RectangleF(0f, 0f, clientBounds.Width, clientBounds.Height),
            new RectangleF(
                scene.Viewport.X,
                scene.Viewport.Y,
                scene.Viewport.Width,
                scene.Viewport.Height));
        if (viewportBounds.Width <= 2f || viewportBounds.Height <= 2f)
        {
            return [];
        }

        var radius = CalculateIconRadius(scene.Viewport.Height);
        var commands = ImmutableArray.CreateBuilder<AreaMapOverlayDrawCommand>(
            scene.Placements.Length);
        foreach (var placement in scene.Placements)
        {
            if (!IsFinite(placement.Center))
            {
                continue;
            }

            var style = ResolveVisualStyle(placement.VisualState);
            var renderMode = ResolveRenderMode(placement.MarkerKind);
            var referenceIconId = ResolveReferenceIconId(placement.MarkerKind);
            AreaMapExpeditionTagLayout? expeditionTag = null;
            if (renderMode == AreaMapOverlayRenderMode.ExpeditionTag)
            {
                var marker = placement.ExpeditionMarker ?? new AreaMapExpeditionMarker(null);
                var fontSize = AreaMapExpeditionTagLayoutCalculator.ResolveFontSize(
                    scene.ViewKind,
                    configuredFontSize);
                var holeText = AreaMapExpeditionTagLayoutCalculator.FormatHoleCount(marker.HoleCount);
                var valueText = scene.ViewKind == AreaMapViewKind.LargeMap
                    ? marker.ValueText
                    : null;
                expeditionTag = AreaMapExpeditionTagLayoutCalculator.Create(
                    viewportBounds,
                    placement.Center,
                    holeText,
                    valueText,
                    fontSize,
                    measureText(holeText, fontSize),
                    valueText is null ? SizeF.Empty : measureText(valueText, fontSize));
            }

            commands.Add(new AreaMapOverlayDrawCommand(
                placement.InstanceId,
                placement.Kind,
                placement.MarkerKind,
                placement.Center,
                radius,
                ResolveGlyph(placement.MarkerKind),
                renderMode,
                referenceIconId,
                style.ColorHex,
                style.IconVariant,
                style.Opacity,
                style.DrawCompletionCheck,
                null,
                null,
                expeditionTag));
        }

        return commands.ToImmutable();
    }

    private void EnsureExpeditionBrushes(AreaMapExpeditionTagStyle settings)
    {
        if (_expeditionPanelBrush is not null
            && _expeditionTagStyle == settings
            && _expeditionBorderBrushes.Count > 0)
        {
            return;
        }

        DisposeExpeditionBrushes();
        _expeditionPanelBrush = _window.RenderTarget.CreateSolidColorBrush(
            ToColor4(settings.BackgroundColor, settings.BackgroundOpacity));
        foreach (var colorHex in new[]
                 {
                     AreaMapOverlayVisualState.ExpeditionAvailable,
                     AreaMapOverlayVisualState.ExpeditionSelected,
                     AreaMapOverlayVisualState.ExpeditionCompleted
                 }
                 .Select(state => ResolveVisualStyle(state).ColorHex)
                 .Distinct(StringComparer.Ordinal))
        {
            _expeditionBorderBrushes.Add(
                colorHex,
                _window.RenderTarget.CreateSolidColorBrush(
                    ToColor4(colorHex, settings.BorderOpacity)));
        }

        _expeditionTagStyle = settings;
    }

    private void DisposeExpeditionBrushes()
    {
        _expeditionPanelBrush?.Dispose();
        _expeditionPanelBrush = null;
        foreach (var brush in _expeditionBorderBrushes.Values)
        {
            brush.Dispose();
        }

        _expeditionBorderBrushes.Clear();
        _expeditionTagStyle = null;
    }

    private IDWriteTextFormat EnsureExpeditionTextFormat(float fontSize)
    {
        if (_expeditionTextFormat is not null
            && _expeditionTextFormatSize == fontSize)
        {
            return _expeditionTextFormat;
        }

        var replacement = _window.DWriteFactory.CreateTextFormat(
            "Microsoft YaHei UI",
            null,
            FontWeight.DemiBold,
            FontStyle.Normal,
            FontStretch.Normal,
            fontSize,
            "zh-CN");
        replacement.TextAlignment = TextAlignment.Center;
        replacement.ParagraphAlignment = ParagraphAlignment.Center;
        replacement.WordWrapping = WordWrapping.NoWrap;
        _expeditionTextFormat?.Dispose();
        _expeditionTextFormat = replacement;
        _expeditionTextFormatSize = fontSize;
        return replacement;
    }

    private SizeF MeasureExpeditionText(string text, float fontSize)
    {
        var format = EnsureExpeditionTextFormat(fontSize);
        var maximumWidth = MathF.Max(
            fontSize,
            fontSize * Math.Max(1, text.Length) * 2f);
        using var layout = _window.DWriteFactory.CreateTextLayout(
            text,
            format,
            maximumWidth,
            fontSize * 3f);
        return new SizeF(
            layout.Metrics.WidthIncludingTrailingWhitespace,
            layout.Metrics.Height);
    }

    private void DrawBitmapOrFallback(
        ID2D1RenderTarget renderTarget,
        AreaMapOverlayDrawCommand command)
    {
        var bitmap = _atlasIconCache?.Get(
            renderTarget,
            command.ReferenceIconId,
            command.IconVariant);
        if (bitmap is null)
        {
            DrawIcon(renderTarget, command);
            return;
        }

        renderTarget.DrawBitmap(
            bitmap,
            CalculateBitmapBounds(command.Center, command.Radius),
            command.Opacity,
            BitmapInterpolationMode.Linear,
            (Vortice.RawRectF?)null);
        DrawCompletionCheck(renderTarget, command);
    }

    private void DrawExpeditionTag(
        ID2D1RenderTarget renderTarget,
        AreaMapOverlayDrawCommand command)
    {
        var layout = command.ExpeditionTag
            ?? throw new InvalidOperationException("Expedition tag command has no layout.");
        var roundedRectangle = new RoundedRectangle(
            ToRawRect(layout.PanelBounds),
            4f,
            4f);
        renderTarget.PushAxisAlignedClip(
            ToRawRect(layout.ClipBounds),
            AntialiasMode.Aliased);
        try
        {
            renderTarget.FillRoundedRectangle(
                roundedRectangle,
                _expeditionPanelBrush!);
            renderTarget.DrawRoundedRectangle(
                roundedRectangle,
                _expeditionBorderBrushes[command.ColorHex],
                2f);
            DrawExpeditionText(
                renderTarget,
                layout.HoleText,
                layout.HoleBounds,
                layout.FontSize);
            if (layout.ValueText is { } valueText
                && layout.ValueBounds is { } valueBounds)
            {
                DrawExpeditionText(
                    renderTarget,
                    valueText,
                    valueBounds,
                    layout.FontSize);
            }
        }
        finally
        {
            renderTarget.PopAxisAlignedClip();
        }
    }

    private void DrawExpeditionText(
        ID2D1RenderTarget renderTarget,
        string text,
        RectangleF bounds,
        float fontSize)
        => renderTarget.DrawText(
            text,
            EnsureExpeditionTextFormat(fontSize),
            new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height),
            _expeditionTextBrush!,
            DrawTextOptions.Clip);

    private void DrawIcon(
        ID2D1RenderTarget renderTarget,
        AreaMapOverlayDrawCommand command)
    {
        var brush = _stateBrushes[(command.ColorHex, command.Opacity)];
        switch (command.Glyph)
        {
            case AreaMapOverlayGlyph.Boss:
                DrawDiamond(renderTarget, brush, command.Center, command.Radius);
                break;
            case AreaMapOverlayGlyph.Expedition:
                DrawExpedition(renderTarget, brush, command.Center, command.Radius);
                break;
            case AreaMapOverlayGlyph.Abyss:
                DrawAbyss(renderTarget, brush, command.Center, command.Radius);
                break;
            case AreaMapOverlayGlyph.Ritual:
                DrawRitual(renderTarget, brush, command.Center, command.Radius);
                break;
            case AreaMapOverlayGlyph.Breach:
                DrawBreach(renderTarget, brush, command.Center, command.Radius);
                break;
            case AreaMapOverlayGlyph.Essence:
                DrawEssence(renderTarget, brush, command.Center, command.Radius);
                break;
            case AreaMapOverlayGlyph.Incursion:
                DrawIncursion(renderTarget, brush, command.Center, command.Radius);
                break;
            case AreaMapOverlayGlyph.Strongbox:
                DrawChest(renderTarget, brush, command.Center, command.Radius, false, false);
                break;
            case AreaMapOverlayGlyph.OmenAltar:
                DrawDiamond(renderTarget, brush, command.Center, command.Radius);
                renderTarget.DrawEllipse(
                    new Ellipse(command.Center, command.Radius * 0.38f, command.Radius * 0.38f),
                    brush,
                    1.8f);
                break;
            case AreaMapOverlayGlyph.RareMonster:
                DrawRareMonster(renderTarget, brush, command.Center, command.Radius);
                break;
            case AreaMapOverlayGlyph.RareChest:
                DrawChest(renderTarget, brush, command.Center, command.Radius, true, false);
                break;
            case AreaMapOverlayGlyph.UniqueChest:
                DrawChest(renderTarget, brush, command.Center, command.Radius, false, true);
                break;
            case AreaMapOverlayGlyph.Pollen:
                DrawPollen(renderTarget, brush, command.Center, command.Radius);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Glyph, null);
        }

        DrawCompletionCheck(renderTarget, command);
    }

    private void DrawCompletionCheck(
        ID2D1RenderTarget renderTarget,
        AreaMapOverlayDrawCommand command)
    {
        if (!command.DrawCompletionCheck)
        {
            return;
        }

        var start = command.Center + new Vector2(-command.Radius * 0.45f, 0f);
        var middle = command.Center + new Vector2(-command.Radius * 0.08f, command.Radius * 0.38f);
        var end = command.Center + new Vector2(command.Radius * 0.55f, -command.Radius * 0.42f);
        renderTarget.DrawLine(start, middle, _completionBrush!, 2f);
        renderTarget.DrawLine(middle, end, _completionBrush!, 2f);
    }

    private static void DrawDiamond(
        ID2D1RenderTarget target,
        ID2D1Brush brush,
        Vector2 center,
        float radius)
        => DrawClosedPolyline(
            target,
            brush,
            2.6f,
            center + new Vector2(0f, -radius),
            center + new Vector2(radius, 0f),
            center + new Vector2(0f, radius),
            center + new Vector2(-radius, 0f));

    private static void DrawExpedition(
        ID2D1RenderTarget target,
        ID2D1Brush brush,
        Vector2 center,
        float radius)
    {
        var top = center + new Vector2(0f, -radius * 0.75f);
        var left = center + new Vector2(-radius * 0.75f, radius * 0.65f);
        var right = center + new Vector2(radius * 0.75f, radius * 0.65f);
        target.DrawLine(top, left, brush, 2.2f);
        target.DrawLine(top, right, brush, 2.2f);
        target.DrawLine(left, right, brush, 2.2f);
        var dotRadius = radius * 0.24f;
        target.FillEllipse(new Ellipse(top, dotRadius, dotRadius), brush);
        target.FillEllipse(new Ellipse(left, dotRadius, dotRadius), brush);
        target.FillEllipse(new Ellipse(right, dotRadius, dotRadius), brush);
    }

    private static void DrawAbyss(
        ID2D1RenderTarget target,
        ID2D1Brush brush,
        Vector2 center,
        float radius)
    {
        target.DrawEllipse(new Ellipse(center, radius * 0.82f, radius * 0.82f), brush, 2.4f);
        target.DrawEllipse(new Ellipse(center, radius * 0.38f, radius * 0.38f), brush, 2f);
        target.DrawLine(
            center + new Vector2(-radius * 0.75f, radius * 0.55f),
            center + new Vector2(radius * 0.15f, -radius * 0.2f),
            brush,
            2.2f);
    }

    private static void DrawRitual(
        ID2D1RenderTarget target,
        ID2D1Brush brush,
        Vector2 center,
        float radius)
    {
        target.DrawEllipse(new Ellipse(center, radius * 0.86f, radius * 0.86f), brush, 2.4f);
        var dotRadius = radius * 0.18f;
        foreach (var offset in new[]
                 {
                     new Vector2(0f, -radius * 0.42f),
                     new Vector2(-radius * 0.38f, radius * 0.3f),
                     new Vector2(radius * 0.38f, radius * 0.3f)
                 })
        {
            target.FillEllipse(new Ellipse(center + offset, dotRadius, dotRadius), brush);
        }
    }

    private static void DrawPollen(
        ID2D1RenderTarget target,
        ID2D1Brush brush,
        Vector2 center,
        float radius)
    {
        var dotRadius = Math.Clamp(
            radius * PollenDotScale,
            MinimumPollenDotRadius,
            MaximumPollenDotRadius);
        target.FillEllipse(new Ellipse(center, dotRadius, dotRadius), brush);
    }

    private static void DrawBreach(
        ID2D1RenderTarget target,
        ID2D1Brush brush,
        Vector2 center,
        float radius)
    {
        target.DrawLine(
            center + new Vector2(-radius * 0.85f, -radius * 0.75f),
            center + new Vector2(-radius * 0.25f, 0f),
            brush,
            2.6f);
        target.DrawLine(
            center + new Vector2(-radius * 0.25f, 0f),
            center + new Vector2(-radius * 0.85f, radius * 0.75f),
            brush,
            2.6f);
        target.DrawLine(
            center + new Vector2(radius * 0.85f, -radius * 0.75f),
            center + new Vector2(radius * 0.25f, 0f),
            brush,
            2.6f);
        target.DrawLine(
            center + new Vector2(radius * 0.25f, 0f),
            center + new Vector2(radius * 0.85f, radius * 0.75f),
            brush,
            2.6f);
    }

    private static void DrawEssence(
        ID2D1RenderTarget target,
        ID2D1Brush brush,
        Vector2 center,
        float radius)
    {
        DrawClosedPolyline(
            target,
            brush,
            2.4f,
            center + new Vector2(0f, -radius),
            center + new Vector2(radius * 0.68f, -radius * 0.12f),
            center + new Vector2(radius * 0.42f, radius),
            center + new Vector2(-radius * 0.42f, radius),
            center + new Vector2(-radius * 0.68f, -radius * 0.12f));
        target.DrawLine(
            center + new Vector2(0f, -radius),
            center + new Vector2(0f, radius),
            brush,
            1.8f);
    }

    private static void DrawIncursion(
        ID2D1RenderTarget target,
        ID2D1Brush brush,
        Vector2 center,
        float radius)
    {
        target.DrawLine(
            center + new Vector2(-radius, -radius * 0.25f),
            center + new Vector2(0f, -radius),
            brush,
            2.5f);
        target.DrawLine(
            center + new Vector2(0f, -radius),
            center + new Vector2(radius, -radius * 0.25f),
            brush,
            2.5f);
        target.DrawLine(
            center + new Vector2(-radius * 0.62f, -radius * 0.2f),
            center + new Vector2(-radius * 0.62f, radius),
            brush,
            2.5f);
        target.DrawLine(
            center + new Vector2(radius * 0.62f, -radius * 0.2f),
            center + new Vector2(radius * 0.62f, radius),
            brush,
            2.5f);
        target.DrawLine(
            center + new Vector2(-radius, radius),
            center + new Vector2(radius, radius),
            brush,
            2.5f);
    }

    private static void DrawRareMonster(
        ID2D1RenderTarget target,
        ID2D1Brush brush,
        Vector2 center,
        float radius)
    {
        DrawClosedPolyline(
            target,
            brush,
            2.5f,
            center + new Vector2(-radius, -radius * 0.5f),
            center + new Vector2(-radius * 0.45f, radius * 0.25f),
            center + new Vector2(0f, -radius * 0.65f),
            center + new Vector2(radius * 0.45f, radius * 0.25f),
            center + new Vector2(radius, -radius * 0.5f),
            center + new Vector2(radius * 0.72f, radius * 0.8f),
            center + new Vector2(-radius * 0.72f, radius * 0.8f));
    }

    private static void DrawChest(
        ID2D1RenderTarget target,
        ID2D1Brush brush,
        Vector2 center,
        float radius,
        bool rare,
        bool unique)
    {
        var bounds = new Vortice.RawRectF(
            center.X - radius,
            center.Y - radius * 0.55f,
            center.X + radius,
            center.Y + radius * 0.72f);
        target.DrawRectangle(bounds, brush, 2.4f);
        target.DrawLine(
            new Vector2(bounds.Left, center.Y - radius * 0.05f),
            new Vector2(bounds.Right, center.Y - radius * 0.05f),
            brush,
            2.2f);
        target.DrawLine(
            center + new Vector2(0f, -radius * 0.05f),
            center + new Vector2(0f, radius * 0.35f),
            brush,
            2f);
        if (rare)
        {
            target.FillEllipse(
                new Ellipse(center + new Vector2(0f, radius * 0.52f), radius * 0.13f, radius * 0.13f),
                brush);
        }
        else if (unique)
        {
            DrawDiamond(target, brush, center + new Vector2(0f, radius * 0.48f), radius * 0.2f);
        }
    }

    private static void DrawClosedPolyline(
        ID2D1RenderTarget target,
        ID2D1Brush brush,
        float strokeWidth,
        params Vector2[] points)
    {
        for (var index = 0; index < points.Length; index++)
        {
            target.DrawLine(
                points[index],
                points[(index + 1) % points.Length],
                brush,
                strokeWidth);
        }
    }

    private static Color4 ToColor4(string colorHex, float alpha)
    {
        var red = byte.Parse(colorHex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var green = byte.Parse(colorHex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var blue = byte.Parse(colorHex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new Color4(red / 255f, green / 255f, blue / 255f, alpha);
    }

    private static Vortice.RawRectF ToRawRect(RectangleF bounds)
        => new(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);

    private static bool IsValidViewport(AreaUiRect viewport)
        => float.IsFinite(viewport.X)
           && float.IsFinite(viewport.Y)
           && float.IsFinite(viewport.Width)
           && float.IsFinite(viewport.Height)
           && viewport.Width > 0f
           && viewport.Height > 0f;

    private static bool IsFinite(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
