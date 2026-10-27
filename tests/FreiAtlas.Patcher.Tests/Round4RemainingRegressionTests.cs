namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 第四轮评审剩余修复的回归测试（P0-4 技能精准还原、P1-9/P1-10 前端保护）。
///
/// 前端部分无法在 .NET 单测里执行，改用源码断言锁定关键实现特征。
/// </summary>
public sealed class Round4RemainingRegressionTests
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

    // ── P0-4：技能屏蔽局部还原必须按参数精准处理 ──

    [Fact]
    public void 技能局部还原按参数精准处理()
    {
        string blocker = Read("src", "FreiAtlas.Patcher", "EffectPatch", "SpellEffectBlocker.cs");

        int start = blocker.IndexOf("public bool RestoreBatch", StringComparison.Ordinal);
        Assert.True(start >= 0, "未找到 RestoreBatch");

        int end = blocker.IndexOf("private static string? LoadErrorHint", start, StringComparison.Ordinal);
        if (end < 0) end = blocker.IndexOf("private static Dictionary<string, byte[]> LoadAllExistingBackups", start, StringComparison.Ordinal);
        Assert.True(end > start, "未找到 RestoreBatch 方法边界");

        string body = blocker[start..end];

        // 必须用 Plan 反查该批技能涉及哪些路径
        Assert.Contains("Poe2SpellBlockDatabase.Plan(spellList)", body);

        // 不能无条件清空整个备份组（那会让其余技能失去原件）
        Assert.DoesNotContain("PatchFileBackup.Clear(BackupGroup)", body);

        // 必须只移除已还原的条目
        Assert.Contains("PatchFileBackup.RemoveOriginals(BackupGroup, restoredPaths)", body);
    }

    [Fact]
    public void 技能局部还原不再全量写回备份()
    {
        string blocker = Read("src", "FreiAtlas.Patcher", "EffectPatch", "SpellEffectBlocker.cs");

        int start = blocker.IndexOf("public bool RestoreBatch", StringComparison.Ordinal);
        int end = blocker.IndexOf("private static string? LoadErrorHint", start, StringComparison.Ordinal);
        if (end < 0) end = blocker.IndexOf("private static Dictionary<string, byte[]> LoadAllExistingBackups", start, StringComparison.Ordinal);
        string body = blocker[start..end];

        // 原缺陷写法：把所有备份直接当写回列表
        Assert.DoesNotContain(
            "var files = allOriginals.Select(kv => (kv.Key, kv.Value)).ToList();", body);
    }

    // ── P1-9：恢复默认设置必须清空前端内存 ──

    [Fact]
    public void 前端恢复默认设置时清空内存配置()
    {
        string js = Read("src", "FreiAtlas.Settings", "Web", "patcher.js");

        // 三处回显都必须有 else 分支清理内存，
        // 否则后端下发 null/空数组时旧值留在内存里，再保存又写回去。
        Assert.Contains("userConfig = new Map();", js);

        // 光柱默认值复位
        int beamIdx = js.IndexOf("state.settings.mapBeamTargets.forEach", StringComparison.Ordinal);
        Assert.True(beamIdx >= 0, "未找到光柱回显");
        string beamAfter = js.Substring(beamIdx, Math.Min(900, js.Length - beamIdx));
        Assert.Contains("} else {", beamAfter);

        // 宝箱自定义名清理
        int chestIdx = js.IndexOf("state.settings.chestItems.forEach", StringComparison.Ordinal);
        Assert.True(chestIdx >= 0, "未找到宝箱回显");
        string chestAfter = js.Substring(chestIdx, Math.Min(900, js.Length - chestIdx));
        Assert.Contains("it.modifiedName = it.originalName;", chestAfter);
    }

    // ── P1-10：前端并发保护必须覆盖清洗按钮与动态按钮 ──

    [Fact]
    public void 前端并发保护覆盖清洗与动态按钮()
    {
        string js = Read("src", "FreiAtlas.Settings", "Web", "patcher.js");

        // 静态清单必须包含清洗按钮
        int idsIdx = js.IndexOf("const PATCH_ACTION_BUTTON_IDS = [", StringComparison.Ordinal);
        Assert.True(idsIdx >= 0, "未找到按钮清单");
        int idsEnd = js.IndexOf("];", idsIdx, StringComparison.Ordinal);
        Assert.True(idsEnd > idsIdx);
        Assert.Contains("'btnCleanPricePatch'", js[idsIdx..idsEnd]);

        // 动态按钮用属性标记统一处理
        int busyIdx = js.IndexOf("function applyBusyState(busy)", StringComparison.Ordinal);
        Assert.True(busyIdx >= 0, "未找到 applyBusyState");
        string busyBody = js.Substring(busyIdx, Math.Min(1200, js.Length - busyIdx));
        Assert.Contains("querySelectorAll('[data-busy-guard]')", busyBody);
    }

    // ── P1-6 配套：还原前应做版本校验 ──

    [Fact]
    public void 血条还原前做备份版本校验()
    {
        string hb = Read("src", "FreiAtlas.Patcher", "EffectPatch", "HealthBar", "HealthBarApplier.cs");
        Assert.Contains("CheckVersionMismatch", hb);
    }
}
