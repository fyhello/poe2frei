namespace FreiAtlas.Atlas.Metadata;

public static class AtlasMapKindClassifier
{
    public static string Classify(string? mapCode)
    {
        if (string.IsNullOrWhiteSpace(mapCode))
        {
            return "Normal";
        }

        if (mapCode.Contains("Citadel", StringComparison.Ordinal))
        {
            return "Citadel";
        }

        if (mapCode.Contains("UberBoss", StringComparison.Ordinal))
        {
            return "Boss";
        }

        if (mapCode.Contains("HildaCampsite", StringComparison.Ordinal)
            || mapCode.Contains("Wildwood", StringComparison.Ordinal))
        {
            return "Unique";
        }

        if (mapCode.Contains("Merchant", StringComparison.Ordinal))
        {
            return "Merchant";
        }

        if (mapCode.Contains("Unique", StringComparison.Ordinal))
        {
            return "Unique";
        }

        if (mapCode.Contains("Tower", StringComparison.Ordinal))
        {
            return "Tower";
        }

        return "Normal";
    }
}
