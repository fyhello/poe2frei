using System.Text.Json;
using FreiAtlas.Platform.Windows.Overlay;

namespace FreiAtlas.Atlas.Tests;

public sealed class ExpeditionRewardIconCacheTests
{
    [Fact]
    public void EmbeddedManifestCoversExactAndDescriptiveRewards()
    {
        using var cache = new ExpeditionRewardIconCache();

        Assert.Equal(231, cache.ExactItemCount);
        Assert.Equal(29, cache.DescriptiveRewardCount);
        Assert.True(cache.TryResolve(
            "Metadata/Items/Currency/CurrencyVerisiumAlloy2",
            "Adaptive Alloy",
            true,
            out _));
        Assert.True(cache.TryResolve(
            string.Empty,
            "Uncut Skill Gem",
            false,
            out _));
        Assert.True(cache.TryResolve(
            string.Empty,
            "[Rarity|Unique] Body Armour",
            false,
            out _));
        Assert.False(cache.TryResolve("missing", "missing", true, out _));
    }

    [Fact]
    public void EmbeddedManifestUsesOnlyLocalExistingResources()
    {
        var assembly = typeof(ExpeditionRewardIconCache).Assembly;
        var manifestName = Assert.Single(
            assembly.GetManifestResourceNames(),
            name => name.EndsWith(
                ".ExpeditionRewards.manifest.json",
                StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(manifestName);
        Assert.NotNull(stream);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var files = root.GetProperty("exactItems")
            .EnumerateObject()
            .Select(property => property.Value.GetString())
            .Concat(root.GetProperty("descriptiveRewards")
                .EnumerateObject()
                .Select(property => property.Value.GetString()))
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(files);
        Assert.DoesNotContain(files, file =>
            file.Contains("http://", StringComparison.OrdinalIgnoreCase)
            || file.Contains("https://", StringComparison.OrdinalIgnoreCase));
        Assert.All(files, file => Assert.Single(
            assembly.GetManifestResourceNames(),
            name => name.EndsWith(
                $".ExpeditionRewards.{file}",
                StringComparison.Ordinal)));
    }

    [Fact]
    public void ManifestReusesFilesForRewardsWithTheSameOfficialArtwork()
    {
        using var cache = new ExpeditionRewardIconCache();

        Assert.True(cache.TryResolve(
            "Metadata/Items/Currency/CurrencyAddModToMagic",
            "Orb of Augmentation",
            true,
            out var normal));
        Assert.True(cache.TryResolve(
            "Metadata/Items/Currency/CurrencyAddModToMagic3",
            "Perfect Orb of Augmentation",
            true,
            out var perfect));
        Assert.Equal(normal, perfect);
        Assert.True(cache.UniqueResourceCount < 231 + 29);
    }
}
