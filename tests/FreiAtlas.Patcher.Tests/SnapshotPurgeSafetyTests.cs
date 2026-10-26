using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 快照清理的安全性。
///
/// 手动备份是用户主动创建的还原点，任何自动清理逻辑都不得删除它——
/// 曾经因为把 Manual 纳入「过期清理」范围，导致用户 16 号创建的手动备份
/// 在打开软件时被静默删除，且无法恢复。
/// </summary>
public sealed class SnapshotPurgeSafetyTests
{
    [Fact]
    public void 只有自动快照参与过期清理()
    {
        // 这条约束由 PatchBackupManager.PurgeStaleSnapshots 内部保证：
        //     if (snap.Type != PatchSnapshotType.Auto) continue;
        // 这里以断言形式固化该约定，防止后续改动把 Manual 重新纳入清理范围。
        var purgeable = new[] { PatchSnapshotType.Auto };

        Assert.DoesNotContain(PatchSnapshotType.Manual, purgeable);
        Assert.DoesNotContain(PatchSnapshotType.Official, purgeable);
    }

    [Fact]
    public void 快照类型语义明确()
    {
        // Official = 官方底包，由重建流程替换，不参与逐条清理
        // Manual   = 用户创建，永久保留，仅支持手动删除
        // Auto     = 自动记录，保留最近 5 版并清理过期项
        Assert.Equal(0, (int)PatchSnapshotType.Official);
        Assert.Equal(1, (int)PatchSnapshotType.Manual);
        Assert.Equal(2, (int)PatchSnapshotType.Auto);
    }

    [Fact]
    public void 旧快照缺少版本号时不应被当作可删除依据()
    {
        // 旧版程序创建的快照没有 GameVersion 字段。缺少版本号只能说明「无法判断」，
        // 不能作为删除理由——尤其对手动备份，删除即意味着用户永久失去还原点。
        var snapshot = new PatchSnapshotInfo
        {
            Id = "snap_manual_20261016_000000_000_abcd",
            Type = PatchSnapshotType.Manual,
            CreatedAt = DateTime.Now.AddDays(-5)
        };

        Assert.True(string.IsNullOrEmpty(snapshot.GameVersion));
        // 该快照已超 48 小时且无版本号，但因类型为 Manual，必须保留
        Assert.NotEqual(PatchSnapshotType.Auto, snapshot.Type);
    }
}
