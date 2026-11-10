namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 第五轮全量深度评审的 21 项 P0/P1 修复回归测试。
///
/// 这一轮的缺陷集中在「端到端闭环」：命令能不能走到底、状态能不能撤回、
/// 写入失败会不会伪装成功。全部用源码断言锁定，防止改动退回。
/// </summary>
public sealed class Round7FullAuditTests
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

    // ── P0：快照版本校验 ──

    [Fact]
    public void 快照列表下发版本与失效标记()
    {
        string mw = Read("src", "FreiAtlas.Settings", "MainWindow.xaml.cs");

        Assert.Contains("gameVersion = s.GameVersion", mw);
        Assert.Contains("isStale = !string.IsNullOrEmpty(s.IndexFingerprint)", mw);
    }

    [Fact]
    public void 还原快照前拦截跨版本()
    {
        string bm = Read("src", "FreiAtlas.Patcher", "Core", "PatchBackupManager.cs");

        // RestoreSnapshot 必须在写盘前比对版本
        int restore = bm.IndexOf("public BackupRestoreResult RestoreSnapshot", StringComparison.Ordinal);
        Assert.True(restore >= 0);

        int readBytes = bm.IndexOf("byte[] snapshotBytes = File.ReadAllBytes(binPath)", restore, StringComparison.Ordinal);
        Assert.True(readBytes > restore, "未找到快照读取点");

        string before = bm[restore..readBytes];
        Assert.Contains("跨版本还原会让索引与资源布局错位", before);
        Assert.Contains("StringComparison.OrdinalIgnoreCase", before);
    }

    // ── P0：泥人指纹与还原 ──

    [Fact]
    public void 泥人指纹刷新可被调用方延后()
    {
        string applier = Read("src", "FreiAtlas.Patcher", "EffectPatch", "MudMan", "MudManApplier.cs");

        Assert.Contains("bool refreshFingerprint = true", applier);
        Assert.Contains("if (refreshFingerprint)", applier);
    }

    [Fact]
    public void 批处理末尾统一刷新泥人指纹()
    {
        string mw = Read("src", "FreiAtlas.Settings", "MainWindow.xaml.cs");

        // 泥人调用必须跳过即时刷新
        Assert.Contains("refreshFingerprint: false", mw);
        // 批次末尾再统一刷新
        Assert.Contains("RefreshMudManFingerprint", mw);

        int apply = mw.IndexOf("HandleApplyAllEffectsAsync", StringComparison.Ordinal);
        int refresh = mw.IndexOf("RefreshMudManFingerprint", apply, StringComparison.Ordinal);
        int ret = mw.IndexOf("return (ruleOk: flag", apply, StringComparison.Ordinal);

        Assert.True(refresh > apply && refresh < ret,
            "统一刷新必须位于批次内 return 之前");
    }

    [Fact]
    public void 泥人还原改为二次确认不再永久拒绝()
    {
        string applier = Read("src", "FreiAtlas.Patcher", "EffectPatch", "MudMan", "MudManApplier.cs");

        Assert.Contains("StaleRequiresConfirmPrefix", applier);
        Assert.Contains("bool force", applier);
        // Stale 分支必须带 force 条件，不能无条件拒绝
        Assert.Contains("SnapshotStaleness.Stale && !force", applier);

        string mw = Read("src", "FreiAtlas.Settings", "MainWindow.xaml.cs");
        Assert.Contains("TryRestoreMudManAsync", mw);
        Assert.Contains("MessageBoxResult.Yes", mw);
    }

    // ── P0：共享文件基底 ──

    [Fact]
    public void 特效与外观对共享文件都用客户端当前内容作基底()
    {
        string fx = Read("src", "FreiAtlas.Patcher", "EffectPatch", "EffectPatchApplier.cs");
        string mtx = Read("src", "FreiAtlas.Patcher", "MtxPatch", "MtxPatchApplier.cs");

        // 两侧都必须识别共享文件并走定点剥离
        Assert.Contains("SharedFileRestore.IsSharedFile(path)", fx);
        Assert.Contains("SharedFileRestore.RemoveMatchingLines", fx);
        Assert.Contains("SharedFileRestore.ViewDistanceLine", fx);

        Assert.Contains("SharedFileRestore.IsSharedFile(virtualPath)", mtx);
        Assert.Contains("SharedFileRestore.RemoveMatchingLines", mtx);
        Assert.Contains("SharedFileRestore.DodgeRollLine", mtx);
    }

    // ── P0：CleanPatch 事务与失败上报 ──

    [Fact]
    public void 物价清洗失败必须上报且合并为一次写入()
    {
        string price = Read("src", "FreiAtlas.Patcher", "Modules", "Price", "PricePatchService.cs");

        int start = price.IndexOf("public PricePatchResult CleanPatch", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = price.IndexOf("return result;", start, StringComparison.Ordinal);
        Assert.True(end > start);
        string body = price[start..end];

        Assert.Contains("pendingWrites", body);
        Assert.Contains("写入清洗后的物价数据表失败", body);
        Assert.Contains("result.Success = false", body);
    }

    // ── P0：动态按钮并发保护 ──

    [Fact]
    public void 快照动态按钮带并发保护属性()
    {
        string js = Read("src", "FreiAtlas.Settings", "Web", "patcher.js");

        // 模板里必须真的有元素带这个属性，不是只有扫描代码
        Assert.Contains("class=\"btn-restore-snap\" data-busy-guard=\"1\"", js);
        Assert.Contains("class=\"btn-delete-snap\" data-busy-guard=\"1\"", js);
    }

    // ── P1 交互闭环 ──

    [Fact]
    public void 商城外观纳入批量应用()
    {
        string js = Read("src", "FreiAtlas.Settings", "Web", "patcher.js");

        // 批量 payload 必须包含外观，否则勾选了也不会被打进去。
        Assert.Contains("payload.mtx = buildMtxPayload()", js);

        // 后端分发必须覆盖 mtxpatch。
        string mw = Read("src", "FreiAtlas.Settings", "MainWindow.xaml.cs");
        Assert.Contains("case \"mtxpatch\" when cmd.Mtx is not null:", mw);
    }

    [Fact]
    public void 宝箱取消勾选会真正禁用()
    {
        string name = Read("src", "FreiAtlas.Patcher", "Modules", "Name", "NamePatchService.cs");

        Assert.Contains("selectedNames", name);
        Assert.Contains("hasExplicitSelection", name);
        // 未选中时必须置 Enabled = false
        Assert.Contains("item.Enabled = false", name);
    }

    [Fact]
    public void 宝箱与传言未扫描时拒绝保存()
    {
        string js = Read("src", "FreiAtlas.Settings", "Web", "patcher.js");

        Assert.Contains("if (CHESTS_DATA.length === 0)", js);
        Assert.Contains("if (RUMOURS_DATA.length === 0)", js);
    }

    [Fact]
    public void 光柱支持取消勾选的差集还原()
    {
        string beam = Read("src", "FreiAtlas.Patcher", "Modules", "MapBeam", "MapBeamPatchService.cs");

        Assert.Contains("ReadAppliedTargets", beam);
        Assert.Contains("WriteAppliedTargets", beam);
        Assert.Contains("removedTargets", beam);
        // 还原时必须真的写回原件
        Assert.Contains("PatchFileBackup.LoadOriginals(BackupGroup)", beam);
        Assert.Contains("PatchFileBackup.RemoveOriginals(BackupGroup", beam);
    }

    [Fact]
    public void 染色全局样式覆盖所有行()
    {
        string color = Read("src", "FreiAtlas.Patcher", "Modules", "ColorMod", "ColorModService.cs");

        // globalStyle 必须真的参与计算
        int use = color.IndexOf("globalStyle == ColorModStyle.FollowGame", StringComparison.Ordinal);
        Assert.True(use >= 0, "globalStyle 仍未参与 customStyleMap 计算");
    }

    [Fact]
    public void 染色提交包含未启用条目()
    {
        string js = Read("src", "FreiAtlas.Settings", "Web", "patcher.js");

        // 不能再 filter 掉未启用项
        Assert.DoesNotContain("colorModData.filter(r => r.enabled)", js);
        Assert.Contains("colorModData.map(r => ({", js);
        Assert.Contains("enabled: !!r.enabled", js);
    }

    [Fact]
    public void 物价预览失败也会复位按钮()
    {
        string js = Read("src", "FreiAtlas.Settings", "Web", "patcher.js");

        Assert.Contains("function resetPricePreviewButton", js);
        // 复位函数必须被调用（可能在 renderPricePreview 或失败分支里）
        Assert.Contains("resetPricePreviewButton();", js);
    }

    [Fact]
    public void 词库扫描空结果也会复位转圈()
    {
        string js = Read("src", "FreiAtlas.Settings", "Web", "patcher.js");

        Assert.Contains("if (payload.scannedEntries && Array.isArray(payload.scannedEntries))", js);
        Assert.DoesNotContain(
            "if (payload.scannedEntries && Array.isArray(payload.scannedEntries) && payload.scannedEntries.length > 0)", js);
    }

    // ── P1 性能与状态 ──

    [Fact]
    public void 首可用文件读取改为批量()
    {
        string engine = Read("src", "FreiAtlas.Patcher", "Core", "PatchEngine.cs");

        int start = engine.IndexOf("public byte[]? ReadFirstAvailableFile", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = engine.IndexOf("/// <summary>", start + 100, StringComparison.Ordinal);
        string body = engine[start..(end > start ? end : start + 1500)];

        Assert.Contains("ReadGameFiles(info, candidates)", body);
        Assert.DoesNotContain("ReadGameFile(info, path, out var resolved)", body);
    }

    [Fact]
    public void 同名外观按目录区分()
    {
        string db = Read("src", "FreiAtlas.Patcher", "MtxPatch", "MtxDatabase.cs");

        Assert.Contains("BuildKey", db);
        Assert.Contains("public MtxEntry? FindByName(string name, string? folder = null)", db);

        string models = Read("src", "FreiAtlas.Patcher", "MtxPatch", "MtxPatchModels.cs");
        Assert.Contains("SelectedArmourSetFolder", models);
    }

    [Fact]
    public void 还原后刷新染色视图()
    {
        string js = Read("src", "FreiAtlas.Settings", "Web", "patcher.js");

        // 取最后一次出现（receive 里的空配置分支），不是文件开头的变量声明
        int cfg = js.LastIndexOf("userConfig = new Map();", StringComparison.Ordinal);
        Assert.True(cfg >= 0);
        string after = js[cfg..(cfg + 700)];
        Assert.Contains("rebuildView()", after);
    }

    [Fact]
    public void 渲染不再把兜底色写回数据()
    {
        string js = Read("src", "FreiAtlas.Settings", "Web", "patcher.js");

        // 不能出现 item.color = 兜底色 这种写回
        Assert.DoesNotContain("item.color = effectiveColor;", js);
        Assert.Contains("const displayColor =", js);
    }

    [Fact]
    public void 亮度弹窗改动需点保存才提交()
    {
        string js = Read("src", "FreiAtlas.Settings", "Web", "patcher.js");

        Assert.Contains("let pendingRemoveDarkLevel", js);
        // change 里不得再直接改待应用集合
        int change = js.IndexOf("remove-dark-level-radio", StringComparison.Ordinal);
        Assert.True(change >= 0);
        int nextBlock = js.IndexOf("});", change + 200, StringComparison.Ordinal);
        string handler = js[change..(nextBlock > change ? nextBlock : change + 500)];
        Assert.DoesNotContain("selectedEffectRules.add", handler);
    }

    [Fact]
    public void 传言默认值投影为数组()
    {
        string mw = Read("src", "FreiAtlas.Settings", "MainWindow.xaml.cs");

        // 必须 Select 成带 originalText/modifiedText 的列表，不能直接下发字典
        // 直接断言投影写法存在（快照里还有一处 Array.Empty 的兜底赋值，不能只取第一处）
        Assert.Contains(".Select(kv => new", mw);
        Assert.Contains("originalText = kv.Key", mw);
        Assert.Contains("modifiedText = kv.Value", mw);
    }
}
