using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FreiAtlas.Patcher.EffectPatch;

/// <summary>
/// PoE2 技能与装备特效屏蔽数据库。
///
/// 数据来源于易刷最新规则包（ve 模块），包含 110 个可选技能/装备特效项，
/// 以及 105 个精确分类、共 1,645 条手工校准的文件级屏蔽规则。
/// 支持单文件精细操作（清块、隐藏网格段、粒子置零、特效包置空、隐藏实体）
/// 与多技能共享文件时的安全交集策略。
/// </summary>
public static class Poe2SpellBlockDatabase
{
    private const string ResourceName = "FreiAtlas.Patcher.Resources.poe2_spell_block_db.json.gz";

    private static readonly Lazy<DatabasePayload> _data = new(Load);

    public static IReadOnlyList<Poe2SpellOption> Options => _data.Value.Skills;

    public static IReadOnlyList<Poe2SpellEntry> Entries => _data.Value.Entries;

    /// <summary>所有记录中的精确路径映射（文件路径 -> 关联记录列表）。</summary>
    public static IReadOnlyDictionary<string, List<Poe2SpellRecord>> GlobalPathRecords => _data.Value.GlobalPathMap;

    /// <summary>精确库中所有以 .ao/.aoc/.epk 结尾的文件路径集合（用于前50项兜底匹配时排除已入库文件）。</summary>
    public static IReadOnlySet<string> ExactAoAocEpkPaths => _data.Value.ExactAoAocEpkPaths;

    /// <summary>
    /// 对用户选中的技能选项进行规则规划。
    /// 100% 还原易刷 ve.rules(selectedOptions) 的规划算法。
    /// </summary>
    public static SpellBlockPlan Plan(IEnumerable<string> selectedOptions, bool includeAudio = true)
    {
        var selectedSet = new HashSet<string>(
            selectedOptions.Where(s => !string.IsNullOrWhiteSpace(s)),
            StringComparer.OrdinalIgnoreCase);

        if (selectedSet.Count == 0)
        {
            return SpellBlockPlan.Empty;
        }

        // 1. 互斥消解：若勾选了「保留球体」版本，忽略对应的全清分类
        var ignoredCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (selectedSet.Contains("poe2_skill:Comet_With_Ball"))
        {
            ignoredCategories.Add("s_skill@Comet");
        }
        if (selectedSet.Contains("poe2_skill:Frostbolt_With_Ball"))
        {
            ignoredCategories.Add("s_skill@Frostbolt");
        }

        var matchedEntries = Entries
            .Where(e => selectedSet.Contains(e.Selection) && !ignoredCategories.Contains(e.Category))
            .ToList();

        // 收集已匹配条目的所有 records
        var pathRecordsMap = new Dictionary<string, List<Poe2SpellRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in matchedEntries)
        {
            foreach (var record in entry.Records)
            {
                if (!pathRecordsMap.TryGetValue(record.Path, out var list))
                {
                    list = [];
                    pathRecordsMap[record.Path] = list;
                }
                list.Add(record);
            }
        }

        // 2. 处理前 50 项非 poe2_skill: 开头的关键字选项
        var ignoredSelections = new HashSet<string>(
            Entries.Where(e => ignoredCategories.Contains(e.Category)).Select(e => e.Selection),
            StringComparer.OrdinalIgnoreCase);

        var keywordOptions = selectedSet
            .Where(s => !s.StartsWith("poe2_skill:", StringComparison.OrdinalIgnoreCase) && !ignoredSelections.Contains(s))
            .Select(s => s == "lightning_arrow|bow_ice_shot" ? "bow_ice_shot" : s)
            .ToList();

        if (keywordOptions.Count > 0)
        {
            var keywordPattern = new Regex(
                @"^metadata/(effects|particles)/.*(?:" + string.Join("|", keywordOptions) + ")",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(2));

            foreach (var (path, records) in GlobalPathRecords)
            {
                if (!pathRecordsMap.ContainsKey(path) && keywordPattern.IsMatch(path))
                {
                    pathRecordsMap[path] = [.. records];
                }
            }
        }

        // 3. 多技能共享同一文件时的交集聚合
        var plannedFiles = new Dictionary<string, PlannedFileAction>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, records) in pathRecordsMap)
        {
            if (records.Count == 0) continue;

            if (records[0].Blocks is not null)
            {
                // 取所有命中记录的 blocks 交集
                var commonBlocks = new HashSet<string>(records[0].Blocks!, StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < records.Count; i++)
                {
                    if (records[i].Blocks is null)
                    {
                        commonBlocks.Clear();
                        break;
                    }
                    commonBlocks.IntersectWith(records[i].Blocks!);
                }

                // 若取消勾选音效屏蔽，从清空列表中剔除 SoundEvents 与 SoundParams
                if (!includeAudio)
                {
                    commonBlocks.Remove("SoundEvents");
                    commonBlocks.Remove("SoundParams");
                }

                // 取 hideSegments 交集
                List<string>? commonHideSegments = null;
                if (records[0].HideSegments is not null && records[0].HideSegments!.Count > 0)
                {
                    var segSet = new HashSet<string>(records[0].HideSegments!, StringComparer.Ordinal);
                    for (int i = 1; i < records.Count; i++)
                    {
                        if (records[i].HideSegments is null)
                        {
                            segSet.Clear();
                            break;
                        }
                        segSet.IntersectWith(records[i].HideSegments!);
                    }
                    if (segSet.Count > 0)
                    {
                        commonHideSegments = [.. segSet];
                    }
                }

                if (commonBlocks.Count > 0 || (commonHideSegments is not null && commonHideSegments.Count > 0))
                {
                    plannedFiles[path] = new PlannedFileAction
                    {
                        Kind = PlannedActionKind.ClearBlockAndHideSegments,
                        Blocks = commonBlocks.Count > 0 ? [.. commonBlocks] : null,
                        HideSegments = commonHideSegments
                    };
                }
            }
            else
            {
                var op = records[0].Operation;
                bool allSame = records.All(r => r.Operation == op);
                if (allSame && !string.IsNullOrWhiteSpace(op) && op != "preserve")
                {
                    var kind = op switch
                    {
                        "zero" => PlannedActionKind.Zero,
                        "empty" => PlannedActionKind.Empty,
                        "hide-entity" => PlannedActionKind.HideEntity,
                        _ => PlannedActionKind.Preserve
                    };

                    if (kind != PlannedActionKind.Preserve)
                    {
                        plannedFiles[path] = new PlannedFileAction { Kind = kind };
                    }
                }
            }
        }

        return new SpellBlockPlan(
            plannedFiles,
            keywordOptions,
            ExactAoAocEpkPaths,
            includeAudio);
    }

    private static DatabasePayload Load()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new FileNotFoundException($"内嵌资源未找到：{ResourceName}");

        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, System.Text.Encoding.UTF8);
        var json = reader.ReadToEnd();

        var root = JsonSerializer.Deserialize<RawDatabaseRoot>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidDataException("反序列化 PoE2 技能数据库失败。");

        var skills = root.Skills ?? [];
        var entries = root.Entries ?? [];

        var globalMap = new Dictionary<string, List<Poe2SpellRecord>>(StringComparer.OrdinalIgnoreCase);
        var exactPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            foreach (var r in entry.Records)
            {
                if (!globalMap.TryGetValue(r.Path, out var list))
                {
                    list = [];
                    globalMap[r.Path] = list;
                }
                list.Add(r);

                if (r.Path.EndsWith(".ao", StringComparison.OrdinalIgnoreCase)
                    || r.Path.EndsWith(".aoc", StringComparison.OrdinalIgnoreCase)
                    || r.Path.EndsWith(".epk", StringComparison.OrdinalIgnoreCase))
                {
                    exactPaths.Add(r.Path);
                }
            }
        }

        return new DatabasePayload(skills, entries, globalMap, exactPaths);
    }

    private sealed record DatabasePayload(
        IReadOnlyList<Poe2SpellOption> Skills,
        IReadOnlyList<Poe2SpellEntry> Entries,
        IReadOnlyDictionary<string, List<Poe2SpellRecord>> GlobalPathMap,
        IReadOnlySet<string> ExactAoAocEpkPaths);

    private sealed class RawDatabaseRoot
    {
        public List<Poe2SpellOption>? Skills { get; set; }
        public List<Poe2SpellEntry>? Entries { get; set; }
    }
}

public sealed class Poe2SpellOption
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}

public sealed class Poe2SpellEntry
{
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("selection")]
    public string Selection { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("records")]
    public List<Poe2SpellRecord> Records { get; set; } = [];
}

public sealed class Poe2SpellRecord
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("blocks")]
    public List<string>? Blocks { get; set; }

    [JsonPropertyName("hideSegments")]
    public List<string>? HideSegments { get; set; }

    [JsonPropertyName("operation")]
    public string? Operation { get; set; }
}

public enum PlannedActionKind
{
    Preserve,
    ClearBlockAndHideSegments,
    Zero,
    Empty,
    HideEntity
}

public sealed class PlannedFileAction
{
    public required PlannedActionKind Kind { get; init; }
    public IReadOnlyList<string>? Blocks { get; init; }
    public IReadOnlyList<string>? HideSegments { get; init; }
}

public sealed record SpellBlockPlan(
    IReadOnlyDictionary<string, PlannedFileAction> PlannedFiles,
    IReadOnlyList<string> FallbackKeywords,
    IReadOnlySet<string> ExactAoAocEpkPaths,
    bool IncludeAudio)
{
    public static readonly SpellBlockPlan Empty = new(
        new Dictionary<string, PlannedFileAction>(),
        [],
        new HashSet<string>(),
        true);

    /// <summary>
    /// 前 50 项兜底匹配时清空的 11 种安全块。
    /// </summary>
    public static readonly string DefaultFallbackBlocks =
        "ParticleEffects|WindEvents|TrailsEffects|Lights|SoundEvents|SkinMesh|ScreenShake|FixedMesh|EffectPack|AttachedAnimatedObject|SoundParams";

    /// <summary>
    /// 当 includeAudio 为 false 时剔除音效的安全块。
    /// </summary>
    public static readonly string DefaultFallbackBlocksNoAudio =
        "ParticleEffects|WindEvents|TrailsEffects|Lights|SkinMesh|ScreenShake|FixedMesh|EffectPack|AttachedAnimatedObject";
}
