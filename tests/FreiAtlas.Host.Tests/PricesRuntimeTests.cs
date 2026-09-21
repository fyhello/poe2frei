using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using FreiAtlas.Core.Settings;
using FreiAtlas.Host;

namespace FreiAtlas.Host.Tests;

public sealed class PricesRuntimeTests
{
    [Theory]
    [InlineData("Runes of Aldur", "runes")]
    [InlineData("Forbidden Rites", "forbiddenrites")]
    [InlineData("Standard", "standard")]
    public async Task Refresh_PinsAllSourcesAndExchangeRateToTheManualChoice(string league, string shortName)
    {
        var directory = Path.Combine(Path.GetTempPath(), "frei-runtime-prices-" + Guid.NewGuid());
        var handler = new PriceHandler();
        try
        {
            await using var runtime = new PricesRuntime(new PriceSettings(league),
                Path.Combine(directory, "prices.json"), new HttpClient(handler));
            await runtime.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(league, runtime.Current.Prices.LeagueId);
            Assert.Equal(league, runtime.CurrentBook.LeagueId);
            Assert.Equal(500, runtime.CurrentBook.ExaltedPerDivine);
            Assert.True(runtime.CurrentBook.TryGet("shared", "物品", out var primary));
            Assert.Equal(2, primary);
            Assert.True(runtime.CurrentBook.TryGet("extra", "补充物品", out var fallback));
            Assert.Equal(3, fallback);
            Assert.Equal(["poe.ninja", "poe2scout"], runtime.Current.Prices.Sources.Select(source => source.Name));
            Assert.Equal(12, handler.Uris.Count);
            Assert.All(handler.Uris, uri =>
            {
                Assert.DoesNotContain("poe2db", uri.Host);
                if (uri.Host == "poe.ninja") Assert.Contains("league=" + Uri.EscapeDataString(league), uri.Query);
                if (uri.Host == "api.poe2scout.com" && uri.AbsolutePath != "/poe2/Leagues")
                    Assert.Contains("/Leagues/" + shortName + "/", uri.AbsolutePath);
            });
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class PriceHandler : HttpMessageHandler
    {
        public ConcurrentBag<Uri> Uris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Uris.Add(uri);
            string json;
            if (uri.AbsolutePath == "/poe2/Leagues")
                json = JsonSerializer.Serialize(new[] {
                    new { Value = "Forbidden Rites", ShortName = "forbiddenrites", IsCurrent = true },
                    new { Value = "Runes of Aldur", ShortName = "runes", IsCurrent = true },
                    new { Value = "Standard", ShortName = "standard", IsCurrent = false } });
            else if (uri.Host == "poe.ninja")
                json = """{"core":{"rates":{"exalted":500}},"items":[{"id":"shared","name":"物品"}],"lines":[{"id":"shared","primaryValue":2}]}""";
            else if (uri.AbsolutePath.EndsWith("ReferenceCurrencies", StringComparison.Ordinal))
                json = """[{"ApiId":"divine","RelativePrice":700}]""";
            else
                json = """{"Items":[{"ApiId":"shared","Text":"物品","CurrentPrice":9},{"ApiId":"extra","Text":"补充物品","CurrentPrice":3}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
