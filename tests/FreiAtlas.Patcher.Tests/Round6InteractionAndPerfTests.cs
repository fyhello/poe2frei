namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 第六轮：实机反馈的 6 项交互/UI/性能问题的回归测试。
///
/// 这些问题全部来自实际使用，前四轮静态评审未覆盖（只查内核逻辑，不查操作路径）。
/// 测试以源码断言锁定，防止改动退回。
/// </summary>
public sealed class Round6InteractionAndPerfTests
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

    // ── 痛点 1：汉化日志必须有非 null 的收尾消息 ──

    [Fact]
    public void 汉化完成后发送非空收尾消息()
    {
        string mw = Read("src", "FreiAtlas.Settings", "MainWindow.xaml.cs");

        int start = mw.IndexOf("private async Task HandleApplyTranslationPatchAsync", StringComparison.Ordinal);
        Assert.True(start >= 0, "未找到汉化 handler 定义");

        // 方法体到下一个成员声明为止
        int end = mw.IndexOf("private async Task Handle", start + 100, StringComparison.Ordinal);
        if (end < 0) end = mw.IndexOf("private ", start + 100, StringComparison.Ordinal);
        Assert.True(end > start, "未找到汉化 handler 结束位置");
        string body = mw[start..end];

        // 词库刷新后必须发一条带内容的完成消息（message 为 null 会让前端停在旧文案）
        Assert.Contains("客户端词库已刷新", body);

        // catch 必须能拿到异常信息，不能裸捕获后静默 return
        Assert.Contains("catch (Exception scanEx)", body);
        Assert.Contains("刷新词库失败", body);
    }

    // ── 痛点 2：血条不得在勾选时立即写盘 ──

    [Fact]
    public void 血条勾选只改前端状态不直接写盘()
    {
        string js = Read("src", "FreiAtlas.Settings", "Web", "patcher.js");

        // 不再保留「勾选即写盘」的函数
        Assert.DoesNotContain("function toggleHealthBar", js);
        Assert.DoesNotContain("post('applyHealthBar'", js);
        Assert.DoesNotContain("post('restoreHealthBar'", js);

        // 改为内存状态变量
        Assert.Contains("let healthBarDesired", js);

        // change 事件只记录状态
        int idx = js.IndexOf("chkOpt-healthbar')?.addEventListener('change'", StringComparison.Ordinal);
        Assert.True(idx >= 0, "未找到血条勾选框事件");
        string handler = js[idx..(idx + 320)];
        Assert.Contains("healthBarDesired = e.target.checked", handler);
        Assert.DoesNotContain("post(", handler);
    }

    [Fact]
    public void 血条随批量命令一起提交()
    {
        string js = Read("src", "FreiAtlas.Settings", "Web", "patcher.js");

        int start = js.IndexOf("function applyAllEffectSettings", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = js.IndexOf("function ", start + 50, StringComparison.Ordinal);
        string body = js[start..end];

        Assert.Contains("healthBarEnabled", body);
        Assert.Contains("post(\"applyAllEffects\"", body);
    }

    [Fact]
    public void 后端批量命令处理血条状态比对()
    {
        string mw = Read("src", "FreiAtlas.Settings", "MainWindow.xaml.cs");

        int start = mw.IndexOf("private async Task HandleApplyAllEffectsAsync", StringComparison.Ordinal);
        Assert.True(start >= 0, "未找到 HandleApplyAllEffectsAsync 定义");
        int end = mw.IndexOf("private async Task Handle", start + 100, StringComparison.Ordinal);
        if (end < 0) end = mw.IndexOf("private ", start + 100, StringComparison.Ordinal);
        Assert.True(end > start, "未找到方法结束位置");
        string body = mw[start..end];

        Assert.Contains("cmd.HealthBarEnabled", body);
        Assert.Contains("_patcher.IsHealthBarApplied()", body);
        Assert.Contains("_patcher.ApplyHealthBar(", body);
        Assert.Contains("_patcher.RestoreHealthBar(", body);
    }

    // ── 痛点 3：特效卡片不得有第二个保存按钮 ──

    [Fact]
    public void 特效卡片内不再有重复保存按钮()
    {
        string html = Read("src", "FreiAtlas.Settings", "Web", "index.html");

        Assert.DoesNotContain("id=\"btnApplyEffectRules\"", html);
        // 顶栏统一入口必须保留
        Assert.Contains("id=\"btnApplyCurrentWorkbench\"", html);
        // 还原按钮是独立语义，应保留
        Assert.Contains("id=\"btnRestoreEffectRules\"", html);
    }

    // ── 痛点 4：图标雪碧图必须随 Web 资源输出 ──

    [Fact]
    public void 图标雪碧图配置为随包输出()
    {
        string csproj = Read("src", "FreiAtlas.Settings", "FreiAtlas.Settings.csproj");

        Assert.Contains("icons.sheet.webp", csproj);
        Assert.Contains("Web\\icons.sheet.webp", csproj);

        // 源文件必须真实存在
        string src = Path.Combine(RepoRoot(), "src", "FreiAtlas.Patcher", "Resources", "iconpatch", "icons.sheet.webp");
        Assert.True(File.Exists(src), $"图标雪碧图源文件不存在：{src}");
    }

    [Fact]
    public void 前端引用雪碧图使用相对路径()
    {
        string js = Read("src", "FreiAtlas.Settings", "Web", "patcher.js");
        // 与 csproj 的 Link 目标保持一致（Web 根目录）
        Assert.Contains("url(icons.sheet.webp)", js);
    }

    // ── 痛点 5：字体补丁必须批量读，不能循环单读 ──

    [Fact]
    public void 字体补丁使用批量读取而非循环单读()
    {
        string font = Read("src", "FreiAtlas.Patcher", "Modules", "Font", "FontPatchService.cs");

        // 两个入口都必须走批量读
        Assert.True(
            (font.Split("_engine.ReadGameFiles(").Length - 1) >= 2,
            "ScanFontSettings 与 ApplyFont 都应使用 ReadGameFiles 批量读取");

        // 不允许在循环里逐个读（每次都会重新解析整份索引）
        Assert.DoesNotContain("_engine.ReadGameFile(info, path", font);
    }

    [Fact]
    public void 字体补丁批量读取覆盖全部UiSettingPaths()
    {
        string font = Read("src", "FreiAtlas.Patcher", "Modules", "Font", "FontPatchService.cs");

        // 批量读取必须把整个路径表传进去，而不是只读一个
        Assert.Contains("_engine.ReadGameFiles(info, UiSettingPaths)", font);
    }

    // ── 痛点 6：全屏假倒计时遮罩必须彻底移除 ──

    [Fact]
    public void 全屏遮罩已彻底移除()
    {
        string js = Read("src", "FreiAtlas.Settings", "Web", "patcher.js");

        Assert.DoesNotContain("showEffectOperationOverlay", js);
        Assert.DoesNotContain("hideEffectOperationOverlay", js);

        // 旧的假倒计时痕迹也不应残留
        Assert.DoesNotContain("勿拔掉硬盘", js);
        Assert.DoesNotContain("effectPatchOperationOverlay", js);
    }

    [Fact]
    public void 写入状态统一由按钮禁用与顶部状态条呈现()
    {
        string js = Read("src", "FreiAtlas.Settings", "Web", "patcher.js");

        // 统一的忙碌状态入口
        Assert.Contains("function applyBusyState(busy)", js);
        // 顶部状态条
        Assert.Contains("patcherFeedback", js);
    }
}
