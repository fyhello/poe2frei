using FreiAtlas.Core.Atlas;
using FreiAtlas.Core.Settings;
using FreiAtlas.Platform.Windows.Overlay;
using System.Numerics;
using System.Text.Json;
using Vortice;
using Vortice.Mathematics;

namespace FreiAtlas.Atlas.Tests;

public sealed class AtlasNameOverlaySurfaceTests
{
    [Fact]
    public void AtlasIconCache_DecodesAllEmbeddedPngs()
    {
        using var cache = new AtlasIconCache();

        Assert.Equal(66, cache.Count);
        Assert.Equal(132, cache.DecodedVariantCount);
    }

    [Fact]
    public void CreateGrayscalePremultipliedBgra_PreservesAlphaAndTransparency()
    {
        byte[] pixels =
        [
            0, 0, 0, 0,
            0, 0, 128, 128,
            30, 60, 90, 255
        ];
        var original = pixels.ToArray();

        var grayscale = AtlasIconCache.CreateGrayscalePremultipliedBgra(pixels);

        Assert.Equal(
            [
                0, 0, 0, 0,
                39, 39, 39, 128,
                66, 66, 66, 255
            ],
            grayscale);
        Assert.Equal(original, pixels);
    }

    [Fact]
    public void CreateGrayscalePremultipliedBgra_RejectsIncompletePixel()
    {
        Assert.Throws<ArgumentException>(() =>
            AtlasIconCache.CreateGrayscalePremultipliedBgra([0, 0, 0]));
    }

    [Theory]
    [InlineData("AtlasIconContentMapBoss")]
    [InlineData("AtlasIconContentAbyss")]
    [InlineData("AtlasIconContentRitual")]
    [InlineData("AtlasIconContentBreach")]
    [InlineData("AtlasIconContentEssence")]
    [InlineData("AtlasIconContentIncursion")]
    [InlineData("AtlasIconContentStrongBox")]
    public void EmbeddedAtlasIconCatalog_ContainsRequiredAreaMapIcon(string iconId)
    {
        var resources = typeof(AtlasIconCache).Assembly.GetManifestResourceNames();

        Assert.Single(
            resources,
            name => name.EndsWith(
                $".AtlasIcons.{iconId}.png",
                StringComparison.Ordinal));
    }

    [Fact]
    public void EmbeddedAtlasIconCatalog_ContainsAllPoe2DbMapContentAssets()
    {
        var assembly = typeof(AtlasNameOverlaySurface).Assembly;
        var resources = assembly.GetManifestResourceNames();
        var manifestName = Assert.Single(
            resources,
            name => name.EndsWith(
                ".AtlasIcons.manifest.json",
                StringComparison.Ordinal));

        using var manifestStream = assembly.GetManifestResourceStream(manifestName);
        Assert.NotNull(manifestStream);
        using var manifest = JsonDocument.Parse(manifestStream);
        var entries = manifest.RootElement.GetProperty("entries")
            .EnumerateArray()
            .ToArray();

        Assert.Equal(66, entries.Length);
        Assert.Equal(
            66,
            entries.Select(entry => entry.GetProperty("id").GetString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        Assert.Contains(
            entries,
            entry => entry.GetProperty("id").GetString()
                == "AtlasIconContentMapBoss");
        Assert.Contains(
            entries,
            entry => entry.GetProperty("id").GetString()
                == "AtlasIconContentExpedition");

        var pngResources = resources.Where(name => name.EndsWith(
                ".png",
                StringComparison.OrdinalIgnoreCase))
            .Where(name => name.Contains(
                ".Assets.AtlasIcons.",
                StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(66, pngResources.Length);
        Assert.Contains(
            pngResources,
            name => name.EndsWith(
                ".AtlasIconContentMapBoss.png",
                StringComparison.Ordinal));
        Assert.Contains(
            pngResources,
            name => name.EndsWith(
                ".AtlasIconContentExpedition.png",
                StringComparison.Ordinal));
    }

    [Fact]
    public void CreateTextLayoutRect_ConvertsPanelBoundsToPositionAndSize()
    {
        var panel = new RawRectF(120f, 80f, 268f, 100f);

        var textLayout = AtlasNameOverlaySurface.CreateTextLayoutRect(panel);

        Assert.Equal(120f, textLayout.X);
        Assert.Equal(80f, textLayout.Y);
        Assert.Equal(148f, textLayout.Width);
        Assert.Equal(20f, textLayout.Height);
    }

    [Fact]
    public void ApplyLabelAnchorOffset_CalibratesHorizontalCenterOnly()
    {
        var anchor = new Vector2(554f, 313f);

        var adjusted = AtlasNameOverlaySurface.ApplyLabelAnchorOffset(anchor);

        Assert.Equal(559f, adjusted.X);
        Assert.Equal(320f, adjusted.Y);
    }

    [Fact]
    public void ApplyEdgeAnchorOffset_UsesTheSameHorizontalCalibrationAsLabels()
    {
        var endpoint = new Vector2(554f, 313f);

        var adjusted = AtlasNameOverlaySurface.ApplyEdgeAnchorOffset(endpoint);

        Assert.Equal(559f, adjusted.X);
        Assert.Equal(320f, adjusted.Y);
    }

    [Fact]
    public void CalculateContentIconCenters_PlacesSingleIconAboveAnchor()
    {
        var centers = AtlasNameOverlaySurface.CalculateContentIconCenters(
            new Vector2(559f, 320f),
            1);

        var center = Assert.Single(centers);
        Assert.Equal(new Vector2(559f, 305f), center);
    }

    [Fact]
    public void CalculateContentIconCenters_CentersMultipleIconsAboveAnchor()
    {
        var centers = AtlasNameOverlaySurface.CalculateContentIconCenters(
            new Vector2(559f, 320f),
            2);

        Assert.Equal(
            [new Vector2(548f, 305f), new Vector2(570f, 305f)],
            centers);
    }

    [Fact]
    public void CalculateContentIconBounds_CentersTwentyPixelBitmap()
    {
        var bounds = AtlasNameOverlaySurface.CalculateContentIconBounds(
            new Vector2(559f, 305f));

        Assert.Equal(549f, bounds.Left);
        Assert.Equal(295f, bounds.Top);
        Assert.Equal(569f, bounds.Right);
        Assert.Equal(315f, bounds.Bottom);
    }

    [Fact]
    public void CalculateLabelTop_MovesNameCloserToAnchor()
    {
        Assert.Equal(
            328f,
            AtlasNameOverlaySurface.CalculateLabelTop(320f));
    }

    [Fact]
    public void LabelGeometryHelpers_MatchSharedPanelLayoutExactly()
    {
        var label = new AtlasLabelPlacement(
            new AtlasGridPos(3, 4),
            "MapUniqueMerchant03_Raft",
            new Vector2(554f, 313f));
        var adjustedAnchor = AtlasLabelPanelLayout.AdjustAnchor(label.Anchor);

        Assert.Equal(
            AtlasLabelPanelLayout.GetWidth(label.Text),
            AtlasNameOverlaySurface.CalculateLabelWidth(label.Text));
        Assert.Equal(
            adjustedAnchor,
            AtlasNameOverlaySurface.ApplyLabelAnchorOffset(label.Anchor));
        Assert.Equal(
            AtlasLabelPanelLayout.GetTop(adjustedAnchor.Y),
            AtlasNameOverlaySurface.CalculateLabelTop(adjustedAnchor.Y));
        Assert.Equal(
            AtlasLabelPanelLayout.GetPanelBounds(label),
            AtlasNameOverlaySurface.CalculateLabelPanel(label));
    }

    [Fact]
    public void CalculateLabelPanel_MaximumFontSizeExpandsWithoutMovingAnchor()
    {
        var label = new AtlasLabelPlacement(
            new AtlasGridPos(3, 4),
            "隐秘神庙",
            new Vector2(554f, 313f));

        var defaultPanel = AtlasNameOverlaySurface.CalculateLabelPanel(label);
        var largePanel = AtlasNameOverlaySurface.CalculateLabelPanel(label, 30f);

        Assert.Equal(defaultPanel.Top, largePanel.Top);
        Assert.True(largePanel.Width > defaultPanel.Width);
        Assert.True(largePanel.Height >= 38f);
        Assert.Equal(defaultPanel.Left + (defaultPanel.Width / 2f),
            largePanel.Left + (largePanel.Width / 2f));
    }

    [Fact]
    public void ToColor4_ConvertsRgbHexAndAppliesOpacity()
    {
        var color = AtlasOverlayStyleResolver.ToColor4("#336699", 0.42f);

        Assert.Equal(0.2f, color.R, 6);
        Assert.Equal(0.4f, color.G, 6);
        Assert.Equal(0.6f, color.B, 6);
        Assert.Equal(0.42f, color.A, 6);
    }

    [Fact]
    public void Resolve_PreservesDisabledLabelBackground()
    {
        var settings = AtlasDisplaySettings.Default with
        {
            Labels = AtlasDisplaySettings.Default.Labels with
            {
                ShowBackground = false
            }
        };

        var style = AtlasOverlayStyleResolver.Resolve(settings);

        Assert.False(style.ShowLabelBackground);
    }

    [Fact]
    public void Resolve_UsesDynamicLineWidthsAndFontSize()
    {
        var settings = AtlasDisplaySettings.Default with
        {
            Edges = AtlasDisplaySettings.Default.Edges with { Width = 3.25f },
            Highlight = AtlasDisplaySettings.Default.Highlight with { Width = 4.5f },
            Labels = AtlasDisplaySettings.Default.Labels with { FontSize = 18f }
        };

        var style = AtlasOverlayStyleResolver.Resolve(settings);

        Assert.Equal(3.25f, style.EdgeWidth);
        Assert.Equal(4.5f, style.HighlightWidth);
        Assert.Equal(18f, style.FontSize);
    }

    [Fact]
    public void Resolve_NavigationPanelUsesAtLeastNinetyPercentOpacity()
    {
        var settings = AtlasDisplaySettings.Default with
        {
            Labels = AtlasDisplaySettings.Default.Labels with
            {
                BackgroundColor = "#123456",
                BackgroundOpacity = 0.2f
            }
        };

        var style = AtlasOverlayStyleResolver.Resolve(settings);

        Assert.Equal(0.9f, style.NavigationPanelColor.A, 6);
        Assert.Equal(style.PanelColor.R, style.NavigationPanelColor.R, 6);
        Assert.Equal(style.PanelColor.G, style.NavigationPanelColor.G, 6);
        Assert.Equal(style.PanelColor.B, style.NavigationPanelColor.B, 6);
    }

    [Fact]
    public void CalculateDirectionArrow_PointsTowardUnitDirection()
    {
        var tip = new Vector2(400f, 250f);
        var direction = Vector2.Normalize(new Vector2(3f, 4f));

        var arrow = AtlasNameOverlaySurface.CalculateDirectionArrow(
            tip,
            direction);

        Assert.Equal(tip, arrow.Tip);
        Assert.True(Vector2.Dot(arrow.Left - tip, direction) < 0f);
        Assert.True(Vector2.Dot(arrow.Right - tip, direction) < 0f);
        Assert.True(Vector2.Distance(arrow.Left, arrow.Right) > 0f);
    }

    [Fact]
    public void GetResourceChanges_RebuildsOnlyResourcesAffectedByStyleChanges()
    {
        var baseline = AtlasOverlayStyleResolver.Resolve(AtlasDisplaySettings.Default);
        var geometryOnly = AtlasOverlayStyleResolver.Resolve(
            AtlasDisplaySettings.Default with
            {
                Edges = AtlasDisplaySettings.Default.Edges with { Width = 3f },
                Highlight = AtlasDisplaySettings.Default.Highlight with { Width = 4f },
                Labels = AtlasDisplaySettings.Default.Labels with
                {
                    ShowBackground = false
                }
            });
        var edgeColor = AtlasOverlayStyleResolver.Resolve(
            AtlasDisplaySettings.Default with
            {
                Edges = AtlasDisplaySettings.Default.Edges with
                {
                    ReachableColor = "#112233"
                }
            });
        var highlightColor = AtlasOverlayStyleResolver.Resolve(
            AtlasDisplaySettings.Default with
            {
                Highlight = AtlasDisplaySettings.Default.Highlight with
                {
                    Opacity = 0.5f
                }
            });
        var labelColors = AtlasOverlayStyleResolver.Resolve(
            AtlasDisplaySettings.Default with
            {
                Labels = AtlasDisplaySettings.Default.Labels with
                {
                    TextColor = "#123456",
                    BackgroundOpacity = 0.5f
                }
            });
        var font = AtlasOverlayStyleResolver.Resolve(
            AtlasDisplaySettings.Default with
            {
                Labels = AtlasDisplaySettings.Default.Labels with
                {
                    FontSize = 18f
                }
            });

        Assert.Equal(
            AtlasOverlayResourceChanges.None,
            AtlasOverlayStyleResolver.GetResourceChanges(baseline, geometryOnly));
        Assert.Equal(
            AtlasOverlayResourceChanges.EdgeBrushes,
            AtlasOverlayStyleResolver.GetResourceChanges(baseline, edgeColor));
        Assert.Equal(
            AtlasOverlayResourceChanges.HighlightBrush,
            AtlasOverlayStyleResolver.GetResourceChanges(baseline, highlightColor));
        Assert.Equal(
            AtlasOverlayResourceChanges.TextBrush
            | AtlasOverlayResourceChanges.PanelBrush,
            AtlasOverlayStyleResolver.GetResourceChanges(baseline, labelColors));
        Assert.Equal(
            AtlasOverlayResourceChanges.TextFormat,
            AtlasOverlayStyleResolver.GetResourceChanges(baseline, font));
    }

    [Fact]
    public void Render_DrawsNavigationLayersInTheRequiredOrder()
    {
        var source = File.ReadAllText(Path.Combine(
            FindWorkspaceRoot(),
            "src",
            "FreiAtlas.Platform.Windows",
            "Overlay",
            "AtlasNameOverlaySurface.cs"));

        var ordinaryEdges = source.IndexOf(
            "foreach (var edge in edges)",
            StringComparison.Ordinal);
        var hoveredEdges = source.IndexOf(
            "foreach (var edge in edges.Where(edge => edge.IsHighlighted))",
            StringComparison.Ordinal);
        var navigationRoutes = source.IndexOf(
            "foreach (var edge in navigationRouteEdges)",
            StringComparison.Ordinal);
        var labels = source.IndexOf(
            "foreach (var label in labels)",
            StringComparison.Ordinal);
        var icons = source.IndexOf(
            "DrawContentIcons(renderTarget, contentIcons);",
            StringComparison.Ordinal);
        var directions = source.IndexOf(
            "DrawDirections(renderTarget, directions, style);",
            StringComparison.Ordinal);

        Assert.True(
            ordinaryEdges >= 0
            && ordinaryEdges < hoveredEdges
            && hoveredEdges < navigationRoutes
            && navigationRoutes < labels
            && labels < icons
            && icons < directions);
    }

    private static string FindWorkspaceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "FreiAtlas.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate FreiAtlas.slnx.");
    }
}
