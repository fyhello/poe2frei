using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 快照版本校验。
///
/// 游戏更新后旧索引会把客户端还原成旧版本资源，导致客户端与资源不匹配而无法启动。
/// 因此快照必须记录生成时的索引指纹，恢复前与当前客户端比对。
/// </summary>
public sealed class SnapshotVersionTests
{
    [Fact]
    public void 相同索引产生相同指纹()
    {
        byte[] a = [1, 2, 3, 4, 5];
        byte[] b = [1, 2, 3, 4, 5];

        Assert.Equal(PatchBackupManager.ComputeFingerprint(a), PatchBackupManager.ComputeFingerprint(b));
    }

    [Fact]
    public void 不同索引产生不同指纹()
    {
        byte[] a = [1, 2, 3, 4, 5];
        byte[] b = [1, 2, 3, 4, 6];

        Assert.NotEqual(PatchBackupManager.ComputeFingerprint(a), PatchBackupManager.ComputeFingerprint(b));
    }

    [Fact]
    public void 空索引指纹为空串()
    {
        Assert.Equal(string.Empty, PatchBackupManager.ComputeFingerprint([]));
        Assert.Equal(string.Empty, PatchBackupManager.ComputeFingerprint(null!));
    }

    [Fact]
    public void 新快照默认无指纹表示旧版程序创建()
    {
        // 旧版本程序生成的清单没有该字段，反序列化后为空串。
        // 调用方应把空指纹视为「无法证明适用」，从而禁止恢复。
        var snapshot = new PatchSnapshotInfo();

        Assert.Equal(string.Empty, snapshot.IndexFingerprint);
        Assert.Equal(string.Empty, snapshot.GameVersion);
    }

    [Fact]
    public void 快照可记录版本与指纹()
    {
        var snapshot = new PatchSnapshotInfo
        {
            Id = "snap_auto_test",
            Type = PatchSnapshotType.Auto,
            IndexFingerprint = "ABCDEF",
            GameVersion = "0.5.5e"
        };

        Assert.Equal("ABCDEF", snapshot.IndexFingerprint);
        Assert.Equal("0.5.5e", snapshot.GameVersion);
    }
}
