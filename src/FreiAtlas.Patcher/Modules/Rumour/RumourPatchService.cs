using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.Modules.Rumour;

/// <summary>
/// 异界地图传言条目
/// </summary>
public sealed class EndgameMapRumourItem
{
    public int RowIndex { get; set; }
    public int WorldAreaIndex { get; set; }
    public string MapName { get; set; } = string.Empty;
    public string OriginalRumourText { get; set; } = string.Empty;
    public string RumourText { get; set; } = string.Empty;
    public string Memo { get; set; } = string.Empty;
    public string Category { get; set; } = "常规";
    public string Color { get; set; } = "#ffffff";
    public bool Enabled { get; set; } = true;
    public bool IsModified => !string.Equals(OriginalRumourText, RumourText, StringComparison.Ordinal);
}

/// <summary>
/// 传言规则定义实体
/// </summary>
public sealed class RumourRule
{
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = "常规";
    public string Color { get; init; } = "#ffffff";
    public string PrefixSimplified { get; init; } = string.Empty;
    public string PrefixTraditional { get; init; } = string.Empty;
    public string[] Keywords { get; init; } = [];
    public string[] AreaKeys { get; init; } = [];

    public string GetPrefix(bool isSimplified) => isSimplified ? PrefixSimplified : PrefixTraditional;
}

/// <summary>
/// 异界传言补丁服务（针对 endgamemaps.datc64）
/// 负责以客户端真实数据表为源头提取传言、应用语义规则识别并精准闭环注入
/// </summary>
public sealed class RumourPatchService
{
    private sealed record DatLayout(int RowCount, int RowSize, int StringBase);

    public const int RumourTextOffset = 227;
    private static readonly Regex FormatPrefixRe = new(@"^\[[^\]]*\]", RegexOptions.Compiled);

    /// <summary>
    /// 内置模式识别规则库（简/繁/英通用语义对齐）
    /// </summary>
    public static readonly IReadOnlyList<RumourRule> BuiltinRules =
    [
        new()
        {
            Name = "纯净乐园",
            Category = "顶级神图",
            Color = "#eab308",
            PrefixSimplified = "[<unique>{纯净乐园}]",
            PrefixTraditional = "[<unique>{純淨樂園}]",
            Keywords = ["近乎天堂", "天堂", "Paradise"],
            AreaKeys = ["PureParadise", "UntaintedParadise"]
        },
        new()
        {
            Name = "漂流者之所",
            Category = "暗金神图",
            Color = "#06b6d4",
            PrefixSimplified = "[<rgb(14,186,255)>{颠沛领域}]",
            PrefixTraditional = "[<rgb(14,186,255)>{漂流者之所}]",
            Keywords = ["闪闪发光", "閃光的未必是金", "漂流", "Castaway", "glitters"],
            AreaKeys = ["Castaway"]
        },
        new()
        {
            Name = "无名先知",
            Category = "终极大奖",
            Color = "#a855f7",
            PrefixSimplified = "[<unique>{无名先知}]",
            PrefixTraditional = "[<unique>{無名先知}]",
            Keywords = ["好人", "先知", "A good fellow"],
            AreaKeys = []
        },
        new()
        {
            Name = "破裂迷湖/千裂泽",
            Category = "暗金神图",
            Color = "#eab308",
            PrefixSimplified = "[<unique>{千裂泽}]",
            PrefixTraditional = "[<unique>{破裂迷湖}]",
            Keywords = ["倒映的水面", "映照水域", "Reflective waters"],
            AreaKeys = ["ReflectiveWaters"]
        },
        new()
        {
            Name = "天陨荒原",
            Category = "稀有神图",
            Color = "#f97316",
            PrefixSimplified = "[★<rgb(14,186,255)>{天陨荒原}]",
            PrefixTraditional = "[★<rgb(14,186,255)>{殞空荒原}]",
            Keywords = ["坠落的星辰", "殞落群星", "Fallen stars"],
            AreaKeys = []
        },
        new()
        {
            Name = "终局首领 · 沃拉娜",
            Category = "终局首领",
            Color = "#ef4444",
            PrefixSimplified = "[<unique>{沃拉娜}]",
            PrefixTraditional = "[<unique>{沃拉娜}]",
            Keywords = ["最后一个倒下", "最後倒下者", "The last to fall"],
            AreaKeys = []
        },
        new()
        {
            Name = "终局首领 · 乌崔德",
            Category = "终局首领",
            Color = "#ef4444",
            PrefixSimplified = "[<unique>{乌崔德}]",
            PrefixTraditional = "[<unique>{烏崔德}]",
            Keywords = ["吞星者", "飲星者", "Stardrinker"],
            AreaKeys = []
        },
        new()
        {
            Name = "终局首领 · 梅德维德",
            Category = "终局首领",
            Color = "#ef4444",
            PrefixSimplified = "[<unique>{梅德维德}]",
            PrefixTraditional = "[<unique>{梅德維德}]",
            Keywords = ["循环的尽头", "圓環的終點", "End of the circle"],
            AreaKeys = []
        },
        new()
        {
            Name = "终局首领 · 欧罗什",
            Category = "终局首领",
            Color = "#ef4444",
            PrefixSimplified = "[<unique>{欧罗什}]",
            PrefixTraditional = "[<unique>{歐羅什}]",
            Keywords = ["陨落的源头", "墮落的起源", "Origin of the fall"],
            AreaKeys = []
        },
        new()
        {
            Name = "致命机制 · 无复活",
            Category = "致命机制",
            Color = "#ef4444",
            PrefixSimplified = "[<rgb(227,158,255)>{无复活}]",
            PrefixTraditional = "[<rgb(227,158,255)>{無復活}]",
            Keywords = ["没东西喝", "沒東西可喝", "至少很干燥", "至少很乾燥", "Nothin' to drink", "dry at least"],
            AreaKeys = []
        },
        new()
        {
            Name = "探索增益 · 开视野",
            Category = "探索增益",
            Color = "#34d399",
            PrefixSimplified = "[<rgb(74,230,58)>{开视野}]",
            PrefixTraditional = "[<rgb(74,230,58)>{開視野}]",
            Keywords = ["未知的废墟", "未知的遺跡", "Unknown ruins"],
            AreaKeys = []
        },
        new()
        {
            Name = "机制收益 · 宝箱与复活",
            Category = "机制收益",
            Color = "#38b469",
            PrefixSimplified = "[<rgb(209,46,46)>{箱}|<rgb(74,230,58)>{复活}]",
            PrefixTraditional = "[<rgb(209,46,46)>{箱}|<rgb(74,230,58)>{復活}]",
            Keywords = ["有点可疑", "有點可疑", "Somethin' fishy"],
            AreaKeys = []
        },
        new()
        {
            Name = "优质地形 · 好图",
            Category = "优质地形",
            Color = "#34d399",
            PrefixSimplified = "[<rgb(74,230,58)>{好图}]",
            PrefixTraditional = "[<rgb(74,230,58)>{好圖}]",
            Keywords = ["无尽的悬崖", "無盡懸崖", "Endless cliffs"],
            AreaKeys = []
        },
        new()
        {
            Name = "机制收益 · 温暖复活",
            Category = "机制收益",
            Color = "#34d399",
            PrefixSimplified = "[<rgb(74,230,58)>{复活}]",
            PrefixTraditional = "[<rgb(74,230,58)>{復活}]",
            Keywords = ["温暖但危险", "溫暖但危險", "Warm but dangerous"],
            AreaKeys = []
        },
        new()
        {
            Name = "机制收益 · 双Boss与复活",
            Category = "机制收益",
            Color = "#60a5fa",
            PrefixSimplified = "[<rgb(209,46,46)>{Boss}|<rgb(74,230,58)>{复活}]",
            PrefixTraditional = "[<rgb(209,46,46)>{Boss}|<rgb(74,230,58)>{復活}]",
            Keywords = ["寒冷如冰", "冷如寒冰", "Cold as ice"],
            AreaKeys = []
        },
        new()
        {
            Name = "机制收益 · 野性精灵",
            Category = "机制收益",
            Color = "#fb7185",
            PrefixSimplified = "[<rgb(209,46,46)>{精灵}]",
            PrefixTraditional = "[<rgb(209,46,46)>{精靈}]",
            Keywords = ["肆意奔走的野性", "野性自由遊蕩", "Wild running"],
            AreaKeys = []
        },
        new()
        {
            Name = "贫瘠差图",
            Category = "贫瘠差图",
            Color = "#94a3b8",
            PrefixSimplified = "[差图]",
            PrefixTraditional = "[差圖]",
            Keywords = ["荒凉而可怕", "荒涼又糟糕", "Bleak and terrible"],
            AreaKeys = []
        }
    ];

    /// <summary>
    /// 国服简体中文预设传言词典（兼容已有测试与旧调用）
    /// </summary>
    public static readonly Dictionary<string, string> DefaultModifiedRumourTextsSimplified = new(StringComparer.Ordinal)
    {
        ["所有闪闪发光之物……"] = "[<rgb(14,186,255)>{颠沛领域}]所有闪闪发光之物……",
        ["近乎天堂。"] = "[<unique>{纯净乐园}]近乎天堂。",
        ["一个好人……"] = "[<unique>{无名先知}]一个好人……",
        ["倒映的水面……"] = "[<unique>{千裂泽}]倒映的水面……",
        ["有点可疑……"] = "[<rgb(209,46,46)>{箱}|<rgb(74,230,58)>{复活}]有点可疑……",
        ["循环的尽头……"] = "[<unique>{梅德维德}]循环的尽头……",
        ["最后一个倒下……"] = "[<unique>{沃拉娜}]最后一个倒下……",
        ["吞星者……"] = "[<unique>{乌崔德}]吞星者……",
        ["陨落的源头……"] = "[<unique>{欧罗什}]陨落的源头……",
        ["没东西喝……"] = "[<rgb(227,158,255)>{无复活}]没东西喝……",
        ["未知的废墟……"] = "[<rgb(74,230,58)>{开视野}]未知的废墟……",
        ["至少很干燥……"] = "[<rgb(227,158,255)>{无复活}]至少很干燥……",
        ["坠落的星辰……"] = "[★<rgb(14,186,255)>{天陨荒原}]坠落的星辰……",
        ["无尽的悬崖……"] = "[<rgb(74,230,58)>{好图}]无尽的悬崖……",
        ["温暖但危险……"] = "[<rgb(74,230,58)>{复活}]温暖但危险……",
        ["荒凉而可怕……"] = "[差图]荒凉而可怕……",
        ["肆意奔走的野性……"] = "[<rgb(209,46,46)>{精灵}]肆意奔走的野性……",
        ["寒冷如冰……"] = "[<rgb(209,46,46)>{Boss}|<rgb(74,230,58)>{复活}]寒冷如冰……"
    };

    /// <summary>
    /// 国际服繁体中文预设传言词典（兼容已有测试与旧调用）
    /// </summary>
    public static readonly Dictionary<string, string> DefaultModifiedRumourTextsTraditional = new(StringComparer.Ordinal)
    {
        ["閃光的未必是金……"] = "[<rgb(14,186,255)>{漂流者之所}]閃光的未必是金……",
        ["近乎天堂。"] = "[<unique>{純淨樂園}]近乎天堂。",
        ["好人一個……"] = "[<unique>{無名先知}]好人一個……",
        ["映照水域……"] = "[<unique>{破裂迷湖}]映照水域……",
        ["有點可疑……"] = "[<rgb(209,46,46)>{箱}|<rgb(74,230,58)>{復活}]有點可疑……",
        ["圓環的終點……"] = "[<unique>{梅德維德}]圓環的終點……",
        ["最後倒下者……"] = "[<unique>{沃拉娜}]最後倒下者……",
        ["飲星者……"] = "[<unique>{烏崔德}]飲星者……",
        ["墮落的起源……"] = "[<unique>{歐羅什}]墮落的起源……",
        ["沒東西可喝……"] = "[<rgb(227,158,255)>{無復活}]沒東西可喝……",
        ["未知的遺跡……"] = "[<rgb(74,230,58)>{開視野}]未知的遺跡……",
        ["至少很乾燥……"] = "[<rgb(227,158,255)>{無復活}]至少很乾燥……",
        ["殞落群星……"] = "[★<rgb(14,186,255)>{殞空荒原}]殞落群星……",
        ["無盡懸崖……"] = "[<rgb(74,230,58)>{好圖}]無盡懸崖……",
        ["溫暖但危險……"] = "[<rgb(74,230,58)>{復活}]溫暖但危險……",
        ["荒涼又糟糕……"] = "[差圖]荒涼又糟糕……",
        ["野性自由遊蕩……"] = "[<rgb(209,46,46)>{精靈}]野性自由遊蕩……",
        ["冷如寒冰……"] = "[<rgb(209,46,46)>{Boss}|<rgb(74,230,58)>{復活}]冷如寒冰……"
    };

    private readonly PatchEngine _engine;

    public RumourPatchService(PatchEngine engine)
    {
        _engine = engine;
    }

    /// <summary>
    /// 匹配传言规则
    /// </summary>
    public static (RumourRule? Rule, string RecommendedText) MatchRule(string rawText, string mapName, bool isSimplified)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return (null, string.Empty);
        }

        string cleanText = StripPrefix(rawText);

        // 1. 优先通过地图名称 / 元数据关联规则
        if (!string.IsNullOrEmpty(mapName))
        {
            foreach (var rule in BuiltinRules)
            {
                if (rule.AreaKeys.Any(k => mapName.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    string prefix = rule.GetPrefix(isSimplified);
                    return (rule, prefix + cleanText);
                }
            }
        }

        // 2. 通过关键词语义特征匹配
        foreach (var rule in BuiltinRules)
        {
            foreach (var kw in rule.Keywords)
            {
                if (cleanText.Contains(kw, StringComparison.OrdinalIgnoreCase))
                {
                    string prefix = rule.GetPrefix(isSimplified);
                    return (rule, prefix + cleanText);
                }
            }
        }

        return (null, cleanText);
    }

    /// <summary>
    /// 去除开头的注入前缀
    /// </summary>
    public static string StripPrefix(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return FormatPrefixRe.Replace(text, string.Empty);
    }

    /// <summary>
    /// 获取默认预设备注文本（兼容保留）
    /// </summary>
    public static string GetDefaultMemo(string rumourText, bool isSimplified)
    {
        var (_, rec) = MatchRule(rumourText, string.Empty, isSimplified);
        return rec;
    }

    /// <summary>
    /// 动态扫描游戏客户端，提取真实传言条目并运行规则引擎
    /// </summary>
    public List<EndgameMapRumourItem> ScanClientRumours(GameModeInfo info, Action<string>? progress = null)
    {
        var all = ParseRumours(info, progress);
        // 只保留实际拥有传言文本的有效行（通常约 20 条）
        return all.Where(r => !string.IsNullOrWhiteSpace(r.OriginalRumourText)).ToList();
    }

    /// <summary>
    /// 解析游戏中的全部行（包含空行）
    /// </summary>
    public List<EndgameMapRumourItem> ParseRumours(GameModeInfo info, Action<string>? progress = null)
    {
        if (string.IsNullOrEmpty(info.EndgameMapsPath))
        {
            throw new InvalidOperationException("未配置 EndgameMapsPath 路径。");
        }

        progress?.Invoke("正在读取异界地图传言数据表...");
        byte[]? egData = _engine.ReadGameFile(info, info.EndgameMapsPath);
        if (egData == null || egData.Length == 0)
        {
            throw new FileNotFoundException($"找不到文件：{info.EndgameMapsPath}");
        }

        byte[]? waData = !string.IsNullOrEmpty(info.WorldAreasPath) ? _engine.ReadGameFile(info, info.WorldAreasPath) : null;
        var waNames = waData != null ? LoadWorldAreaNames(waData) : new Dictionary<int, string>();

        return ParseFromBytes(egData, waNames, info.IsSimplifiedChinese);
    }

    /// <summary>
    /// 应用异界传言补丁（严格以客户端解析出的实际行结构为基准，闭环对齐 RowIndex，写后自校验）
    /// </summary>
    public bool ApplyPatch(GameModeInfo info, List<EndgameMapRumourItem>? customItems = null, Action<string>? progress = null)
    {
        if (string.IsNullOrEmpty(info.EndgameMapsPath))
        {
            progress?.Invoke("当前游戏模式未检测到有效的 EndgameMapsPath。");
            return false;
        }

        progress?.Invoke("正在读取游戏内真实传言数据表...");
        byte[]? rawBytes = _engine.ReadGameFile(info, info.EndgameMapsPath);
        if (rawBytes == null || rawBytes.Length == 0)
        {
            progress?.Invoke($"读取 {info.EndgameMapsPath} 失败。");
            return false;
        }

        byte[]? waData = !string.IsNullOrEmpty(info.WorldAreasPath) ? _engine.ReadGameFile(info, info.WorldAreasPath) : null;
        var waNames = waData != null ? LoadWorldAreaNames(waData) : new Dictionary<int, string>();

        // 1. 先从客户端当前数据表中解析出带有绝对行号（RowIndex）的基础列表
        var baseItems = ParseFromBytes(rawBytes, waNames, info.IsSimplifiedChinese);

        // 2. 将用户修改或默认规则对齐合并到带有确定 RowIndex 的基础条目上
        if (customItems != null && customItems.Count > 0)
        {
            foreach (var custom in customItems)
            {
                EndgameMapRumourItem? target = null;
                // 优先按 RowIndex 精准匹配
                if (custom.RowIndex >= 0 && custom.RowIndex < baseItems.Count)
                {
                    target = baseItems[custom.RowIndex];
                }

                // 兜底按纯净文本匹配
                if (target == null || string.IsNullOrWhiteSpace(target.OriginalRumourText))
                {
                    string cleanCustom = StripPrefix(custom.OriginalRumourText);
                    target = baseItems.FirstOrDefault(b => StripPrefix(b.OriginalRumourText) == cleanCustom);
                }

                if (target != null)
                {
                    target.RumourText = custom.RumourText;
                    target.Enabled = custom.Enabled;
                }
            }
        }
        else
        {
            // 若未提供自定义项，则将规则引擎推荐的 Memo 作为默认生效内容
            foreach (var item in baseItems)
            {
                if (!string.IsNullOrEmpty(item.Memo))
                {
                    item.RumourText = item.Memo;
                    item.Enabled = true;
                }
            }
        }

        int modifiedCount = baseItems.Count(i => i.Enabled && i.IsModified);
        if (modifiedCount == 0)
        {
            progress?.Invoke("未发现需要应用的传言修改。");
            return false;
        }

        progress?.Invoke($"正在为 {modifiedCount} 条异界地图传言精准注入标注...");
        byte[] modifiedBytes = ApplyRumourChanges(rawBytes, baseItems);

        bool ok = _engine.WriteGameFiles(info, [(info.EndgameMapsPath, modifiedBytes)]);
        if (!ok)
        {
            // 透出根因，否则用户只看到「写入失败」无法定位问题。
            progress?.Invoke($"写入传言补丁失败：{PatchEngine.LastWriteException?.Message ?? "未知原因"}");
            return false;
        }

        // 3. 写后回读自检：确保游戏数据表中目标行已真正包含修改前缀
        //
        // 这道校验必须 fail-closed：写入 API 返回成功只代表字节落盘，不代表
        // 写入的行位正确。校验没过就说明改动没生效（列偏移与当前版本不符等），
        // 此时若仍报「应用成功」，用户会以为打了补丁而实际客户端没有任何变化。
        progress?.Invoke("正在验证客户端写入完整性...");
        byte[]? verifiedBytes = _engine.ReadGameFile(info, info.EndgameMapsPath);
        if (verifiedBytes is null)
        {
            progress?.Invoke("写入后无法回读 endgamemaps.datc64，无法确认传言是否生效。请重试。");
            return false;
        }

        var verifiedItems = ParseFromBytes(verifiedBytes, waNames, info.IsSimplifiedChinese);
        int verifiedSuccess = baseItems
            .Where(b => b.Enabled && b.IsModified)
            .Count(b => b.RowIndex < verifiedItems.Count && verifiedItems[b.RowIndex].OriginalRumourText == b.RumourText);

        if (verifiedSuccess == 0)
        {
            progress?.Invoke(
                "写入后校验未通过：客户端数据表中没有找到任何已改写的传言。\n" +
                "这通常意味着当前客户端版本的行结构与补丁内置的列偏移不一致，未做任何破坏性改动。");
            return false;
        }

        progress?.Invoke($"异界地图传言补丁应用成功！（已校验增强 {verifiedSuccess} 条传言）");
        return true;
    }

    /// <summary>
    /// 二进制处理：向 endgamemaps.datc64 注入修改后的传言字符串
    /// </summary>
    public static byte[] ApplyRumourChanges(byte[] egData, List<EndgameMapRumourItem> items)
    {
        var datLayout = DetectLayout(egData);

        // 委托给统一的追加重写实现（原为各模块各一份副本，现已收敛）。
        var replacements = new List<(int PointerPos, string NewText)>();

        foreach (var item in items)
        {
            if (!item.Enabled || !item.IsModified || string.IsNullOrWhiteSpace(item.RumourText))
            {
                continue;
            }

            int pointerPos = 4 + item.RowIndex * datLayout.RowSize + RumourTextOffset;
            if (pointerPos + 8 <= datLayout.StringBase)
            {
                replacements.Add((pointerPos, item.RumourText));
            }
        }

        if (replacements.Count == 0) return egData;

        return DatBinaryUtils.RewriteStringPointersAppend(egData, datLayout.StringBase, replacements);
    }

    public static List<EndgameMapRumourItem> ParseFromBytes(byte[] egData, Dictionary<int, string> waNames, bool isSimplified)
    {
        var datLayout = DetectLayout(egData);
        var list = new List<EndgameMapRumourItem>(datLayout.RowCount);

        for (int i = 0; i < datLayout.RowCount; i++)
        {
            int worldAreaIdx = ReadWorldAreaIndex(egData, datLayout, i);
            string text = ReadRumourText(egData, datLayout, i);
            string mapName = waNames.TryGetValue(worldAreaIdx, out string? name) ? name : $"WorldArea#{worldAreaIdx}";

            var (rule, recommended) = MatchRule(text, mapName, isSimplified);

            list.Add(new EndgameMapRumourItem
            {
                RowIndex = i,
                WorldAreaIndex = worldAreaIdx,
                MapName = mapName,
                OriginalRumourText = text,
                RumourText = string.IsNullOrEmpty(text) ? string.Empty : recommended,
                Memo = recommended,
                Category = rule?.Category ?? "常规",
                Color = rule?.Color ?? "#ffffff",
                Enabled = !string.IsNullOrEmpty(recommended) && recommended != text
            });
        }

        return list;
    }

    private static DatLayout DetectLayout(byte[] data)
    {
        // 通用结构校验交给公共实现，这里只补本模块特有的「行宽必须容纳传言文本字段」。
        var (rowCount, rowSize, sentinelPos) = DatBinaryUtils.DetectLayout(data, "EndgameMaps.datc64");

        if (rowSize <= RumourTextOffset + 4)
        {
            throw new ArgumentException($"行大小 {rowSize} 不足以包含传言文本字段（偏移 {RumourTextOffset}）。");
        }

        return new DatLayout(rowCount, rowSize, sentinelPos);
    }

    private static string ReadRumourText(byte[] data, DatLayout layout, int rowIndex)
    {
        int pointerPos = 4 + rowIndex * layout.RowSize + RumourTextOffset;
        if (pointerPos + 8 > layout.StringBase) return string.Empty;

        ulong offset = BitConverter.ToUInt64(data, pointerPos);
        if (offset == 0 || offset == DatBinaryUtils.SentinelValue) return string.Empty;

        long start = layout.StringBase + (long)offset;
        if (start <= 0 || start >= data.Length - 1) return string.Empty;
        return ReadUtf16LeZ(data, (int)start);
    }

    private static int ReadWorldAreaIndex(byte[] data, DatLayout layout, int rowIndex)
    {
        int pos = 4 + rowIndex * layout.RowSize;
        return pos + 4 > layout.StringBase ? -1 : BitConverter.ToInt32(data, pos);
    }

    private static Dictionary<int, string> LoadWorldAreaNames(byte[] waData)
    {
        var dict = new Dictionary<int, string>();
        if (waData.Length < 16) return dict;

        int rowCount = BitConverter.ToInt32(waData, 0);
        int sentinelPos = DatBinaryUtils.FindSentinel(waData);

        if (sentinelPos < 0 || rowCount <= 0) return dict;
        int rowBytes = sentinelPos - 4;
        if (rowBytes % rowCount != 0) return dict;

        int rowSize = rowBytes / rowCount;
        for (int j = 0; j < rowCount; j++)
        {
            int pointerPos = 4 + j * rowSize + 8;
            if (pointerPos + 8 <= sentinelPos)
            {
                ulong offset = BitConverter.ToUInt64(waData, pointerPos);
                if (offset >= 8 && offset != 18374403900871474942uL)
                {
                    int start = sentinelPos + (int)offset;
                    if (start + 1 < waData.Length)
                    {
                        string str = ReadUtf16LeZ(waData, start);
                        if (!string.IsNullOrEmpty(str))
                        {
                            dict[j] = str;
                        }
                    }
                }
            }
        }

        return dict;
    }

    private static string ReadUtf16LeZ(byte[] data, int start, int maxChars = 500) =>
        DatBinaryUtils.ReadUtf16LeZ(data, start, maxChars);
}
