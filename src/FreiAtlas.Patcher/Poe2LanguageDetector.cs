using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FreiAtlas.Patcher;

/// <summary>
/// PoE2 语言探测器，根据游戏配置文件与客户端文件特征动态判断语言（国服简中、国际服繁中、汉化版等）
/// </summary>
public static class Poe2LanguageDetector
{
    public const string TraditionalChineseBaseItemsPath = "data/balance/traditional chinese/baseitemtypes.datc64";
    public const string SimplifiedChineseBaseItemsPath = "data/balance/simplified chinese/baseitemtypes.datc64";

    public static IReadOnlyList<string> KnownBaseItemsPaths { get; } =
    [
        "data/balance/baseitemtypes.datc64",
        "data/balance/traditional chinese/baseitemtypes.datc64",
        "data/balance/simplified chinese/baseitemtypes.datc64",
        "data/balance/japanese/baseitemtypes.datc64",
        "data/balance/korean/baseitemtypes.datc64",
        "data/balance/russian/baseitemtypes.datc64",
        "data/balance/french/baseitemtypes.datc64",
        "data/balance/german/baseitemtypes.datc64",
        "data/balance/spanish/baseitemtypes.datc64",
        "data/balance/portuguese/baseitemtypes.datc64",
        "data/balance/thai/baseitemtypes.datc64"
    ];

    /// <summary>
    /// 从用户游戏配置目录探测当前客户端使用的语言代码 (如 zh-TW, zh-CN, en 等)
    /// </summary>
    public static string? DetectLanguageCode()
    {
        try
        {
            string configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "My Games",
                "Path of Exile 2");

            if (!Directory.Exists(configDir))
            {
                return null;
            }

            var configFiles = Directory.GetFiles(configDir, "poe2_production*_Config.ini")
                .OrderByDescending(File.GetLastWriteTime)
                .ToArray();

            if (configFiles.Length == 0)
            {
                return null;
            }

            foreach (var path in configFiles)
            {
                try
                {
                    var lines = File.ReadAllLines(path);
                    bool inLanguageSection = false;

                    foreach (var rawLine in lines)
                    {
                        var line = rawLine.Trim();
                        if (line.StartsWith('[') && line.EndsWith(']'))
                        {
                            inLanguageSection = line.Substring(1, line.Length - 2)
                                .Equals("LANGUAGE", StringComparison.OrdinalIgnoreCase);
                        }
                        else if (inLanguageSection && line.StartsWith("language", StringComparison.OrdinalIgnoreCase))
                        {
                            int eqIdx = line.IndexOf('=');
                            if (eqIdx > 0)
                            {
                                string val = line.Substring(eqIdx + 1).Trim();
                                if (!string.IsNullOrEmpty(val))
                                {
                                    return val;
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // 忽略单个文件读取异常
                }
            }
        }
        catch
        {
        }

        return null;
    }

    /// <summary>
    /// 将语言代码转换为 CSD description 文件中的段落语言名称 (如 "Simplified Chinese", "Traditional Chinese", "English")
    /// </summary>
    public static string LanguageCodeToCsdLangName(string? languageCode)
    {
        switch ((languageCode ?? "").Trim().ToLowerInvariant().Replace('_', '-'))
        {
            case "sc":
            case "zh-cn":
            case "zh-hans":
            case "simplified chinese":
            case "simplified-chinese":
                return "Simplified Chinese";

            case "tc":
            case "zh-tw":
            case "zh-hant":
            case "traditional chinese":
            case "traditional-chinese":
                return "Traditional Chinese";

            case "en":
            case "en-us":
            case "en-gb":
            case "english":
                return "English";

            case "ja":
            case "japanese":
                return "Japanese";

            case "ko":
            case "korean":
                return "Korean";

            case "ru":
            case "russian":
                return "Russian";

            case "fr":
            case "french":
                return "French";

            case "de":
            case "german":
                return "German";

            case "es":
            case "spanish":
                return "Spanish";

            case "pt":
            case "portuguese":
                return "Portuguese";

            case "th":
            case "thai":
                return "Thai";

            default:
                // 默认采用繁体中文（国际服通用），如果是国服环境则在调用处兜底为简中
                return "Traditional Chinese";
        }
    }

    /// <summary>
    /// 获取当前游戏环境的目标 CSD 语言键
    /// </summary>
    public static string GetTargetCsdLanguage(bool isChinaServer = false)
    {
        if (isChinaServer)
        {
            return "Simplified Chinese";
        }

        string? detected = DetectLanguageCode();
        if (!string.IsNullOrEmpty(detected))
        {
            return LanguageCodeToCsdLangName(detected);
        }

        return "Traditional Chinese";
    }

    /// <summary>
    /// 根据语言代码获取对应 balance 目录下的 baseitemtypes.datc64 相对路径
    /// </summary>
    public static string GetBaseItemsPath(string? languageCode, bool isChina = false)
    {
        switch ((languageCode ?? "").Trim().ToLowerInvariant().Replace('_', '-'))
        {
            case "sc":
            case "zh-cn":
            case "zh-hans":
            case "simplified chinese":
            case "simplified-chinese":
                return "data/balance/simplified chinese/baseitemtypes.datc64";

            case "tc":
            case "zh-tw":
            case "zh-hant":
            case "traditional chinese":
            case "traditional-chinese":
                return "data/balance/traditional chinese/baseitemtypes.datc64";

            case "en":
            case "en-us":
            case "en-gb":
            case "english":
                return "data/balance/baseitemtypes.datc64";

            default:
                return isChina
                    ? "data/balance/simplified chinese/baseitemtypes.datc64"
                    : "data/balance/traditional chinese/baseitemtypes.datc64";
        }
    }
}
