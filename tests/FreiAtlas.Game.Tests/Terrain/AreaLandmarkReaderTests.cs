using FreiAtlas.Core.Area;
using FreiAtlas.Game.Terrain;

namespace FreiAtlas.Game.Tests.Terrain;

public sealed class AreaLandmarkReaderTests
{
    private const string BossPath = "Metadata/Terrain/Test/BossArena_01.tdt";

    [Fact]
    public void Read_MatchesIncursionWaygateAnywhereInTilePathAndClustersIt()
    {
        var reader = AreaLandmarkReader.FromJson(
            """
            {
              "*": {
                "*WaygateDevice*": "神庙"
              }
            }
            """);
        const string tilePath =
            "Metadata/Terrain/Leagues/Incursion/Tiles/Features/Waygates/AncientWaygateDevice_01.tdt";
        AreaTerrainTile[] tiles =
        [
            new(2, 3, tilePath),
            new(3, 4, tilePath)
        ];

        var landmark = Assert.Single(reader.Read("MapTest", tiles));

        Assert.Equal(AreaLandmarkKind.Incursion, landmark.Kind);
        Assert.Equal("神庙", landmark.DisplayName);
        Assert.Equal(2, landmark.TileCount);
        Assert.Equal(new System.Numerics.Vector2(57.5f, 80.5f), landmark.GridPosition);
    }

    [Fact]
    public void Read_ClustersNearbyTilesAndPublishesGridCentroid()
    {
        var reader = AreaLandmarkReader.FromJson(
            """
            {
              "MapTest": {
                "Metadata/Terrain/Test/BossArena_01.tdtx:2-y:1": "The Test Tyrant"
              }
            }
            """);
        AreaTerrainTile[] tiles =
        [
            new(0, 0, BossPath),
            new(2, 1, BossPath),
            new(10, 10, BossPath)
        ];

        var landmarks = reader.Read("MapTest", tiles);

        Assert.Equal(2, landmarks.Count);
        var clustered = Assert.Single(landmarks, landmark => landmark.TileCount == 2);
        Assert.Equal(23f, clustered.GridPosition.X);
        Assert.Equal(11.5f, clustered.GridPosition.Y);
        Assert.Equal("The Test Tyrant", clustered.DisplayName);
    }

    [Fact]
    public void Read_UsesAreaRulesBeforeGlobalRules()
    {
        var reader = AreaLandmarkReader.FromJson(
            """
            {
              "MapTest": {
                "BossArena": "Area Boss"
              },
              "*": {
                "BossArena": "Global Boss",
                "Waypoint": "Waypoint"
              }
            }
            """);
        AreaTerrainTile[] tiles =
        [
            new(1, 1, BossPath),
            new(5, 5, "Metadata/Terrain/Test/Waypoint_01.tdt")
        ];

        var landmarks = reader.Read("MapTest", tiles);

        Assert.Contains(landmarks, landmark => landmark.DisplayName == "Area Boss");
        Assert.Contains(landmarks, landmark => landmark.DisplayName == "Waypoint");
        Assert.DoesNotContain(landmarks, landmark => landmark.DisplayName == "Global Boss");
    }

    [Fact]
    public void Read_DoesNotPublishDecorativeTilesOutsideCatalog()
    {
        var reader = AreaLandmarkReader.FromJson(
            """
            {
              "MapTest": {
                "BossArena": "Boss"
              }
            }
            """);
        AreaTerrainTile[] tiles =
        [
            new(1, 1, "Metadata/Terrain/Test/DecorativeVaultDoor.tdt")
        ];

        Assert.Empty(reader.Read("MapTest", tiles));
    }

    [Fact]
    public void Read_LeavesForeignGlobalBossTerrainAsHint()
    {
        var reader = AreaLandmarkReader.FromJson(
            """
            {
              "*": {
                "Metadata/Terrain/Maps/Plantation/Tiles/Plantaton_Boss_01.tdtx:[]": "Boss",
                "Metadata/Terrain/Woods/Village/RoadFields/Fills/Fill_Wildcard_01_blank.tdtx:[]": "Boss"
              }
            }
            """);
        AreaTerrainTile[] tiles =
        [
            new(
                1,
                1,
                "Metadata/Terrain/Maps/Plantation/Tiles/Plantaton_Boss_01.tdt"),
            new(
                2,
                2,
                "Metadata/Terrain/Woods/Village/RoadFields/Fills/Fill_Wildcard_01_blank.tdt")
        ];

        var landmarks = reader.Read("MapPlantation", tiles);

        Assert.Single(landmarks, item => item.Kind == AreaLandmarkKind.BossArena);
        Assert.Single(landmarks, item => item.Kind != AreaLandmarkKind.BossArena);
    }

    [Fact]
    public void Read_DoesNotTrustAreaTokenEmbeddedInsideDifferentMapDirectory()
    {
        var reader = AreaLandmarkReader.FromJson(
            """
            {
              "*": {
                "Metadata/Terrain/Maps/SteamingSprings/Tiles/SteamingSprings_boss_*.tdtx:[]": "Boss"
              }
            }
            """);
        AreaTerrainTile[] tiles =
        [
            new(
                1,
                1,
                "Metadata/Terrain/Maps/SteamingSprings/Tiles/SteamingSprings_boss_01.tdt")
        ];

        var landmark = Assert.Single(reader.Read("MapSpring_", tiles));

        Assert.NotEqual(AreaLandmarkKind.BossArena, landmark.Kind);
    }

    [Fact]
    public void Read_DoesNotTrustBossRuleFromBaseMapForNoBossVariant()
    {
        var reader = AreaLandmarkReader.FromJson(
            """
            {
              "*": {
                "Metadata/Terrain/Maps/LostTowers/Tiles/PillarArena01.tdtx:[]": "Boss"
              }
            }
            """);
        AreaTerrainTile[] tiles =
        [
            new(
                1,
                1,
                "Metadata/Terrain/Maps/LostTowers/Tiles/PillarArena01.tdt")
        ];

        var landmark = Assert.Single(reader.Read("MapLostTowers_NoBoss", tiles));

        Assert.NotEqual(AreaLandmarkKind.BossArena, landmark.Kind);
    }

    [Fact]
    public void Read_PreservesDistinctBossLandmarksInSameMapDirectory()
    {
        var reader = AreaLandmarkReader.FromJson(
            """
            {
              "*": {
                "Metadata/Terrain/Maps/TwinBoss/Tiles/BossArena_Left.tdtx:[]": "Boss",
                "Metadata/Terrain/Maps/TwinBoss/Tiles/BossArena_Right.tdtx:[]": "Boss"
              }
            }
            """);
        AreaTerrainTile[] tiles =
        [
            new(
                1,
                1,
                "Metadata/Terrain/Maps/TwinBoss/Tiles/BossArena_Left.tdt"),
            new(
                20,
                20,
                "Metadata/Terrain/Maps/TwinBoss/Tiles/BossArena_Right.tdt")
        ];

        var landmarks = reader.Read("MapTwinBoss", tiles);

        Assert.Equal(2, landmarks.Count);
        Assert.All(
            landmarks,
            landmark => Assert.Equal(AreaLandmarkKind.BossArena, landmark.Kind));
        Assert.Equal(2, landmarks.Select(landmark => landmark.LandmarkId).Distinct().Count());
    }

    [Fact]
    public void Read_TrustsAreaScopedBossRuleWithoutPathInference()
    {
        var reader = AreaLandmarkReader.FromJson(
            """
            {
              "MapChannel": {
                "Metadata/Terrain/Shared/Tiles/CentrePattern_01.tdtx:[]": "Boss"
              }
            }
            """);
        AreaTerrainTile[] tiles =
        [
            new(
                3,
                4,
                "Metadata/Terrain/Shared/Tiles/CentrePattern_01.tdt")
        ];

        var landmark = Assert.Single(reader.Read("MapChannel", tiles));

        Assert.Equal(AreaLandmarkKind.BossArena, landmark.Kind);
    }

    [Fact]
    public void Read_PreservesNonBossLandmarkClassifications()
    {
        var reader = AreaLandmarkReader.FromJson(
            """
            {
              "MapTest": {
                "Metadata/Terrain/Test/Waypoint_01.tdtx:[]": "Waypoint",
                "Metadata/Terrain/Test/Entrance_01.tdtx:[]": "Entrance",
                "Metadata/Terrain/Test/Shrine_01.tdtx:[]": "Shrine"
              }
            }
            """);
        AreaTerrainTile[] tiles =
        [
            new(1, 1, "Metadata/Terrain/Test/Waypoint_01.tdt"),
            new(4, 4, "Metadata/Terrain/Test/Entrance_01.tdt"),
            new(7, 7, "Metadata/Terrain/Test/Shrine_01.tdt")
        ];

        var landmarks = reader.Read("MapTest", tiles);

        Assert.Contains(landmarks, item => item.Kind == AreaLandmarkKind.Waypoint);
        Assert.Contains(landmarks, item => item.Kind == AreaLandmarkKind.Transition);
        Assert.Contains(landmarks, item => item.Kind == AreaLandmarkKind.Mechanic);
    }

    [Fact]
    public void Read_MatchesPoeFixerPathWildcardAsPrefix()
    {
        var reader = AreaLandmarkReader.FromJson(
            """
            {
              "*": {
                "Metadata/Terrain/Maps/Slick/Tiles/SlickArena*.tdtx:[]": "Boss"
              }
            }
            """);
        AreaTerrainTile[] tiles =
        [
            new(4, 5, "Metadata/Terrain/Maps/Slick/Tiles/SlickArena01.tdt")
        ];

        var landmark = Assert.Single(reader.Read("MapSlick", tiles));

        Assert.Equal(AreaLandmarkKind.BossArena, landmark.Kind);
        Assert.Equal("Boss", landmark.DisplayName);
    }

    [Theory]
    [InlineData("Metadata/Terrain/Maps/Plantation/Tiles/Plantaton_Boss_01.tdtx:[]")]
    [InlineData("Metadata/Terrain/Maps/Plantation/Tiles/Plantaton_Boss_01.tdtx:[12]")]
    public void Read_IgnoresPoeFixerLayoutSuffixAndMatchesRuntimeTileExtension(
        string rulePath)
    {
        var reader = AreaLandmarkReader.FromJson(
            $$"""
            {
              "*": {
                "{{rulePath}}": "Boss"
              }
            }
            """);
        AreaTerrainTile[] tiles =
        [
            new(
                8,
                9,
                "Metadata/Terrain/Maps/Plantation/Tiles/Plantaton_Boss_01.tdt")
        ];

        Assert.Single(reader.Read("MapPlantation", tiles));
    }

    [Fact]
    public void Read_DoesNotMatchCatalogPathInsideDifferentTilePath()
    {
        var reader = AreaLandmarkReader.FromJson(
            """
            {
              "*": {
                "Metadata/Terrain/Maps/Plantation/Tiles/Plantaton_Boss_01.tdtx:[]": "Boss"
              }
            }
            """);
        AreaTerrainTile[] tiles =
        [
            new(
                8,
                9,
                "Metadata/Terrain/Maps/Fake/Tiles/Metadata/Terrain/Maps/Plantation/Tiles/Plantaton_Boss_01.tdt")
        ];

        Assert.Empty(reader.Read("MapFake", tiles));
    }

    [Fact]
    public void EmbeddedCatalog_ContainsPlantationBossRule()
    {
        var reader = AreaLandmarkReader.LoadEmbedded();
        AreaTerrainTile[] tiles =
        [
            new(
                8,
                9,
                "Metadata/Terrain/Maps/Plantation/Tiles/Plantaton_Boss_01.tdt")
        ];

        var landmark = Assert.Single(reader.Read("MapPlantation", tiles));

        Assert.Equal(AreaLandmarkKind.BossArena, landmark.Kind);
        Assert.Equal("Boss", landmark.DisplayName);
    }

    [Theory]
    [InlineData(
        "MapChannel",
        "Metadata/Terrain/Maps/Channel/Tiles/CentrePattern_01.tdt")]
    [InlineData(
        "MapCreek",
        "Metadata/Terrain/Woods/OldForestWoods/CvMM_01.tdt")]
    [InlineData(
        "MapHeadland",
        "Metadata/Terrain/Desert/Waterfront/Gates_thinwall_St_01.tdt")]
    [InlineData(
        "MapVaalCity",
        "Metadata/Terrain/forced_blank.tdt")]
    public void EmbeddedCatalog_ContainsAreaScopedBossRule(
        string areaCode,
        string tilePath)
    {
        var reader = AreaLandmarkReader.LoadEmbedded();
        AreaTerrainTile[] tiles = [new(6, 7, tilePath)];

        var landmark = Assert.Single(reader.Read(areaCode, tiles));

        Assert.Equal(AreaLandmarkKind.BossArena, landmark.Kind);
        Assert.Equal("Boss", landmark.DisplayName);
    }

    [Fact]
    public void EmbeddedCatalog_ContainsSteamingSpringsBossRule()
    {
        var reader = AreaLandmarkReader.LoadEmbedded();
        AreaTerrainTile[] tiles =
        [
            new(
                12,
                34,
                "Metadata/Terrain/Maps/SteamingSprings/Tiles/SteamingSprings_boss_01.tdt")
        ];

        var landmark = Assert.Single(reader.Read("MapSteamingSprings", tiles));

        Assert.Equal(AreaLandmarkKind.BossArena, landmark.Kind);
        Assert.Equal("Boss", landmark.DisplayName);
        Assert.Equal(new System.Numerics.Vector2(276, 782), landmark.GridPosition);
    }
}
