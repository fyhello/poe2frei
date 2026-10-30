using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

#nullable enable

namespace FreiAtlas.Patcher.Modules.ColorMod;

public static class ColorModDefaults
{
    private const string ResourceName = "FreiAtlas.Patcher.Resources.colormod.defaults.json.gz";

    private static readonly JsonSerializerOptions ResourceJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static readonly byte[] ColorRed = new byte[3] { 227, 23, 13 };

    public static readonly byte[] ColorGreen = new byte[3] { 0, 255, 127 };

    public static readonly byte[] ColorBlue = new byte[3] { 14, 186, 255 };

    public static readonly byte[] ColorYellow = new byte[3] { 231, 179, 37 };

    public static readonly byte[] ColorPink = new byte[3] { 160, 32, 240 };

    public static readonly byte[] ColorGray = new byte[3] { 170, 158, 130 };

    public static readonly byte[] ColorNone = new byte[3];

    public static readonly string[] Groups = new string[4] { "通用", "地图", "石板", "终局地图" };

    public static readonly (string CsdFile, string Group)[] GroupCsdFiles = new (string, string)[10]
    {
        ("stat_descriptions.csd", "通用"),
        ("map_stat_descriptions.csd", "地图"),
        ("atlas_stat_descriptions.csd", "地图"),
        ("tablet_stat_descriptions.csd", "石板"),
        ("expedition_relic_stat_descriptions.csd", "石板"),
        ("sanctum_relic_stat_descriptions.csd", "石板"),
        ("chest_stat_descriptions.csd", "地图"),
        ("primordial_altar_stat_descriptions.csd", "地图"),
        ("monster_stat_descriptions.csd", "地图"),
        ("endgame_map_stat_descriptions.csd", "终局地图")
    };

    public static readonly (string Prefix, string Group)[] PrefixGroupMap = new (string, string)[3]
    {
        ("map_", "地图"),
        ("expedition_", "石板"),
        ("tablet_", "石板")
    };

    public static readonly ColorModStyleOption[] StyleOptions = new ColorModStyleOption[3]
    {
        new ColorModStyleOption(ColorModStyle.Whole, "整句染色"),
        new ColorModStyleOption(ColorModStyle.NumberOnly, "仅数字染色"),
        new ColorModStyleOption(ColorModStyle.FollowGame, "跟随游戏原版")
    };

    public static readonly Dictionary<string, byte[]> DefaultColorMap;

    public static readonly Dictionary<string, ColorModStyle> DefaultStyleMap;

    public static readonly Regex NumberExprRe;

    public static readonly Regex[] FollowGamePatterns;

    public static ColorModStyle DefaultStyleFor(string displayName)
    {
        if (!NumberExprRe.IsMatch(displayName ?? ""))
        {
            return ColorModStyle.Whole;
        }
        return ColorModStyle.NumberOnly;
    }

    public static List<(int S, int E)> FollowGameSpans(string text)
    {
        List<(int, int)> list = new List<(int, int)>();
        if (string.IsNullOrEmpty(text))
        {
            return list;
        }
        Regex[] followGamePatterns = FollowGamePatterns;
        for (int i = 0; i < followGamePatterns.Length; i++)
        {
            foreach (Match item in followGamePatterns[i].Matches(text))
            {
                list.Add((item.Index, item.Index + item.Length));
            }
        }
        if (list.Count == 0)
        {
            return list;
        }
        list.Sort();
        List<(int, int)> list2 = new List<(int, int)> { list[0] };
        foreach (var item2 in list.Skip(1))
        {
            (int, int) tuple = list2[list2.Count - 1];
            if (item2.Item1 <= tuple.Item2)
            {
                list2[list2.Count - 1] = (tuple.Item1, Math.Max(tuple.Item2, item2.Item2));
            }
            else
            {
                list2.Add(item2);
            }
        }
        return list2;
    }


    public static string DetermineGroup(string statId, string csdGroup)
    {
        (string, string)[] prefixGroupMap = PrefixGroupMap;
        for (int i = 0; i < prefixGroupMap.Length; i++)
        {
            var (value, result) = prefixGroupMap[i];
            if (statId.StartsWith(value, StringComparison.Ordinal))
            {
                return result;
            }
        }
        return csdGroup;
    }

    public static string CleanDisplayText(string raw)
    {
        return CleanDisplayTextCore(raw).Trim();
    }

    public static string CleanDisplayTextCore(string raw)
    {
        return Regex.Replace(Regex.Replace(Regex.Replace(Regex.Replace(raw, "<[^>]+>", ""), "\\[[^\\|]*?\\|", "").Replace("]", ""), "%[\\u4e00-\\u9fff]+%", "").Replace("{{", "").Replace("}}", ""), "\\{[0-9](?::[^}]*)?\\}", "#").Replace("{}%", "#%").Replace("{}", "#");
    }

    public static List<CsdDescriptionBlock> ParseDescriptionBlocks(string text, string targetLang)
    {
        List<CsdDescriptionBlock> result = new List<CsdDescriptionBlock>();
        string[] array = text.Split(new string[2] { "\r\n", "\n" }, StringSplitOptions.None);
        string[]? currentStatIds = null;
        int num = 0;
        string? defaultDisplay = null;
        byte[]? defaultColor = null;
        string defaultColorTag = "";
        string? targetDisplay = null;
        byte[]? targetColor = null;
        string targetColorTag = "";
        string? text2 = null;
        foreach (string text3 in array)
        {
            string text4 = text3.TrimStart('\t', ' ');
            if (text4.StartsWith("lang "))
            {
                string text5 = text4.Substring(5).TrimEnd(':', ' ', '"').Trim('"');
                if (text2 == null && currentStatIds != null)
                {
                    _ = defaultDisplay;
                }
                text2 = text5;
                num = 0;
            }
            else if (text4.StartsWith("description"))
            {
                FlushBlock();
                currentStatIds = null;
                num = 0;
                defaultDisplay = null;
                defaultColor = null;
                defaultColorTag = "";
                targetDisplay = null;
                targetColor = null;
                targetColorTag = "";
                text2 = null;
            }
            else
            {
                if (text4.StartsWith("no_description") || text4.StartsWith("include "))
                {
                    continue;
                }
                if (currentStatIds == null)
                {
                    string[] array2 = text4.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (array2.Length >= 2 && int.TryParse(array2[0], out var _))
                    {
                        currentStatIds = array2[1..];
                    }
                    continue;
                }
                Match match = Regex.Match(text3, "\"([^\"]{1,150})\"");
                if (!match.Success && num == 0)
                {
                    string[] array3 = text4.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (array3.Length != 0 && int.TryParse(array3[0], out var result3) && result3 > 0)
                    {
                        num = result3;
                        continue;
                    }
                }
                if (match.Success && currentStatIds != null)
                {
                    string text6 = CleanDisplayText(match.Groups[1].Value);
                    byte[]? array4 = null;
                    string text7 = "";
                    Match match2 = Regex.Match(text3, "<rgb\\((\\d+),(\\d+),(\\d+)\\)>");
                    if (match2.Success)
                    {
                        array4 = new byte[3]
                        {
                            byte.Parse(match2.Groups[1].Value),
                            byte.Parse(match2.Groups[2].Value),
                            byte.Parse(match2.Groups[3].Value)
                        };
                    }
                    else
                    {
                        Match match3 = Regex.Match(text3, "<([A-Z][A-Z0-9]*)>");
                        if (match3.Success)
                        {
                            text7 = match3.Groups[1].Value;
                            array4 = MapNamedColor(text7);
                        }
                    }
                    if (text2 == null)
                    {
                        if (defaultDisplay == null)
                        {
                            defaultDisplay = text6;
                            defaultColor = array4;
                            defaultColorTag = text7;
                        }
                    }
                    else if (targetLang != null && text2 == targetLang && targetDisplay == null)
                    {
                        targetDisplay = text6;
                        targetColor = array4;
                        targetColorTag = text7;
                    }
                }
                num--;
            }
        }
        FlushBlock();
        return result;
        void FlushBlock()
        {
            if (currentStatIds != null)
            {
                string? text8 = targetDisplay ?? defaultDisplay;
                byte[]? currentColor = targetColor ?? defaultColor;
                string currentColorTag = ((targetDisplay != null) ? targetColorTag : defaultColorTag);
                if (text8 != null)
                {
                    result.Add(new CsdDescriptionBlock(currentStatIds, text8, currentColor, currentColorTag));
                }
            }
        }
    }

    private static byte[]? MapNamedColor(string tag)
    {
        return tag switch
        {
            "DF1" => new byte[3] { 0, 255, 127 }, 
            "DF2" => new byte[3] { 0, 255, 127 }, 
            "DF3" => new byte[3] { 0, 255, 127 }, 
            "DF4" => new byte[3] { 0, 255, 127 }, 
            "AT1" => new byte[3] { 231, 179, 37 }, 
            "AT2" => new byte[3] { 231, 179, 37 }, 
            "AT3" => new byte[3] { 231, 179, 37 }, 
            "AT4" => new byte[3] { 231, 179, 37 }, 
            "LUK1" => new byte[3] { 170, 158, 130 }, 
            "LUK2" => new byte[3] { 170, 158, 130 }, 
            "LUK3" => new byte[3] { 170, 158, 130 }, 
            "LUK4" => new byte[3] { 170, 158, 130 }, 
            "DA1" => new byte[3] { 227, 23, 13 }, 
            "DA2" => new byte[3] { 227, 23, 13 }, 
            "DA3" => new byte[3] { 227, 23, 13 }, 
            "DA4" => new byte[3] { 227, 23, 13 }, 
            "SP1" => new byte[3] { 160, 32, 240 }, 
            "SP2" => new byte[3] { 160, 32, 240 }, 
            "SP3" => new byte[3] { 160, 32, 240 }, 
            "SP4" => new byte[3] { 160, 32, 240 }, 
            _ => null, 
        };
    }

    public static List<ColorModEntry> BuildEntriesFromBlocks(List<CsdDescriptionBlock> blocks, string csdGroup)
    {
        HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
        List<ColorModEntry> list = new List<ColorModEntry>();
        foreach (CsdDescriptionBlock block in blocks)
        {
            string[] statIds = block.StatIds;
            foreach (string text in statIds)
            {
                if (!hashSet.Contains(text))
                {
                    hashSet.Add(text);
                    string text2 = DetermineGroup(text, csdGroup);
                    // hasDef 为 false 时 value 必为 null；下方用三元表达式已避免取值，
                    // 这里只需让编译器知道它可空。
                    bool hasDef = DefaultColorMap.TryGetValue(text, out byte[]? value);
                    bool isDanger = hasDef && value is not null
                        && (value.SequenceEqual(ColorRed) || value.SequenceEqual(ColorPink));
                    list.Add(new ColorModEntry
                    {
                        StatId = text,
                        DisplayName = block.DisplayName,
                        Color = hasDef && value is not null ? value : ColorNone,
                        Group = text2,
                        Enabled = hasDef,
                        Style = DefaultStyleFor(block.DisplayName),
                        IsDanger = isDanger,
                        CurrentColor = block.CurrentColor,
                        CurrentColorTag = block.CurrentColorTag
                    });
                }
            }
        }
        return list;
    }

    static ColorModDefaults()
    {
        var document = LoadEmbeddedData();

        var colorMap = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in document.Colors)
        {
            colorMap[entry.Id] = ToRgb(entry.Id, entry.Rgb);
        }
        DefaultColorMap = colorMap;

        var styleMap = new Dictionary<string, ColorModStyle>(StringComparer.Ordinal);
        foreach (var entry in document.Styles)
        {
            if (!Enum.TryParse<ColorModStyle>(entry.Style, ignoreCase: false, out var style)
                || !Enum.IsDefined(style))
            {
                throw new InvalidOperationException(
                    $"内嵌颜色表条目 {entry.Id} 的样式无效：{entry.Style}");
            }
            styleMap[entry.Id] = style;
        }
        DefaultStyleMap = styleMap;

        // 超时用于兜底灾难性回溯：NumberExprRe 含嵌套量词
        // (?:...)(?:...)*，输入异常长时正则引擎可能指数级回溯。
        // 正常文本只有几十个字符，2 秒足够，超时说明输入有问题。
        var regexTimeout = TimeSpan.FromSeconds(2);
        NumberExprRe = new Regex("(?:#|\\{[0-9][^}]*\\})(?:\\s*[-–—～~到至%·,，.。、/:：]?\\s*(?:#|\\{[0-9][^}]*\\}))*(?:%+|[:+][+\\-.:a-zA-Z0-9%]*)?", RegexOptions.Compiled, regexTimeout);
        FollowGamePatterns = new Regex[5]
        {
            NumberExprRe,
            new Regex("\\d+", RegexOptions.Compiled, regexTimeout),
            new Regex("【[^】]*】", RegexOptions.Compiled, regexTimeout),
            new Regex("\\[[^\\]|]*\\|?[^\\]]*\\]", RegexOptions.Compiled, regexTimeout),
            new Regex("额外|額外|附加|必定|无法|無法|不能|不再|随机|隨機|期间|期間|几率|機率|阈值|閾值|上限|下限|恢复|恢復|减少|減少|提高|降低|所有|总是|總是|賦予|赋予", RegexOptions.Compiled, regexTimeout)
        };
    }

    private static ColorModDocument LoadEmbeddedData()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"内嵌颜色表资源缺失：{ResourceName}");

        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        var document = JsonSerializer.Deserialize<ColorModDocument>(gzip, ResourceJsonOptions)
            ?? throw new InvalidOperationException($"内嵌颜色表资源为空：{ResourceName}");

        if (document.Version != 1)
        {
            throw new InvalidOperationException(
                $"内嵌颜色表版本不支持：{document.Version}（期望 1）");
        }
        return document;
    }

    private static byte[] ToRgb(string id, int[]? rgb)
    {
        if (rgb is not { Length: 3 } || rgb.Any(value => value is < 0 or > 255))
        {
            throw new InvalidOperationException($"内嵌颜色表条目 {id} 的 RGB 值无效。");
        }
        return [(byte)rgb[0], (byte)rgb[1], (byte)rgb[2]];
    }

    private sealed class ColorModDocument
    {
        public int Version { get; set; }

        public List<ColorEntry> Colors { get; set; } = [];

        public List<StyleEntry> Styles { get; set; } = [];
    }

    private sealed class ColorEntry
    {
        public string Id { get; set; } = string.Empty;

        public int[]? Rgb { get; set; }
    }

    private sealed class StyleEntry
    {
        public string Id { get; set; } = string.Empty;

        public string Style { get; set; } = string.Empty;
    }
}
