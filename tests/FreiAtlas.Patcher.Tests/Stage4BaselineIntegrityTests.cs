using System.Reflection;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// GGPK 脏基线与空版本还原的回归测试。
///
/// 2026-10-28 事故：10-20 建立的「官方纯净底包」实际是从已打补丁的客户端捕获的
/// （120,749,196 字节，含 2 个 LibGGPK3 bundle 引用），而修复后的客户端是
/// 115,161,316 字节。点「恢复纯净」把旧索引写进去 → Content.ggpk 报废。
///
/// 根因有两条，各锁一个断言：GGPK 补丁目录不在检测前缀里、空版本快照被放行。
/// </summary>
public sealed class Stage4BaselineIntegrityTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(params string[] parts)
    {
        string path = Path.Combine([RepoRoot(), .. parts]);
        Assert.True(File.Exists(path), $"未找到文件：{path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void GGPK补丁目录参与脏净检测()
    {
        string src = Read("src", "FreiAtlas.Patcher", "Core", "PatchBackupManager.cs");

        // 干净客户端的 Bundles2/ 下只有 Streaming/ 与 Content/，没有 LibGGPK3/。
        // 漏掉这一项会让打满补丁的 GGPK 客户端被判定为「纯净」。
        Assert.Contains("GgpkPatchBundlePrefixes", src);
        Assert.Contains("\"LibGGPK3\"", src);
        Assert.Contains(".. GgpkPatchBundlePrefixes]", src);

        // 只检测，不能进删除清单。
        var type = typeof(FreiAtlas.Patcher.Core.PatchBackupManager);
        var known = type.GetField("KnownPatchPrefixes", BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null) as string[];
        var managed = type.GetField("FreiAtlasManagedPrefixes", BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null) as string[];

        Assert.NotNull(known);
        Assert.NotNull(managed);
        Assert.Contains("LibGGPK3", known!);
        Assert.DoesNotContain("LibGGPK3", managed!);
    }

    [Fact]
    public void 空版本快照一律拒绝还原()
    {
        string src = Read("src", "FreiAtlas.Patcher", "Core", "PatchBackupManager.cs");

        int start = src.IndexOf("public BackupRestoreResult RestoreSnapshot", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = src.IndexOf("public BackupRestoreResult Restore(", start, StringComparison.Ordinal);
        Assert.True(end > start);
        string body = src[start..end];

        // 旧写法：版本为空时因为 !IsNullOrEmpty 短路而被放行。
        Assert.DoesNotContain("if (!string.IsNullOrEmpty(target.GameVersion)", body);
        Assert.Contains("if (string.IsNullOrEmpty(target.GameVersion))", body);
        Assert.Contains("没有记录游戏版本", body);
    }

    [Fact]
    public void 还原不再删除补丁bundle目录()
    {
        string src = Read("src", "FreiAtlas.Patcher", "Core", "PatchBackupManager.cs");

        // 删 LibGGPK3/ 的前提是「基线一定干净」。事故中基线是脏的，
        // 删除动作把它所依赖的 bundle 一并删掉，直接把客户端变成不可恢复。
        // 宁可留孤儿节点，也不做这个假设。
        Assert.DoesNotContain("RemovePatchBundleDirectory", src);
    }
}
