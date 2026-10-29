using System.Reflection;
using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 阶段 1：基线安全与物价前置条件的回归测试。
///
/// 这一轮修的是「恢复纯净会误删第三方工具补丁」和「旧版本基线被用于新客户端」，
/// 两者都会让用户的客户端无法启动，必须用测试锁死。
/// </summary>
public sealed class Stage1BaselineSafetyTests : IDisposable
{
    private readonly string _root;
    private readonly string _gameDir;
    private readonly string _backupDir;

    public Stage1BaselineSafetyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "frei-stage1-" + Guid.NewGuid().ToString("N")[..8]);
        _gameDir = Path.Combine(_root, "game");
        _backupDir = Path.Combine(_root, "backup");
        Directory.CreateDirectory(Path.Combine(_gameDir, "logs"));
        Directory.CreateDirectory(_backupDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void WriteClientVersion(string version)
    {
        File.WriteAllText(
            Path.Combine(_gameDir, "logs", "LatestClient.txt"),
            $"[INFO] Client version: poe2_production/tags/{version}\n");
    }

    private GameModeInfo CreateInfo() => new()
    {
        GameDirectory = _gameDir,
        Mode = GameMode.Bundles2,
        IsValid = true
    };

    private static string[] ReadPrivatePrefixes(string fieldName)
    {
        var field = typeof(PatchBackupManager)
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return (string[])field!.GetValue(null)!;
    }

    // ── 前缀分类：删除范围与检测范围必须分开 ──

    [Fact]
    public void 恢复只删本工具自身前缀()
    {
        var managed = ReadPrivatePrefixes("FreiAtlasManagedPrefixes");

        Assert.Equal(3, managed.Length);
        Assert.Contains("FreiAtlas", managed);
        Assert.Contains("TinyPoe2Smoother", managed);
        Assert.Contains("Poe2Price", managed);
    }

    [Fact]
    public void 第三方前缀绝不进入删除清单()
    {
        var managed = ReadPrivatePrefixes("FreiAtlasManagedPrefixes");
        var thirdParty = ReadPrivatePrefixes("ThirdPartyPatchPrefixes");

        Assert.Equal(2, thirdParty.Length);
        Assert.Contains("EniBundle", thirdParty);
        Assert.Contains("TinyBundle", thirdParty);

        // 删除清单与第三方清单必须完全不相交，否则恢复会删掉别人的补丁。
        Assert.Empty(managed.Intersect(thirdParty, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void 检测清单是自身与第三方的并集()
    {
        var managed = ReadPrivatePrefixes("FreiAtlasManagedPrefixes");
        var thirdParty = ReadPrivatePrefixes("ThirdPartyPatchPrefixes");
        var ggpk = ReadPrivatePrefixes("GgpkPatchBundlePrefixes");
        var known = ReadPrivatePrefixes("KnownPatchPrefixes");

        Assert.Equal(managed.Length + thirdParty.Length + ggpk.Length, known.Length);
        foreach (var prefix in managed) Assert.Contains(prefix, known);
        foreach (var prefix in thirdParty) Assert.Contains(prefix, known);
        foreach (var prefix in ggpk) Assert.Contains(prefix, known);

        // GGPK 补丁目录只检测、不删除。
        foreach (var prefix in ggpk) Assert.DoesNotContain(prefix, managed);
    }

    // ── 基线三态 ──

    [Fact]
    public void 无基线时返回Missing()
    {
        var manager = new PatchBackupManager(_backupDir);
        var info = CreateInfo();
        WriteClientVersion("0.5.6");

        var status = manager.CheckBaseline(info, out var official, out var current);

        Assert.Equal(BaselineStatus.Missing, status);
        Assert.Null(official);
        Assert.Equal("0.5.6", current);
    }

    [Fact]
    public void 基线版本一致时返回Ready()
    {
        var manager = new PatchBackupManager(_backupDir);
        var info = CreateInfo();
        WriteClientVersion("0.5.6");

        var created = manager.CreateSnapshot(
            info, PatchSnapshotType.Official, "测试底包", "测试",
            settingsJson: null, activeModules: [], logger: null,
            indexBytes: [1, 2, 3, 4]);

        Assert.True(created.Success, created.Message);

        var status = manager.CheckBaseline(info, out var official, out var current);

        Assert.Equal(BaselineStatus.Ready, status);
        Assert.Equal("0.5.6", official);
        Assert.Equal("0.5.6", current);
    }

    [Fact]
    public void 客户端升级后基线版本不符()
    {
        var manager = new PatchBackupManager(_backupDir);
        var info = CreateInfo();
        WriteClientVersion("0.5.5e");

        Assert.True(manager.CreateSnapshot(
            info, PatchSnapshotType.Official, "测试底包", "测试",
            settingsJson: null, activeModules: [], logger: null,
            indexBytes: [1, 2, 3, 4]).Success);

        // 模拟游戏更新
        WriteClientVersion("0.5.6");

        var status = manager.CheckBaseline(info, out var official, out var current);

        Assert.Equal(BaselineStatus.VersionMismatch, status);
        Assert.Equal("0.5.5e", official);
        Assert.Equal("0.5.6", current);
    }

    [Fact]
    public void 基线登记时记录第三方前缀()
    {
        var manager = new PatchBackupManager(_backupDir);
        var info = CreateInfo();
        WriteClientVersion("0.5.6");

        Assert.True(manager.CreateSnapshot(
            info, PatchSnapshotType.Official, "脏基线", "含易刷",
            settingsJson: null, activeModules: [], logger: null,
            indexBytes: [9, 9, 9], thirdPartyPrefixes: ["EniBundle", "TinyBundle"]).Success);

        var official = manager.GetSnapshots(info)
            .First(s => s.Type == PatchSnapshotType.Official);

        Assert.Equal(2, official.ThirdPartyPrefixes.Count);
        Assert.Contains("EniBundle", official.ThirdPartyPrefixes);
        Assert.Contains("TinyBundle", official.ThirdPartyPrefixes);
    }

    // ── 物价新鲜度 ──

    [Fact]
    public void 物价时间戳缺失判为过期()
    {
        Assert.False(PatchPreconditions.IsPriceDataFresh(
            DateTimeOffset.MinValue, DateTimeOffset.UtcNow, PatchPreconditions.PriceDataMaxAge));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(23)]
    public void 未超过24小时判为可用(int hours)
    {
        var now = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        var fetched = now.AddHours(-hours);

        Assert.True(PatchPreconditions.IsPriceDataFresh(fetched, now, PatchPreconditions.PriceDataMaxAge));
    }

    [Fact]
    public void 恰好24小时仍判为可用()
    {
        var now = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

        Assert.True(PatchPreconditions.IsPriceDataFresh(
            now.AddHours(-24), now, PatchPreconditions.PriceDataMaxAge));
    }

    [Fact]
    public void 超过24小时判为过期()
    {
        var now = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

        Assert.False(PatchPreconditions.IsPriceDataFresh(
            now.AddHours(-24).AddSeconds(-1), now, PatchPreconditions.PriceDataMaxAge));
    }

    [Fact]
    public void 时间戳落在未来按刚拉取处理()
    {
        var now = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

        Assert.True(PatchPreconditions.IsPriceDataFresh(
            now.AddMinutes(5), now, PatchPreconditions.PriceDataMaxAge));
    }

    [Fact]
    public void 物价允许的最大陈旧时间为24小时()
    {
        Assert.Equal(TimeSpan.FromHours(24), PatchPreconditions.PriceDataMaxAge);
    }
}
