namespace FreiAtlas.Plugin.ExpeditionPanel;

public static class ExpeditionValuePresentation
{
    public static ExpeditionPanelValueBand Classify(
        decimal? totalDivine,
        decimal? exaltedPerDivine)
    {
        if (totalDivine is not { } total
            || total < 0m
            || exaltedPerDivine is not { } rate
            || rate <= 0m)
        {
            return ExpeditionPanelValueBand.Unknown;
        }

        if (total * rate <= 20m)
        {
            return ExpeditionPanelValueBand.UpToTwentyExalted;
        }

        return total switch
        {
            < 1m => ExpeditionPanelValueBand.UnderOneDivine,
            < 5m => ExpeditionPanelValueBand.OneToFiveDivine,
            < 10m => ExpeditionPanelValueBand.FiveToTenDivine,
            < 50m => ExpeditionPanelValueBand.TenToFiftyDivine,
            _ => ExpeditionPanelValueBand.FiftyPlusDivine
        };
    }

    public static uint GetArgb(ExpeditionPanelValueBand band)
        => band switch
        {
            ExpeditionPanelValueBand.UpToTwentyExalted => 0xFF707A80u,
            ExpeditionPanelValueBand.UnderOneDivine => 0xFFF2F2F2u,
            ExpeditionPanelValueBand.OneToFiveDivine => 0xFF39D98Au,
            ExpeditionPanelValueBand.FiveToTenDivine => 0xFFFFD166u,
            ExpeditionPanelValueBand.TenToFiftyDivine => 0xFFC084FCu,
            ExpeditionPanelValueBand.FiftyPlusDivine => 0xFFFF5C5Cu,
            _ => 0xFF50585Du
        };
}
