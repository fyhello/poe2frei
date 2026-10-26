using System.IO;
using System.Reflection;
using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 快照清单写入的诚实性。
///
/// 清单是管理全部快照的唯一索引。早期 <c>SaveManifest</c> 返回 <c>void</c> 且用
/// 裸 <c>catch { }</c> 吞掉异常，7 个调用方全部无从得知写入失败：
/// 用户看到「已创建快照」，但重启后快照消失；或「已删除快照」，但重启后又出现。
///
/// 本测试锁定：写入失败必须能被调用方感知。
/// </summary>
public sealed class ManifestWriteHonestyTests
{
    [Fact]
    public void SaveManifest_返回bool而不是void()
    {
        var method = typeof(PatchBackupManager).GetMethod(
            "SaveManifest",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.Equal(typeof(bool), method!.ReturnType);
    }

    [Fact]
    public void SaveManifest_存在LastManifestError属性()
    {
        var prop = typeof(PatchBackupManager).GetProperty("LastManifestError");

        Assert.NotNull(prop);
        Assert.Equal(typeof(string), prop!.PropertyType);
    }

    /// <summary>
    /// 能返回失败状态的 4 个调用方必须在清单写入失败时如实返回失败。
    /// 这里用源码校验，因为构造真实的磁盘只读环境跨平台不稳定。
    /// </summary>
    [Fact]
    public void 可返回失败的调用方必须检查SaveManifest返回值()
    {
        string source = FindSourceFile("PatchBackupManager.cs");
        string text = File.ReadAllText(source);

        // 这 4 处必须用 if (!SaveManifest(...)) 形式检查
        string[] mustCheck =
        [
            "if (!SaveManifest(info, list))",
            "if (!SaveManifest(info, remaining))"
        ];

        int checkedCount = mustCheck.Sum(p => CountOccurrences(text, p));
        Assert.True(checkedCount >= 4,
            $"应至少有 4 处检查 SaveManifest 返回值，实际 {checkedCount} 处");
    }

    [Fact]
    public void SaveManifest不得使用裸catch()
    {
        string source = FindSourceFile("PatchBackupManager.cs");
        string[] lines = File.ReadAllLines(source);

        int start = Array.FindIndex(lines, l =>
            l.Contains("private bool SaveManifest(", StringComparison.Ordinal));
        Assert.True(start >= 0, "未找到 SaveManifest 方法");

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
            if (!lines[i].Contains("catch", StringComparison.Ordinal)) continue;

            bool bare = System.Text.RegularExpressions.Regex.IsMatch(
                lines[i].Trim(), @"^catch\s*(\{\s*\}|)$");

            Assert.False(bare, $"第 {i + 1} 行存在裸 catch：{lines[i].Trim()}");
        }
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
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
