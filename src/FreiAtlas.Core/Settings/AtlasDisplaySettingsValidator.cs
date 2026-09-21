using System.Collections.ObjectModel;

namespace FreiAtlas.Core.Settings;

public static class AtlasDisplaySettingsValidator
{
    public static AtlasDisplaySettings Validate(
        AtlasDisplaySettings settings,
        IEnumerable<string>? requiredContentIds = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settings.Nodes);
        ArgumentNullException.ThrowIfNull(settings.ContentVisibility);
        ArgumentNullException.ThrowIfNull(settings.Edges);
        ArgumentNullException.ThrowIfNull(settings.Highlight);
        ArgumentNullException.ThrowIfNull(settings.Labels);
        ArgumentNullException.ThrowIfNull(settings.AreaMap);
        ArgumentNullException.ThrowIfNull(settings.AreaMap.ExpeditionTag);
        ArgumentNullException.ThrowIfNull(settings.AreaMap.ExpeditionPanel);
        ArgumentNullException.ThrowIfNull(settings.Navigation);
        ArgumentNullException.ThrowIfNull(settings.Navigation.Rules);

        if (settings.SchemaVersion != AtlasDisplaySettings.Default.SchemaVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings.SchemaVersion),
                settings.SchemaVersion,
                $"Schema version must be {AtlasDisplaySettings.Default.SchemaVersion}.");
        }

        if (!Enum.IsDefined(settings.Theme))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings.Theme),
                settings.Theme,
                "Theme is not supported.");
        }

        if (!Enum.IsDefined(settings.AltOverlayMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings.AltOverlayMode),
                settings.AltOverlayMode,
                "Alt overlay mode is not supported.");
        }

        if (!Enum.IsDefined(settings.Navigation.TargetMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings.Navigation.TargetMode),
                settings.Navigation.TargetMode,
                "Navigation target mode is not supported.");
        }

        ValidateSteppedRange(settings.Edges.Width, 0.5f, 6.0f, 0.25f, "Edges.Width");
        ValidateRange(settings.Edges.Opacity, 0f, 1f, "Edges.Opacity");
        ValidateSteppedRange(settings.Highlight.Width, 0.5f, 8.0f, 0.25f, "Highlight.Width");
        ValidateRange(settings.Highlight.Opacity, 0f, 1f, "Highlight.Opacity");
        ValidateSteppedRange(settings.Labels.FontSize, 9f, 30f, 1f, "Labels.FontSize");
        ValidateRange(settings.Labels.BackgroundOpacity, 0f, 1f, "Labels.BackgroundOpacity");
        ValidateSteppedRange(
            settings.AreaMap.LargeMapLabelFontSize,
            9f,
            30f,
            1f,
            "AreaMap.LargeMapLabelFontSize");
        ValidateRange(
            settings.AreaMap.ExpeditionTag.BackgroundOpacity,
            0f,
            1f,
            "AreaMap.ExpeditionTag.BackgroundOpacity");
        ValidateRange(
            settings.AreaMap.ExpeditionTag.BorderOpacity,
            0f,
            1f,
            "AreaMap.ExpeditionTag.BorderOpacity");

        var nodes = NormalizeNodes(settings.Nodes);
        var contentVisibility = NormalizeContentVisibility(
            settings.ContentVisibility,
            requiredContentIds);
        var navigation = NormalizeNavigation(settings.Navigation);

        return settings with
        {
            Nodes = nodes,
            ContentVisibility = contentVisibility,
            Navigation = navigation,
            Edges = settings.Edges with
            {
                ReachableColor = NormalizeColor(settings.Edges.ReachableColor, "Edges.ReachableColor"),
                LockedColor = NormalizeColor(settings.Edges.LockedColor, "Edges.LockedColor")
            },
            Highlight = settings.Highlight with
            {
                Color = NormalizeColor(settings.Highlight.Color, "Highlight.Color")
            },
            Labels = settings.Labels with
            {
                TextColor = NormalizeColor(settings.Labels.TextColor, "Labels.TextColor"),
                BackgroundColor = NormalizeColor(settings.Labels.BackgroundColor, "Labels.BackgroundColor")
            },
            AreaMap = settings.AreaMap with
            {
                ExpeditionTag = settings.AreaMap.ExpeditionTag with
                {
                    BackgroundColor = NormalizeColor(
                        settings.AreaMap.ExpeditionTag.BackgroundColor,
                        "AreaMap.ExpeditionTag.BackgroundColor")
                }
            }
        };
    }

    private static AtlasNavigationSettings NormalizeNavigation(
        AtlasNavigationSettings navigation)
    {
        var normalized = new Dictionary<string, AtlasNavigationRule>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var pair in navigation.Rules)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                throw new ArgumentException(
                    "Navigation rule names cannot be empty.",
                    nameof(navigation));
            }

            if (pair.Value is null)
            {
                throw new ArgumentException(
                    "Navigation rules cannot contain null values.",
                    nameof(navigation));
            }

            var name = pair.Key.Trim();
            if (normalized.TryGetValue(name, out var existing))
            {
                normalized[name] = new AtlasNavigationRule(
                    existing.Highlight || pair.Value.Highlight,
                    existing.Route || pair.Value.Route,
                    existing.Direction || pair.Value.Direction);
            }
            else
            {
                normalized.Add(name, pair.Value);
            }
        }

        foreach (var name in normalized
                     .Where(pair => !pair.Value.IsActive)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            normalized.Remove(name);
        }

        return new AtlasNavigationSettings(
            navigation.TargetMode,
            new ReadOnlyDictionary<string, AtlasNavigationRule>(normalized),
            navigation.HideCompletedMaps);
    }

    private static IReadOnlyDictionary<AtlasNodeCategory, AtlasNodeCategoryVisibility> NormalizeNodes(
        IReadOnlyDictionary<AtlasNodeCategory, AtlasNodeCategoryVisibility> nodes)
    {
        var normalized = new Dictionary<AtlasNodeCategory, AtlasNodeCategoryVisibility>();
        foreach (var category in Enum.GetValues<AtlasNodeCategory>())
        {
            if (!nodes.TryGetValue(category, out var visibility) || visibility is null)
            {
                throw new ArgumentException(
                    $"Nodes must contain a value for {category}.",
                    nameof(nodes));
            }

            normalized.Add(category, visibility);
        }

        if (nodes.Count != normalized.Count)
        {
            throw new ArgumentException("Nodes contains an unknown category.", nameof(nodes));
        }

        return new ReadOnlyDictionary<AtlasNodeCategory, AtlasNodeCategoryVisibility>(normalized);
    }

    private static IReadOnlyDictionary<string, bool> NormalizeContentVisibility(
        IReadOnlyDictionary<string, bool> contentVisibility,
        IEnumerable<string>? requiredContentIds)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (requiredContentIds is not null)
        {
            foreach (var contentId in requiredContentIds)
            {
                ValidateContentId(contentId, nameof(requiredContentIds));
                required.Add(contentId);
            }
        }

        var normalized = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var pair in contentVisibility)
        {
            ValidateContentId(pair.Key, nameof(contentVisibility));
            if (required.Count > 0 && !required.Contains(pair.Key))
            {
                throw new ArgumentException(
                    $"Content ID '{pair.Key}' is not in the required content catalog.",
                    nameof(contentVisibility));
            }

            normalized.Add(pair.Key, pair.Value);
        }

        foreach (var contentId in required)
        {
            normalized.TryAdd(contentId, true);
        }

        return new ReadOnlyDictionary<string, bool>(normalized);
    }

    private static void ValidateContentId(string? contentId, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(contentId))
        {
            throw new ArgumentException("Content IDs cannot be empty.", parameterName);
        }
    }

    private static void ValidateRange(
        float value,
        float minimum,
        float maximum,
        string parameterName)
    {
        if (!float.IsFinite(value) || value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Value must be between {minimum} and {maximum}.");
        }
    }

    private static void ValidateSteppedRange(
        float value,
        float minimum,
        float maximum,
        float step,
        string parameterName)
    {
        ValidateRange(value, minimum, maximum, parameterName);

        var steps = (value - minimum) / step;
        if (steps != MathF.Round(steps))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Value must use increments of {step}.");
        }
    }

    private static string NormalizeColor(string? color, string parameterName)
    {
        if (color is null || color.Length != 7 || color[0] != '#')
        {
            throw new ArgumentException("Color must use #RRGGBB format.", parameterName);
        }

        for (var index = 1; index < color.Length; index++)
        {
            if (!char.IsAsciiHexDigit(color[index]))
            {
                throw new ArgumentException("Color must use #RRGGBB format.", parameterName);
            }
        }

        return color.ToUpperInvariant();
    }
}
