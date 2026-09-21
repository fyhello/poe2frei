using FreiAtlas.Atlas.Metadata;
using FreiAtlas.Core.Atlas;

namespace FreiAtlas.Atlas.Tests;

public sealed class AtlasMetadataTests
{
    [Fact]
    public void EmbeddedCatalog_PreservesReferenceMapDimensions()
    {
        var catalog = AtlasMetadataCatalog.Embedded;

        Assert.Equal(176, catalog.MapCount);
        Assert.True(catalog.TryGetMap("MapAlpineRidge", out var map));
        Assert.Equal("Alpine Ridge", map.Name);
        Assert.Equal("normal", map.Type);
        Assert.Equal("map", map.Group);
    }

    [Fact]
    public void EmbeddedCatalog_PreservesMapDataTags()
    {
        var catalog = AtlasMetadataCatalog.Embedded;

        Assert.True(catalog.TryGetMap("RitualLeagueBoss", out var map));
        Assert.Contains("boss", map.Tags);
        Assert.Contains("ritual", map.Tags);
    }

    [Theory]
    [InlineData("MapUberBoss_StoneCitadel", "Citadel")]
    [InlineData("MapUberBoss_Monolith", "Boss")]
    [InlineData("Map_HildaCampsite", "Unique")]
    [InlineData("MapWildwood", "Unique")]
    [InlineData("MapUniqueMerchant01_Oasis", "Merchant")]
    [InlineData("MapUniqueCastaway", "Unique")]
    [InlineData("MapPrecursorTower", "Tower")]
    [InlineData("MapAlpineRidge", "Normal")]
    public void KindClassifier_MatchesReferencePriority(
        string mapCode,
        string expectedKind)
    {
        Assert.Equal(
            expectedKind,
            AtlasMapKindClassifier.Classify(mapCode));
    }

    [Fact]
    public void EmbeddedCatalog_PreservesContentIconAndDescription()
    {
        var catalog = AtlasMetadataCatalog.Embedded;

        Assert.True(
            catalog.TryGetContentByDisplayName(
                "Powerful Map Boss",
                out var content));
        Assert.Equal("Powerful Map Boss", content.DisplayName);
        Assert.Equal("AtlasIconContentMapBoss", content.IconId);
        Assert.Contains("Map Boss", content.Description);
    }

    [Fact]
    public void NodeSnapshot_ExposesIndependentMetadataLayers()
    {
        var node = new AtlasNodeSnapshot(
            new AtlasGridPos(1, 2),
            "MapAlpineRidge",
            "Alpine Ridge",
            10,
            20,
            false,
            true,
            false,
            false,
            [],
            [])
        {
            Kind = "Normal",
            MapType = "normal",
            MapGroup = "map",
            MapDataTags = ["lineage", "craft"]
        };

        Assert.Equal("Alpine Ridge", node.DisplayName);
        Assert.Equal("Normal", node.Kind);
        Assert.Equal("normal", node.MapType);
        Assert.Equal("map", node.MapGroup);
        Assert.Equal(["lineage", "craft"], node.MapDataTags);
    }
}
