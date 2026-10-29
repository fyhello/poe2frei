namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 事故后的安全网回归测试。
///
/// 2026-10-28 的客户端损坏事故源于三条缺失：GGPK 索引裸写、批量前无自检、
/// 空列表覆盖已存配置。这三条各自锁一个断言，防止后续改动把它们改回去。
/// </summary>
public sealed class Stage3SafetyNetTests
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
    public void GGPK索引写入不使用裸写节点()
    {
        string backup = Read("src", "FreiAtlas.Patcher", "Core", "PatchBackupManager.cs");

        // FileRecord.Write 会改变节点长度，收缩时的空闲链表簿记不完整，
        // 会破坏 GGPK 结构（实测导致客户端启动报 bad allocation）。
        Assert.DoesNotContain("record.Write(", backup);
        Assert.DoesNotContain("indexRecord.Write(", backup);

        // 必须走库的节点替换接口。
        Assert.Contains("LibGGPK3.GGPK.Replace(", backup);
        Assert.Contains("RenewHashes()", backup);
    }

    [Fact]
    public void GGPK索引写入后读回自检()
    {
        string backup = Read("src", "FreiAtlas.Patcher", "Core", "PatchBackupManager.cs");

        // 写坏了必须在当下暴露，而不是等用户启动游戏才发现客户端起不来。
        Assert.Contains("写入后自检失败", backup);
        Assert.Contains("SequenceEqual(indexBytes)", backup);
    }

    [Fact]
    public void 批量在写盘前做客户端自检()
    {
        string mw = Read("src", "FreiAtlas.Settings", "MainWindow.xaml.cs");

        int start = mw.IndexOf("private async Task HandleApplyBatchAsync", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = mw.IndexOf("private async Task DispatchBatchModuleAsync", start, StringComparison.Ordinal);
        string body = mw[start..end];

        Assert.Contains("VerifyClientReadable(info)", body);

        // 自检必须排在物价校验和基线决策之前，否则会先花时间做后续步骤。
        int selfCheck = body.IndexOf("VerifyClientReadable", StringComparison.Ordinal);
        int priceCheck = body.IndexOf("IsPriceDataFresh", StringComparison.Ordinal);
        Assert.True(selfCheck < priceCheck, "自检必须排在物价校验之前");
    }

    [Fact]
    public void 批量用索引指纹判断脏净()
    {
        string mw = Read("src", "FreiAtlas.Settings", "MainWindow.xaml.cs");

        int start = mw.IndexOf("private async Task HandleApplyBatchAsync", StringComparison.Ordinal);
        int end = mw.IndexOf("private async Task DispatchBatchModuleAsync", start, StringComparison.Ordinal);
        string body = mw[start..end];

        // GGPK 下前缀匹配永远返回空，必须优先用指纹。
        Assert.Contains("IsClientPatched(info)", body);
        Assert.Contains("patchedByFingerprint ?? detectedPrefixes.Count > 0", body);
    }

    [Fact]
    public void 空列表不会覆盖已保存配置()
    {
        string mw = Read("src", "FreiAtlas.Settings", "MainWindow.xaml.cs");

        // 传言：条目列表只有扫描过客户端才有内容。
        // 空列表落库会把上次保存的配置清空，所以必须先判空再持久化。
        const string rumourMarker = "尚未读取客户端传言";
        Assert.Contains(rumourMarker, mw);

        int guard = mw.IndexOf(rumourMarker, StringComparison.Ordinal);
        int persist = mw.LastIndexOf("UpdateClientPatch", guard, StringComparison.Ordinal);
        Assert.True(persist >= 0);
        Assert.True(persist < guard, "传言的空列表判断必须早于持久化");

        Assert.Contains("useDefaultRules && cmd.PersistOnly", mw);
        Assert.Contains("RumourItems = useDefaultRules ? null : cmd.Items", mw);
        Assert.Contains("ChestItems = useDefaultRules ? null : cmd.Items", mw);
        Assert.Contains("ColorModRules = useDefaultRules ? null : cmd.Rules", mw);
    }

    [Fact]
    public void 客户端自检覆盖三条关键链路()
    {
        string engine = Read("src", "FreiAtlas.Patcher", "Core", "PatchEngine.cs");

        Assert.Contains("public string? VerifyClientReadable", engine);
        Assert.Contains("metadata/ui/uisettings.xml", engine);
        Assert.Contains("data/balance/baseitemtypes.datc64", engine);
        Assert.Contains("metadata/characters/character.ot", engine);
    }
}
