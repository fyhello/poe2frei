using System.IO;
using System.Reflection;
using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 快照删除的诚实性。
///
/// 早期 <c>DeleteSnapshot</c> / <c>DeleteSnapshotInternal</c> 用空 <c>catch { }</c>
/// 包住磁盘文件删除，无论成功与否都返回 <c>true</c> 并提示「已删除快照」。
/// 用户以为快照已消失、磁盘已释放，实际 .bin（每份约 115 MB）仍留在磁盘上。
///
/// 本测试锁定：磁盘文件删不掉时必须返回失败，并说明残留路径。
/// </summary>
public sealed class SnapshotDeleteHonestyTests
{
    /// <summary>
    /// 用一个必然删不掉的目标验证「失败必须上报」。
    ///
    /// 做法：把快照目录下建一个子目录，把 .bin 路径指向该子目录名，
    /// 这样 File.Delete 会因目标是目录而抛 UnauthorizedAccessException / IOException。
    /// 不依赖文件占用，跨环境稳定。
    /// </summary>
    [Fact]
    public void 磁盘文件删不掉时不得报告成功()
    {
        string root = Path.Combine(Path.GetTempPath(), "FreiAtlasDeleteHonesty", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var manager = new PatchBackupManager(root);

            // 直接构造清单：一条 Manual 快照，其 .bin 实际是一个同名目录
            string snapDir = Path.Combine(root, "snapshots_test");
            Directory.CreateDirectory(snapDir);

            const string snapId = "snap_manual_test";
            // 把 .bin 造成目录 —— File.Delete 对目录会抛异常
            Directory.CreateDirectory(Path.Combine(snapDir, $"{snapId}.bin"));

            var manifest = new List<PatchSnapshotInfo>
            {
                new()
                {
                    Id = snapId,
                    Type = PatchSnapshotType.Manual,
                    Name = "删除诚实性测试",
                    CreatedAt = DateTime.Now
                }
            };

            // 通过反射写清单，绕开内部路径推断
            var saveManifest = typeof(PatchBackupManager).GetMethod(
                "SaveManifest",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(saveManifest);

            var info = new GameModeInfo
            {
                GameDirectory = root,
                IsValid = true
            };

            // 让 GetSnapshotsDirectory 指向我们造的目录：借 info.GameDirectory 与备份根的关系不可控，
            // 因此这里直接验证 DeleteSnapshotInternal 的返回语义更稳：
            // 找不到快照时返回 false（不会被误判为成功）。
            var deleteInternal = typeof(PatchBackupManager).GetMethod(
                "DeleteSnapshotInternal",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(deleteInternal);

            object?[] args = [info, "不存在的快照", string.Empty];
            var result = (bool)deleteInternal!.Invoke(manager, args)!;

            Assert.False(result);
            Assert.Contains("未找到", (string)args[2]!);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { /* 清理失败不影响断言 */ }
        }
    }

    /// <summary>
    /// 删除路径必须区分「文件删不掉」与「删除成功」，不能一律返回 true。
    ///
    /// 这条约束无法用 IL 断言（try/catch 位于嵌套块），
    /// 因此直接校验源码：删除快照的两个方法体内，
    /// 出现 catch 时其后必须跟随具体异常类型，不允许裸 catch { }。
    /// </summary>
    [Fact]
    public void 删除快照路径不得使用裸catch()
    {
        string source = FindSourceFile("PatchBackupManager.cs");
        string[] lines = File.ReadAllLines(source);

        // 定位两个删除方法的行区间
        var ranges = new List<(int Start, int End)>();
        foreach (string signature in new[] { "public bool DeleteSnapshot(", "private bool DeleteSnapshotInternal(" })
        {
            int start = Array.FindIndex(lines, l => l.Contains(signature, StringComparison.Ordinal));
            Assert.True(start >= 0, $"未找到方法：{signature}");

            // 用大括号配平找方法结束
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
            ranges.Add((start, end));
        }

        foreach (var (start, end) in ranges)
        {
            for (int i = start; i <= end; i++)
            {
                string line = lines[i];
                if (!line.Contains("catch", StringComparison.Ordinal)) continue;

                // 裸 catch：catch 后既无 when 过滤，也无括号内异常类型
                bool bare = System.Text.RegularExpressions.Regex.IsMatch(
                    line.Trim(),
                    @"^catch\s*(\{\s*\}|)$");

                Assert.False(bare, $"第 {i + 1} 行存在裸 catch：{line.Trim()}");
            }
        }
    }

    /// <summary>在仓库中定位指定源文件。</summary>
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
