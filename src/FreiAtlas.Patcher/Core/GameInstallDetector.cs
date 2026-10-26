using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace FreiAtlas.Patcher.Core;

public static class GameInstallDetector
{
    private static readonly string[] KnownSearchPaths =
    [
        @"E:\PSAutoRecover\ui\rood\Grinding Gear Games\Path of Exile 2",
        @"C:\Program Files (x86)\Steam\steamapps\common\Path of Exile 2",
        @"D:\Steam\steamapps\common\Path of Exile 2",
        @"D:\SteamLibrary\steamapps\common\Path of Exile 2",
        @"E:\Steam\steamapps\common\Path of Exile 2",
        @"E:\SteamLibrary\steamapps\common\Path of Exile 2",
        @"F:\Steam\steamapps\common\Path of Exile 2",
        @"F:\SteamLibrary\steamapps\common\Path of Exile 2",
        @"D:\WeGameApps\rail_apps\Path of Exile 2",
        @"E:\WeGameApps\rail_apps\Path of Exile 2",
        @"C:\Program Files (x86)\Grinding Gear Games\Path of Exile 2",
        @"D:\Grinding Gear Games\Path of Exile 2",
        @"E:\Grinding Gear Games\Path of Exile 2"
    ];

    public static GameModeInfo Detect(string? preferredDirectory = null)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(preferredDirectory))
        {
            candidates.Add(preferredDirectory.Trim());
        }

        // Try detecting from active process
        try
        {
            foreach (var name in new[] { "PathOfExile", "PathOfExile_x64", "Client", "PathOfExileSteam" })
            {
                var procs = Process.GetProcessesByName(name);
                foreach (var proc in procs)
                {
                    try
                    {
                        var mainModule = proc.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(mainModule))
                        {
                            var dir = Path.GetDirectoryName(mainModule);
                            if (!string.IsNullOrEmpty(dir) && !candidates.Contains(dir, StringComparer.OrdinalIgnoreCase))
                            {
                                candidates.Add(dir);
                            }
                        }
                    }
                    catch
                    {
                        // 读取其他进程的 MainModule 需要更高权限，32/64 位进程间也常失败。
                        // 探测类逻辑：拿不到就跳过这个进程，继续试下一个候选来源。
                    }
                }
            }
        }
        catch
        {
            // 枚举进程整体失败（权限、进程已退出等）。安装探测是尽力而为，
            // 失败后仍会继续尝试注册表与 Steam 库路径。
        }

        // Check registry
        AddRegistryCandidates(candidates);

        // Check Steam library folders
        AddSteamCandidates(candidates);

        // Add hardcoded known paths
        foreach (var p in KnownSearchPaths)
        {
            if (!candidates.Contains(p, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(p);
            }
        }

        // Evaluate first valid candidate
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate))
            {
                continue;
            }

            var info = EvaluateDirectory(candidate);
            if (info.IsValid)
            {
                return info;
            }
        }

        return new GameModeInfo
        {
            IsValid = false,
            ErrorMessage = "未检测到有效的 PoE2 游戏目录。请在设置中指定游戏安装目录。"
        };
    }

    public static GameModeInfo EvaluateDirectory(string gameDirectory)
    {
        var info = new GameModeInfo
        {
            GameDirectory = gameDirectory
        };

        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
        {
            info.ErrorMessage = "目录不存在";
            return info;
        }

        string ggpkPath = Path.Combine(gameDirectory, "Content.ggpk");
        string indexPath = Path.Combine(gameDirectory, "Bundles2", "_.index.bin");

        if (File.Exists(ggpkPath))
        {
            info.Mode = GameMode.GGPK;
            info.DisplayName = "国际服官方 GGPK";
            info.IsChina = false;
            // 国际服可以是繁中、简中（打过汉化）或英文，必须以配置文件为准，
            // 硬编码 zh-TW 会让简中环境下输出繁体内容。
            info.LanguageCode = Poe2LanguageDetector.DetectLanguageCode() ?? "zh-TW";
            info.BaseItemsPath = "data/balance/traditional chinese/baseitemtypes.datc64";
            info.WordsPath = "data/balance/traditional chinese/words.datc64";
            info.EndgameMapsPath = "data/balance/traditional chinese/endgamemaps.datc64";
            info.WorldAreasPath = "data/balance/traditional chinese/worldareas.datc64";
            info.ChestsPath = "data/balance/traditional chinese/chests.datc64";
            info.IsValid = true;
            return info;
        }

        if (File.Exists(indexPath))
        {
            info.Mode = GameMode.Bundles2;

            // Check if WeGame (Tencent)
            int wegameMarkers = 0;
            string[] markers = ["wegame.ini", "rail_api64.dll", "rail_files", "TCLS", "AntiCheatExpert"];
            foreach (var m in markers)
            {
                if (File.Exists(Path.Combine(gameDirectory, m)) || Directory.Exists(Path.Combine(gameDirectory, m)))
                {
                    wegameMarkers++;
                }
            }

            if (wegameMarkers >= 1)
            {
                info.DisplayName = "国服 WeGame (Bundles2)";
                info.IsChina = true;
                info.LanguageCode = "zh-CN";
            }
            else
            {
                info.DisplayName = "Steam / Epic (Bundles2)";
                info.IsChina = false;
                // Steam / Epic 客户端语言各异，同样以配置文件为准
                info.LanguageCode = Poe2LanguageDetector.DetectLanguageCode() ?? "zh-TW";
            }

            // 数据表目录必须跟随客户端实际语言：客户端读哪个目录，补丁就必须写哪个目录。
            // 简体客户端读 simplified chinese，繁体客户端读 traditional chinese。
            string langDir = info.IsSimplifiedChinese
                ? "data/balance/simplified chinese"
                : "data/balance/traditional chinese";
            info.BaseItemsPath = $"{langDir}/baseitemtypes.datc64";
            info.WordsPath = $"{langDir}/words.datc64";
            info.EndgameMapsPath = $"{langDir}/endgamemaps.datc64";
            info.WorldAreasPath = $"{langDir}/worldareas.datc64";
            info.ChestsPath = $"{langDir}/chests.datc64";

            info.IsValid = true;
            return info;
        }

        info.ErrorMessage = "目录下既未找到 Content.ggpk 也未找到 Bundles2/_.index.bin";
        return info;
    }

    private static void AddRegistryCandidates(List<string> candidates)
    {
        try
        {
            RegistryKey?[] keys =
            [
                Registry.CurrentUser.OpenSubKey(@"SOFTWARE\GrindingGearGames"),
                Registry.LocalMachine.OpenSubKey(@"SOFTWARE\GrindingGearGames"),
                Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GrindingGearGames"),
                Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall")
            ];

            foreach (var parent in keys)
            {
                if (parent == null) continue;
                using (parent)
                {
                    foreach (var subName in parent.GetSubKeyNames())
                    {
                        if (subName.Contains("Path of Exile", StringComparison.OrdinalIgnoreCase) ||
                            subName.Contains("PoE2", StringComparison.OrdinalIgnoreCase) ||
                            subName.Contains("流放之路", StringComparison.OrdinalIgnoreCase))
                        {
                            using var sub = parent.OpenSubKey(subName);
                            var loc = sub?.GetValue("InstallLocation") as string ?? sub?.GetValue("Path") as string;
                            if (!string.IsNullOrWhiteSpace(loc) && !candidates.Contains(loc, StringComparer.OrdinalIgnoreCase))
                            {
                                candidates.Add(loc);
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // 注册表读取失败（键不存在、无权限、非 Windows）。探测类逻辑，
            // 跳过注册表来源，继续用 Steam 库与目录扫描找客户端。
        }
    }

    private static void AddSteamCandidates(List<string> candidates)
    {
        try
        {
            using var steamKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam") ??
                                 Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            var steamPath = steamKey?.GetValue("SteamPath") as string;
            if (string.IsNullOrEmpty(steamPath)) return;

            var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdfPath))
            {
                foreach (var line in File.ReadAllLines(vdfPath))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = trimmed.Split('"');
                        if (parts.Length >= 4)
                        {
                            var lib = parts[3].Replace(@"\\", @"\");
                            var gameDir = Path.Combine(lib, "steamapps", "common", "Path of Exile 2");
                            if (!candidates.Contains(gameDir, StringComparer.OrdinalIgnoreCase))
                            {
                                candidates.Add(gameDir);
                            }
                        }
                    }
                }
            }
        }
        catch { }
    }
}
