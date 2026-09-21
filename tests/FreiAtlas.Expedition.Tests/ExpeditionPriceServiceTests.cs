using FreiAtlas.Expedition;

namespace FreiAtlas.Expedition.Tests;

public sealed class ExpeditionPriceServiceTests
{
    [Fact]
    public void Options_DefaultToHourlyRefreshAndTwentyFourHourExpiry()
    {
        var options = new ExpeditionPriceServiceOptions("prices.json");

        Assert.Equal(TimeSpan.FromHours(1), options.RefreshInterval);
        Assert.Equal(TimeSpan.FromHours(24), options.CacheLifetime);
    }

    [Fact]
    public async Task RefreshAsync_AtomicallyPublishesMergedSnapshotAndWritesCache()
    {
        var cachePath = Path.Combine(
            Path.GetTempPath(),
            $"frei-atlas-price-{Guid.NewGuid():N}.json");
        try
        {
            await using var service = new ExpeditionPriceService(
                new ExpeditionPriceServiceOptions(cachePath),
                [
                    new TestSource("primary", 399.6m, new ExpeditionPriceEntry("a", "A", 1m)),
                    new TestSource("fallback", 400m, new ExpeditionPriceEntry("b", "B", 2m))
                ]);

            await service.RefreshAsync(force: true);

            Assert.True(service.Current.TryGet("a", "A", out var a));
            Assert.Equal(1m, a);
            Assert.True(service.Current.TryGet("b", "B", out var b));
            Assert.Equal(2m, b);
            Assert.Equal(399.6m, service.Current.ExaltedPerDivine);
            Assert.True(File.Exists(cachePath));
        }
        finally
        {
            if (File.Exists(cachePath)) File.Delete(cachePath);
            if (File.Exists(cachePath + ".tmp")) File.Delete(cachePath + ".tmp");
        }
    }

    [Fact]
    public async Task Constructor_LoadsOnlyUnexpiredCache()
    {
        var cachePath = Path.Combine(
            Path.GetTempPath(),
            $"frei-atlas-price-{Guid.NewGuid():N}.json");
        try
        {
            var snapshot = ExpeditionPriceSnapshot.Merge(
                DateTimeOffset.UtcNow,
                [new ExpeditionPriceSourceResult(
                    "cache",
                    400m,
                    [new ExpeditionPriceEntry("a", "A", 1m)])]);
            snapshot.Save(cachePath);

            await using var service = new ExpeditionPriceService(
                new ExpeditionPriceServiceOptions(cachePath),
                []);

            Assert.True(service.Current.TryGet("a", "A", out var value));
            Assert.Equal(1m, value);
        }
        finally
        {
            if (File.Exists(cachePath)) File.Delete(cachePath);
        }
    }

    [Fact]
    public async Task Current_StopsPublishingCacheAtTwentyFourHours()
    {
        var cachePath = Path.Combine(
            Path.GetTempPath(),
            $"frei-atlas-price-{Guid.NewGuid():N}.json");
        var fetchedAt = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        try
        {
            ExpeditionPriceSnapshot.Merge(
                    fetchedAt,
                    [new ExpeditionPriceSourceResult(
                        "cache",
                        400m,
                        [new ExpeditionPriceEntry("a", "A", 1m)])])
                .Save(cachePath);
            var clock = new ManualTimeProvider(fetchedAt);
            await using var service = new ExpeditionPriceService(
                new ExpeditionPriceServiceOptions(cachePath),
                [],
                clock);

            Assert.Equal(1, service.Current.ItemCount);
            clock.Advance(TimeSpan.FromHours(24));

            Assert.Equal(0, service.Current.ItemCount);
        }
        finally
        {
            if (File.Exists(cachePath)) File.Delete(cachePath);
        }
    }

    private sealed class TestSource(
        string sourceName,
        decimal rate,
        ExpeditionPriceEntry entry) : IExpeditionPriceSource
    {
        public string Name => sourceName;

        public Task<ExpeditionPriceSourceResult> FetchAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ExpeditionPriceSourceResult(sourceName, rate, [entry]));
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan interval)
            => utcNow += interval;
    }
}
