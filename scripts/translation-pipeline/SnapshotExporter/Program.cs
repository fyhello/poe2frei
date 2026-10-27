using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibBundle3;
using LibBundledGGPK3;

// 把客户端里的某类 .datc64 表导出到本地目录，供后续提取使用。
//
// 通过环境变量接收参数（而非命令行），因为本程序通常由 PowerShell 以反射方式
// 调用——在这种调用下拿不到命令行参数。游戏客户端路径在沙箱下禁止 dotnet 子进程
// 直接访问，只能由 PowerShell 宿主进程发起读取。
//
//   FREIATLAS_SNAP_MODE    ggpk | bundles2
//   FREIATLAS_SNAP_GAME    游戏根目录（ggpk 模式）或 Bundles2 目录（bundles2 模式）
//   FREIATLAS_SNAP_OUT     输出目录
//   FREIATLAS_SNAP_PREFIX  虚拟路径前缀，如 "data/balance/traditional chinese/"
namespace SnapshotExporter
{
    internal static class Program
    {
        private static int Main()
        {
            string mode = Environment.GetEnvironmentVariable("FREIATLAS_SNAP_MODE") ?? "";
            string game = Environment.GetEnvironmentVariable("FREIATLAS_SNAP_GAME") ?? "";
            string outDir = Environment.GetEnvironmentVariable("FREIATLAS_SNAP_OUT") ?? "";
            string prefix = Environment.GetEnvironmentVariable("FREIATLAS_SNAP_PREFIX") ?? "";
            string extension = Environment.GetEnvironmentVariable("FREIATLAS_SNAP_EXTENSION") ?? ".datc64";

            if (mode.Length == 0 || game.Length == 0 || outDir.Length == 0 || prefix.Length == 0)
            {
                Console.Error.WriteLine("缺少环境变量：FREIATLAS_SNAP_MODE / _GAME / _OUT / _PREFIX");
                return 2;
            }

            Dictionary<string, LibBundle3.Records.FileRecord> files;
            IDisposable[] owned;
            if (mode == "ggpk")
            {
                var ggpkPath = Path.Combine(game, "Content.ggpk");
                if (!File.Exists(ggpkPath))
                {
                    Console.Error.WriteLine($"未找到 {ggpkPath}");
                    return 3;
                }
                var archive = new BundledGGPK(ggpkPath, parsePathsInIndex: false);
                archive.Index.ParsePaths();
                files = archive.Index.Files.Values
                    .Where(f => !string.IsNullOrEmpty(f.Path))
                    .ToDictionary(f => f.Path.Replace('\\', '/'), f => f, StringComparer.OrdinalIgnoreCase);
                owned = new IDisposable[] { archive };
            }
            else if (mode == "bundles2")
            {
                var indexPath = Path.Combine(game, "_.index.bin");
                if (!File.Exists(indexPath))
                {
                    Console.Error.WriteLine($"未找到 {indexPath}");
                    return 3;
                }
                var factory = new DriveBundleFactory(game);
                var index = new LibBundle3.Index(indexPath, parsePaths: false, factory);
                index.ParsePaths();
                files = index.Files.Values
                    .Where(f => !string.IsNullOrEmpty(f.Path))
                    .ToDictionary(f => f.Path.Replace('\\', '/'), f => f, StringComparer.OrdinalIgnoreCase);
                owned = new IDisposable[] { index };
            }
            else
            {
                Console.Error.WriteLine($"未知模式：{mode}");
                return 2;
            }

            Directory.CreateDirectory(outDir);

            var targets = files.Keys
                .Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                            && p.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            long totalBytes = 0;
            int written = 0;
            var failures = new List<string>();

            foreach (var virtualPath in targets)
            {
                // 保留前缀之后的目录层级：CSD 存在 specific_skill_stat_descriptions/
                // 这样的子目录，平铺会导致与客户端路径无法对应。
                string relative = virtualPath.Substring(prefix.Length).TrimStart('/');
                string destination = Path.Combine(outDir, relative.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    byte[] data = files[virtualPath].Read().ToArray();
                    File.WriteAllBytes(destination, data);
                    totalBytes += data.Length;
                    written++;
                }
                catch (Exception ex)
                {
                    failures.Add($"{relative}: {ex.Message}");
                }
            }

            foreach (var disposable in owned) disposable.Dispose();

            Console.WriteLine($"模式: {mode}");
            Console.WriteLine($"前缀: {prefix}");
            Console.WriteLine($"扩展名: {extension}");
            Console.WriteLine($"匹配: {targets.Count} 个文件");
            Console.WriteLine($"导出: {written} 个，{totalBytes / 1024.0 / 1024.0:F1} MB");
            Console.WriteLine($"输出: {outDir}");

            if (failures.Count > 0)
            {
                Console.Error.WriteLine($"失败 {failures.Count} 个：");
                foreach (var message in failures.Take(10)) Console.Error.WriteLine("  " + message);
                return 4;
            }
            return 0;
        }
    }
}
