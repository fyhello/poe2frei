using FreiAtlas.Expedition;

namespace FreiAtlas.Expedition.Tests;

public sealed class LiveExpeditionPriceSourceTests
{
    [Fact]
    public async Task CurrentSources_ReturnUsableSnapshots()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("FREIATLAS_LIVE_PRICE_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("FreiAtlas.Tests/1.0");
        var league = await Poe2LeagueResolver.ResolveCurrentLeagueAsync(
            httpClient,
            CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(league));

        var ninja = await new PoeNinjaPriceSource(httpClient, league)
            .FetchAsync(CancellationToken.None);
        var poe2Db = await new Poe2DbPriceSource(httpClient)
            .FetchAsync(CancellationToken.None);
        var scout = await new Poe2ScoutPriceSource(httpClient)
            .FetchAsync(CancellationToken.None);

        var summary = $"ninja={ninja.Entries.Length}/{ninja.ExaltedPerDivine}; "
                      + $"poe2db={poe2Db.Entries.Length}/{poe2Db.ExaltedPerDivine}; "
                      + $"scout={scout.Entries.Length}/{scout.ExaltedPerDivine}";
        Assert.True(ninja.Entries.Length > 100, summary);
        Assert.True(ninja.ExaltedPerDivine > 100m, summary);
        Assert.True(poe2Db.Entries.Length > 20, summary);
        Assert.True(poe2Db.ExaltedPerDivine > 100m, summary);
        Assert.True(scout.Entries.Length > 100, summary);
        Assert.True(scout.ExaltedPerDivine > 100m, summary);
    }
}
