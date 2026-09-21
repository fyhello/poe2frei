using System.Drawing;
using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Platform.Windows.Overlay.Native;
using FreiAtlas.Plugin.ExpeditionPanel;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace FreiAtlas.Platform.Windows.Overlay;

internal enum ExpeditionPanelHitKind
{
    None,
    PanelToggle,
    Title,
    EncounterHeader,
    Body
}

internal readonly record struct ExpeditionPanelHit(
    ExpeditionPanelHitKind Kind,
    string? EncounterId = null);

public sealed class ExpeditionPanelSurface : IExpeditionPanelSurface
{
    private readonly LayeredOverlayWindow _window;
    private readonly ExpeditionPanelUiState _uiState;
    private readonly ExpeditionRuneIconCache _iconCache;
    private readonly ExpeditionRewardIconCache _rewardIconCache;
    private readonly HashSet<int> _reportedMissingRuneIndices = [];
    private readonly HashSet<string> _reportedMissingRewardKeys =
        new(StringComparer.Ordinal);
    private readonly ID2D1SolidColorBrush _panelBrush;
    private readonly ID2D1SolidColorBrush _titleBrush;
    private readonly ID2D1SolidColorBrush _headerBrush;
    private readonly ID2D1SolidColorBrush _rowBrush;
    private readonly ID2D1SolidColorBrush _alternateRowBrush;
    private readonly ID2D1SolidColorBrush _borderBrush;
    private readonly ID2D1SolidColorBrush _primaryTextBrush;
    private readonly ID2D1SolidColorBrush _secondaryTextBrush;
    private readonly ID2D1SolidColorBrush _valueDividerBrush;
    private readonly ID2D1SolidColorBrush _availableBrush;
    private readonly ID2D1SolidColorBrush _selectedBrush;
    private readonly ID2D1SolidColorBrush _completedBrush;
    private readonly Dictionary<ExpeditionPanelValueBand, ID2D1SolidColorBrush>
        _valueBandBrushes = [];
    private IDWriteTextFormat? _titleFormat;
    private IDWriteTextFormat? _bodyFormat;
    private IDWriteTextFormat? _metaFormat;

    private ExpeditionPanelLayout? _layout;
    private Rectangle _workArea;
    private float _textScale;
    private bool _hasPosition;
    private bool _disposed;

    private ExpeditionPanelSurface(
        LayeredOverlayWindow window,
        Point? initialPosition)
    {
        _window = window;
        _uiState = new ExpeditionPanelUiState(initialPosition ?? Point.Empty);
        _hasPosition = initialPosition.HasValue;
        _iconCache = new ExpeditionRuneIconCache();
        _rewardIconCache = new ExpeditionRewardIconCache();

        try
        {
            var renderTarget = _window.RenderTarget;
            _panelBrush = renderTarget.CreateSolidColorBrush(new Color4(0.035f, 0.043f, 0.05f, 0.97f));
            _titleBrush = renderTarget.CreateSolidColorBrush(new Color4(0.075f, 0.088f, 0.098f, 0.99f));
            _headerBrush = renderTarget.CreateSolidColorBrush(new Color4(0.105f, 0.12f, 0.13f, 0.98f));
            _rowBrush = renderTarget.CreateSolidColorBrush(new Color4(0.052f, 0.061f, 0.068f, 0.97f));
            _alternateRowBrush = renderTarget.CreateSolidColorBrush(new Color4(0.065f, 0.074f, 0.082f, 0.97f));
            _borderBrush = renderTarget.CreateSolidColorBrush(new Color4(0.35f, 0.39f, 0.41f, 0.8f));
            _primaryTextBrush = renderTarget.CreateSolidColorBrush(new Color4(0.96f, 0.96f, 0.94f, 1f));
            _secondaryTextBrush = renderTarget.CreateSolidColorBrush(new Color4(0.68f, 0.71f, 0.72f, 1f));
            _valueDividerBrush = renderTarget.CreateSolidColorBrush(new Color4(0.24f, 0.27f, 0.28f, 0.85f));
            _availableBrush = renderTarget.CreateSolidColorBrush(new Color4(0.29f, 0.78f, 0.55f, 1f));
            _selectedBrush = renderTarget.CreateSolidColorBrush(new Color4(0.98f, 0.72f, 0.28f, 1f));
            _completedBrush = renderTarget.CreateSolidColorBrush(new Color4(0.56f, 0.59f, 0.61f, 1f));
            foreach (var band in Enum.GetValues<ExpeditionPanelValueBand>())
            {
                _valueBandBrushes[band] = renderTarget.CreateSolidColorBrush(
                    ToColor4(ExpeditionValuePresentation.GetArgb(band)));
            }

            EnsureTextFormats(1f);
            _window.MessageReceived += OnWindowMessage;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public event Action<Point>? PositionChanged;

    public event Action? PanelToggleRequested;

    public event Action<string>? EncounterToggleRequested;

    public static ExpeditionPanelSurface Create(Point? initialPosition = null)
        => new(
            LayeredOverlayWindow.Create(OverlayWindowInteraction.Selective),
            initialPosition);

    public bool PumpMessages()
    {
        ThrowIfDisposed();
        var keepRunning = _window.PumpMessages();
        if (_uiState.IsDragging
            && (!IsKeyDown(OverlayNative.VK_LBUTTON)
                || !IsKeyDown(OverlayNative.VK_CONTROL)))
        {
            EndDrag();
        }

        return keepRunning;
    }

    public void Render(
        Rectangle gameClientBounds,
        ExpeditionPanelScene scene,
        float panelScale = 1f)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(scene);
        if (gameClientBounds.Width <= 0 || gameClientBounds.Height <= 0)
        {
            Hide();
            return;
        }

        _workArea = gameClientBounds;
        var layout = ExpeditionPanelLayoutCalculator.Create(
            scene,
            gameClientBounds.Size,
            _uiState.ScrollOffset,
            panelScale);
        EnsureTextFormats(layout.Metrics.Scale);
        if (!_hasPosition)
        {
            _uiState.SetPosition(new Point(
                gameClientBounds.Right - layout.PanelSize.Width - layout.Metrics.Px(24),
                gameClientBounds.Top + layout.Metrics.Px(64)));
            _hasPosition = true;
        }

        var previousPosition = _uiState.Position;
        _uiState.ClampToWorkArea(gameClientBounds, layout.PanelSize);
        if (_uiState.Position != previousPosition)
        {
            PositionChanged?.Invoke(_uiState.Position);
        }

        _layout = layout;
        _window.ResizeAndMove(new Rectangle(_uiState.Position, layout.PanelSize));
        Draw(scene, layout);
    }

    public void Hide()
    {
        ThrowIfDisposed();
        EndDrag();
        _layout = null;
        _window.Hide();
    }

    public void RequestHide()
    {
        EndDrag();
        _window.RequestHide();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.MessageReceived -= OnWindowMessage;
        _window.ReleaseMouseCapture();
        _titleFormat?.Dispose();
        _bodyFormat?.Dispose();
        _metaFormat?.Dispose();
        _panelBrush?.Dispose();
        _titleBrush?.Dispose();
        _headerBrush?.Dispose();
        _rowBrush?.Dispose();
        _alternateRowBrush?.Dispose();
        _borderBrush?.Dispose();
        _primaryTextBrush?.Dispose();
        _secondaryTextBrush?.Dispose();
        _valueDividerBrush?.Dispose();
        _availableBrush?.Dispose();
        _selectedBrush?.Dispose();
        _completedBrush?.Dispose();
        foreach (var brush in _valueBandBrushes.Values)
        {
            brush.Dispose();
        }
        _valueBandBrushes.Clear();
        _iconCache?.Dispose();
        _rewardIconCache?.Dispose();
        _window.Dispose();
    }

    internal static ExpeditionPanelHit HitTest(
        ExpeditionPanelLayout layout,
        Point clientPosition)
    {
        var panelBounds = new Rectangle(Point.Empty, layout.PanelSize);
        if (!panelBounds.Contains(clientPosition))
        {
            return new ExpeditionPanelHit(ExpeditionPanelHitKind.None);
        }

        if (layout.PanelToggleBounds.Contains(clientPosition))
        {
            return new ExpeditionPanelHit(ExpeditionPanelHitKind.PanelToggle);
        }

        if (layout.TitleBounds.Contains(clientPosition))
        {
            return new ExpeditionPanelHit(ExpeditionPanelHitKind.Title);
        }

        if (!layout.BodyBounds.Contains(clientPosition))
        {
            return new ExpeditionPanelHit(ExpeditionPanelHitKind.None);
        }

        foreach (var encounter in layout.Encounters)
        {
            if (encounter.SummaryBounds.IntersectsWith(layout.BodyBounds)
                && encounter.SummaryBounds.Contains(clientPosition))
            {
                return new ExpeditionPanelHit(
                    ExpeditionPanelHitKind.EncounterHeader,
                    encounter.Encounter.InstanceId);
            }
        }

        return new ExpeditionPanelHit(ExpeditionPanelHitKind.Body);
    }

    private IDWriteTextFormat CreateTextFormat(float size, FontWeight weight)
    {
        var format = _window.DWriteFactory.CreateTextFormat(
            "Microsoft YaHei UI",
            null,
            weight,
            FontStyle.Normal,
            FontStretch.Normal,
            size,
            "zh-CN");
        format.TextAlignment = TextAlignment.Leading;
        format.ParagraphAlignment = ParagraphAlignment.Center;
        format.WordWrapping = WordWrapping.NoWrap;
        return format;
    }

    private void EnsureTextFormats(float scale)
    {
        if (_titleFormat is not null
            && _bodyFormat is not null
            && _metaFormat is not null
            && Math.Abs(_textScale - scale) < 0.001f)
        {
            return;
        }

        _titleFormat?.Dispose();
        _bodyFormat?.Dispose();
        _metaFormat?.Dispose();
        _titleFormat = CreateTextFormat(12f * scale, FontWeight.SemiBold);
        _bodyFormat = CreateTextFormat(10f * scale, FontWeight.Normal);
        _metaFormat = CreateTextFormat(9f * scale, FontWeight.Normal);
        _textScale = scale;
    }

    private void Draw(ExpeditionPanelScene scene, ExpeditionPanelLayout layout)
    {
        var renderTarget = _window.RenderTarget;
        renderTarget.BeginDraw();
        renderTarget.Clear(new Color4(0f, 0f, 0f, 0f));
        renderTarget.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;

        var panelRect = ToRawRect(new Rectangle(Point.Empty, layout.PanelSize));
        renderTarget.FillRoundedRectangle(
            new RoundedRectangle(
                panelRect,
                layout.Metrics.CornerRadius,
                layout.Metrics.CornerRadius),
            _panelBrush);
        renderTarget.FillRoundedRectangle(
            new RoundedRectangle(
                ToRawRect(layout.TitleBounds),
                layout.Metrics.CornerRadius,
                layout.Metrics.CornerRadius),
            _titleBrush);
        var titleSquareTop = Math.Min(
            layout.TitleBounds.Bottom,
            layout.TitleBounds.Top + (int)MathF.Ceiling(layout.Metrics.CornerRadius));
        renderTarget.FillRectangle(
            new Vortice.RawRectF(
                layout.TitleBounds.Left,
                titleSquareTop,
                layout.TitleBounds.Right,
                layout.TitleBounds.Bottom),
            _titleBrush);
        DrawPanelToggle(renderTarget, layout.PanelToggleBounds, scene.IsExpanded);
        var titleLeft = layout.Metrics.Px(34);
        var countWidth = layout.Metrics.Px(76);
        DrawText(
            renderTarget,
            "先祖秘藏配方",
            new Rectangle(
                titleLeft,
                0,
                Math.Max(1, layout.PanelSize.Width - titleLeft - countWidth),
                layout.TitleBounds.Height),
            _titleFormat!,
            _primaryTextBrush);
        DrawText(
            renderTarget,
            $"{scene.Encounters.Length} 个秘藏",
            new Rectangle(
                Math.Max(0, layout.PanelSize.Width - countWidth),
                0,
                Math.Max(1, countWidth - layout.Metrics.Px(7)),
                layout.TitleBounds.Height),
            _metaFormat!,
            _secondaryTextBrush);

        if (!layout.BodyBounds.IsEmpty)
        {
            renderTarget.PushAxisAlignedClip(ToRawRect(layout.BodyBounds), AntialiasMode.Aliased);
            DrawEncounters(renderTarget, layout);
            renderTarget.PopAxisAlignedClip();
            DrawScrollbar(renderTarget, layout);
        }

        renderTarget.DrawRoundedRectangle(
            new RoundedRectangle(
                panelRect,
                layout.Metrics.CornerRadius,
                layout.Metrics.CornerRadius),
            _borderBrush,
            1f);

        renderTarget.EndDraw();
        _window.Present();
    }

    private void DrawEncounters(
        ID2D1RenderTarget renderTarget,
        ExpeditionPanelLayout layout)
    {
        var rowIndex = 0;
        foreach (var encounter in layout.Encounters)
        {
            DrawEncounterSummary(renderTarget, layout, encounter);
            foreach (var recipe in encounter.Recipes)
            {
                DrawRecipe(renderTarget, layout, recipe, rowIndex++);
            }

            if (!encounter.Recipes.IsDefaultOrEmpty)
            {
                var first = encounter.Recipes[0].Bounds;
                var last = encounter.Recipes[^1].Bounds;
                var groupBounds = new Vortice.RawRectF(
                    first.Left + 0.5f,
                    first.Top + 0.5f,
                    first.Right - 0.5f,
                    last.Bottom - 0.5f);
                renderTarget.DrawRoundedRectangle(
                    new RoundedRectangle(
                        groupBounds,
                        layout.Metrics.Px(5),
                        layout.Metrics.Px(5)),
                    _borderBrush,
                    1f);
            }
        }
    }

    private void DrawEncounterSummary(
        ID2D1RenderTarget renderTarget,
        ExpeditionPanelLayout panelLayout,
        ExpeditionPanelEncounterLayout encounterLayout)
    {
        var bounds = encounterLayout.SummaryBounds;
        FillRowBackground(renderTarget, panelLayout, bounds, _headerBrush);
        if (_valueBandBrushes.TryGetValue(
                encounterLayout.Encounter.ValueBand,
                out var valueBandBrush))
        {
            renderTarget.FillRectangle(
                new Vortice.RawRectF(
                    bounds.Left,
                    bounds.Top,
                    bounds.Left + panelLayout.Metrics.Px(3),
                    bounds.Bottom),
                valueBandBrush);
        }

        var phaseBrush = encounterLayout.Encounter.Phase switch
        {
            AreaContentPhase.Completed => _completedBrush,
            AreaContentPhase.Active or AreaContentPhase.Selected => _selectedBrush,
            _ => _availableBrush
        };
        DrawChevron(
            renderTarget,
            new PointF(
                bounds.Left + panelLayout.Metrics.Px(13),
                bounds.Top + bounds.Height / 2f),
            encounterLayout.Encounter.IsExpanded,
            phaseBrush,
            panelLayout.Metrics.Px(4));
        if (encounterLayout.Encounter.RecommendedRecipe is { } recipe)
        {
            DrawRecipeContent(
                renderTarget,
                panelLayout.Metrics,
                bounds,
                recipe,
                displayIndex: null);
        }

        DrawRowSeparator(renderTarget, panelLayout, bounds);
    }

    private void DrawRecipe(
        ID2D1RenderTarget renderTarget,
        ExpeditionPanelLayout panelLayout,
        ExpeditionPanelRecipeLayout recipeLayout,
        int rowIndex)
    {
        var bounds = recipeLayout.Bounds;
        FillRowBackground(
            renderTarget,
            panelLayout,
            bounds,
            rowIndex % 2 == 0 ? _rowBrush : _alternateRowBrush);
        DrawRecipeContent(
            renderTarget,
            panelLayout.Metrics,
            bounds,
            recipeLayout.Recipe,
            recipeLayout.DisplayIndex);
        DrawRowSeparator(renderTarget, panelLayout, bounds);
    }

    private void DrawRecipeContent(
        ID2D1RenderTarget renderTarget,
        ExpeditionPanelMetrics metrics,
        Rectangle bounds,
        ExpeditionPanelRecipeRow recipe,
        int? displayIndex)
    {
        var prefixWidth = metrics.Px(24);
        var valueWidth = Math.Min(metrics.ValueWidth, Math.Max(1, bounds.Width / 4));
        var rewardWidth = Math.Min(
            metrics.RewardWidth,
            Math.Max(1, bounds.Width - prefixWidth - valueWidth));
        var valueLeft = bounds.Right - valueWidth;
        var rewardLeft = valueLeft - rewardWidth;
        var runesLeft = bounds.Left + prefixWidth;
        var runesRight = Math.Max(runesLeft + 1, rewardLeft - metrics.Px(2));

        if (displayIndex is { } index)
        {
            DrawText(
                renderTarget,
                index.ToString(),
                new Rectangle(
                    bounds.Left + metrics.Px(4),
                    bounds.Top,
                    Math.Max(1, prefixWidth - metrics.Px(7)),
                    bounds.Height),
                _metaFormat!,
                _secondaryTextBrush);
        }
        DrawRunes(
            renderTarget,
            recipe.RuneIndices,
            new Rectangle(runesLeft, bounds.Top, runesRight - runesLeft, bounds.Height),
            metrics);
        DrawRewards(
            renderTarget,
            recipe.Rewards,
            new Rectangle(rewardLeft, bounds.Top, rewardWidth, bounds.Height),
            metrics);

        renderTarget.DrawLine(
            new Vector2(valueLeft, bounds.Top + metrics.Px(6)),
            new Vector2(valueLeft, bounds.Bottom - metrics.Px(6)),
            _valueDividerBrush,
            1f);
        if (!string.IsNullOrWhiteSpace(recipe.ValueText))
        {
            var valueBrush = _valueBandBrushes.TryGetValue(
                recipe.ValueBand,
                out var bandBrush)
                ? bandBrush
                : _valueBandBrushes[ExpeditionPanelValueBand.Unknown];
            DrawText(
                renderTarget,
                recipe.ValueText,
                new Rectangle(
                    valueLeft + metrics.Px(5),
                    bounds.Top,
                    Math.Max(1, valueWidth - metrics.Px(8)),
                    bounds.Height),
                _bodyFormat!,
                valueBrush);
        }
    }

    private void DrawRunes(
        ID2D1RenderTarget renderTarget,
        System.Collections.Immutable.ImmutableArray<int> runeIndices,
        Rectangle bounds,
        ExpeditionPanelMetrics metrics)
    {
        if (runeIndices.IsDefaultOrEmpty || bounds.Width <= 0)
        {
            return;
        }

        var iconSize = (float)metrics.RuneSize;
        var availableWidth = Math.Max(1f, bounds.Width - metrics.Px(2));
        var step = runeIndices.Length == 1
            ? 0f
            : Math.Min(
                iconSize + metrics.RuneGap,
                Math.Max(
                    metrics.Px(4),
                    (availableWidth - iconSize) / (runeIndices.Length - 1)));
        var top = bounds.Top + (bounds.Height - iconSize) / 2f;
        var left = bounds.Left + metrics.Px(1);
        for (var index = 0; index < runeIndices.Length; index++)
        {
            var runeIndex = runeIndices[index];
            var iconBounds = new Vortice.RawRectF(
                left + index * step,
                top,
                left + index * step + iconSize,
                top + iconSize);
            var bitmap = _iconCache.Get(renderTarget, runeIndex);
            if (bitmap is not null)
            {
                renderTarget.DrawBitmap(
                    bitmap,
                    iconBounds,
                    1f,
                    BitmapInterpolationMode.Linear,
                    (Vortice.RawRectF?)null);
            }
            else
            {
                if (_reportedMissingRuneIndices.Add(runeIndex))
                {
                    Console.Error.WriteLine(
                        $"Expedition rune icon {runeIndex} is not embedded; using placeholder.");
                }

                var center = new Vector2(
                    (iconBounds.Left + iconBounds.Right) / 2f,
                    (iconBounds.Top + iconBounds.Bottom) / 2f);
                renderTarget.DrawEllipse(
                    new Ellipse(center, iconSize * 0.42f, iconSize * 0.42f),
                    _secondaryTextBrush,
                    1.5f);
                renderTarget.DrawLine(
                    center + new Vector2(-metrics.Px(4), 0f),
                    center + new Vector2(metrics.Px(4), 0f),
                    _secondaryTextBrush,
                    1.5f);
            }
        }
    }

    private void DrawRewards(
        ID2D1RenderTarget renderTarget,
        System.Collections.Immutable.ImmutableArray<AreaExpeditionReward> rewards,
        Rectangle bounds,
        ExpeditionPanelMetrics metrics)
    {
        if (bounds.Width <= 0)
        {
            return;
        }

        var iconSize = (float)metrics.RewardSize;
        var iconAreaWidth = Math.Max(iconSize, bounds.Width - metrics.Px(17));
        var rewardCount = rewards.IsDefaultOrEmpty ? 1 : rewards.Length;
        var step = rewardCount == 1
            ? 0f
            : Math.Min(
                iconSize + metrics.RuneGap,
                Math.Max(metrics.Px(4), (iconAreaWidth - iconSize) / (rewardCount - 1)));
        var left = bounds.Left + metrics.Px(1);
        var top = bounds.Top + (bounds.Height - iconSize) / 2f;
        for (var index = 0; index < rewardCount; index++)
        {
            var reward = rewards.IsDefaultOrEmpty ? null : rewards[index];
            var iconBounds = new Vortice.RawRectF(
                left + index * step,
                top,
                left + index * step + iconSize,
                top + iconSize);
            var bitmap = reward is null
                ? null
                : _rewardIconCache.Get(renderTarget, reward);
            if (bitmap is not null)
            {
                renderTarget.DrawBitmap(
                    bitmap,
                    iconBounds,
                    1f,
                    BitmapInterpolationMode.Linear,
                    (Vortice.RawRectF?)null);
            }
            else
            {
                var key = reward is null
                    ? "empty"
                    : reward.IsExactItem
                        ? reward.ItemId
                        : reward.DisplayName;
                if (_reportedMissingRewardKeys.Add(key))
                {
                    Console.Error.WriteLine(
                        $"Expedition reward icon '{key}' is not embedded; using placeholder.");
                }
                DrawRewardPlaceholder(renderTarget, iconBounds, metrics);
            }
        }

        if (!rewards.IsDefaultOrEmpty)
        {
            var quantity = Math.Max(1, rewards[^1].Quantity);
            DrawText(
                renderTarget,
                $"x{quantity}",
                new Rectangle(
                    bounds.Right - metrics.Px(17),
                    bounds.Top,
                    metrics.Px(16),
                    bounds.Height),
                _metaFormat!,
                _primaryTextBrush);
        }
    }

    private void DrawRewardPlaceholder(
        ID2D1RenderTarget renderTarget,
        Vortice.RawRectF bounds,
        ExpeditionPanelMetrics metrics)
    {
        renderTarget.DrawRoundedRectangle(
            new RoundedRectangle(bounds, metrics.Px(3), metrics.Px(3)),
            _secondaryTextBrush,
            1f);
        renderTarget.DrawLine(
            new Vector2(bounds.Left + metrics.Px(5), bounds.Top + metrics.Px(5)),
            new Vector2(bounds.Right - metrics.Px(5), bounds.Bottom - metrics.Px(5)),
            _secondaryTextBrush,
            1f);
    }

    private static void FillRowBackground(
        ID2D1RenderTarget renderTarget,
        ExpeditionPanelLayout layout,
        Rectangle bounds,
        ID2D1Brush brush)
    {
        var visible = Rectangle.Intersect(bounds, layout.BodyBounds);
        if (visible.IsEmpty)
        {
            return;
        }

        if (visible.Bottom >= layout.PanelSize.Height)
        {
            var radius = layout.Metrics.CornerRadius;
            renderTarget.FillRoundedRectangle(
                new RoundedRectangle(ToRawRect(visible), radius, radius),
                brush);
            renderTarget.FillRectangle(
                new Vortice.RawRectF(
                    visible.Left,
                    visible.Top,
                    visible.Right,
                    Math.Max(visible.Top, visible.Bottom - radius)),
                brush);
            return;
        }

        renderTarget.FillRectangle(ToRawRect(visible), brush);
    }

    private void DrawRowSeparator(
        ID2D1RenderTarget renderTarget,
        ExpeditionPanelLayout layout,
        Rectangle bounds)
    {
        if (bounds.Bottom <= layout.BodyBounds.Top
            || bounds.Bottom >= layout.BodyBounds.Bottom)
        {
            return;
        }

        renderTarget.DrawLine(
            new Vector2(bounds.Left, bounds.Bottom - 0.5f),
            new Vector2(bounds.Right, bounds.Bottom - 0.5f),
            _valueDividerBrush,
            1f);
    }

    private void DrawPanelToggle(
        ID2D1RenderTarget renderTarget,
        Rectangle bounds,
        bool expanded)
    {
        DrawChevron(
            renderTarget,
            new PointF(bounds.Left + bounds.Width / 2f, bounds.Top + bounds.Height / 2f),
            expanded,
            _primaryTextBrush,
            Math.Max(2f, bounds.Height * 0.22f));
    }

    private static void DrawChevron(
        ID2D1RenderTarget renderTarget,
        PointF center,
        bool expanded,
        ID2D1Brush brush,
        float radius)
    {
        if (expanded)
        {
            renderTarget.DrawLine(
                new Vector2(center.X - radius, center.Y - radius / 2f),
                new Vector2(center.X, center.Y + radius / 2f),
                brush,
                1.8f);
            renderTarget.DrawLine(
                new Vector2(center.X, center.Y + radius / 2f),
                new Vector2(center.X + radius, center.Y - radius / 2f),
                brush,
                1.8f);
        }
        else
        {
            renderTarget.DrawLine(
                new Vector2(center.X - radius / 2f, center.Y - radius),
                new Vector2(center.X + radius / 2f, center.Y),
                brush,
                1.8f);
            renderTarget.DrawLine(
                new Vector2(center.X + radius / 2f, center.Y),
                new Vector2(center.X - radius / 2f, center.Y + radius),
                brush,
                1.8f);
        }
    }

    private void DrawScrollbar(
        ID2D1RenderTarget renderTarget,
        ExpeditionPanelLayout layout)
    {
        if (layout.ContentHeight <= layout.BodyBounds.Height
            || layout.BodyBounds.Height <= 0)
        {
            return;
        }

        var width = (float)layout.Metrics.Px(3);
        var trackTop = layout.BodyBounds.Top + layout.Metrics.Px(5);
        var trackHeight = layout.BodyBounds.Height - layout.Metrics.Px(10);
        var thumbHeight = Math.Max(
            layout.Metrics.Px(24),
            trackHeight * layout.BodyBounds.Height / layout.ContentHeight);
        var maximumScroll = layout.ContentHeight - layout.BodyBounds.Height;
        var thumbTop = trackTop
                       + (trackHeight - thumbHeight) * layout.ScrollOffset / maximumScroll;
        var left = layout.PanelSize.Width - layout.Metrics.Px(6);
        renderTarget.FillRectangle(
            new Vortice.RawRectF(left, trackTop, left + width, trackTop + trackHeight),
            _valueDividerBrush);
        renderTarget.FillRectangle(
            new Vortice.RawRectF(left, thumbTop, left + width, thumbTop + thumbHeight),
            _secondaryTextBrush);
    }

    private static void DrawText(
        ID2D1RenderTarget renderTarget,
        string text,
        Rectangle bounds,
        IDWriteTextFormat format,
        ID2D1Brush brush)
        => renderTarget.DrawText(
            text,
            format,
            new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height),
            brush,
            DrawTextOptions.Clip);

    private void OnWindowMessage(LayeredOverlayMessage message)
    {
        if (_layout is null)
        {
            return;
        }

        switch (message.Message)
        {
            case OverlayNative.WM_LBUTTONDOWN:
                HandleLeftButtonDown(message);
                break;
            case OverlayNative.WM_MOUSEMOVE:
                HandleMouseMove(message);
                break;
            case OverlayNative.WM_LBUTTONUP:
                EndDrag();
                break;
            case OverlayNative.WM_MOUSEWHEEL:
                HandleMouseWheel(message);
                break;
        }
    }

    private void HandleLeftButtonDown(LayeredOverlayMessage message)
    {
        var layout = _layout!;
        var hit = HitTest(layout, ClientPoint(message.LParam));
        switch (hit.Kind)
        {
            case ExpeditionPanelHitKind.PanelToggle:
                PanelToggleRequested?.Invoke();
                break;
            case ExpeditionPanelHitKind.EncounterHeader when hit.EncounterId is not null:
                EncounterToggleRequested?.Invoke(hit.EncounterId);
                break;
            case ExpeditionPanelHitKind.Title:
                if (TryGetCursorPosition(out var screenPosition)
                    && _uiState.BeginDrag(
                        screenPosition,
                        leftButton: HasFlag(message.WParam, OverlayNative.MK_LBUTTON),
                        ctrl: HasFlag(message.WParam, OverlayNative.MK_CONTROL),
                        Offset(layout.TitleBounds, _uiState.Position)))
                {
                    _window.CaptureMouse();
                }
                break;
        }
    }

    private void HandleMouseMove(LayeredOverlayMessage message)
    {
        if (!_uiState.IsDragging || !TryGetCursorPosition(out var screenPosition))
        {
            return;
        }

        var previousPosition = _uiState.Position;
        _uiState.MoveDrag(
            screenPosition,
            leftButton: HasFlag(message.WParam, OverlayNative.MK_LBUTTON),
            ctrl: HasFlag(message.WParam, OverlayNative.MK_CONTROL));
        if (!_uiState.IsDragging)
        {
            _window.ReleaseMouseCapture();
            return;
        }

        if (_layout is not null && !_workArea.IsEmpty)
        {
            _uiState.ClampToWorkArea(_workArea, _layout.PanelSize);
        }

        if (_uiState.Position != previousPosition)
        {
            PositionChanged?.Invoke(_uiState.Position);
        }
    }

    private void HandleMouseWheel(LayeredOverlayMessage message)
    {
        var layout = _layout!;
        var wheelDelta = (short)(((ulong)message.WParam >> 16) & 0xffff);
        if (wheelDelta == 0)
        {
            return;
        }

        var screenPoint = ScreenPoint(message.LParam);
        var clientPoint = new Point(
            screenPoint.X - _uiState.Position.X,
            screenPoint.Y - _uiState.Position.Y);
        if (!layout.BodyBounds.Contains(clientPoint))
        {
            return;
        }

        var steps = Math.Max(1, Math.Abs(wheelDelta) / 120);
        _uiState.Scroll(
            -Math.Sign(wheelDelta) * layout.Metrics.RecipeHeight * steps,
            layout.ContentHeight,
            layout.BodyBounds.Height);
    }

    private void EndDrag()
    {
        if (!_uiState.IsDragging)
        {
            return;
        }

        _uiState.EndDrag();
        _window.ReleaseMouseCapture();
    }

    private static bool HasFlag(nuint value, nuint flag)
        => (value & flag) != 0;

    private static bool IsKeyDown(int virtualKey)
        => (OverlayNative.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static bool TryGetCursorPosition(out Point position)
    {
        if (OverlayNative.GetCursorPos(out var point))
        {
            position = new Point(point.X, point.Y);
            return true;
        }

        position = Point.Empty;
        return false;
    }

    private static Point ClientPoint(nint lParam)
    {
        var value = lParam.ToInt64();
        return new Point(
            (short)(value & 0xffff),
            (short)((value >> 16) & 0xffff));
    }

    private static Point ScreenPoint(nint lParam)
        => ClientPoint(lParam);

    private static Rectangle Offset(Rectangle rectangle, Point offset)
    {
        rectangle.Offset(offset);
        return rectangle;
    }

    private static Vortice.RawRectF ToRawRect(Rectangle rectangle)
        => new(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);

    private static Color4 ToColor4(uint argb)
        => new(
            ((argb >> 16) & 0xff) / 255f,
            ((argb >> 8) & 0xff) / 255f,
            (argb & 0xff) / 255f,
            ((argb >> 24) & 0xff) / 255f);

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);
}
