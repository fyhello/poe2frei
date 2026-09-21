using FreiAtlas.Settings.Bridge;

namespace FreiAtlas.Settings.Tests;

public sealed class PriceMessageTests
{
    [Theory]
    [InlineData("Runes of Aldur")]
    [InlineData("Forbidden Rites")]
    [InlineData("Standard")]
    public void Route_PreservesSelectedLeague(string league)
    {
        var router = new WebMessageRouter([]);
        Assert.True(router.TryRoute(System.Text.Json.JsonSerializer.Serialize(
            new { version = 1, type = "selectPriceLeague", payload = new { leagueId = league } }),
            out var command, out _));
        Assert.Equal(league, Assert.IsType<SelectPriceLeagueWebCommand>(command).LeagueId);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("123")]
    [InlineData("\"\"")]
    public void Route_RejectsInvalidLeague(string value)
    {
        Assert.False(new WebMessageRouter([]).TryRoute(
            "{\"version\":1,\"type\":\"selectPriceLeague\",\"payload\":{\"leagueId\":" + value + "}}", out _, out _));
    }
}
