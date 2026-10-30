using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

using FreiAtlas.Patcher.Core;
namespace FreiAtlas.Patcher.MtxPatch;

/// <summary>
/// PoE 2 商城外观 (MTX) 中英对照词典与本地化翻译器。
/// 提供针对 843 款商城外观特效的自然中文命名映射与词根拆解。
/// 词典正文存放在内嵌资源 mtx/translations.json.gz，首次访问时惰性加载。
/// </summary>
public static class MtxTranslationDictionary
{
    private const string ResourceName = "FreiAtlas.Patcher.Resources.mtx.translations.json.gz";

    private static readonly JsonSerializerOptions ResourceJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Lazy<TranslationData> Loaded =
        new(LoadEmbeddedData, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>精确翻译表条目数。</summary>
    internal static int ExactCount => Loaded.Value.ExactMap.Count;

    /// <summary>短语表条目数。</summary>
    internal static int PhraseCount => Loaded.Value.Phrases.Length;

    /// <summary>精确翻译表只读快照，供迁移回归测试逐条比对。</summary>
    internal static IReadOnlyDictionary<string, string> ExactEntries => Loaded.Value.ExactMap;

    /// <summary>短语表只读快照，顺序即替换顺序。</summary>
    internal static IReadOnlyList<(string Pattern, string Replacement)> PhraseEntries => Loaded.Value.Phrases;

    private static TranslationData LoadEmbeddedData()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"内嵌翻译词典资源缺失：{ResourceName}");

        var jsonBytes = GzipUtil.DecompressToBytes(stream, 16 * 1024 * 1024);
        var document = JsonSerializer.Deserialize<TranslationDocument>(jsonBytes, ResourceJsonOptions)
            ?? throw new InvalidOperationException($"内嵌翻译词典资源为空：{ResourceName}");

        if (document.Version != 1)
        {
            throw new InvalidOperationException(
                $"内嵌翻译词典版本不支持：{document.Version}（期望 1）");
        }

        var exactMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in document.Exact)
        {
            exactMap[entry.En] = entry.Zh;
        }

        // 顺序即替换优先级；OrderBy 是稳定排序，仅用于兜底数据损坏导致的乱序。
        var phrases = document.Phrases
            .OrderBy(entry => entry.Index)
            .Select(entry => (entry.Pattern, entry.Zh))
            .ToArray();

        return new TranslationData(exactMap, phrases);
    }

    // Translate 会被上千次调用（843 款外观 × 每次刷新列表），
    // 原来每次调用都要 new 874 个 Regex 并重新解析模式，是刷新卡顿的主要来源。
    // 这里一次性构建并保持资源中的原有顺序。
    private static readonly (System.Text.RegularExpressions.Regex Regex, string Replacement)[] CompiledPhrases
        = BuildCompiledPhrases();

    private static (System.Text.RegularExpressions.Regex Regex, string Replacement)[] BuildCompiledPhrases()
    {
        var phraseList = Loaded.Value.Phrases;
        var tmp = new List<(int Length, System.Text.RegularExpressions.Regex Regex, string Replacement)>(phraseList.Length);
        foreach (var (pattern, rep) in phraseList)
        {
            tmp.Add((pattern.Length,
                new System.Text.RegularExpressions.Regex(
                    $@"\b{pattern}\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                rep));
        }

        // 保持原有数组顺序：改成「长度降序」会改变替换优先级，
        // 进而改变部分外观的中文名（属行为变更，不在本次清理范围内）。
        var result = new (System.Text.RegularExpressions.Regex, string)[tmp.Count];
        for (int i = 0; i < tmp.Count; i++)
        {
            result[i] = (tmp[i].Regex, tmp[i].Replacement);
        }
        return result;
    }

    private static readonly System.Text.RegularExpressions.Regex CamelSplit =
        new(@"([a-z])([A-Z])", System.Text.RegularExpressions.RegexOptions.None);

    private static readonly System.Text.RegularExpressions.Regex Possessive =
        new(@"'s\b", System.Text.RegularExpressions.RegexOptions.None);

    private static readonly System.Text.RegularExpressions.Regex StopWords =
        new(@"\b(of|the|and|in|on|with|a|an|to)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex CategorySuffix =
        new(@"传送门|宠物|防具|特效|背饰|光效|外观|外观包|指针|仓库|变身|翅膀|足迹|披风|饰品|套装|技能|藏身处|组合包",
            System.Text.RegularExpressions.RegexOptions.None);

    /// <summary>
    /// 将英文商城外观名称翻译为地道、直观的中文名称。
    /// </summary>
    public static string Translate(string englishName, MtxCategory category)
    {
        if (string.IsNullOrWhiteSpace(englishName)) return string.Empty;
        if (Loaded.Value.ExactMap.TryGetValue(englishName, out var exact)) return exact;

        // 驼峰命名分词
        var s = CamelSplit.Replace(englishName, "$1 $2");
        s = Possessive.Replace(s, "之");

        // 依序进行短语与词根匹配替换（顺序与原有实现一致，仅复用预编译实例）
        foreach (var (reg, rep) in CompiledPhrases)
        {
            if (reg.IsMatch(s))
            {
                s = reg.Replace(s, $" {rep} ");
            }
        }

        // 清理介词虚词
        s = StopWords.Replace(s, " ");

        // 分词与去重
        var tokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var resList = new List<string>(tokens.Length);
        foreach (var t in tokens)
        {
            if (resList.Count > 0 && resList[^1].Equals(t, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            resList.Add(t);
        }

        var finalZh = string.Join(" ", resList);

        // 兜底补齐分类后缀
        if (!CategorySuffix.IsMatch(finalZh))
        {
            finalZh += category switch
            {
                MtxCategory.Portals => " 传送门",
                MtxCategory.Pets => " 宠物",
                MtxCategory.ArmourSets => " 防具套装",
                MtxCategory.Skills => " 技能特效",
                MtxCategory.CharacterEffects => " 角色特效",
                _ => " 外观"
            };
        }

        return finalZh;
    }

    private sealed record TranslationData(
        Dictionary<string, string> ExactMap,
        (string Pattern, string Replacement)[] Phrases);

    private sealed class TranslationDocument
    {
        public int Version { get; set; }

        public List<TranslationEntry> Exact { get; set; } = [];

        public List<PhraseEntry> Phrases { get; set; } = [];
    }

    private sealed class TranslationEntry
    {
        public string En { get; set; } = string.Empty;

        public string Zh { get; set; } = string.Empty;
    }

    private sealed class PhraseEntry
    {
        public int Index { get; set; }

        public string Pattern { get; set; } = string.Empty;

        public string Zh { get; set; } = string.Empty;
    }
}
