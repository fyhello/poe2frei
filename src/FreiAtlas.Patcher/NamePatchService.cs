using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FreiAtlas.Patcher;

public enum NamePatchKind
{
    Chests,
    ArchnemesisMods
}

public sealed class NamePatchItem
{
    public int RowIndex { get; set; }
    public List<int> GroupedRowIndices { get; set; } = [];
    public string IdField { get; set; } = string.Empty;
    public string OriginalName { get; set; } = string.Empty;
    public string ModifiedName { get; set; } = string.Empty;
    public string Memo { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string DisplayName => !string.IsNullOrEmpty(Memo) ? Memo : ModifiedName;
    public bool IsModified => !string.IsNullOrEmpty(DisplayName) && DisplayName != OriginalName;
}

public sealed record ChestSemanticRule(
    string[] Keywords,
    string Category,
    string Tag,
    string? ColorRgb = null,
    string? Prefix = null,
    string? Suffix = null
);

public sealed class NamePatchService
{
    private sealed record KindInfo(string FileName, int NameOffset)
    {
        public bool IsIdString => FileName != "archnemesismods.datc64";
    }

    private record DatLayout(int RowCount, int RowSize, int StringBase);

    private static readonly Dictionary<NamePatchKind, KindInfo> KindInfos = new()
    {
        [NamePatchKind.ArchnemesisMods] = new KindInfo("archnemesismods.datc64", 16),
        [NamePatchKind.Chests] = new KindInfo("chests.datc64", 13)
    };

    private static readonly Regex ColorWrapRe = new(@"^<rgb\([^)]*\)>\{(.+)\}$", RegexOptions.Compiled);
    private static readonly Regex DecorationWrapRe = new(@"^(?:<[^>]*>\{[^}]*\})+(.*?)(?:<[^>]*>\{[^}]*\})+$", RegexOptions.Compiled);

    public static readonly Dictionary<string, string> DefaultArchnemesisTextsSimplified = new(StringComparer.Ordinal)
    {
        ["阿曼娜姆的虚空"] = "<rgb(227,23,13)>{￥￥￥}阿曼娜姆的虚空<rgb(227,23,13)>{￥￥￥}"
    };

    public static readonly Dictionary<string, string> DefaultArchnemesisTextsTraditional = new(StringComparer.Ordinal)
    {
        ["阿姆那姆的虛無"] = "<rgb(227,23,13)>{￥￥￥}阿姆那姆的虛無<rgb(227,23,13)>{￥￥￥}"
    };

    public static readonly Dictionary<string, string> DefaultChestTextsSimplified = new(StringComparer.Ordinal)
    {
        ["秘术保险箱"] = "秘术保险箱[法系装备]",
        ["研究员的保险箱"] = "研究员的保险箱[通货]",
        ["白银奥术师的宝匣"] = "白银奥术师的宝匣[通货]",
        ["优异白银奥术师的宝匣"] = "优异白银奥术师的宝匣[通货]",
        ["至尊白银奥术师的宝匣"] = "至尊白银奥术师的宝匣[通货]",
        ["白银皇家宝匣"] = "白银皇家宝匣[珠宝]",
        ["优异白银皇家宝匣"] = "优异白银皇家宝匣[珠宝]",
        ["至尊白银皇家宝匣"] = "至尊白银皇家宝匣[珠宝]",
        ["黄金奥术师的宝匣"] = "黄金奥术师的宝匣[通货]",
        ["优异黄金奥术师的宝匣"] = "优异黄金奥术师的宝匣[通货]",
        ["至尊黄金奥术师的宝匣"] = "至尊黄金奥术师的宝匣[通货]",
        ["黄金皇家宝匣"] = "黄金皇家宝匣[珠宝]",
        ["优异黄金皇家宝匣"] = "优异黄金皇家宝匣[珠宝]",
        ["至尊黄金皇家宝匣"] = "至尊黄金皇家宝匣[珠宝]",
        ["黄金失落宝匣"] = "<rgb(166,90,255)>{黄金失落宝匣[时迭珠宝]}",
        ["黄金聚光宝匣"] = "<rgb(255,0,0)>{黄金聚光宝匣[★光谱珠宝]}",
        ["紧握之手"] = "<rgb(166,90,255)>{紧握之手}",
        ["裂隙之主的紧握之手"] = "<rgb(166,90,255)>{裂隙之主的紧握之手}",
        ["薛乌勒的紧握之手"] = "<rgb(166,90,255)>{薛乌勒的紧握之手}",
        ["深渊密箱"] = "<rgb(211,50,247)>{■■深渊密箱■■}",
        ["菌潮囊肿"] = "<rgb(102,255,255)>{菌潮囊肿}",
        ["发掘的箱子"] = "<rgb(0,255,128)>{发掘的箱子}"
    };

    public static readonly Dictionary<string, string> DefaultChestTextsTraditional = new(StringComparer.Ordinal)
    {
        ["秘術保險箱"] = "秘術保險箱[法系裝備]",
        ["研究者的保險箱"] = "研究者的保險箱[通貨]",
        ["白銀奧術師貯藏箱"] = "白銀奧術師貯藏箱[通貨]",
        ["高階白銀奧術師貯藏箱"] = "高階白銀奧術師貯藏箱[通貨]",
        ["至尊白銀奧術師貯藏箱"] = "至尊白銀奧術師貯藏箱[通貨]",
        ["白銀皇家貯藏箱"] = "白銀皇家貯藏箱[珠寶]",
        ["高階白銀皇家貯藏箱"] = "高階白銀皇家貯藏箱[珠寶]",
        ["至尊白銀皇家貯藏箱"] = "至尊白銀皇家貯藏箱[珠寶]",
        ["黃金奧術師貯藏箱"] = "黃金奧術師貯藏箱[通貨]",
        ["高階黃金奧術師貯藏箱"] = "高階黃金奧術師貯藏箱[通貨]",
        ["至尊黃金奧術師貯藏箱"] = "至尊黃金奧術師貯藏箱[通貨]",
        ["黃金皇家貯藏箱"] = "黃金皇家貯藏箱[珠寶]",
        ["高階黃金皇家貯藏箱"] = "高階黃金皇家貯藏箱[珠寶]",
        ["至尊黃金皇家貯藏箱"] = "至尊黃金皇家貯藏箱[珠寶]",
        ["黃金時迭貯藏箱"] = "<rgb(166,90,255)>{黃金時迭貯藏箱[時迭珠寶]}",
        ["黃金光譜密盒"] = "<rgb(255,0,0)>{黃金光譜密盒[★光譜珠寶]}",
        ["保險箱"] = "保險箱[全道具]",
        ["製圖師的保險箱"] = "製圖師的保險箱[地圖]",
        ["華麗保險箱"] = "華麗保險箱[全道具]",
        ["大型保險箱"] = "大型保險箱[全道具]",
        ["護甲鍛造師的保險箱"] = "護甲鍛造師的保險箱[防具]",
        ["工匠的保險箱"] = "工匠的保險箱[飾品]",
        ["鐵匠的保險箱"] = "鐵匠的保險箱[武器]",
        ["抑制之手"] = "<rgb(166,90,255)>{抑制之手}",
        ["裂痕君王的抑制之手"] = "<rgb(166,90,255)>{裂痕君王的抑制之手}",
        ["烈許烏拉的抑制之手"] = "<rgb(166,90,255)>{烈許烏拉的抑制之手}",
        ["深淵密箱"] = "<rgb(211,50,247)>{■■深淵密箱■■}",
        ["凋落囊胞"] = "<rgb(102,255,255)>{凋落囊胞}"
    };

    private readonly PatchEngine _engine;

    public NamePatchService(PatchEngine engine)
    {
        _engine = engine;
    }

    public static string StripNamePatchSuffix(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        var match = DecorationWrapRe.Match(name);
        if (match.Success)
        {
            return TrimBracketSuffix(match.Groups[1].Value.Trim('■'));
        }
        match = ColorWrapRe.Match(name);
        if (match.Success)
        {
            return TrimBracketSuffix(match.Groups[1].Value.Trim('■'));
        }
        return TrimBracketSuffix(name.Trim('■'));
    }

    private static string TrimBracketSuffix(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        int num = name.IndexOf('[');
        if (num <= 0) return name;
        int num2 = name.IndexOf(']', num);
        if (num2 < 0) return name;
        if (name.Substring(num + 1, num2 - num - 1).Contains('|'))
        {
            return name;
        }
        return name[..num];
    }

    public static readonly List<ChestSemanticRule> ChestRules =
    [
        new(["失落", "時迭", "Time-Lost"], "时迭珠宝", "时迭珠宝", "166,90,255"),
        new(["聚光", "光譜", "Spectrum"], "光谱珠宝", "★光谱珠宝", "255,0,0"),
        new(["紧握之手", "抑制之手", "Clasping Hand"], "裂隙", "", "166,90,255"),
        new(["裂隙之主", "裂痕君王", "Breachlord"], "裂隙", "", "166,90,255"),
        new(["薛乌勒", "烈許烏拉", "Uul-Netol", "Xoph", "Esh", "Tul", "Chayula"], "裂隙", "", "166,90,255"),
        new(["深渊密箱", "深淵密箱", "Abyssal Trove"], "深渊", "", "211,50,247", "■■", "■■"),
        new(["菌潮囊肿", "凋落囊胞", "Blighted Cyst"], "菌潮", "", "102,255,255"),
        new(["发掘的箱子", "發掘的箱子", "Excavated Chest"], "探险", "", "0,255,128"),
        new(["奥术师", "奧術師", "Arcanist"], "通货", "通货"),
        new(["皇家", "Royal", "Jeweller"], "珠宝", "珠宝"),
        new(["秘术", "秘術", "Arcane"], "装备", "法系装备"),
        new(["研究员", "研究者", "Researcher"], "通货", "通货"),
        new(["制图师", "製圖師", "Cartographer"], "地图", "地图"),
        new(["护甲锻造师", "護甲鍛造師", "Armourer"], "装备", "防具"),
        new(["工匠", "Artisan"], "装备", "饰品"),
        new(["铁匠", "鐵匠", "Blacksmith"], "装备", "武器"),
        new(["华丽", "華麗", "Ornate"], "全道具", "全道具"),
        new(["大型", "Large"], "全道具", "全道具"),
        new(["保险箱", "保險箱", "Strongbox"], "基础", "全道具")
    ];

    public static ChestSemanticRule? MatchChestRule(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string clean = StripNamePatchSuffix(name);
        foreach (var rule in ChestRules)
        {
            if (rule.Keywords.Any(kw => clean.Contains(kw, StringComparison.OrdinalIgnoreCase)))
            {
                return rule;
            }
        }
        return null;
    }

    public static string GetDefaultMemo(string name, bool isChina, NamePatchKind kind)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        string key = StripNamePatchSuffix(name);
        if (kind == NamePatchKind.ArchnemesisMods)
        {
            var dict = isChina ? DefaultArchnemesisTextsSimplified : DefaultArchnemesisTextsTraditional;
            return dict.TryGetValue(key, out var val) ? val : string.Empty;
        }

        var chestDict = isChina ? DefaultChestTextsSimplified : DefaultChestTextsTraditional;
        if (chestDict.TryGetValue(key, out var preset))
        {
            return preset;
        }

        var rule = MatchChestRule(key);
        if (rule != null)
        {
            if (!string.IsNullOrEmpty(rule.ColorRgb))
            {
                string tagPart = !string.IsNullOrEmpty(rule.Tag) ? $"[{rule.Tag}]" : "";
                return $"<rgb({rule.ColorRgb})>{{{rule.Prefix ?? ""}{key}{tagPart}{rule.Suffix ?? ""}}}";
            }
            if (!string.IsNullOrEmpty(rule.Tag))
            {
                return $"{key}[{rule.Tag}]";
            }
        }

        return string.Empty;
    }

    public bool ApplyNamePatch(
        GameModeInfo info,
        NamePatchKind kind,
        Dictionary<string, string>? customMappings = null,
        Action<string>? progress = null)
    {
        string label = kind == NamePatchKind.Chests ? "保险箱标注" : "宿敌怪物词缀高亮";
        progress?.Invoke($"正在定位 {label} 数据文件...");

        string[] candidatePaths = kind == NamePatchKind.Chests
            ? [
                info.ChestsPath,
                "data/balance/simplified chinese/chests.datc64",
                "data/balance/traditional chinese/chests.datc64",
                "data/balance/chinese/chests.datc64",
                "data/simplified chinese/chests.datc64",
                "data/traditional chinese/chests.datc64",
                "data/chests.datc64"
            ]
            : [
                info.ArchnemesisModsPath,
                "data/balance/simplified chinese/archnemesismods.datc64",
                "data/balance/traditional chinese/archnemesismods.datc64",
                "data/balance/chinese/archnemesismods.datc64",
                "data/simplified chinese/archnemesismods.datc64",
                "data/traditional chinese/archnemesismods.datc64",
                "data/archnemesismods.datc64"
            ];

        byte[]? datBytes = null;
        string actualPath = string.Empty;

        foreach (var p in candidatePaths.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            datBytes = _engine.ReadGameFile(info, p, out actualPath);
            if (datBytes != null && datBytes.Length > 0)
            {
                break;
            }
        }

        if (datBytes == null || datBytes.Length == 0)
        {
            progress?.Invoke($"未在客户端中找到匹配的 {label} 数据文件。");
            return false;
        }

        progress?.Invoke($"已读取 {actualPath}，正在解析条目...");
        var items = ParseFromBytes(datBytes, info.IsChina, kind);

        // 应用预设或用户自定义覆盖
        int enabledCount = 0;
        foreach (var item in items)
        {
            if (customMappings != null && customMappings.TryGetValue(item.OriginalName, out var userCustom))
            {
                item.Memo = userCustom;
                item.Enabled = true;
                enabledCount++;
            }
            else if (!string.IsNullOrEmpty(item.Memo))
            {
                item.Enabled = true;
                enabledCount++;
            }
        }

        if (enabledCount == 0)
        {
            progress?.Invoke($"未在当前客户端数据中匹配到可应用的 {label} 预设。");
            return false;
        }

        progress?.Invoke($"已匹配到 {enabledCount} 个增强条目，正在重写数据并构建微型 Bundle...");
        var modifiedData = ApplyNamePatchChanges(datBytes, items, kind);

        bool success = _engine.WriteGameFiles(info, [(actualPath, modifiedData)]);
        progress?.Invoke(success ? $"{label}补丁已成功应用！（已生效 {enabledCount} 条标记）" : $"{label}补丁写入失败。");
        return success;
    }

    public List<NamePatchItem> ScanClientChests(GameModeInfo info)
    {
        string[] candidatePaths = [
            info.ChestsPath,
            "data/balance/simplified chinese/chests.datc64",
            "data/balance/traditional chinese/chests.datc64",
            "data/balance/chinese/chests.datc64",
            "data/simplified chinese/chests.datc64",
            "data/traditional chinese/chests.datc64",
            "data/chests.datc64"
        ];

        byte[]? datBytes = null;
        foreach (var p in candidatePaths.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            datBytes = _engine.ReadGameFile(info, p, out _);
            if (datBytes != null && datBytes.Length > 0)
            {
                break;
            }
        }

        if (datBytes == null || datBytes.Length == 0)
        {
            return [];
        }

        return ParseAndAggregateChests(datBytes, info.IsChina);
    }

    public static List<NamePatchItem> ParseAndAggregateChests(byte[] data, bool isChina)
    {
        var rawItems = ParseFromBytes(data, isChina, NamePatchKind.Chests);
        var aggregated = new Dictionary<string, NamePatchItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in rawItems)
        {
            if (string.IsNullOrWhiteSpace(item.OriginalName)) continue;

            if (aggregated.TryGetValue(item.OriginalName, out var existing))
            {
                existing.GroupedRowIndices.AddRange(item.GroupedRowIndices);
            }
            else
            {
                aggregated[item.OriginalName] = item;
            }
        }

        return aggregated.Values
            .OrderBy(x => string.IsNullOrEmpty(x.Memo) ? 1 : 0)
            .ThenBy(x => x.Category)
            .ThenBy(x => x.OriginalName)
            .ToList();
    }

    public static List<NamePatchItem> ParseFromBytes(byte[] data, bool isChina, NamePatchKind kind)
    {
        var datLayout = DetectLayout(data, kind);
        var list = new List<NamePatchItem>();

        for (int i = 0; i < datLayout.RowCount; i++)
        {
            string idField = ReadIdField(data, datLayout, i, kind);
            string text = ReadName(data, datLayout, i, kind);
            if (!string.IsNullOrEmpty(text))
            {
                string cleanName = StripNamePatchSuffix(text);
                var rule = kind == NamePatchKind.Chests ? MatchChestRule(cleanName) : null;
                list.Add(new NamePatchItem
                {
                    RowIndex = i,
                    GroupedRowIndices = [i],
                    IdField = idField,
                    OriginalName = cleanName,
                    ModifiedName = text,
                    Memo = GetDefaultMemo(text, isChina, kind),
                    Category = rule?.Category ?? (kind == NamePatchKind.Chests ? "其它" : "宿敌"),
                    Color = !string.IsNullOrEmpty(rule?.ColorRgb) ? $"rgb({rule.ColorRgb})" : string.Empty,
                    Enabled = false
                });
            }
        }

        return list.OrderBy(x => string.IsNullOrEmpty(x.Memo) ? 1 : 0).ThenBy(x => x.RowIndex).ToList();
    }

    public static byte[] ApplyNamePatchChanges(byte[] data, List<NamePatchItem> items, NamePatchKind kind)
    {
        var datLayout = DetectLayout(data, kind);
        int nameOffset = KindInfos[kind].NameOffset;
        using var memoryStream = new MemoryStream();
        memoryStream.Write(data, 0, data.Length);
        int modifiedCount = 0;

        foreach (var item in items.OrderBy(i => i.RowIndex))
        {
            if (!item.Enabled || !item.IsModified || string.IsNullOrWhiteSpace(item.DisplayName) || item.GroupedRowIndices.Count == 0)
            {
                continue;
            }

            if ((memoryStream.Length - datLayout.StringBase) % 2 != 0L)
            {
                memoryStream.WriteByte(0);
            }

            long offset = memoryStream.Length - datLayout.StringBase;
            byte[] bytes = Encoding.Unicode.GetBytes(item.DisplayName);
            memoryStream.Write(bytes, 0, bytes.Length);
            memoryStream.WriteByte(0);
            memoryStream.WriteByte(0);
            memoryStream.WriteByte(0);
            memoryStream.WriteByte(0);

            byte[] pointerBytes = BitConverter.GetBytes((ulong)offset);
            foreach (int groupedRowIndex in item.GroupedRowIndices)
            {
                int pointerPos = 4 + groupedRowIndex * datLayout.RowSize + nameOffset;
                if (pointerPos + 8 <= datLayout.StringBase)
                {
                    long pos = memoryStream.Position;
                    memoryStream.Position = pointerPos;
                    memoryStream.Write(pointerBytes, 0, 8);
                    memoryStream.Position = pos;
                }
            }
            modifiedCount++;
        }

        if (modifiedCount == 0)
        {
            return data;
        }

        return memoryStream.ToArray();
    }

    private static DatLayout DetectLayout(byte[] data, NamePatchKind kind)
    {
        if (data.Length < 16) throw new ArgumentException($"{kind} 文件过小");
        int rowCount = BitConverter.ToInt32(data, 0);
        if (rowCount <= 0) throw new ArgumentException($"无效的行数: {rowCount}");

        int sentinelPos = -1;
        for (int i = 4; i < data.Length - 8; i++)
        {
            if (data[i] == 187 && data[i + 1] == 187 && data[i + 2] == 187 && data[i + 3] == 187 &&
                data[i + 4] == 187 && data[i + 5] == 187 && data[i + 6] == 187 && data[i + 7] == 187)
            {
                sentinelPos = i;
                break;
            }
        }

        if (sentinelPos < 0) throw new ArgumentException("无法定位字符串表哨兵（0xBB×8）");
        int totalRowBytes = sentinelPos - 4;
        if (totalRowBytes % rowCount != 0) throw new ArgumentException($"行总字节数 {totalRowBytes} 不能被行数 {rowCount} 整除");

        int rowSize = totalRowBytes / rowCount;
        int nameOffset = KindInfos[kind].NameOffset;
        if (rowSize <= nameOffset + 8) throw new ArgumentException($"行大小 {rowSize} 不足包含 Name 字段 (偏移 {nameOffset})");

        return new DatLayout(rowCount, rowSize, sentinelPos);
    }

    private static string ReadUtf16LeZ(byte[] data, int start, int maxChars = 500)
    {
        var chars = new List<char>(maxChars);
        for (int i = start; i + 1 < data.Length; i += 2)
        {
            if (chars.Count >= maxChars) break;
            int ch = data[i] | (data[i + 1] << 8);
            if (ch == 0) return new string(chars.ToArray());
            chars.Add((char)ch);
        }
        return new string(chars.ToArray());
    }

    private static string ReadPoE2String(byte[] data, int pointerPos, int sentinelPos)
    {
        if (pointerPos + 8 > sentinelPos) return string.Empty;
        ulong offset = BitConverter.ToUInt64(data, pointerPos);
        if (offset < 8 || offset == 18374403900871474942uL) return string.Empty;
        int stringPos = sentinelPos + (int)offset;
        if (stringPos + 1 >= data.Length) return string.Empty;
        return ReadUtf16LeZ(data, stringPos);
    }

    private static string ReadIdField(byte[] data, DatLayout layout, int rowIndex, NamePatchKind kind)
    {
        int rowStart = 4 + rowIndex * layout.RowSize;
        if (KindInfos[kind].IsIdString)
        {
            return ReadPoE2String(data, rowStart, layout.StringBase);
        }
        return BitConverter.ToInt32(data, rowStart).ToString();
    }

    private static string ReadName(byte[] data, DatLayout layout, int rowIndex, NamePatchKind kind)
    {
        int nameOffset = KindInfos[kind].NameOffset;
        int pointerPos = 4 + rowIndex * layout.RowSize + nameOffset;
        return ReadPoE2String(data, pointerPos, layout.StringBase);
    }
}
