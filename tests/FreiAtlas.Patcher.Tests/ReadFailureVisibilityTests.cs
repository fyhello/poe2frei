using FreiAtlas.Patcher.Core;
using FreiAtlas.Patcher.Modules.ColorMod;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 读取失败可见化。
///
/// 早期 <c>PatchEngine.ReadGameFiles</c> 捕获异常后只写静态属性 <c>LastReadException</c>，
/// 然后返回空字典。5 个调用方都不检查它，于是「IO 占用 / 解压损坏 / 权限异常」
/// 与「客户端确实没有这个文件」在调用方看来完全一样。
///
/// 后果举例：<c>SpellEffectBlocker</c> 读不到原件时会把空数据交给 <c>SaveOriginals</c>，
/// 备份底包被污染，后续还原会把空内容写回客户端，原件永久丢失。
///
/// 本测试锁定三件事：
///   1. 每次读取前必须清空上一次的异常（避免读到陈旧值）
///   2. 异常必须被记录到 <c>LastReadException</c>
///   3. 关键调用方必须检查该异常并中止
/// </summary>
public sealed class ReadFailureVisibilityTests
{
    /// <summary>
    /// 连续两次读取之间必须重置异常。
    ///
    /// 若不重置，第一次失败的异常会残留，第二次成功时调用方仍会看到「有错误」而误中止。
    /// </summary>
    [Fact]
    public void 每次读取前必须重置上一次的异常()
    {
        string source = FindSourceFile("PatchEngine.cs");
        string[] lines = File.ReadAllLines(source);

        foreach (string signature in new[]
                 {
                     "public Dictionary<string, byte[]> ReadGameFiles(",
                     "public Dictionary<string, byte[]> ReadGameFilesMatching("
                 })
        {
            int start = Array.FindIndex(lines, l => l.Contains(signature, StringComparison.Ordinal));
            Assert.True(start >= 0, $"未找到方法：{signature}");

            // 找 lock 块起点
            int lockLine = -1;
            for (int i = start; i < start + 20 && i < lines.Length; i++)
            {
                if (lines[i].Contains("lock (_fileLock)", StringComparison.Ordinal)) { lockLine = i; break; }
            }
            Assert.True(lockLine > 0, $"{signature} 中未找到 lock 块");

            // lock 块内前 6 行必须出现 LastReadException = null
            bool found = false;
            for (int i = lockLine; i < Math.Min(lockLine + 6, lines.Length); i++)
            {
                if (lines[i].Contains("LastReadException = null", StringComparison.Ordinal)) { found = true; break; }
            }
            Assert.True(found, $"{signature} 的 lock 块内缺少 LastReadException = null");
        }
    }

    /// <summary>
    /// 备份原件处必须检查读取异常并中止，绝不能把空数据存成「原始备份」。
    /// </summary>
    [Fact]
    public void 备份原件处必须检查读取异常并中止()
    {
        string source = FindSourceFile(Path.Combine("EffectPatch", "SpellEffectBlocker.cs"));
        string text = File.ReadAllText(source);

        // 在 SaveOriginals 之前必须出现 LastReadException 检查
        int readIdx = text.IndexOf("_engine.ReadGameFiles(info, unbackedTargets)", StringComparison.Ordinal);
        int saveIdx = text.IndexOf("PatchFileBackup.SaveOriginals(BackupGroup, unbackedTargets", StringComparison.Ordinal);
        int checkIdx = text.IndexOf("PatchEngine.LastReadException", readIdx, StringComparison.Ordinal);

        Assert.True(readIdx > 0, "未找到读取原件的调用");
        Assert.True(saveIdx > readIdx, "未找到 SaveOriginals 调用");
        Assert.True(checkIdx > readIdx && checkIdx < saveIdx,
            "SaveOriginals 之前必须先检查 LastReadException，否则空数据会被当成原始备份");
    }

    /// <summary>
    /// 染色写入处必须检查读取异常并返回失败，避免「没改任何文件却报成功」。
    /// </summary>
    [Fact]
    public void 染色写入处必须检查读取异常并返回失败()
    {
        string source = FindSourceFile("ColorModService.cs");
        string text = File.ReadAllText(source);

        int readIdx = text.IndexOf("var existingFiles = _engine.ReadGameFiles(info, csdFilesToRead);", StringComparison.Ordinal);
        Assert.True(readIdx > 0, "未找到染色写入的读取调用");

        // 读取之后 400 字符内必须出现异常检查与 return false
        string window = text.Substring(readIdx, Math.Min(500, text.Length - readIdx));
        Assert.Contains("PatchEngine.LastReadException", window, StringComparison.Ordinal);
        Assert.Contains("return false;", window, StringComparison.Ordinal);
    }

    /// <summary>
    /// 汉化注入处必须检查读取异常并写入 ErrorMessage。
    /// </summary>
    [Fact]
    public void 汉化注入处必须检查读取异常()
    {
        string source = FindSourceFile(Path.Combine("Translation", "TranslationPatchService.cs"));
        string text = File.ReadAllText(source);

        int readIdx = text.IndexOf("var loadedTables = _engine.ReadGameFiles(info, targetPaths);", StringComparison.Ordinal);
        Assert.True(readIdx > 0, "未找到汉化的读取调用");

        string window = text.Substring(readIdx, Math.Min(600, text.Length - readIdx));
        Assert.Contains("PatchEngine.LastReadException", window, StringComparison.Ordinal);
        Assert.Contains("ErrorMessage", window, StringComparison.Ordinal);
    }

    /// <summary>
    /// 静态属性在多线程下可能被后续调用覆盖，调用方必须立即快照到本地变量。
    /// </summary>
    [Fact]
    public void 调用方必须立即快照异常到本地变量()
    {
        string source = FindSourceFile(Path.Combine("EffectPatch", "SpellEffectBlocker.cs"));
        string[] lines = File.ReadAllLines(source);

        int readLine = Array.FindIndex(lines, l => l.Contains("_engine.ReadGameFiles(info, unbackedTargets)", StringComparison.Ordinal));
        Assert.True(readLine >= 0, "未找到读取调用");

        // 读取后的 3 行内必须出现 var xxx = PatchEngine.LastReadException;
        bool snapshotted = false;
        for (int i = readLine; i < Math.Min(readLine + 4, lines.Length); i++)
        {
            if (lines[i].Contains("= PatchEngine.LastReadException", StringComparison.Ordinal)) { snapshotted = true; break; }
        }

        Assert.True(snapshotted, "读取后必须立即把 LastReadException 快照到本地变量，避免被后续读取覆盖");
    }

    private static string FindSourceFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string baseDir = Path.Combine(dir.FullName, "src", "FreiAtlas.Patcher");
            if (Directory.Exists(baseDir))
            {
                // 文件可能位于 Core/、Modules/ 等子目录，递归查找
                var found = Directory.GetFiles(baseDir, Path.GetFileName(relativePath), SearchOption.AllDirectories);
                if (found.Length > 0) return found[0];
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"未能定位 {relativePath}");
    }
}
