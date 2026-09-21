using System.Globalization;
using FreiAtlas.Core.Atlas;
using FreiAtlas.Atlas.Metadata;

namespace FreiAtlas.Atlas.Content;

public static class AtlasContentDecoder
{
    private const int MapBossIconType = 6;
    private const byte MapBossNodeFlag = 0x10;

    public static NormalizedMapContent Decode(
        string rawCode,
        string sourceField = "runtime",
        nint sourceAddress = 0,
        float confidence = 1f)
    {
        var normalizedRawCode = rawCode?.Trim() ?? string.Empty;
        if (ContentDictionary.TryGetContentId(normalizedRawCode, out var contentId))
        {
            return CreateKnownContent(
                contentId,
                normalizedRawCode,
                sourceField,
                sourceAddress,
                confidence);
        }

        var attributes = CreateAttributes(
            normalizedRawCode,
            sourceField,
            sourceAddress,
            confidence);

        return new NormalizedMapContent(
            "unknown",
            ContentDictionary.IsLayoutTemplate(normalizedRawCode)
                ? string.Empty
                : string.Empty,
            "content.unknown",
            "#9AA4B2",
            0,
            attributes);
    }

    public static NormalizedMapContent? InferFromNodeSignals(
        int iconType,
        uint rawContentValue,
        byte rawFlags)
    {
        if (iconType != MapBossIconType
            || rawContentValue != 0
            || (rawFlags & MapBossNodeFlag) == 0)
        {
            return null;
        }

        return Decode(
            "MapBoss",
            sourceField: "atlas-icon-type",
            confidence: 0.8f);
    }

    private static NormalizedMapContent CreateKnownContent(
        string contentId,
        string rawCode,
        string sourceField,
        nint sourceAddress,
        float confidence)
    {
        var (displayName, iconId, defaultColor, priority) = contentId switch
        {
            "map_boss" => ("地图首领", "content.map-boss", "#E35D6A", 100),
            "breach" => ("裂隙", "content.breach", "#B47CFF", 70),
            "delirium" => ("幻像", "content.delirium", "#D18BFF", 60),
            "abyss" => ("深渊", "content.abyss", "#58B987", 55),
            "ritual" => ("祭坛", "content.ritual", "#E5C35B", 50),
            "incursion" => ("瓦尔信标", "content.incursion", "#4FA3A5", 45),
            "expedition" => ("先祖秘藏", "content.expedition", "#D6B449", 80),
            _ => (string.Empty, "content.unknown", "#9AA4B2", 0)
        };

        return new NormalizedMapContent(
            contentId,
            displayName,
            iconId,
            defaultColor,
            priority,
            CreateAttributes(rawCode, sourceField, sourceAddress, confidence))
        {
            Description = AtlasMetadataCatalog.Embedded.TryGetContentByDisplayName(
                ContentDictionary.TryGetDisplayName(
                    contentId,
                    out var catalogDisplayName)
                    ? catalogDisplayName
                    : displayName,
                out var metadata)
                ? metadata.Description
                : string.Empty,
            ReferenceIconId = AtlasMetadataCatalog.Embedded.TryGetContentByDisplayName(
                ContentDictionary.TryGetDisplayName(
                    contentId,
                    out var resolvedDisplayName)
                    ? resolvedDisplayName
                    : displayName,
                out var resolvedMetadata)
                ? resolvedMetadata.IconId
                : string.Empty
        };
    }

    private static IReadOnlyDictionary<string, string> CreateAttributes(
        string rawCode,
        string sourceField,
        nint sourceAddress,
        float confidence)
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["rawCode"] = rawCode,
            ["sourceField"] = sourceField,
            ["sourceAddressToken"] = sourceAddress == 0
                ? "redacted"
                : $"token:{sourceAddress.ToInt64():X}",
            ["confidence"] = confidence.ToString("0.###", CultureInfo.InvariantCulture)
        };
}
