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
    public void SaveOriginals_已有孤儿文件时不覆盖但补录进清单()
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

            // 先手工造出「清单缺失但文件在」的孤儿场景：文件名与 FileNameFor 一致。
            int first = PatchFileBackup.SaveOriginals(groupId, originals.Keys, originals);
            Assert.Equal(1, first);
            var savedFile = PatchFileBackup.ListOriginals(groupId).Single().FilePath;
            File.Delete(savedFile);
            File.Delete(Path.Combine(dir, "manifest.json"));
            File.WriteAllBytes(savedFile, [9, 9, 9]);

            int saved = PatchFileBackup.SaveOriginals(groupId, originals.Keys, originals);

            // 不覆盖磁盘上的既有字节，但必须把它补录进清单——
            // 否则下一次应用会把它当作「已备份」跳过，这份原件永远不可见。
            Assert.Equal(1, saved);
            Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(savedFile));
            Assert.Equal(1, PatchFileBackup.LastAdoptedCount);
            Assert.Single(PatchFileBackup.LoadOriginals(groupId));
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

    [Fact]
    public void SaveOriginals_清单有条目但文件缺失时重新备份()
    {
        var root = CreateTempRoot();
        PatchFileBackup.RootDirectoryOverride = root;
        var groupId = "missing_" + Guid.NewGuid().ToString("N");

        try
        {
            var originals = new Dictionary<string, byte[]> { ["dir/missing.ot"] = [7, 7, 7] };
            Assert.Equal(1, PatchFileBackup.SaveOriginals(groupId, originals.Keys, originals));

            var entry = PatchFileBackup.ListOriginals(groupId).Single();
            File.Delete(entry.FilePath);

            // 清单里仍留着记录、备份文件却没了（杀软隔离 / 手工删除 / 磁盘损坏）。
            // 下一次应用必须重建，而不是因为 manifest.ContainsKey 就跳过。
            Assert.Equal(1, PatchFileBackup.SaveOriginals(groupId, originals.Keys, originals));
            Assert.Equal(new byte[] { 7, 7, 7 }, PatchFileBackup.LoadOriginals(groupId).Single().Data);
        }
        finally
        {
            PatchFileBackup.RootDirectoryOverride = null;
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void LoadOriginals_截断的备份文件被拒绝()
    {
        var root = CreateTempRoot();
        PatchFileBackup.RootDirectoryOverride = root;
        var groupId = "truncated_" + Guid.NewGuid().ToString("N");

        try
        {
            var originals = new Dictionary<string, byte[]> { ["dir/trunc.ot"] = [1, 2, 3, 4, 5] };
            PatchFileBackup.SaveOriginals(groupId, originals.Keys, originals);

            var entry = PatchFileBackup.ListOriginals(groupId).Single();
            File.WriteAllBytes(entry.FilePath, [1, 2]);

            Assert.Empty(PatchFileBackup.LoadOriginals(groupId));
            Assert.Contains("长度不符", PatchFileBackup.LastManifestError ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            PatchFileBackup.RootDirectoryOverride = null;
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void SaveOriginals_扁平化会碰撞的两条路径不会互相覆盖()
    {
        var root = CreateTempRoot();
        PatchFileBackup.RootDirectoryOverride = root;
        var groupId = "collision_" + Guid.NewGuid().ToString("N");

        try
        {
            // 旧实现把 "/" 换成 "_"：a/b_c 与 a_b/c 会得到同一个文件名。
            var originals = new Dictionary<string, byte[]>
            {
                ["a/b_c.dds"] = [1, 1, 1],
                ["a_b/c.dds"] = [2, 2, 2]
            };

            Assert.Equal(2, PatchFileBackup.SaveOriginals(groupId, originals.Keys, originals));

            var loaded = PatchFileBackup.LoadOriginals(groupId)
                .ToDictionary(x => x.VirtualPath, x => x.Data, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(2, loaded.Count);
            Assert.Equal(new byte[] { 1, 1, 1 }, loaded["a/b_c.dds"]);
            Assert.Equal(new byte[] { 2, 2, 2 }, loaded["a_b/c.dds"]);
        }
        finally
        {
            PatchFileBackup.RootDirectoryOverride = null;
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void SaveOriginals_清单原子写不残留临时文件()
    {
        var root = CreateTempRoot();
        PatchFileBackup.RootDirectoryOverride = root;
        var groupId = "atomic_" + Guid.NewGuid().ToString("N");

        try
        {
            var originals = new Dictionary<string, byte[]> { ["dir/a.ot"] = [1] };
            PatchFileBackup.SaveOriginals(groupId, originals.Keys, originals);

            var dir = PatchFileBackup.GetGroupDirectory(groupId);
            Assert.True(File.Exists(Path.Combine(dir, "manifest.json")));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
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
