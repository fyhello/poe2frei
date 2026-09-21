using FreiAtlas.Core.Settings;

namespace FreiAtlas.App.Settings;

internal sealed class AtlasSettingsDocument
{
    public int? SchemaVersion { get; set; }

    public AtlasTheme? Theme { get; set; }

    public AltOverlayMode? AltOverlayMode { get; set; }

    public string? OverlayToggleHotkey { get; set; }

    public AtlasDisplayDocument? Display { get; set; }

    public FreiAtlas.Core.Recovery.QuickAssistSettings? QuickAssist { get; set; }

    public PriceSettings? Prices { get; set; }
}

internal sealed class AtlasDisplayDocument
{
    public Dictionary<AtlasNodeCategory, AtlasNodeCategoryVisibilityDocument>? Nodes { get; set; }

    public Dictionary<string, bool>? ContentVisibility { get; set; }

    public AtlasEdgeStyleDocument? Edges { get; set; }

    public AtlasHighlightStyleDocument? Highlight { get; set; }

    public AtlasLabelStyleDocument? Labels { get; set; }

    public AreaMapDisplaySettingsDocument? AreaMap { get; set; }

    public AtlasNavigationSettingsDocument? Navigation { get; set; }
}

internal sealed class AtlasNavigationSettingsDocument
{
    public AtlasNavigationTargetMode? TargetMode { get; set; }

    public bool? HideCompletedMaps { get; set; }

    public Dictionary<string, AtlasNavigationRuleDocument>? Rules { get; set; }
}

internal sealed class AtlasNavigationRuleDocument
{
    public bool? Highlight { get; set; }

    public bool? Route { get; set; }

    public bool? Direction { get; set; }
}

internal sealed class AtlasNodeCategoryVisibilityDocument
{
    public bool? ShowMapNames { get; set; }

    public bool? ShowConnections { get; set; }

    public bool? ShowMapContents { get; set; }
}

internal sealed class AtlasEdgeStyleDocument
{
    public float? Width { get; set; }

    public float? Opacity { get; set; }

    public string? ReachableColor { get; set; }

    public string? LockedColor { get; set; }
}

internal sealed class AtlasHighlightStyleDocument
{
    public float? Width { get; set; }

    public float? Opacity { get; set; }

    public string? Color { get; set; }
}

internal sealed class AtlasLabelStyleDocument
{
    public float? FontSize { get; set; }

    public string? TextColor { get; set; }

    public bool? ShowBackground { get; set; }

    public string? BackgroundColor { get; set; }

    public float? BackgroundOpacity { get; set; }
}

internal sealed class AreaMapDisplaySettingsDocument
{
    public bool? ShowExpedition { get; set; }

    public bool? ShowBoss { get; set; }

    public bool? ShowAbyss { get; set; }

    public bool? ShowRitual { get; set; }

    public bool? ShowBreach { get; set; }

    public bool? ShowEssence { get; set; }

    public bool? ShowIncursion { get; set; }

    public bool? ShowStrongbox { get; set; }

    public bool? ShowRareMonster { get; set; }

    public bool? ShowRareChests { get; set; }

    public float? LargeMapLabelFontSize { get; set; }

    public AreaMapExpeditionTagStyleDocument? ExpeditionTag { get; set; }

    public AreaMapExpeditionPanelSettingsDocument? ExpeditionPanel { get; set; }
}

internal sealed class AreaMapExpeditionTagStyleDocument
{
    public string? BackgroundColor { get; set; }

    public float? BackgroundOpacity { get; set; }

    public float? BorderOpacity { get; set; }
}

internal sealed class AreaMapExpeditionPanelSettingsDocument
{
    public bool? ExpandOnAreaEntry { get; set; }

    public bool? ShowNativeRecipeValues { get; set; }

    public bool? AutoHideStandalonePanel { get; set; }
}
