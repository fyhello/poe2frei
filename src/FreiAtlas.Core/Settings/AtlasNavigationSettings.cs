using System.Collections.ObjectModel;

namespace FreiAtlas.Core.Settings;

public enum AtlasNavigationTargetMode
{
    Nearest,
    All
}

public sealed record AtlasNavigationRule(
    bool Highlight,
    bool Route,
    bool Direction)
{
    public bool IsActive => Highlight || Route || Direction;
}

public sealed record AtlasNavigationSettings(
    AtlasNavigationTargetMode TargetMode,
    IReadOnlyDictionary<string, AtlasNavigationRule> Rules,
    bool HideCompletedMaps = true)
{
    public static AtlasNavigationSettings Default { get; } = new(
        AtlasNavigationTargetMode.Nearest,
        new ReadOnlyDictionary<string, AtlasNavigationRule>(
            new Dictionary<string, AtlasNavigationRule>(
                StringComparer.OrdinalIgnoreCase)));
}
