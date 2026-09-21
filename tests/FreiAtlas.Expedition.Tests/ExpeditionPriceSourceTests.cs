using FreiAtlas.Expedition;

namespace FreiAtlas.Expedition.Tests;

public sealed class ExpeditionPriceSourceTests
{
    [Fact]
    public void PoeNinjaParser_ReadsNestedCoreItemsAndDivineUnitPrices()
    {
        var result = PoeNinjaPriceSource.ParseOverview(NinjaFixture, "Currency");

        Assert.Equal(399.6m, result.ExaltedPerDivine);
        var exalted = Assert.Single(result.Entries, item => item.ItemId == "exalted");
        Assert.Equal("Exalted Orb", exalted.DisplayName);
        Assert.Equal(0.0025m, exalted.DivineValue);
        Assert.Contains("Orb of Augmentation", result.Entries.Select(item => item.DisplayName));
    }

    [Fact]
    public void Poe2DbParser_ConvertsExchangeRowsToDivineValuesAndAliasesEnglishNames()
    {
        var result = Poe2DbPriceSource.ParseHtml(Poe2DbFixture);

        var exalted = Assert.Single(result.Entries, item => item.ItemId == "exalted");
        Assert.Equal(0.0025m, exalted.DivineValue);
        Assert.Contains("Exalted Orb", exalted.Aliases);
        Assert.Equal(400m, result.ExaltedPerDivine);
        Assert.Equal(0.5m, Assert.Single(result.Entries, item => item.ItemId == "orb-of-augmentation").DivineValue);
    }

    [Theory]
    [InlineData("gcp", "Gemcutter's Prism")]
    [InlineData("annul", "Orb of Annulment")]
    [InlineData("alch", "Orb of Alchemy")]
    [InlineData("scrap", "Armourer's Scrap")]
    [InlineData("chaos", "Chaos Orb")]
    [InlineData("chance", "Orb of Chance")]
    [InlineData("vaal", "Vaal Orb")]
    [InlineData("aug", "Orb of Augmentation")]
    public void Poe2DbParser_MapsCurrentAbbreviatedSlugsToCatalogNames(
        string slug,
        string expectedAlias)
    {
        var html = $"""
            <table><tr>
              <td><a href="Economy_{slug}">价格名称</a></td>
              <td>1 <a href="Economy_divine">神圣石</a> <i>swap</i> 1 <a href="Economy_{slug}">价格名称</a></td>
            </tr></table>
            """;

        var entry = Assert.Single(Poe2DbPriceSource.ParseHtml(html).Entries);

        Assert.Contains(expectedAlias, entry.Aliases);
    }

    [Fact]
    public void PriceSnapshot_MergesByPriorityAndKeepsPrimaryRate()
    {
        var snapshot = ExpeditionPriceSnapshot.Merge(
            DateTimeOffset.Parse("2026-08-02T00:00:00Z"),
            [
                new ExpeditionPriceSourceResult(
                    "ninja",
                    399.6m,
                    [new ExpeditionPriceEntry("a", "Shared", 1m)]),
                new ExpeditionPriceSourceResult(
                    "poe2db",
                    400m,
                    [
                        new ExpeditionPriceEntry("a", "Shared", 2m),
                        new ExpeditionPriceEntry("b", "Fallback", 3m)
                    ])
            ]);

        Assert.Equal(399.6m, snapshot.ExaltedPerDivine);
        Assert.True(snapshot.TryGet("a", "Shared", out var primary));
        Assert.Equal(1m, primary);
        Assert.True(snapshot.TryGet("b", "Fallback", out var fallback));
        Assert.Equal(3m, fallback);
    }

    [Fact]
    public void PriceSnapshot_RejectsCacheAfterTwentyFourHours()
    {
        var fetched = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        var snapshot = ExpeditionPriceSnapshot.Merge(
            fetched,
            [new ExpeditionPriceSourceResult(
                "cache",
                400m,
                [new ExpeditionPriceEntry("a", "A", 1m)])]);

        Assert.False(snapshot.IsExpired(fetched.AddHours(23).AddMinutes(59)));
        Assert.True(snapshot.IsExpired(fetched.AddHours(24)));
    }

    [Fact]
    public void Poe2ScoutParser_UsesCurrentPriceAlreadyExpressedInDivine()
    {
        const string json = """
            {
              "CurrentPage": 1,
              "Pages": 1,
              "Total": 1,
              "Items": [
                {
                  "ApiId": "ancient-rune-of-witchcraft",
                  "BaseItemTypeId": "Metadata/Items/SoulCores/RuneOfTheAncients9",
                  "Text": "Ancient Rune of Witchcraft",
                  "CurrentPrice": 0.026498003
                }
              ]
            }
            """;

        var result = Poe2ScoutPriceSource.ParseByCategory(json, 390.08265045284276m);

        Assert.Equal(390.08265045284276m, result.ExaltedPerDivine);
        var rune = Assert.Single(result.Entries);
        Assert.Equal(0.026498003m, rune.DivineValue);
        Assert.Equal("Metadata/Items/SoulCores/RuneOfTheAncients9", rune.ItemId);
        Assert.Contains("ancient-rune-of-witchcraft", rune.Aliases);
    }

    [Fact]
    public async Task Poe2ScoutFetch_RequestsCategoryPricesInDivine()
    {
        var handler = new ScoutResponseHandler();
        using var httpClient = new HttpClient(handler);
        var source = new Poe2ScoutPriceSource(
            httpClient,
            "https://example.test",
            "runes",
            ["runes"]);

        var result = await source.FetchAsync(CancellationToken.None);

        Assert.Equal(390.08265045284276m, result.ExaltedPerDivine);
        var rune = Assert.Single(result.Entries);
        Assert.Equal(0.026498003m, rune.DivineValue);
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, uri => uri.AbsolutePath.Contains("SnapshotPairs"));
        var categoryRequest = Assert.Single(
            handler.Requests,
            uri => uri.AbsolutePath.EndsWith("/Currencies/ByCategory", StringComparison.Ordinal));
        Assert.Contains("referenceCurrency=divine", categoryRequest.Query, StringComparison.Ordinal);
    }

    private const string NinjaFixture = """
        {
          "core": {
            "items": [
              { "id": "divine", "name": "Divine Orb", "image": "divine.png" },
              { "id": "exalted", "name": "Exalted Orb", "image": "exalted.png" },
              { "id": "aug", "name": "Orb of Augmentation", "image": "aug.png" }
            ],
            "rates": { "exalted": 399.6, "chaos": 8.1 },
            "primary": "divine",
            "secondary": "chaos"
          },
          "lines": [
            { "id": "divine", "primaryValue": 1.0 },
            { "id": "exalted", "primaryValue": 0.0025 },
            { "id": "aug", "primaryValue": 0.5 }
          ]
        }
        """;

    private const string Poe2DbFixture = """
        <table>
          <tr>
            <td><a href="Economy_divine"><img src="divine.png"/>神圣石</a><a href="Divine_Orb">Wiki</a></td>
            <td>8.1 <a href="Economy_chaos"><img src="chaos.png"/>混沌石</a> <i>swap</i> 1 <a href="Economy_divine"><img src="divine.png"/>神圣石</a></td>
          </tr>
          <tr>
            <td><a href="Economy_exalted"><img src="exalted.png"/>崇高石</a><a href="Exalted_Orb">Wiki</a></td>
            <td>1 <a href="Economy_divine"><img src="divine.png"/>神圣石</a> <i>swap</i> 400 <a href="Economy_exalted"><img src="exalted.png"/>崇高石</a></td>
          </tr>
          <tr>
            <td><a href="Economy_orb-of-augmentation"><img src="aug.png"/>增幅石</a><a href="Orb_of_Augmentation">Wiki</a></td>
            <td>0.5 <a href="Economy_divine"><img src="divine.png"/>神圣石</a> <i>swap</i> 1 <a href="Economy_orb-of-augmentation"><img src="aug.png"/>增幅石</a></td>
          </tr>
        </table>
        """;

    private const string ScoutReferencesFixture = """
        [
          {
            "ApiId": "exalted",
            "BaseItemTypeId": "Metadata/Items/Currency/CurrencyAddModToRare",
            "Text": "Exalted Orb",
            "RelativePrice": 1
          },
          {
            "ApiId": "divine",
            "BaseItemTypeId": "Metadata/Items/Currency/CurrencyModValues",
            "Text": "Divine Orb",
            "RelativePrice": 390.08265045284276
          }
        ]
        """;

    private const string ScoutCategoryFixture = """
        {
          "CurrentPage": 1,
          "Pages": 1,
          "Total": 1,
          "Items": [
            {
              "ApiId": "ancient-rune-of-witchcraft",
              "BaseItemTypeId": "Metadata/Items/SoulCores/RuneOfTheAncients9",
              "Text": "Ancient Rune of Witchcraft",
              "CurrentPrice": 0.026498003
            }
          ]
        }
        """;

    private sealed class ScoutResponseHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri
                ?? throw new InvalidOperationException("Request URI is required.");
            Requests.Add(uri);
            var json = uri.AbsolutePath.EndsWith(
                "/ReferenceCurrencies",
                StringComparison.Ordinal)
                ? ScoutReferencesFixture
                : uri.AbsolutePath.EndsWith(
                    "/Currencies/ByCategory",
                    StringComparison.Ordinal)
                    ? ScoutCategoryFixture
                    : null;
            var response = new HttpResponseMessage(
                json is null
                    ? System.Net.HttpStatusCode.NotFound
                    : System.Net.HttpStatusCode.OK);
            if (json is not null)
            {
                response.Content = new StringContent(
                    json,
                    System.Text.Encoding.UTF8,
                    "application/json");
            }

            return Task.FromResult(response);
        }
    }
}
