using System.Text;
using FreiAtlas.Game.Content;

namespace FreiAtlas.Game.Tests.Content;

public sealed class ExpeditionRecipeCatalogTests
{
    [Fact]
    public void Resolve_PreservesCatalogRowOrderAndRewardQuantity()
    {
        var catalog = LoadCatalog(TestCatalogJson);

        var recipes = catalog.Resolve(
            anchorRuneIndex: 3,
            anchorPosition: 0,
            holeCount: 4,
            isUnique: false,
            areaLevel: 80);

        Assert.Equal(["full-first", "partial-second"], recipes.Select(item => item.RecipeId));
        Assert.Equal(5, recipes[0].Rewards[0].Quantity);
        Assert.Equal("First Currency", recipes[0].Rewards[0].DisplayName);
        Assert.False(recipes[1].Rewards[0].IsExactItem);
        Assert.Equal(["Tempest", "Cold"], recipes[0].Runes.Select(item => item.DisplayName));
    }

    [Fact]
    public void Resolve_AppliesLevelAndPartialRecipeGates()
    {
        var catalog = LoadCatalog(TestCatalogJson);

        var belowPartialLevel = catalog.Resolve(3, 0, 4, false, 70);
        var wrongAnchorPosition = catalog.Resolve(3, 1, 4, false, 80);

        Assert.Equal(["full-first"], belowPartialLevel.Select(item => item.RecipeId));
        Assert.Empty(wrongAnchorPosition);
    }

    [Fact]
    public void Resolve_DoesNotGuessRecipesWhenAnchorDecodeFailed()
    {
        var catalog = LoadCatalog(TestCatalogJson);

        Assert.Empty(catalog.Resolve(-1, 0, 4, false, 80));
        Assert.Equal(2, catalog.Resolve(-1, -1, 4, true, 80).Length);
    }

    private static ExpeditionRecipeCatalog LoadCatalog(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return ExpeditionRecipeCatalog.Load(stream);
    }

    private const string TestCatalogJson = """
        {
          "runes": { "1": "Cold", "3": "Tempest" },
          "recipes": [
            {
              "row": 20,
              "id": "full-first",
              "size": 4,
              "runeIdx": [3, 1],
              "runes": ["Tempest", "Cold"],
              "reward": {
                "id": "Metadata/Items/Currency/First",
                "name": "First Currency"
              },
              "rewardCount": 5,
              "description": "",
              "minLevel": 1,
              "maxLevel": 100
            },
            {
              "row": 21,
              "id": "partial-second",
              "size": 2,
              "runeIdx": [3, 1],
              "runes": ["Tempest", "Cold"],
              "reward": null,
              "rewardCount": 1,
              "description": "Random reward",
              "minLevel": 1,
              "maxLevel": 100
            },
            {
              "row": 22,
              "id": "wrong-level",
              "size": 4,
              "runeIdx": [3, 1],
              "runes": ["Tempest", "Cold"],
              "reward": { "id": "wrong", "name": "Wrong" },
              "rewardCount": 1,
              "description": "",
              "minLevel": 90,
              "maxLevel": 100
            }
          ],
          "runeWeights": [
            { "rune": 3, "pos": 1, "size": 2, "minLevel": 75 }
          ]
        }
        """;
}
