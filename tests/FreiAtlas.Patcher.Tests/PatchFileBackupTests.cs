using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 逐文件备份的核心写入契约。
///
/// SaveOriginals 用一次目录枚举判断「备份文件是否已存在」，
/// 这些测试锁定两个不可破坏的行为：已有文件绝不覆盖、新文件正常写入并进入清单。
/// </summary>
public sealed class PatchFileBackupTests
{
    [Fact]
    public void SaveOriginals_已有孤儿文件时不覆盖也不计数()
    {
        var root = CreateTempRoot();
        PatchFileBackup.RootDirectoryOverride = root;
        var groupId = "orphan_" + Guid.NewGuid().ToString("N");
        var dir = PatchFileBackup.GetGroupDirectory(groupId);
        var filePath = Path.Combine(dir, "orphan.ot");

        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(filePath, [9, 9, 9]);

            var originals = new Dictionary<string, byte[]> { ["orphan.ot"] = [1, 2, 3] };
            int saved = PatchFileBackup.SaveOriginals(groupId, originals.Keys, originals);

            Assert.Equal(0, saved);
            Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(filePath));
        }
        finally
        {
            PatchFileBackup.RootDirectoryOverride = null;
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void SaveOriginals_新文件正常写入并生成可读取清单()
    {
        var root = CreateTempRoot();
        PatchFileBackup.RootDirectoryOverride = root;
        var groupId = "new_" + Guid.NewGuid().ToString("N");

        try
        {
            var originals = new Dictionary<string, byte[]> { ["dir/new.ot"] = [1, 2, 3, 4] };
            int saved = PatchFileBackup.SaveOriginals(groupId, originals.Keys, originals);

            Assert.Equal(1, saved);
            Assert.Equal(1, PatchFileBackup.CountOriginals(groupId));

            var loaded = PatchFileBackup.LoadOriginals(groupId);
            Assert.Single(loaded);
            Assert.Equal("dir/new.ot", loaded[0].VirtualPath);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, loaded[0].Data);
        }
        finally
        {
            PatchFileBackup.RootDirectoryOverride = null;
            DeleteTempRoot(root);
        }
    }

    private static string CreateTempRoot() =>
        Path.Combine(Path.GetTempPath(), "FreiAtlasPatchFileBackupTest_" + Guid.NewGuid().ToString("N"));

    private static void DeleteTempRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
