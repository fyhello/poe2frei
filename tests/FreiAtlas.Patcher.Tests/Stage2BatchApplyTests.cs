namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 阶段 2：批量应用与自动保存的回归测试。
///
/// 锁的是三条容易在后续改动中被破坏的不变量：写入顺序、两级门控、物价前置校验。
/// </summary>
public sealed class Stage2BatchApplyTests
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
    public void 批量顺序把特效放最前外观放最后()
    {
        string mw = Read("src", "FreiAtlas.Settings", "MainWindow.xaml.cs");

        // 泥人（含在特效模块内）切档会回滚整个索引，必须最先；
        // 外观的防具套装是整份 .ao 替换，必须最后，否则会盖掉泥人对 items 的改动。
        // 用正则抽取数组里的模块 id，避免因换行/空格调整而误报。
        int arrayStart = mw.IndexOf("BatchModuleOrder", StringComparison.Ordinal);
        Assert.True(arrayStart >= 0);
        int arrayEnd = mw.IndexOf("];", arrayStart, StringComparison.Ordinal);
        Assert.True(arrayEnd > arrayStart);
        var ids = System.Text.RegularExpressions.Regex
            .Matches(mw[arrayStart..arrayEnd], "\"([a-z]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToArray();
        Assert.Equal(
            [
                "effectpatch", "translation", "fontmod", "price", "colormod",
                "chests", "rumours", "mapbeam", "iconpatch", "mtxpatch"
            ],
            ids);

        int order = mw.IndexOf("BatchModuleOrder", StringComparison.Ordinal);
        int effects = mw.IndexOf("\"effectpatch\", \"translation\"", order, StringComparison.Ordinal);
        int mtx = mw.IndexOf("\"iconpatch\", \"mtxpatch\"", order, StringComparison.Ordinal);

        Assert.True(effects > order, "未找到顺序表起始项");
        Assert.True(mtx > effects, "外观必须排在图标之后");
    }

    [Fact]
    public void 大类与小功能是两级门控()
    {
        string js = Read("src", "FreiAtlas.Settings", "Web", "patcher.js");

        int start = js.IndexOf("function collectSelectedModules", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = js.IndexOf("function updateBatchHint", start, StringComparison.Ordinal);
        string body = js[start..end];

        // 大类未勾选时整组跳过
        Assert.Contains(".batch-category-toggle", body);
        Assert.Contains("if (!catEl || !catEl.checked) return;", body);
        // 小功能未勾选时跳过
        Assert.Contains(".batch-module-toggle", body);
        Assert.Contains("if (el && el.checked) out.push(mod);", body);
    }

    [Fact]
    public void 批量应用前校验物价新鲜度()
    {
        string mw = Read("src", "FreiAtlas.Settings", "MainWindow.xaml.cs");

        int start = mw.IndexOf("private async Task HandleApplyBatchAsync", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = mw.IndexOf("private async Task DispatchBatchModuleAsync", start, StringComparison.Ordinal);
        string body = mw[start..end];

        // 物价过期必须中止整批，而不是跳过物价继续
        Assert.Contains("PatchPreconditions.IsPriceDataFresh", body);
        Assert.Contains("modules.Contains(\"price\")", body);
        Assert.Contains("return;", body);
    }

    [Fact]
    public void 批量只记一份快照()
    {
        string mw = Read("src", "FreiAtlas.Settings", "MainWindow.xaml.cs");

        // RecordAutoSnapshotAsync 在批量模式下必须短路，否则每模块一份 115 MB
        int start = mw.IndexOf("private async Task<BackupRestoreResult?> RecordAutoSnapshotAsync", StringComparison.Ordinal);
        Assert.True(start >= 0);
        string head = mw[start..(start + 700)];
        Assert.Contains("if (_batchMode) return null;", head);
    }

    [Fact]
    public void 批量前先恢复基线再打补丁()
    {
        string mw = Read("src", "FreiAtlas.Settings", "MainWindow.xaml.cs");

        int start = mw.IndexOf("private async Task HandleApplyBatchAsync", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = mw.IndexOf("private async Task DispatchBatchModuleAsync", start, StringComparison.Ordinal);
        string body = mw[start..end];

        // 取消勾选某模块后，若不做整体回滚，该模块的旧改动会一直留在客户端。
        // 恢复动作已抽到 RestoreClientPreservingSettingsAsync（释放索引句柄 + 保住配置）。
        Assert.Contains("RestoreClientPreservingSettingsAsync", body);
        Assert.Contains("ClearTransientPatchStates", body);
        // 客户端本身干净时不应白花一次恢复
        Assert.Contains("needRestore = clientDirty", body);
    }

    [Fact]
    public void 参数改动走自动保存而非写客户端()
    {
        string js = Read("src", "FreiAtlas.Settings", "Web", "patcher.js");

        Assert.Contains("function scheduleAutoSave", js);
        Assert.Contains("function flushAutoSave", js);
        Assert.Contains("applyColorMod(true)", js);
        // 旧的两条写入路径必须已消失
        Assert.DoesNotContain("btnApplyCurrentWorkbench", js);
    }
}
