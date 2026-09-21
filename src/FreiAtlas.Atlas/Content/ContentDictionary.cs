namespace FreiAtlas.Atlas.Content;

public static class ContentDictionary
{
    private static readonly IReadOnlyDictionary<string, string> KnownCodes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PowerfulMapBoss"] = "map_boss",
            ["MapBoss"] = "map_boss",
            ["Powerful Map Boss"] = "map_boss",
            ["Deadly Map Boss"] = "map_boss",
            ["Map Boss"] = "map_boss",
            ["强力地图首领"] = "map_boss",
            ["地图首领"] = "map_boss",
            ["強大地圖頭目"] = "map_boss",
            ["地圖頭目"] = "map_boss",
            ["致命地图首领"] = "map_boss",
            ["致命地圖頭目"] = "map_boss",
            ["Breach"] = "breach",
            ["BreachCity"] = "breach",
            ["Breach City"] = "breach",
            ["Delirium"] = "delirium",
            ["DeliriumGigaMirror"] = "delirium",
            ["Delirium Giga Mirror"] = "delirium",
            ["RitualLocusts"] = "ritual",
            ["Ritual Locusts"] = "ritual",
            ["Ritual"] = "ritual",
            ["Abyss"] = "abyss",
            ["Incursion"] = "incursion",
            ["Expedition"] = "expedition",
            ["Grand Expedition"] = "expedition",
            ["ExpeditionLogbook"] = "expedition",
            ["Expedition Logbook"] = "expedition",
            ["先祖秘藏"] = "expedition",
            ["探险"] = "expedition",
            ["死境探險"] = "expedition",
            ["死境探险"] = "expedition",
            ["大型探險"] = "expedition",
            ["大型探险"] = "expedition",
            ["AzmeriSpiritBossPossessed"] = "map_boss"
        };

    private static readonly IReadOnlyDictionary<string, string> DisplayNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["map_boss"] = "Powerful Map Boss",
            ["breach"] = "Breach",
            ["delirium"] = "Delirium",
            ["ritual"] = "Ritual",
            ["abyss"] = "Abyss",
            ["incursion"] = "Vaal Beacons",
            ["expedition"] = "Expedition"
        };

    public static bool TryGetContentId(string rawCode, out string contentId)
        => KnownCodes.TryGetValue(rawCode, out contentId!);

    public static bool TryGetDisplayName(
        string contentId,
        out string displayName)
        => DisplayNames.TryGetValue(contentId, out displayName!);

    public static bool IsLayoutTemplate(string rawCode)
        => rawCode.StartsWith("AtlasNormalMaps", StringComparison.OrdinalIgnoreCase)
           || rawCode.StartsWith("AtlasLeague", StringComparison.OrdinalIgnoreCase);

    public static bool IsNoiseCode(string rawCode)
        => !string.IsNullOrWhiteSpace(rawCode)
           && (rawCode.StartsWith("[DNT]", StringComparison.OrdinalIgnoreCase)
               || rawCode.Contains(
                   "Not Shown to Players",
                   StringComparison.OrdinalIgnoreCase));
}
