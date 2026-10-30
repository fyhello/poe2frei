using System.Text;
using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 快照/底包文件的安全性约束。
///
/// 这些用例锁定的都是「已经写坏过一次」的路径：
/// · 清单损坏时被官方底包初始化流程覆盖，用户全部快照消失；
/// · 底包被截断但仍被当成可用备份，还原时把坏索引写回客户端。
/// </summary>
public sealed class PatchBackupManagerSafetyTests
{
    [Fact]
    public void GetSnapshots_清单损坏时不得覆盖原文件()
    {
        var root = CreateTempRoot();
        try
        {
            var mgr = new PatchBackupManager(root);
            var info = NewInfo(root);
            var snapDir = mgr.GetSnapshotsDirectory(info);
            var manifestPath = Path.Combine(snapDir, "manifest.json");

            const string corrupt = "{ 这不是合法清单";
            File.WriteAllText(manifestPath, corrupt, Encoding.UTF8);

            // 同时造出「历史底包存在」的条件——旧实现正是在这个条件下
            // 构造 official 记录并 SaveManifest，把损坏清单整份覆盖掉。
            File.WriteAllBytes(mgr.GetBackupFilePath(info), [1, 2, 3]);

            var list = mgr.GetSnapshots(info);

            Assert.Empty(list);
            Assert.Equal(corrupt, File.ReadAllText(manifestPath, Encoding.UTF8));
            Assert.NotEmpty(Directory.GetFiles(snapDir, "manifest.json.corrupt-*"));
            Assert.False(string.IsNullOrWhiteSpace(mgr.LastManifestError));

            // 后续任何写入都必须被拒绝，而不是悄悄重建一份只含 official 的清单。
            var result = mgr.CreateSnapshot(
                info, PatchSnapshotType.Auto, "test", "test", indexBytes: [9, 9, 9]);
            Assert.False(result.Success);
            Assert.Equal(corrupt, File.ReadAllText(manifestPath, Encoding.UTF8));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void HasBackup_空文件与长度不符的底包不算可用()
    {
        var root = CreateTempRoot();
        try
        {
            var mgr = new PatchBackupManager(root);
            var info = NewInfo(root);
            var backupPath = mgr.GetBackupFilePath(info);

            File.WriteAllBytes(backupPath, []);
            Assert.False(mgr.HasBackup(info));

            File.WriteAllBytes(backupPath, new byte[10]);
            File.WriteAllText(backupPath + ".meta.json", "{\"Length\":20}", Encoding.UTF8);
            Assert.False(mgr.HasBackup(info));

            File.WriteAllText(backupPath + ".meta.json", "{\"Length\":10}", Encoding.UTF8);
            Assert.True(mgr.HasBackup(info));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static GameModeInfo NewInfo(string root) => new()
    {
        IsValid = true,
        Mode = GameMode.GGPK,
        GameDirectory = Path.Combine(root, "game")
    };

    private static string CreateTempRoot() =>
        Path.Combine(Path.GetTempPath(), "FreiAtlasBackupSafetyTest_" + Guid.NewGuid().ToString("N"));

    private static void DeleteTempRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
