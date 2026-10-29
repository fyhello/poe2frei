using System.Text;
using System.Text.Json;
using FreiAtlas.Patcher;
using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.Tests;

public class SnapshotTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _gameDir;
    private readonly string _backupDir;
    private readonly GameModeInfo _gameInfo;

    public SnapshotTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FreiAtlas_SnapTest_" + Guid.NewGuid().ToString("N"));
        _gameDir = Path.Combine(_tempDir, "Game");
        _backupDir = Path.Combine(_tempDir, "Backup");

        Directory.CreateDirectory(_gameDir);
        Directory.CreateDirectory(_backupDir);

        // 模拟 Bundles2 模式客户端
        var bundlesDir = Path.Combine(_gameDir, "Bundles2");
        Directory.CreateDirectory(bundlesDir);
        File.WriteAllBytes(Path.Combine(bundlesDir, "_.index.bin"), Encoding.UTF8.GetBytes("MOCK_INDEX_BIN_DATA"));

        // 快照会记录游戏版本；还原时版本为空一律拒绝，所以测试环境也要有版本日志。
        var logsDir = Path.Combine(_gameDir, "logs");
        Directory.CreateDirectory(logsDir);
        File.WriteAllText(Path.Combine(logsDir, "LatestClient.txt"), "poe2_production/tags/0.5.6");

        _gameInfo = new GameModeInfo
        {
            GameDirectory = _gameDir,
            Mode = GameMode.Bundles2,
            IsValid = true
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void Snapshot_CreateAutoSnapshot_UsesActionNameAndDecouplesSettingsJson()
    {
        var manager = new PatchBackupManager(_backupDir);
        string mockSettings = JsonSerializer.Serialize(new { PricePatchEnabled = true, FakeRules = new string('A', 5000) });

        var result = manager.CreateSnapshot(
            _gameInfo,
            PatchSnapshotType.Auto,
            name: "[自动] 物价补丁写入前 (10-18 18:00)",
            description: "写入物价补丁前自动生成的还原点",
            settingsJson: mockSettings,
            activeModules: ["物价标签"]);

        Assert.True(result.Success, result.Message);

        var list = manager.GetSnapshots(_gameInfo);
        var autoSnap = list.FirstOrDefault(s => s.Type == PatchSnapshotType.Auto);
        Assert.NotNull(autoSnap);
        Assert.Equal("[自动] 物价补丁写入前 (10-18 18:00)", autoSnap.Name);
        Assert.Contains("物价标签", autoSnap.EnabledModules);

        // 验证 manifest 中的 SettingsJson 为 null（解耦轻量化）
        Assert.Null(autoSnap.SettingsJson);

        // 验证独立设置文件存在且内容无损
        string snapDir = manager.GetSnapshotsDirectory(_gameInfo);
        string settingsFile = Path.Combine(snapDir, $"{autoSnap.Id}.settings.json");
        Assert.True(File.Exists(settingsFile));
        Assert.Equal(mockSettings, File.ReadAllText(settingsFile, Encoding.UTF8));

        // 验证可正确从独立文件还原出设置
        var restoreResult = manager.RestoreSnapshot(_gameInfo, autoSnap.Id, out string? restoredJson);
        Assert.True(restoreResult.Success);
        Assert.Equal(mockSettings, restoredJson);
    }

    [Fact]
    public void Snapshot_AutoRotate_KeepsMax5SnapshotsAndCleansFiles()
    {
        var manager = new PatchBackupManager(_backupDir);
        string snapDir = manager.GetSnapshotsDirectory(_gameInfo);

        // 连续创建 7 个自动快照
        for (int i = 1; i <= 7; i++)
        {
            var res = manager.CreateSnapshot(
                _gameInfo,
                PatchSnapshotType.Auto,
                name: $"[自动] 补丁前第 {i} 版",
                description: $"版本 {i}",
                settingsJson: $"{{\"version\": {i}}}",
                activeModules: []);
            Assert.True(res.Success);
            Thread.Sleep(50); // 确保时间戳微差
        }

        var list = manager.GetSnapshots(_gameInfo).Where(s => s.Type == PatchSnapshotType.Auto).ToList();
        // 严格保留最近 5 版
        Assert.Equal(5, list.Count);

        // 最早的第 1、2 版应该被淘汰，保留第 3、4、5、6、7 版
        Assert.DoesNotContain(list, s => s.Name == "[自动] 补丁前第 1 版");
        Assert.DoesNotContain(list, s => s.Name == "[自动] 补丁前第 2 版");
        Assert.Contains(list, s => s.Name == "[自动] 补丁前第 7 版");

        // 验证磁盘上的 .bin 和 .settings.json 文件数量与保留版本严格一致
        var binFiles = Directory.GetFiles(snapDir, "snap_auto_*.bin");
        var settingsFiles = Directory.GetFiles(snapDir, "snap_auto_*.settings.json");
        Assert.Equal(5, binFiles.Length);
        Assert.Equal(5, settingsFiles.Length);
    }

    [Fact]
    public void Snapshot_GetSnapshots_SmoothlyMigratesLegacyFatManifest()
    {
        var manager = new PatchBackupManager(_backupDir);
        string snapDir = manager.GetSnapshotsDirectory(_gameInfo);
        string manifestPath = Path.Combine(snapDir, "manifest.json");

        // 模拟一个带有臃肿历史 SettingsJson 的旧版 manifest.json
        string legacySnapId = "snap_legacy_fat_001";
        string fatSettings = "{\"huge\":\"" + new string('X', 10000) + "\"}";
        var legacyList = new List<PatchSnapshotInfo>
        {
            new()
            {
                Id = legacySnapId,
                Type = PatchSnapshotType.Manual,
                Name = "历史旧快照",
                Description = "带有内嵌大设置的历史备份",
                CreatedAt = DateTime.Now.AddDays(-1),
                SizeBytes = 1024,
                SettingsJson = fatSettings
            }
        };

        File.WriteAllText(manifestPath, JsonSerializer.Serialize(legacyList), Encoding.UTF8);

        // 执行 GetSnapshots，应自动迁移
        var loaded = manager.GetSnapshots(_gameInfo);
        var target = loaded.FirstOrDefault(s => s.Id == legacySnapId);
        Assert.NotNull(target);
        Assert.Null(target.SettingsJson); // manifest 内存中已清空

        // 独立设置文件已自动生成
        string migratedSettingsFile = Path.Combine(snapDir, $"{legacySnapId}.settings.json");
        Assert.True(File.Exists(migratedSettingsFile));
        Assert.Equal(fatSettings, File.ReadAllText(migratedSettingsFile, Encoding.UTF8));

        // 检查 manifest.json 文件本身是否也被重写瘦身
        string manifestText = File.ReadAllText(manifestPath, Encoding.UTF8);
        Assert.DoesNotContain(fatSettings, manifestText);
    }
}
