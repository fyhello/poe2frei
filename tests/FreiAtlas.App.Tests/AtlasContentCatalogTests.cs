using FreiAtlas.App.Content;

namespace FreiAtlas.App.Tests;

public sealed class AtlasContentCatalogTests
{
    [Fact]
    public void Embedded_HasSixtySixUniqueEntriesWithLocalIcons()
    {
        var catalog = AtlasContentCatalog.Embedded;

        Assert.Equal(66, catalog.Entries.Count);
        Assert.Equal(
            66,
            catalog.Entries.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(catalog.Entries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Id));
            Assert.False(string.IsNullOrWhiteSpace(entry.DisplayName));
            Assert.Equal($"assets/icons/{entry.Id}.png", entry.IconPath);
        });
    }

    [Fact]
    public void Embedded_UsesConfirmedChineseNamesForPrimaryContents()
    {
        var entries = AtlasContentCatalog.Embedded.Entries
            .ToDictionary(entry => entry.Id, StringComparer.Ordinal);

        Assert.Equal("强力地图首领", entries["AtlasIconContentMapBoss"].DisplayName);
        Assert.Equal("先祖秘藏", entries["AtlasIconContentExpedition"].DisplayName);
        Assert.Equal("裂隙", entries["AtlasIconContentBreach"].DisplayName);
    }
}
