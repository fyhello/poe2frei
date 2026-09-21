using System.Collections.ObjectModel;

namespace FreiAtlas.Core.Settings;

public enum AtlasTheme
{
    Dark,
    Light
}

public enum AltOverlayMode
{
    HoldToHide,
    ToggleOnPress
}

public enum AtlasNodeCategory
{
    Completed,
    Unlocked,
    Locked
}

public sealed record AtlasNodeCategoryVisibility(
    bool ShowMapNames,
    bool ShowConnections)
{
    public bool ShowMapContents { get; init; } = true;
}

public sealed record AtlasEdgeStyle(
    float Width,
    float Opacity,
    string ReachableColor,
    string LockedColor);

public sealed record AtlasHighlightStyle(
    float Width,
    float Opacity,
    string Color);

public sealed record AtlasLabelStyle(
    float FontSize,
    string TextColor,
    bool ShowBackground,
    string BackgroundColor,
    float BackgroundOpacity);

public sealed record AreaMapExpeditionTagStyle(
    string BackgroundColor,
    float BackgroundOpacity,
    float BorderOpacity)
{
    public static AreaMapExpeditionTagStyle Default { get; } =
        new("#080A0D", 0.96f, 0.98f);
}

public sealed record AreaMapExpeditionPanelSettings(
    bool ShowNativeRecipeValues,
    bool AutoHideStandalonePanel)
{
    public bool ExpandOnAreaEntry { get; init; } = true;

    public static AreaMapExpeditionPanelSettings Default { get; } =
        new(true, true);
}

public sealed record AreaMapDisplaySettings(
    bool ShowExpedition,
    bool ShowBoss,
    float LargeMapLabelFontSize)
{
    public bool ShowAbyss { get; init; } = true;

    public bool ShowRitual { get; init; } = true;

    public bool ShowBreach { get; init; } = true;

    public bool ShowEssence { get; init; } = true;

    public bool ShowIncursion { get; init; } = true;

    public bool ShowStrongbox { get; init; } = true;

    public bool ShowRareMonster { get; init; } = true;

    public bool ShowRareChests { get; init; } = true;

    public AreaMapExpeditionTagStyle ExpeditionTag { get; init; } =
        AreaMapExpeditionTagStyle.Default;

    public AreaMapExpeditionPanelSettings ExpeditionPanel { get; init; } =
        AreaMapExpeditionPanelSettings.Default;
}

public sealed record AtlasDisplaySettings(
    int SchemaVersion,
    AtlasTheme Theme,
    IReadOnlyDictionary<AtlasNodeCategory, AtlasNodeCategoryVisibility> Nodes,
    IReadOnlyDictionary<string, bool> ContentVisibility,
    AtlasEdgeStyle Edges,
    AtlasHighlightStyle Highlight,
    AtlasLabelStyle Labels,
    AreaMapDisplaySettings AreaMap)
{
    public AltOverlayMode AltOverlayMode { get; init; } =
        AltOverlayMode.HoldToHide;

    public AtlasNavigationSettings Navigation { get; init; } =
        AtlasNavigationSettings.Default;

    public static AtlasDisplaySettings Default { get; } = new(
        1,
        AtlasTheme.Dark,
        new ReadOnlyDictionary<AtlasNodeCategory, AtlasNodeCategoryVisibility>(
            new Dictionary<AtlasNodeCategory, AtlasNodeCategoryVisibility>
            {
                [AtlasNodeCategory.Completed] = new(true, true),
                [AtlasNodeCategory.Unlocked] = new(true, true),
                [AtlasNodeCategory.Locked] = new(true, true)
            }),
        new ReadOnlyDictionary<string, bool>(
            new Dictionary<string, bool>(StringComparer.Ordinal)),
        new AtlasEdgeStyle(1.75f, 0.90f, "#33E661", "#F2382E"),
        new AtlasHighlightStyle(2.0f, 1.0f, "#FFC850"),
        new AtlasLabelStyle(13f, "#FFFFFF", true, "#0D1216", 0.78f),
        new AreaMapDisplaySettings(true, true, 13f));
}

public static class AtlasNodeCategoryClassifier
{
    public static AtlasNodeCategory Classify(bool completed, bool accessible)
        => completed
            ? AtlasNodeCategory.Completed
            : accessible
                ? AtlasNodeCategory.Unlocked
                : AtlasNodeCategory.Locked;
}
