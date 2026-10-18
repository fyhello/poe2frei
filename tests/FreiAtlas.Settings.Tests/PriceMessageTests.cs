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

    [Fact]
    public void Route_ApplyCurrentPricesPatch_ParsesFilterOptions()
    {
        const string json = """
            {
              "version": 1,
              "type": "applyCurrentPricesPatch",
              "payload": {
                "mode": "append",
                "separator": " ",
                "minExalted": 1.5,
                "unitMode": "auto",
                "stripExisting": true,
                "categories": ["Currency", "Runes"]
              }
            }
            """;

        Assert.True(new WebMessageRouter([]).TryRoute(json, out var command, out _));
        var apply = Assert.IsType<ApplyCurrentPricesPatchWebCommand>(command);
        Assert.Equal(1.5m, apply.MinExalted);
        Assert.Equal("auto", apply.UnitMode);
        Assert.True(apply.StripExisting);
        Assert.Equal(["Currency", "Runes"], apply.Categories);
    }

    [Fact]
    public void Route_ApplyCurrentPricesPatch_UsesSafeDefaults()
    {
        const string json = """
            {
              "version": 1,
              "type": "applyCurrentPricesPatch",
              "payload": { "mode": "append", "separator": " " }
            }
            """;

        Assert.True(new WebMessageRouter([]).TryRoute(json, out var command, out _));
        var apply = Assert.IsType<ApplyCurrentPricesPatchWebCommand>(command);
        Assert.Equal(1m, apply.MinExalted);
        Assert.Equal("auto", apply.UnitMode);
        Assert.True(apply.StripExisting);
        Assert.Null(apply.Categories);
    }

    [Fact]
    public void Route_PreviewCurrentPricesPatch_ParsesFilterOptions()
    {
        const string json = """
            {
              "version": 1,
              "type": "previewCurrentPricesPatch",
              "payload": {
                "mode": "append",
                "separator": " ",
                "minExalted": 1,
                "unitMode": "auto",
                "stripExisting": true,
                "categories": ["Currency"]
              }
            }
            """;

        Assert.True(new WebMessageRouter([]).TryRoute(json, out var command, out _));
        var preview = Assert.IsType<PreviewCurrentPricesPatchWebCommand>(command);
        Assert.Equal(1m, preview.MinExalted);
        Assert.Equal("auto", preview.UnitMode);
        Assert.True(preview.StripExisting);
        Assert.Equal(["Currency"], preview.Categories);
    }

    [Fact]
    public void Route_ConfirmCurrentPricesPatch_RequiresToken()
    {
        const string valid = """
            {
              "version": 1,
              "type": "confirmCurrentPricesPatch",
              "payload": { "token": "abc123" }
            }
            """;

        Assert.True(new WebMessageRouter([]).TryRoute(valid, out var command, out _));
        Assert.Equal("abc123", Assert.IsType<ConfirmCurrentPricesPatchWebCommand>(command).Token);

        Assert.False(new WebMessageRouter([]).TryRoute(
            """{ "version": 1, "type": "confirmCurrentPricesPatch", "payload": { "token": "" } }""",
            out _,
            out _));
    }
}
