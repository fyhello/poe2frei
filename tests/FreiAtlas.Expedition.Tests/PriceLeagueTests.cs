using FreiAtlas.Core.Settings;
using FreiAtlas.Expedition;

namespace FreiAtlas.Expedition.Tests;

public sealed class PriceLeagueTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "frei-leagues-" + Guid.NewGuid().ToString("N"));
    private string Cache => Path.Combine(_directory, "prices.json");
    private const string Aldur = "Runes of Aldur";

    [Fact]
    public void Catalog_KeepsBothCurrentLeaguesAndPermanentEvenWhenNotCurrent()
    {
        var leagues = PriceLeagueCatalog.Parse("""
            [{"Value":"Forbidden Rites","ShortName":"forbiddenrites","IsCurrent":true},
             {"Value":"HC Forbidden Rites","IsCurrent":true},
             {"Value":"Standard","ShortName":"standard","IsCurrent":false},
             {"Value":"Runes of Aldur","ShortName":"runes","IsCurrent":true},
             {"Value":"Old League","IsCurrent":false},
             {"Value":"Future League","ShortName":"future","IsCurrent":true}]
            """);
        Assert.Equal(["Forbidden Rites", Aldur, "Future League", "Standard"], leagues.Select(item => item.Id));
        Assert.Equal("禁忌仪式", leagues[0].DisplayName);
        Assert.Equal("永久区", leagues[^1].DisplayName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" Standard")]
    [InlineData("Standard\n")]
    public void Settings_RejectInvalidIdentity(string id)
        => Assert.Throws<ArgumentException>(() => new PriceSettings(id).Validate());

    [Theory]
    [InlineData(Aldur, 2, 500)]
    [InlineData("Forbidden Rites", 3, 250)]
    [InlineData("Standard", 4, 450)]
    public async Task Refresh_RestoresOnlySelectedMarketIncludingItsExchangeRate(string league, int price, int rate)
    {
        await using (var service = Create(league, _ => new Source(() => Result(league, price, rate))))
        {
            await service.RefreshAsync(true);
            Assert.Equal(league, service.Current.LeagueId);
            Assert.Equal(rate, service.Current.ExaltedPerDivine);
            Assert.True(service.Current.TryGet("item", "物品", out var value));
            Assert.Equal(price, value);
            Assert.NotEqual(service.GetCachePath(Aldur), service.GetCachePath("Standard"));
        }
        await using var restarted = Create(league, _ => new Source(() => throw new HttpRequestException()));
        await restarted.RefreshAsync(true);
        Assert.Equal(league, restarted.Current.LeagueId);
        Assert.Equal(rate, restarted.Current.ExaltedPerDivine);
        Assert.Contains("有效缓存", restarted.Status.Message);
    }

    [Fact]
    public async Task Switch_RejectsLateOldRequestAndDoesNotWaitForIt()
    {
        var oldResponse = new TaskCompletionSource<ExpeditionPriceSourceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = Create(Aldur, id => id == Aldur
            ? new AsyncSource(() => { started.TrySetResult(); return oldResponse.Task; })
            : new Source(() => Result(id, 9, 300)));
        var oldRefresh = service.RefreshAsync(true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.SelectLeague("Standard");
        await service.RefreshAsync(true).WaitAsync(TimeSpan.FromSeconds(5));
        oldResponse.SetResult(Result(Aldur, 1, 600));
        await oldRefresh;
        Assert.Equal("Standard", service.Current.LeagueId);
        Assert.Equal(300, service.Current.ExaltedPerDivine);
        Assert.True(service.Current.TryGet("item", "物品", out var value));
        Assert.Equal(9, value);
        Assert.False(File.Exists(service.GetCachePath(Aldur)));
    }

    [Fact]
    public async Task Switch_ToOfflineMarketClearsPreviousPrices()
    {
        await using var service = Create(Aldur, id => new Source(() => id == Aldur
            ? Result(id, 2, 500) : throw new HttpRequestException()));
        await service.RefreshAsync(true);
        service.SelectLeague("Standard");
        Assert.Equal(0, service.Current.ItemCount);
        await service.RefreshAsync(true);
        Assert.Equal("Standard", service.Status.LeagueId);
        Assert.Equal(0, service.Current.ItemCount);
        Assert.Contains("暂无", service.Status.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(Aldur)]
    public async Task Cache_RejectsUnlabelledOrOtherMarketEvenInCorrectFile(string cachedLeague)
    {
        await using var paths = Create("Standard", _ => new Source(() => throw new HttpRequestException()));
        var snapshot = ExpeditionPriceSnapshot.Merge(DateTimeOffset.UtcNow,
            [Result(cachedLeague, 10, 300)], cachedLeague);
        snapshot.Save(paths.GetCachePath("Standard"));
        await using var service = Create("Standard", _ => new Source(() => throw new HttpRequestException()));
        Assert.Equal(0, service.Current.ItemCount);
    }

    [Fact]
    public async Task Refresh_RejectsWrongSourceMarketAndMissingExchangeRate()
    {
        await using var wrong = Create("Standard", _ => new Source(() => Result(Aldur, 1, 300)));
        await wrong.RefreshAsync(true);
        Assert.Equal(0, wrong.Current.ItemCount);
        Assert.Contains("赛季身份不匹配", Assert.Single(wrong.Status.Sources).Message);
        await using var noRate = Create("Standard", id => new Source(() => Result(id, 1, 0)));
        await noRate.RefreshAsync(true);
        Assert.Equal(0, noRate.Current.ItemCount);
        Assert.Null(noRate.Status.ExaltedPerDivine);
    }

    [Fact]
    public async Task Cache_ExpiresWithoutBorrowingAnotherMarketsPrices()
    {
        var clock = new ManualClock();
        await using var service = new ExpeditionPriceService(
            new ExpeditionPriceServiceOptions(Cache, leagueId: "Standard"),
            id => [new Source(() => Result(id, 1, 400))], clock);
        await service.RefreshAsync(true);
        clock.Now += TimeSpan.FromHours(24);
        Assert.Equal(0, service.Current.ItemCount);
        Assert.Null(service.Status.FetchedAtUtc);
    }

    [Fact]
    public async Task Dispose_CancelsRunningRequestAndStopsBackgroundLoop()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<ExpeditionPriceSourceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Create(Aldur, _ => new AsyncSource(() => { started.TrySetResult(); return pending.Task; }));
        await service.StartAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(service.IsRunning);
        pending.SetResult(Result(Aldur, 2, 500));
    }

    private ExpeditionPriceService Create(string league, Func<string, IExpeditionPriceSource> source)
        => new(new ExpeditionPriceServiceOptions(Cache, leagueId: league), id => [source(id)]);

    private static ExpeditionPriceSourceResult Result(string league, decimal price, decimal rate)
        => new("测试源", rate, [new("item", "物品", price)]) { LeagueId = league };

    private sealed class Source(Func<ExpeditionPriceSourceResult> fetch) : IExpeditionPriceSource
    {
        public string Name => "测试源";
        public Task<ExpeditionPriceSourceResult> FetchAsync(CancellationToken cancellationToken) => Task.FromResult(fetch());
    }

    private sealed class AsyncSource(Func<Task<ExpeditionPriceSourceResult>> fetch) : IExpeditionPriceSource
    {
        public string Name => "测试源";
        public Task<ExpeditionPriceSourceResult> FetchAsync(CancellationToken cancellationToken) => fetch();
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
