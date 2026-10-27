namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 第四轮评审 P0/P1 修复的结构级回归测试。
///
/// 这些修复涉及文件 IO 时序、UI 异常边界与跨模块调用顺序，
/// 难以在单测中构造完整场景，改用源码断言锁定「关键实现特征」。
/// 一旦有人把修复改回去（例如又把 RemoveOriginals 挪到写入之前），
/// 这里会立即失败并指出是哪个修复被破坏。
/// </summary>
public sealed class Round4StructuralRegressionTests
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

    private static int IndexOf(string text, string token, string context)
    {
        int i = text.IndexOf(token, StringComparison.Ordinal);
        Assert.True(i >= 0, $"{context}：未找到 {token}");
        return i;
    }

    // ── P0-1：顶层异常捕获必须覆盖全部异常，错误通知走 IsWriteCommand ──

    [Fact]
    public void 顶层异常捕获不限制异常类型()
    {
        string mw = Read("src", "FreiAtlas.Settings", "MainWindow.xaml.cs");

        // 定位 Web 命令分发的 catch 块：从 gateLease 声明处往后找
        int gate = IndexOf(mw, "IDisposable? gateLease = null;", "P0-1 闸门定位");
        int catchIdx = mw.IndexOf("catch (Exception exception)", gate, StringComparison.Ordinal);
        Assert.True(catchIdx > gate, "未找到 Web 命令分发的 catch 块");

        // 该 catch 不得带 when 过滤（其余方法如覆盖层开关不属补丁模块范围）
        string catchHeader = mw.Substring(catchIdx, 120);
        Assert.DoesNotContain("when (", catchHeader);
        Assert.DoesNotContain("InvalidOperationException", catchHeader);

        // 写命令必须按 IsWriteCommand 判定，而不是手写白名单
        int writeIdx = mw.IndexOf("if (IsWriteCommand(command))", catchIdx, StringComparison.Ordinal);
        Assert.True(writeIdx > catchIdx, "catch 块内必须用 IsWriteCommand 判定写命令");
        Assert.Contains("SendPatcherSnapshot(error:", mw.Substring(writeIdx, 200));
    }

    // ── P0-2：泥人换档必须先于画面优化与技能屏蔽 ──

    [Fact]
    public void 全量特效批处理中泥人最先执行()
    {
        string mw = Read("src", "FreiAtlas.Settings", "MainWindow.xaml.cs");

        int start = IndexOf(mw, "HandleApplyAllEffectsAsync(ApplyAllEffectsWebCommand", "P0-2 方法定位");
        int end = mw.IndexOf("return (ruleOk", start, StringComparison.Ordinal);
        Assert.True(end > start);
        string body = mw[start..end];

        int mud = IndexOf(body, "_patcher.ApplyMudManTier", "P0-2 泥人调用");
        int fx = IndexOf(body, "_patcher.ApplyEffectRules", "P0-2 画面优化调用");
        int spell = IndexOf(body, "_patcher.ApplySpellBlocks", "P0-2 技能屏蔽调用");

        Assert.True(mud < fx, "泥人必须在画面优化之前执行，否则回滚会抹掉后者");
        Assert.True(mud < spell, "泥人必须在技能屏蔽之前执行，否则回滚会抹掉后者");
    }

    // ── P0-3：快照写入必须原子，还原必须校验长度 ──

    [Fact]
    public void 快照写入使用原子替换()
    {
        string bm = Read("src", "FreiAtlas.Patcher", "Core", "PatchBackupManager.cs");

        Assert.Contains("private static void WriteFileAtomic", bm);
        Assert.Contains("File.Move(tmp, path, overwrite: true)", bm);

        // 两处关键写入点（快照 + 纯净底包）都必须走原子写
        Assert.True(
            bm.Split("WriteFileAtomic(").Length - 1 >= 3,
            "WriteFileAtomic 的调用点不足：快照与纯净底包的写入都必须走原子替换");

        // 不允许再对快照 .bin / 纯净底包直接 WriteAllBytes
        Assert.DoesNotContain("File.WriteAllBytes(binPath, indexBytes)", bm);
        Assert.DoesNotContain("File.WriteAllBytes(backupPath, indexBytes)", bm);
    }

    [Fact]
    public void 还原快照前校验长度()
    {
        string bm = Read("src", "FreiAtlas.Patcher", "Core", "PatchBackupManager.cs");

        int idx = IndexOf(bm, "byte[] snapshotBytes = File.ReadAllBytes(binPath)", "P0-3 快照读取");
        string after = bm.Substring(idx, Math.Min(900, bm.Length - idx));

        Assert.Contains("snapshotBytes.Length != target.SizeBytes", after);
        Assert.Contains("已拒绝还原以避免损坏客户端索引", after);
    }

    // ── P0-5：HasBackup 不得读盘 ──

    [Fact]
    public void 图标备份查询走清单而不读文件内容()
    {
        string icon = Read("src", "FreiAtlas.Patcher", "IconPatch", "IconPatchService.cs");

        Assert.Contains("CountOriginals(BackupGroup)", icon);
        Assert.DoesNotContain("LoadOriginals(BackupGroup).Count > 0", icon);

        string backup = Read("src", "FreiAtlas.Patcher", "Core", "PatchFileBackup.cs");
        Assert.Contains("public static int CountOriginals", backup);

        // CountOriginals 内部不得调用 LoadOriginals（那正是要避免的读盘路径）
        int idx = IndexOf(backup, "public static int CountOriginals", "P0-5 CountOriginals");
        int end = backup.IndexOf("public static List<", idx, StringComparison.Ordinal);
        Assert.True(end > idx);
        Assert.DoesNotContain("LoadOriginals", backup[idx..end]);
    }

    // ── P0-8：图标备份必须在写入成功后才删除 ──

    [Fact]
    public void 图标备份延迟到写入成功后删除()
    {
        string icon = Read("src", "FreiAtlas.Patcher", "IconPatch", "IconPatchService.cs");

        int applyStart = IndexOf(icon, "public bool Apply(", "P0-8 Apply 定位");
        int saveStart = icon.IndexOf("private static void SaveOriginalIcons", applyStart, StringComparison.Ordinal);
        Assert.True(saveStart > applyStart);
        string applyBody = icon[applyStart..saveStart];

        int collect = IndexOf(applyBody, "restoredPathsForCleanup", "P0-8 收集");
        int write = IndexOf(applyBody, "bool ok = _engine.WriteGameFiles", "P0-8 写入");
        int remove = IndexOf(applyBody, "PatchFileBackup.RemoveOriginals", "P0-8 删除备份");

        Assert.True(collect < write, "待清理清单必须在写入前收集");
        Assert.True(write < remove, "备份必须在写入成功后才删除，否则写入失败会丢失原件");
    }

    // ── P0-9：启动校验必须持闸门 ──

    [Fact]
    public void 启动版本校验申请补丁闸门()
    {
        string mw = Read("src", "FreiAtlas.Settings", "MainWindow.xaml.cs");

        int start = IndexOf(mw, "private void StartBackupVersionCheck", "P0-9 方法定位");
        int end = mw.IndexOf("private IDisposable? WaitPatchGate", start, StringComparison.Ordinal);
        Assert.True(end > start);
        string body = mw[start..end];

        Assert.Contains("WaitPatchGate", body);
        Assert.Contains("lease.Dispose()", body);
        Assert.Contains("private IDisposable? WaitPatchGate", mw);
    }

    // ── P1-1：CSD 染色语法必须是 {{}}，不能有 </rgb> ──

    [Fact]
    public void CsD染色标签使用花括号包裹()
    {
        string csd = Read("src", "FreiAtlas.Patcher", "Translation", "CsdSmartPatchTransformer.cs");

        // 方法体内不含 </rgb> 的【代码行】。
        // 注释里会提到「原先用 </rgb>」作为背景说明，因此逐行判断时跳过注释行。
        int codeIdx = IndexOf(csd, "private static List<string> ReapplyColorTags", "P1-1 方法定位");
        int end = csd.IndexOf("private static bool TryGetMissingRule", codeIdx, StringComparison.Ordinal);
        Assert.True(end > codeIdx);
        string[] methodLines = csd[codeIdx..end].Split('\n');

        var offenders = methodLines
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Where(l => l.Contains("</rgb>", StringComparison.Ordinal))
            .ToList();
        Assert.True(offenders.Count == 0,
            "ReapplyColorTags 中仍有 </rgb> 代码：" + string.Join(" | ", offenders.Select(o => o.Trim())));

        string body = csd[codeIdx..end];
        Assert.Contains("openWrap", body);
        Assert.Contains("closeWrap", body);
    }

    // ── P1-2：GGPK 分支必须按语言选目录 ──

    [Fact]
    public void Ggpk模式数据表路径不硬编码繁中()
    {
        string gid = Read("src", "FreiAtlas.Patcher", "Core", "GameInstallDetector.cs");

        Assert.Contains("langDirGgpk", gid);
        Assert.Contains("info.IsSimplifiedChinese", gid);

        // 不允许出现直接赋 traditional chinese 全路径
        Assert.DoesNotContain(
            "info.BaseItemsPath = \"data/balance/traditional chinese/baseitemtypes.datc64\"", gid);
    }

    // ── P1-3：快照还原无条件清状态 ──

    [Fact]
    public void 快照还原无条件清理状态文件()
    {
        string mw = Read("src", "FreiAtlas.Settings", "MainWindow.xaml.cs");

        int start = IndexOf(mw, "HandleRestorePatcherSnapshotAsync(RestorePatcherSnapshotWebCommand", "P1-3 方法定位");
        int end = mw.IndexOf("SendPatcherSnapshot(message: progressLog ?? result.Message)", start, StringComparison.Ordinal);
        Assert.True(end > start);
        string body = mw[start..end];

        int clear = IndexOf(body, "_patcher.ClearTransientPatchStates()", "P1-3 清状态");
        int branch = IndexOf(body, "if (!string.IsNullOrWhiteSpace(restoredSettingsJson))", "P1-3 设置分支");

        Assert.True(clear < branch, "清状态必须在设置分支之前无条件执行");
    }

    // ── P1-4：头顶血条不得依赖全量快照 ──

    [Fact]
    public void 头顶血条改用逐文件备份不再依赖快照()
    {
        string hb = Read("src", "FreiAtlas.Patcher", "EffectPatch", "HealthBar", "HealthBarApplier.cs");

        Assert.DoesNotContain("RestoreSnapshot", hb);
        Assert.DoesNotContain("CheckSnapshotStaleness", hb);
        Assert.DoesNotContain("EnsureRestorePoint", hb);
        Assert.Contains("PatchFileBackup.SaveOriginals", hb);
        Assert.Contains("PatchFileBackup.LoadOriginals", hb);
    }

    // ── P1-5：清单损坏必须报错而非静默降级 ──

    [Fact]
    public void 清单损坏时报错不返回空字典()
    {
        string backup = Read("src", "FreiAtlas.Patcher", "Core", "PatchFileBackup.cs");

        int start = IndexOf(backup, "private static Dictionary<string, BackupEntry> LoadManifest", "P1-5 方法定位");
        int end = backup.IndexOf("private static void SaveManifest", start, StringComparison.Ordinal);
        Assert.True(end > start);
        string body = backup[start..end];

        Assert.Contains("throw new InvalidDataException", body);
        Assert.Contains("LastManifestError", body);
        // 不允许静默 catch 后返回空清单
        Assert.DoesNotContain("宁可重新备份一份", body);
    }

    // ── P1-7：物价双表必须一次写入 ──

    [Fact]
    public void 物价两表合并为一次写入()
    {
        string price = Read("src", "FreiAtlas.Patcher", "Modules", "Price", "PricePatchService.cs");

        int start = IndexOf(price, "public PricePatchResult ApplyPatch", "P1-7 方法定位");
        int end = price.IndexOf("private bool VerifyTableContent", start, StringComparison.Ordinal);
        Assert.True(end > start);
        string body = price[start..end];

        Assert.Contains("var pendingWrites", body);
        Assert.Contains("WriteGameFiles(info, pendingWrites)", body);

        // 不允许再分别写两张表
        Assert.DoesNotContain("WriteGameFiles(info, [(targetPath, patchedBytes)])", body);
        Assert.DoesNotContain("WriteGameFiles(info, [(wordsTargetPath, wordsPatchedBytes)])", body);
    }

    // ── P1-6：逐文件备份记录版本并可校验 ──

    [Fact]
    public void 逐文件备份记录游戏版本()
    {
        string backup = Read("src", "FreiAtlas.Patcher", "Core", "PatchFileBackup.cs");

        Assert.Contains("public string? GameVersion { get; set; }", backup);
        Assert.Contains("string? gameVersion = null", backup);
        Assert.Contains("public static string? CheckVersionMismatch", backup);
    }
}
