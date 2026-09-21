using System.Security.Cryptography;
using System.Text.Json;
using FreiAtlas.Platform.Windows.Overlay;

namespace FreiAtlas.Atlas.Tests;

public sealed class ExpeditionRuneIconCacheTests
{
    [Fact]
    public void EmbeddedManifestContainsEveryRuneIndexAndBaitAliasesPower()
    {
        var assembly = typeof(ExpeditionRuneIconCache).Assembly;
        var manifestName = Assert.Single(
            assembly.GetManifestResourceNames(),
            name => name.EndsWith(".Runeshape.manifest.json", StringComparison.Ordinal));
        using var manifestStream = assembly.GetManifestResourceStream(manifestName);
        Assert.NotNull(manifestStream);
        using var document = JsonDocument.Parse(manifestStream);
        var entries = document.RootElement.GetProperty("entries")
            .EnumerateArray()
            .ToArray();

        Assert.Equal(34, entries.Length);
        Assert.Equal(Enumerable.Range(0, 34), entries.Select(entry => entry.GetProperty("index").GetInt32()));
        Assert.Equal(32, entries[33].GetProperty("aliasOf").GetInt32());

        var power = ReadResourceBytes(assembly, "32-Power.png");
        var bait = ReadResourceBytes(assembly, "33-Bait.png");
        Assert.Equal(SHA256.HashData(power), SHA256.HashData(bait));
    }

    [Fact]
    public void CacheDecodesAllThirtyFourEmbeddedPngs()
    {
        using var cache = new ExpeditionRuneIconCache();

        Assert.Equal(34, cache.Count);
        Assert.True(cache.Contains(0));
        Assert.True(cache.Contains(33));
        Assert.False(cache.Contains(34));
    }

    private static byte[] ReadResourceBytes(
        System.Reflection.Assembly assembly,
        string suffix)
    {
        var resourceName = Assert.Single(
            assembly.GetManifestResourceNames(),
            name => name.EndsWith($".Runeshape.{suffix}", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
