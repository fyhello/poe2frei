using FreiAtlas.Core.Area;
using FreiAtlas.Core.Settings;

namespace FreiAtlas.Host;

internal readonly record struct ExpeditionOverlayVisibility(
    bool ShowNativeSurface,
    bool ShowStandaloneSurface);

internal static class ExpeditionOverlayVisibilityPolicy
{
    public static ExpeditionOverlayVisibility Evaluate(
        AreaMapExpeditionPanelSettings settings,
        AreaExpeditionRecipePanelSnapshot panel)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(panel);
        var verifiedOpen = panel.Availability
                == AreaExpeditionRecipePanelAvailability.Verified
            && panel.IsOpen;
        return new ExpeditionOverlayVisibility(
            settings.ShowNativeRecipeValues && verifiedOpen,
            !(settings.AutoHideStandalonePanel && verifiedOpen));
    }
}
