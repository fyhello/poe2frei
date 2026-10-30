using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// P2-3 索引会话借用的核心约束：
/// 多个模块可以复用同一底层会话；租约归还不关闭会话；有租约时禁止写盘释放；
/// 读取时必须先看批量会话叠加层，再退回磁盘索引。
/// </summary>
public sealed class PatchEngineClientIndexLeaseTests
{
    private const string RealGameDir =
        @"E:\PSAutoRecover\ui\rood\Grinding Gear Games\Path of Exile 2";

    [Fact]
    public void 叠加层内容优先于磁盘读取()
    {
        var session = new PatchSession();
        session.Stage("Data/A.OT", [1, 2, 3]);

        bool diskCalled = false;
        var bytes = PatchEngine.ReadWithOverlay(
            session,
            @"Data\a.ot",
            _ =>
            {
                diskCalled = true;
                return [9, 9, 9];
            });

        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
        Assert.False(diskCalled);

        var fallback = PatchEngine.ReadWithOverlay(null, "b.ot", _ => [9, 9]);
        Assert.Equal(new byte[] { 9, 9 }, fallback);
    }

    [Fact]
    public void 叠加层已登记路径视为存在()
    {
        var session = new PatchSession();
        session.Stage("new.ot", [1]);

        bool diskCalled = false;
        Assert.True(PatchEngine.ExistsWithOverlay(
            session,
            "/NEW.OT",
            _ =>
            {
                diskCalled = true;
                return false;
            }));
        Assert.False(diskCalled);
    }

    [Fact]
    public void 真实客户端_租约复用同一会话且归还后可释放()
    {
        if (!Directory.Exists(RealGameDir)) return;

        var info = GameInstallDetector.EvaluateDirectory(RealGameDir);
        if (!info.IsValid) return;

        var engine = new PatchEngine();
        try
        {
            using (var first = engine.AcquireClientIndex(info))
            {
                Assert.NotNull(first);
                var data = first!.ReadFile("metadata/ui/uisettings.xml");
                Assert.NotNull(data);
                Assert.NotEmpty(data);

                using var second = engine.AcquireClientIndex(info);
                Assert.NotNull(second);
                Assert.Same(first.Session, second!.Session);
                Assert.Equal(2, engine.ClientIndexLeaseCount);
            }

            Assert.Equal(0, engine.ClientIndexLeaseCount);
            engine.ReleaseClientIndex();
        }
        finally
        {
            try { engine.ReleaseClientIndex(); } catch { }
        }
    }

    [Fact]
    public void 有租约时禁止释放索引()
    {
        if (!Directory.Exists(RealGameDir)) return;

        var info = GameInstallDetector.EvaluateDirectory(RealGameDir);
        if (!info.IsValid) return;

        var engine = new PatchEngine();
        try
        {
            using var lease = engine.AcquireClientIndex(info);
            Assert.NotNull(lease);

            var ex = Assert.Throws<InvalidOperationException>(() => engine.ReleaseClientIndex());
            Assert.Contains("借用", ex.Message);
        }
        finally
        {
            try { engine.ReleaseClientIndex(); } catch { }
        }
    }

    [Fact]
    public void 模块不得再自行打开索引会话()
    {
        string[] moduleFiles =
        [
            "EffectPatchApplier.cs",
            "HealthBarApplier.cs",
            "MudManApplier.cs",
            "MtxPatchApplier.cs"
        ];

        foreach (var fileName in moduleFiles)
        {
            var path = FindSourceFile(fileName);
            var source = File.ReadAllText(path);
            Assert.DoesNotContain("ClientIndexSession.Open", source, StringComparison.Ordinal);
        }
    }

    private static string FindSourceFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string baseDir = Path.Combine(dir.FullName, "src", "FreiAtlas.Patcher");
            if (Directory.Exists(baseDir))
            {
                var found = Directory.GetFiles(baseDir, fileName, SearchOption.AllDirectories);
                if (found.Length > 0) return found[0];
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"未能定位 {fileName}");
    }
}
