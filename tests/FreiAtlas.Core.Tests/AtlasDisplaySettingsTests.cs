using FreiAtlas.Core.Settings;

namespace FreiAtlas.Core.Tests;

public sealed class AtlasDisplaySettingsTests
{
    [Fact]
    public void Default_UsesHoldToHideAltOverlayMode()
    {
        Assert.Equal(
            AltOverlayMode.HoldToHide,
            AtlasDisplaySettings.Default.AltOverlayMode);
    }

    [Theory]
    [InlineData(AltOverlayMode.HoldToHide)]
    [InlineData(AltOverlayMode.ToggleOnPress)]
    public void Validate_AcceptsDefinedAltOverlayModes(AltOverlayMode mode)
    {
        var settings = AtlasDisplaySettings.Default with { AltOverlayMode = mode };

        var result = AtlasDisplaySettingsValidator.Validate(settings);

        Assert.Equal(mode, result.AltOverlayMode);
    }

    [Fact]
    public void Validate_RejectsUndefinedAltOverlayMode()
    {
        var settings = AtlasDisplaySettings.Default with
        {
            AltOverlayMode = (AltOverlayMode)999
        };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => AtlasDisplaySettingsValidator.Validate(settings));
    }

    [Fact]
    public void NavigationDefaults_UseNearestModeAndEmptyImmutableRules()
    {
        var navigation = AtlasDisplaySettings.Default.Navigation;

        Assert.Equal(AtlasNavigationTargetMode.Nearest, navigation.TargetMode);
        Assert.True(navigation.HideCompletedMaps);
        Assert.Empty(navigation.Rules);
        Assert.Throws<NotSupportedException>(
            () => ((IDictionary<string, AtlasNavigationRule>)navigation.Rules)
                .Add("Dunes", new AtlasNavigationRule(true, false, false)));
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    public void NavigationRule_IsActiveWhenAnyFeatureIsEnabled(
        bool highlight,
        bool route,
        bool direction,
        bool expected)
    {
        var rule = new AtlasNavigationRule(highlight, route, direction);

        Assert.Equal(expected, rule.IsActive);
    }

    [Fact]
    public void Validate_RejectsNullNavigationSettings()
    {
        var settings = AtlasDisplaySettings.Default with { Navigation = null! };

        Assert.Throws<ArgumentNullException>(
            () => AtlasDisplaySettingsValidator.Validate(settings));
    }

    [Fact]
    public void Validate_RejectsNullNavigationRules()
    {
        var settings = AtlasDisplaySettings.Default with
        {
            Navigation = new AtlasNavigationSettings(
                AtlasNavigationTargetMode.Nearest,
                null!)
        };

        Assert.Throws<ArgumentNullException>(
            () => AtlasDisplaySettingsValidator.Validate(settings));
    }

    [Fact]
    public void Validate_RejectsUndefinedNavigationTargetMode()
    {
        var settings = AtlasDisplaySettings.Default with
        {
            Navigation = new AtlasNavigationSettings(
                (AtlasNavigationTargetMode)99,
                new Dictionary<string, AtlasNavigationRule>())
        };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => AtlasDisplaySettingsValidator.Validate(settings));
    }

    [Fact]
    public void Validate_RejectsBlankNavigationRuleName()
    {
        var settings = AtlasDisplaySettings.Default with
        {
            Navigation = new AtlasNavigationSettings(
                AtlasNavigationTargetMode.Nearest,
                new Dictionary<string, AtlasNavigationRule>
                {
                    ["  "] = new(true, false, false)
                })
        };

        Assert.Throws<ArgumentException>(
            () => AtlasDisplaySettingsValidator.Validate(settings));
    }

    [Fact]
    public void Validate_TrimsAndMergesNavigationRulesIgnoringCase()
    {
        var input = new Dictionary<string, AtlasNavigationRule>(StringComparer.Ordinal)
        {
            [" Dunes "] = new(true, false, false),
            ["dUNES"] = new(false, true, true)
        };
        var settings = AtlasDisplaySettings.Default with
        {
            Navigation = new AtlasNavigationSettings(
                AtlasNavigationTargetMode.All,
                input)
        };

        var result = AtlasDisplaySettingsValidator.Validate(settings);

        var pair = Assert.Single(result.Navigation.Rules);
        Assert.Equal("Dunes", pair.Key);
        Assert.Equal(new AtlasNavigationRule(true, true, true), pair.Value);
        Assert.Equal(
            pair.Value,
            result.Navigation.Rules["DUNES"]);
        Assert.Equal(AtlasNavigationTargetMode.All, result.Navigation.TargetMode);
        Assert.True(result.Navigation.HideCompletedMaps);
        Assert.Equal(2, input.Count);
    }

    [Fact]
    public void Validate_PreservesHideCompletedMaps()
    {
        var settings = AtlasDisplaySettings.Default with
        {
            Navigation = new AtlasNavigationSettings(
                AtlasNavigationTargetMode.Nearest,
                new Dictionary<string, AtlasNavigationRule>(),
                HideCompletedMaps: false)
        };

        var result = AtlasDisplaySettingsValidator.Validate(settings);

        Assert.False(result.Navigation.HideCompletedMaps);
    }

    [Fact]
    public void Validate_RemovesInactiveNavigationRules()
    {
        var settings = AtlasDisplaySettings.Default with
        {
            Navigation = new AtlasNavigationSettings(
                AtlasNavigationTargetMode.Nearest,
                new Dictionary<string, AtlasNavigationRule>
                {
                    ["Dunes"] = new(false, false, false),
                    ["Mesa"] = new(false, true, false)
                })
        };

        var result = AtlasDisplaySettingsValidator.Validate(settings);

        var pair = Assert.Single(result.Navigation.Rules);
        Assert.Equal("Mesa", pair.Key);
    }

    [Fact]
    public void Validate_ReturnsImmutableNavigationRules()
    {
        var settings = AtlasDisplaySettings.Default with
        {
            Navigation = new AtlasNavigationSettings(
                AtlasNavigationTargetMode.Nearest,
                new Dictionary<string, AtlasNavigationRule>
                {
                    ["Dunes"] = new(true, false, false)
                })
        };

        var result = AtlasDisplaySettingsValidator.Validate(settings);

        Assert.Throws<NotSupportedException>(
            () => ((IDictionary<string, AtlasNavigationRule>)result.Navigation.Rules)
                .Remove("Dunes"));
    }

    [Fact]
    public void Default_UsesConfirmedAreaMapValues()
    {
        Assert.Equal(
            new AreaMapDisplaySettings(true, true, 13f),
            AtlasDisplaySettings.Default.AreaMap);
    }

    [Fact]
    public void Default_UsesConfirmedExpeditionTagStyle()
    {
        Assert.Equal(
            new AreaMapExpeditionTagStyle("#080A0D", 0.96f, 0.98f),
            AtlasDisplaySettings.Default.AreaMap.ExpeditionTag);
    }

    [Fact]
    public void Default_UsesConfirmedExpeditionPanelValues()
    {
        Assert.True(AtlasDisplaySettings.Default.AreaMap.ExpeditionPanel.ExpandOnAreaEntry);
        Assert.Equal(
            new AreaMapExpeditionPanelSettings(
                ShowNativeRecipeValues: true,
                AutoHideStandalonePanel: true),
            AtlasDisplaySettings.Default.AreaMap.ExpeditionPanel);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    public void Validate_AcceptsExpeditionTagOpacityBoundaries(float opacity)
    {
        var settings = AtlasDisplaySettings.Default with
        {
            AreaMap = AtlasDisplaySettings.Default.AreaMap with
            {
                ExpeditionTag = AtlasDisplaySettings.Default.AreaMap.ExpeditionTag with
                {
                    BackgroundOpacity = opacity,
                    BorderOpacity = opacity
                }
            }
        };

        var result = AtlasDisplaySettingsValidator.Validate(settings);

        Assert.Equal(opacity, result.AreaMap.ExpeditionTag.BackgroundOpacity);
        Assert.Equal(opacity, result.AreaMap.ExpeditionTag.BorderOpacity);
    }

    [Theory]
    [InlineData(-0.01f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Validate_RejectsInvalidExpeditionTagOpacity(float opacity)
    {
        var settings = AtlasDisplaySettings.Default with
        {
            AreaMap = AtlasDisplaySettings.Default.AreaMap with
            {
                ExpeditionTag = AtlasDisplaySettings.Default.AreaMap.ExpeditionTag with
                {
                    BorderOpacity = opacity
                }
            }
        };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => AtlasDisplaySettingsValidator.Validate(settings));
    }

    [Fact]
    public void Validate_RejectsNullExpeditionTagStyle()
    {
        var settings = AtlasDisplaySettings.Default with
        {
            AreaMap = AtlasDisplaySettings.Default.AreaMap with
            {
                ExpeditionTag = null!
            }
        };

        Assert.Throws<ArgumentNullException>(
            () => AtlasDisplaySettingsValidator.Validate(settings));
    }

    [Fact]
    public void Validate_RejectsNullExpeditionPanelSettings()
    {
        var settings = AtlasDisplaySettings.Default with
        {
            AreaMap = AtlasDisplaySettings.Default.AreaMap with
            {
                ExpeditionPanel = null!
            }
        };

        Assert.Throws<ArgumentNullException>(
            () => AtlasDisplaySettingsValidator.Validate(settings));
    }

    [Fact]
    public void Validate_ShowExpeditionDoesNotChangeExpeditionPanelSettings()
    {
        var expeditionPanel = new AreaMapExpeditionPanelSettings(
            ShowNativeRecipeValues: false,
            AutoHideStandalonePanel: true);
        var settings = AtlasDisplaySettings.Default with
        {
            AreaMap = AtlasDisplaySettings.Default.AreaMap with
            {
                ShowExpedition = false,
                ExpeditionPanel = expeditionPanel
            }
        };

        var result = AtlasDisplaySettingsValidator.Validate(settings);

        Assert.Equal(expeditionPanel, result.AreaMap.ExpeditionPanel);
    }

    [Fact]
    public void Validate_NormalizesExpeditionTagBackgroundColor()
    {
        var settings = AtlasDisplaySettings.Default with
        {
            AreaMap = AtlasDisplaySettings.Default.AreaMap with
            {
                ExpeditionTag = AtlasDisplaySettings.Default.AreaMap.ExpeditionTag with
                {
                    BackgroundColor = "#0a0b0c"
                }
            }
        };

        var result = AtlasDisplaySettingsValidator.Validate(settings);

        Assert.Equal("#0A0B0C", result.AreaMap.ExpeditionTag.BackgroundColor);
    }

    [Theory]
    [InlineData(9f)]
    [InlineData(13f)]
    [InlineData(30f)]
    public void Validate_AcceptsAreaMapLargeMapLabelFontSize(float fontSize)
    {
        var settings = AtlasDisplaySettings.Default with
        {
            AreaMap = AtlasDisplaySettings.Default.AreaMap with
            {
                LargeMapLabelFontSize = fontSize
            }
        };

        var result = AtlasDisplaySettingsValidator.Validate(settings);

        Assert.Equal(fontSize, result.AreaMap.LargeMapLabelFontSize);
    }

    [Theory]
    [InlineData(8f)]
    [InlineData(9.5f)]
    [InlineData(31f)]
    public void Validate_RejectsInvalidAreaMapLargeMapLabelFontSize(float fontSize)
    {
        var settings = AtlasDisplaySettings.Default with
        {
            AreaMap = AtlasDisplaySettings.Default.AreaMap with
            {
                LargeMapLabelFontSize = fontSize
            }
        };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => AtlasDisplaySettingsValidator.Validate(settings));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Validate_RejectsNonFiniteAreaMapLargeMapLabelFontSize(float fontSize)
    {
        var settings = AtlasDisplaySettings.Default with
        {
            AreaMap = AtlasDisplaySettings.Default.AreaMap with
            {
                LargeMapLabelFontSize = fontSize
            }
        };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => AtlasDisplaySettingsValidator.Validate(settings));
    }

    [Fact]
    public void Validate_RejectsNullAreaMapSettings()
    {
        var settings = AtlasDisplaySettings.Default with { AreaMap = null! };

        Assert.Throws<ArgumentNullException>(
            () => AtlasDisplaySettingsValidator.Validate(settings));
    }

    [Fact]
    public void Default_UsesConfirmedValues()
    {
        var settings = AtlasDisplaySettings.Default;

        Assert.Equal(1, settings.SchemaVersion);
        Assert.Equal(AtlasTheme.Dark, settings.Theme);
        Assert.Equal(3, settings.Nodes.Count);
        Assert.All(
            Enum.GetValues<AtlasNodeCategory>(),
            category =>
            {
                Assert.True(settings.Nodes[category].ShowMapNames);
                Assert.True(settings.Nodes[category].ShowConnections);
                Assert.True(settings.Nodes[category].ShowMapContents);
            });
        Assert.Empty(settings.ContentVisibility);
        Assert.Equal(new AtlasEdgeStyle(1.75f, 0.90f, "#33E661", "#F2382E"), settings.Edges);
        Assert.Equal(new AtlasHighlightStyle(2.0f, 1.0f, "#FFC850"), settings.Highlight);
        Assert.Equal(
            new AtlasLabelStyle(13f, "#FFFFFF", true, "#0D1216", 0.78f),
            settings.Labels);
    }

    [Theory]
    [InlineData(true, true, AtlasNodeCategory.Completed)]
    [InlineData(true, false, AtlasNodeCategory.Completed)]
    [InlineData(false, true, AtlasNodeCategory.Unlocked)]
    [InlineData(false, false, AtlasNodeCategory.Locked)]
    public void Classify_UsesCompletedThenUnlockedThenLocked(
        bool completed,
        bool accessible,
        AtlasNodeCategory expected)
    {
        Assert.Equal(expected, AtlasNodeCategoryClassifier.Classify(completed, accessible));
    }

    [Theory]
    [InlineData(NumericBoundary.EdgeWidthMinimum)]
    [InlineData(NumericBoundary.EdgeWidthMaximum)]
    [InlineData(NumericBoundary.EdgeOpacityMinimum)]
    [InlineData(NumericBoundary.EdgeOpacityMaximum)]
    [InlineData(NumericBoundary.HighlightWidthMinimum)]
    [InlineData(NumericBoundary.HighlightWidthMaximum)]
    [InlineData(NumericBoundary.HighlightOpacityMinimum)]
    [InlineData(NumericBoundary.HighlightOpacityMaximum)]
    [InlineData(NumericBoundary.FontSizeMinimum)]
    [InlineData(NumericBoundary.FontSizeMaximum)]
    [InlineData(NumericBoundary.BackgroundOpacityMinimum)]
    [InlineData(NumericBoundary.BackgroundOpacityMaximum)]
    public void Validate_AcceptsNumericBoundaries(NumericBoundary boundary)
    {
        var settings = WithNumericBoundary(AtlasDisplaySettings.Default, boundary);

        var result = AtlasDisplaySettingsValidator.Validate(settings);

        Assert.Equal(settings.Edges, result.Edges);
        Assert.Equal(settings.Highlight, result.Highlight);
        Assert.Equal(settings.Labels, result.Labels);
    }

    [Theory]
    [InlineData(InvalidNumericValue.EdgeWidthBelowMinimum)]
    [InlineData(InvalidNumericValue.EdgeWidthAboveMaximum)]
    [InlineData(InvalidNumericValue.EdgeOpacityBelowMinimum)]
    [InlineData(InvalidNumericValue.EdgeOpacityAboveMaximum)]
    [InlineData(InvalidNumericValue.HighlightWidthBelowMinimum)]
    [InlineData(InvalidNumericValue.HighlightWidthAboveMaximum)]
    [InlineData(InvalidNumericValue.HighlightOpacityBelowMinimum)]
    [InlineData(InvalidNumericValue.HighlightOpacityAboveMaximum)]
    [InlineData(InvalidNumericValue.FontSizeBelowMinimum)]
    [InlineData(InvalidNumericValue.FontSizeAboveMaximum)]
    [InlineData(InvalidNumericValue.BackgroundOpacityBelowMinimum)]
    [InlineData(InvalidNumericValue.BackgroundOpacityAboveMaximum)]
    [InlineData(InvalidNumericValue.NonFinite)]
    public void Validate_RejectsValuesOutsideNumericRanges(InvalidNumericValue value)
    {
        var settings = WithInvalidNumericValue(AtlasDisplaySettings.Default, value);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => AtlasDisplaySettingsValidator.Validate(settings));
    }

    [Theory]
    [InlineData(InvalidStep.EdgeWidth)]
    [InlineData(InvalidStep.EdgeWidthNearMinimum)]
    [InlineData(InvalidStep.HighlightWidth)]
    [InlineData(InvalidStep.FontSize)]
    public void Validate_RejectsInvalidNumericSteps(InvalidStep step)
    {
        var settings = WithInvalidStep(AtlasDisplaySettings.Default, step);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => AtlasDisplaySettingsValidator.Validate(settings));
    }

    [Theory]
    [InlineData(ColorField.Reachable, "33E661")]
    [InlineData(ColorField.Locked, "#F2382")]
    [InlineData(ColorField.Highlight, "#GG0000")]
    [InlineData(ColorField.Text, "#FFFFFF00")]
    [InlineData(ColorField.Background, " #0D1216")]
    public void Validate_RejectsColorsOutsideStrictRgbFormat(ColorField field, string color)
    {
        var settings = WithColor(AtlasDisplaySettings.Default, field, color);

        Assert.Throws<ArgumentException>(() => AtlasDisplaySettingsValidator.Validate(settings));
    }

    [Fact]
    public void Validate_NormalizesEveryColorToUppercase()
    {
        var settings = AtlasDisplaySettings.Default with
        {
            Edges = new AtlasEdgeStyle(1.75f, 0.9f, "#33e661", "#f2382e"),
            Highlight = new AtlasHighlightStyle(2f, 1f, "#ffc850"),
            Labels = new AtlasLabelStyle(13f, "#ffffff", true, "#0d1216", 0.78f)
        };

        var result = AtlasDisplaySettingsValidator.Validate(settings);

        Assert.Equal("#33E661", result.Edges.ReachableColor);
        Assert.Equal("#F2382E", result.Edges.LockedColor);
        Assert.Equal("#FFC850", result.Highlight.Color);
        Assert.Equal("#FFFFFF", result.Labels.TextColor);
        Assert.Equal("#0D1216", result.Labels.BackgroundColor);
    }

    [Fact]
    public void Validate_PreservesMapContentVisibilityForEachNodeCategory()
    {
        var nodes = AtlasDisplaySettings.Default.Nodes.ToDictionary();
        nodes[AtlasNodeCategory.Completed] = nodes[AtlasNodeCategory.Completed] with { ShowMapContents = false };
        nodes[AtlasNodeCategory.Unlocked] = nodes[AtlasNodeCategory.Unlocked] with { ShowMapContents = true };
        nodes[AtlasNodeCategory.Locked] = nodes[AtlasNodeCategory.Locked] with { ShowMapContents = false };
        var settings = AtlasDisplaySettings.Default with { Nodes = nodes };

        var result = AtlasDisplaySettingsValidator.Validate(settings);

        Assert.False(result.Nodes[AtlasNodeCategory.Completed].ShowMapContents);
        Assert.True(result.Nodes[AtlasNodeCategory.Unlocked].ShowMapContents);
        Assert.False(result.Nodes[AtlasNodeCategory.Locked].ShowMapContents);
    }

    [Fact]
    public void Validate_RejectsMissingNodeCategory()
    {
        var nodes = AtlasDisplaySettings.Default.Nodes
            .Where(pair => pair.Key != AtlasNodeCategory.Locked)
            .ToDictionary();
        var settings = AtlasDisplaySettings.Default with { Nodes = nodes };

        Assert.Throws<ArgumentException>(() => AtlasDisplaySettingsValidator.Validate(settings));
    }

    [Fact]
    public void Validate_AddsMissingRequiredContentAsVisibleAndPreservesChoices()
    {
        var settings = AtlasDisplaySettings.Default with
        {
            ContentVisibility = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["breach"] = false
            }
        };

        var result = AtlasDisplaySettingsValidator.Validate(
            settings,
            ["breach", "expedition"]);

        Assert.False(result.ContentVisibility["breach"]);
        Assert.True(result.ContentVisibility["expedition"]);
    }

    [Fact]
    public void Validate_UsesOrdinalContentIds()
    {
        var settings = AtlasDisplaySettings.Default with
        {
            ContentVisibility = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["breach"] = false,
                ["BREACH"] = true
            }
        };

        var result = AtlasDisplaySettingsValidator.Validate(
            settings,
            ["breach", "BREACH"]);

        Assert.Equal(2, result.ContentVisibility.Count);
        Assert.False(result.ContentVisibility["breach"]);
        Assert.True(result.ContentVisibility["BREACH"]);
    }

    [Fact]
    public void Validate_RejectsUnknownContentWhenRequiredIdsAreProvided()
    {
        var settings = AtlasDisplaySettings.Default with
        {
            ContentVisibility = new Dictionary<string, bool>
            {
                ["unknown"] = false
            }
        };

        Assert.Throws<ArgumentException>(
            () => AtlasDisplaySettingsValidator.Validate(settings, ["breach"]));
    }

    [Fact]
    public void Validate_RejectsEmptyContentId()
    {
        var settings = AtlasDisplaySettings.Default with
        {
            ContentVisibility = new Dictionary<string, bool>
            {
                [""] = true
            }
        };

        Assert.Throws<ArgumentException>(() => AtlasDisplaySettingsValidator.Validate(settings));
    }

    [Fact]
    public void Validate_ReturnsImmutableSnapshotWithoutModifyingInput()
    {
        var nodes = AtlasDisplaySettings.Default.Nodes.ToDictionary();
        var content = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["breach"] = false
        };
        var settings = AtlasDisplaySettings.Default with
        {
            Nodes = nodes,
            ContentVisibility = content,
            Edges = new AtlasEdgeStyle(1.75f, 0.9f, "#33e661", "#f2382e")
        };

        var result = AtlasDisplaySettingsValidator.Validate(
            settings,
            ["breach", "expedition"]);

        Assert.Equal("#33e661", settings.Edges.ReachableColor);
        Assert.Single(content);
        Assert.False(content["breach"]);
        Assert.Throws<NotSupportedException>(
            () => ((IDictionary<string, bool>)result.ContentVisibility).Add("new", true));
        Assert.Throws<NotSupportedException>(
            () => ((IDictionary<AtlasNodeCategory, AtlasNodeCategoryVisibility>)result.Nodes)
                .Remove(AtlasNodeCategory.Locked));
    }

    private static AtlasDisplaySettings WithNumericBoundary(
        AtlasDisplaySettings settings,
        NumericBoundary boundary)
        => boundary switch
        {
            NumericBoundary.EdgeWidthMinimum => settings with
            {
                Edges = settings.Edges with { Width = 0.5f }
            },
            NumericBoundary.EdgeWidthMaximum => settings with
            {
                Edges = settings.Edges with { Width = 6f }
            },
            NumericBoundary.EdgeOpacityMinimum => settings with
            {
                Edges = settings.Edges with { Opacity = 0f }
            },
            NumericBoundary.EdgeOpacityMaximum => settings with
            {
                Edges = settings.Edges with { Opacity = 1f }
            },
            NumericBoundary.HighlightWidthMinimum => settings with
            {
                Highlight = settings.Highlight with { Width = 0.5f }
            },
            NumericBoundary.HighlightWidthMaximum => settings with
            {
                Highlight = settings.Highlight with { Width = 8f }
            },
            NumericBoundary.HighlightOpacityMinimum => settings with
            {
                Highlight = settings.Highlight with { Opacity = 0f }
            },
            NumericBoundary.HighlightOpacityMaximum => settings with
            {
                Highlight = settings.Highlight with { Opacity = 1f }
            },
            NumericBoundary.FontSizeMinimum => settings with
            {
                Labels = settings.Labels with { FontSize = 9f }
            },
            NumericBoundary.FontSizeMaximum => settings with
            {
                Labels = settings.Labels with { FontSize = 30f }
            },
            NumericBoundary.BackgroundOpacityMinimum => settings with
            {
                Labels = settings.Labels with { BackgroundOpacity = 0f }
            },
            NumericBoundary.BackgroundOpacityMaximum => settings with
            {
                Labels = settings.Labels with { BackgroundOpacity = 1f }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(boundary))
        };

    private static AtlasDisplaySettings WithInvalidNumericValue(
        AtlasDisplaySettings settings,
        InvalidNumericValue value)
        => value switch
        {
            InvalidNumericValue.EdgeWidthBelowMinimum => settings with
            {
                Edges = settings.Edges with { Width = 0.25f }
            },
            InvalidNumericValue.EdgeWidthAboveMaximum => settings with
            {
                Edges = settings.Edges with { Width = 6.25f }
            },
            InvalidNumericValue.EdgeOpacityBelowMinimum => settings with
            {
                Edges = settings.Edges with { Opacity = -0.01f }
            },
            InvalidNumericValue.EdgeOpacityAboveMaximum => settings with
            {
                Edges = settings.Edges with { Opacity = 1.01f }
            },
            InvalidNumericValue.HighlightWidthBelowMinimum => settings with
            {
                Highlight = settings.Highlight with { Width = 0.25f }
            },
            InvalidNumericValue.HighlightWidthAboveMaximum => settings with
            {
                Highlight = settings.Highlight with { Width = 8.25f }
            },
            InvalidNumericValue.HighlightOpacityBelowMinimum => settings with
            {
                Highlight = settings.Highlight with { Opacity = -0.01f }
            },
            InvalidNumericValue.HighlightOpacityAboveMaximum => settings with
            {
                Highlight = settings.Highlight with { Opacity = 1.01f }
            },
            InvalidNumericValue.FontSizeBelowMinimum => settings with
            {
                Labels = settings.Labels with { FontSize = 8f }
            },
            InvalidNumericValue.FontSizeAboveMaximum => settings with
            {
                Labels = settings.Labels with { FontSize = 31f }
            },
            InvalidNumericValue.BackgroundOpacityBelowMinimum => settings with
            {
                Labels = settings.Labels with { BackgroundOpacity = -0.01f }
            },
            InvalidNumericValue.BackgroundOpacityAboveMaximum => settings with
            {
                Labels = settings.Labels with { BackgroundOpacity = 1.01f }
            },
            InvalidNumericValue.NonFinite => settings with
            {
                Edges = settings.Edges with { Opacity = float.NaN }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    private static AtlasDisplaySettings WithInvalidStep(
        AtlasDisplaySettings settings,
        InvalidStep step)
        => step switch
        {
            InvalidStep.EdgeWidth => settings with
            {
                Edges = settings.Edges with { Width = 0.6f }
            },
            InvalidStep.EdgeWidthNearMinimum => settings with
            {
                Edges = settings.Edges with { Width = 0.50001f }
            },
            InvalidStep.HighlightWidth => settings with
            {
                Highlight = settings.Highlight with { Width = 0.6f }
            },
            InvalidStep.FontSize => settings with
            {
                Labels = settings.Labels with { FontSize = 9.5f }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(step))
        };

    private static AtlasDisplaySettings WithColor(
        AtlasDisplaySettings settings,
        ColorField field,
        string color)
        => field switch
        {
            ColorField.Reachable => settings with
            {
                Edges = settings.Edges with { ReachableColor = color }
            },
            ColorField.Locked => settings with
            {
                Edges = settings.Edges with { LockedColor = color }
            },
            ColorField.Highlight => settings with
            {
                Highlight = settings.Highlight with { Color = color }
            },
            ColorField.Text => settings with
            {
                Labels = settings.Labels with { TextColor = color }
            },
            ColorField.Background => settings with
            {
                Labels = settings.Labels with { BackgroundColor = color }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

    public enum NumericBoundary
    {
        EdgeWidthMinimum,
        EdgeWidthMaximum,
        EdgeOpacityMinimum,
        EdgeOpacityMaximum,
        HighlightWidthMinimum,
        HighlightWidthMaximum,
        HighlightOpacityMinimum,
        HighlightOpacityMaximum,
        FontSizeMinimum,
        FontSizeMaximum,
        BackgroundOpacityMinimum,
        BackgroundOpacityMaximum
    }

    public enum InvalidNumericValue
    {
        EdgeWidthBelowMinimum,
        EdgeWidthAboveMaximum,
        EdgeOpacityBelowMinimum,
        EdgeOpacityAboveMaximum,
        HighlightWidthBelowMinimum,
        HighlightWidthAboveMaximum,
        HighlightOpacityBelowMinimum,
        HighlightOpacityAboveMaximum,
        FontSizeBelowMinimum,
        FontSizeAboveMaximum,
        BackgroundOpacityBelowMinimum,
        BackgroundOpacityAboveMaximum,
        NonFinite
    }

    public enum InvalidStep
    {
        EdgeWidth,
        EdgeWidthNearMinimum,
        HighlightWidth,
        FontSize
    }

    public enum ColorField
    {
        Reachable,
        Locked,
        Highlight,
        Text,
        Background
    }
}
