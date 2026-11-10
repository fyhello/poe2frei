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
    /// 每一处 SaveManifest 调用都必须检查返回值（或以 bool 形式返回给上层）。
    ///
    /// 直接写成语句 <c>SaveManifest(...);</c> 等于把「清单没写成功」这一事实丢掉：
    /// 用户会在界面上看到快照已创建 / 已删除，重启后却恢复原状。
    /// 这里按行校验，不锁定具体调用点数量 —— 重构增减调用点不应让这条约束失效。
    /// </summary>
    [Fact]
    public void 每一处SaveManifest调用都必须检查返回值()
    {
        string source = FindSourceFile("PatchBackupManager.cs");
        string[] lines = File.ReadAllLines(source);

        int callSites = 0;
        var uncheckedSites = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (!line.Contains("SaveManifest(info", StringComparison.Ordinal)) continue;
            if (line.Contains("private bool SaveManifest", StringComparison.Ordinal)) continue;

            callSites++;
            bool isChecked = line.Contains("!SaveManifest(info", StringComparison.Ordinal)
                             || line.Contains("return SaveManifest(info", StringComparison.Ordinal);
            if (!isChecked) uncheckedSites.Add($"第 {i + 1} 行：{line.Trim()}");
        }

        Assert.True(callSites >= 4, $"SaveManifest 调用点过少（{callSites}），可能未覆盖全部入口");
        Assert.True(
            uncheckedSites.Count == 0,
            "存在未检查返回值的 SaveManifest 调用：\n" + string.Join("\n", uncheckedSites));
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
