using FreiAtlas.App.Settings;
using FreiAtlas.Core.Settings;

namespace FreiAtlas.App.Tests;

public sealed class PriceSettingsStoreTests
{
    [Theory]
    [InlineData("Runes of Aldur")]
    [InlineData("Forbidden Rites")]
    [InlineData("Standard")]
    public async Task Save_ReloadsManualChoiceAndPreservesOtherSettings(string league)
    {
        var path = Path.Combine(Path.GetTempPath(), "frei-price-settings-" + Guid.NewGuid() + ".json");
        try
        {
            await using (var store = await AtlasSettingsStore.CreateAsync(path, []))
            {
                store.Update(store.Current with { Theme = AtlasTheme.Light });
                store.UpdatePrices(new PriceSettings(league));
                await store.FlushAsync();
            }
            await using var loaded = await AtlasSettingsStore.CreateAsync(path, []);
            Assert.Equal(league, loaded.Prices.LeagueId);
            Assert.Equal(AtlasTheme.Light, loaded.Current.Theme);
            Assert.False(loaded.QuickAssist.Health.Enabled);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task OldSettings_DefaultToExplicitAldurChoice()
    {
        var path = Path.Combine(Path.GetTempPath(), "frei-old-prices-" + Guid.NewGuid() + ".json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"theme\":\"Light\"}");
            await using var store = await AtlasSettingsStore.CreateAsync(path, []);
            Assert.Equal("Runes of Aldur", store.Prices.LeagueId);
            Assert.Equal(AtlasTheme.Light, store.Current.Theme);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
