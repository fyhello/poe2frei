using System.Reflection;
using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 客户端文件读取的查找方式约束。
///
/// <c>PatchEngine.ReadGameFiles</c> 早期在 <c>TryGetFile</c> 未命中后，
/// 还会对 426 万条路径做一次 <c>FirstOrDefault</c> 线性遍历作为「大小写兜底」。
///
/// 但 <c>LibBundle3.Index.TryGetFile</c> 走 <c>NameHash</c> 哈希查找，
/// 而 <c>NameHash</c> 内部对路径无条件执行 <c>ToLowerInvariant</c> ——
/// 也就是说它天生忽略大小写，任何大小写变体都能命中。
///
/// 于是那段兜底是纯死代码：查不到就说明索引里确实没有该文件。
/// 更糟的是它一旦被触发（路径拼错、版本不含该资源），
/// 就要对 426 万条记录线性扫描。实测同一索引上：
///   TryGetFile 未命中      7 ms
///   线性遍历未命中     38,875 ms
/// 慢 5554 倍。
///
/// 本测试锁定：读取路径不得再出现这种线性兜底。
/// </summary>
public sealed class PatchEngineLookupTests
{
    [Fact]
    public void 读取游戏文件不得使用线性遍历兜底()
    {
        string source = FindSourceFile("PatchEngine.cs");
        string[] lines = File.ReadAllLines(source);

        // 定位 ReadGameFiles 方法体
        int start = Array.FindIndex(lines, l => l.Contains("public Dictionary<string, byte[]> ReadGameFiles(", StringComparison.Ordinal));
        Assert.True(start >= 0, "未找到 ReadGameFiles 方法");

        int depth = 0;
        bool begun = false;
        int end = start;
        for (int i = start; i < lines.Length; i++)
        {
            depth += lines[i].Count(c => c == '{');
            depth -= lines[i].Count(c => c == '}');
            if (lines[i].Contains('{')) begun = true;
            if (begun && depth <= 0) { end = i; break; }
        }

        for (int i = start; i <= end; i++)
        {
            string line = lines[i];

            // ReadGameFiles 内部不应出现任何对 Files.Values 的遍历
            Assert.False(
                line.Contains("Files.Values", StringComparison.Ordinal),
                $"第 {i + 1} 行 ReadGameFiles 中出现了 Files.Values 线性遍历：{line.Trim()}");

            // 也不应出现 FirstOrDefault / Where 之类需要全扫的 LINQ
            if (line.Contains("FirstOrDefault", StringComparison.Ordinal) ||
                line.Contains(".Where(", StringComparison.Ordinal))
            {
                Assert.Fail($"第 {i + 1} 行 ReadGameFiles 中出现了线性查找：{line.Trim()}");
            }
        }
    }

    /// <summary>
    /// <c>ReadGameFilesMatching</c> 按谓词匹配，必须全量遍历，属正常实现。
    /// 这条测试用于区分两者，避免后续误删。
    /// </summary>
    [Fact]
    public void 按模式匹配读取允许全量遍历()
    {
        string source = FindSourceFile("PatchEngine.cs");
        string text = File.ReadAllText(source);

        Assert.Contains("ReadGameFilesMatching", text, StringComparison.Ordinal);

        int start = text.IndexOf("public Dictionary<string, byte[]> ReadGameFilesMatching(", StringComparison.Ordinal);
        Assert.True(start > 0, "未找到 ReadGameFilesMatching 方法");

        // 该方法内应保留完整路径枚举（谓词匹配无法走哈希单点索引）。
        // P2-3 起 GGPK/Bundles2 都通过共享会话的 Records 枚举。
        string rest = text[start..];
        int nextMethod = rest.IndexOf("\n    public ", 10, StringComparison.Ordinal);
        string body = nextMethod > 0 ? rest[..nextMethod] : rest;

        Assert.Contains("clientIndex.Records", body, StringComparison.Ordinal);
    }

    private static string FindSourceFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string baseDir = Path.Combine(dir.FullName, "src", "FreiAtlas.Patcher");
            if (Directory.Exists(baseDir))
            {
                // 文件可能位于 Core/、Modules/ 等子目录，递归查找
                var found = Directory.GetFiles(baseDir, Path.GetFileName(fileName), SearchOption.AllDirectories);
                if (found.Length > 0) return found[0];
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"未能定位 {fileName}");
    }
}
