namespace FreiAtlas.Core.Settings;

public sealed record PriceSettings(string LeagueId)
{
    public static PriceSettings Default { get; } = new("Runes of Aldur");

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(LeagueId) || LeagueId.Length > 128
            || LeagueId != LeagueId.Trim() || LeagueId.Any(char.IsControl))
            throw new ArgumentException("请选择有效的物价赛季。");
    }
}
