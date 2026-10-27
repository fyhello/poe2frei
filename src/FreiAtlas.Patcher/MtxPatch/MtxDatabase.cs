using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

namespace FreiAtlas.Patcher.MtxPatch;

/// <summary>
/// PoE 2 商城外观 (MTX) 数据库单例。
///
/// 包含 843 个商城外观条目的完整元数据、路径映射、
/// PoE 2 专属八大职业模型路径、骨骼挂载槽位与指针位图资源。
/// </summary>
public sealed class MtxDatabase
{
    private const string ResourceName = "FreiAtlas.Patcher.Resources.mtx.poe2_mtx.json.gz";

    /// <summary>
    /// 内嵌数据库加载失败时记录原因。
    ///
    /// Lazy&lt;T&gt; 会缓存工厂函数的异常：一旦加载失败，之后每次访问 .Value 都重抛
    /// 同一个异常，即使条件已恢复也要重启进程才能重试。因此工厂本身不能抛 ——
    /// 失败时返回一个空实例，把原因留在这里供调用方查询。
    /// </summary>
    public static string? LoadError { get; private set; }

    /// <summary>内嵌数据库是否可用。不可用时 <see cref="Instance"/> 返回空库。</summary>
    public static bool IsAvailable => LoadError is null;

    private static readonly Lazy<MtxDatabase> LazyInstance = new(
        () => new MtxDatabase(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static MtxDatabase Instance => LazyInstance.Value;

    private readonly List<MtxEntry> _allEntries = [];
    private readonly Dictionary<string, MtxEntry> _byKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// PoE 2 八大基础职业模型映射。
    /// </summary>
    public static readonly IReadOnlyList<MtxCharacterClassItem> SupportedClasses =
    [
        new("all", "全部职业 (推荐，同时生效)", "all"),
        new("int", "女巫 (Witch — 智力)", "metadata/characters/int/intfour.ao"),
        new("intb", "女术士 (Sorceress — 智力B)", "metadata/characters/int/intfourb.ao"),
        new("str", "战士 (Warrior — 力量)", "metadata/characters/str/strfourb.ao"),
        new("dex", "游侠 (Ranger — 敏捷)", "metadata/characters/dex/dexfour.ao"),
        new("dexb", "女猎手 (Huntress — 敏捷B)", "metadata/characters/dex/dexfourb.ao"),
        new("strdex", "佣兵 (Mercenary — 力敏)", "metadata/characters/strdex/strdexfourb.ao"),
        new("dexint", "武僧 (Monk — 敏智)", "metadata/characters/dexint/dexintfourb.ao"),
        new("intstr", "德鲁伊 (Druid — 力智)", "metadata/characters/strint/strintfourb.ao")
    ];

    /// <summary>
    /// PoE 2 七大基础装备底子后缀（严格按长度倒序，防止短后缀误匹配长底子）。
    /// </summary>
    public static readonly string[] ArmourBaseSuffixes =
    [
        "strdexint",
        "strdex",
        "dexint",
        "strint",
        "str",
        "dex",
        "int"
    ];

    /// <summary>
    /// 四大防具目标目录及对应部位缩写。
    /// </summary>
    public static readonly (string Root, string Part)[] ArmourRoots =
    [
        ("metadata/items/armours/bodyarmours/", "body"),
        ("metadata/items/armours/boots/", "boots"),
        ("metadata/items/armours/gloves/", "gloves"),
        ("metadata/items/armours/helmets/", "helmet")
    ];

    /// <summary>
    /// 骨骼挂载槽位映射。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> SlotAttachOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Root (Default)"] = "<root>",
        ["Head slot POE 2"] = "aux_Head_attachment",
        ["Back slot POE 2"] = "aux_Back_attachment",
        ["Waist slot POE 2"] = "spine_1_jntBnd",
        ["Chest slot POE 2"] = "chest_jntBnd",
        ["Left Shoulder slot POE 2"] = "L_shoulder_attachment_jntBnd",
        ["Right Shoulder slot POE 2"] = "R_shoulder_attachment_jntBnd",
        ["Left Ring slot POE 2"] = "L_finger_3_jntBnd_1",
        ["Right Ring slot POE 2"] = "R_finger_3_jntBnd_1",
        ["Neck slot POE 2"] = "neck_jntBnd"
    };

    public IReadOnlyList<MtxEntry> AllEntries => _allEntries;

    private MtxDatabase()
    {
        // 构造函数不能抛：异常会被 Lazy 永久缓存（见 LoadError 的说明）。
        try
        {
            LoadEmbeddedData();
        }
        catch (Exception ex)
        {
            LoadError = $"加载商城外观数据库失败：{ex.Message}";
            _allEntries.Clear();
            _byKey.Clear();
        }
    }

    private void LoadEmbeddedData()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            throw new FileNotFoundException($"嵌入资源未找到: {ResourceName}");
        }

        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var doc = JsonDocument.Parse(gzip);

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var rawCategory = prop.Name;
            foreach (var itemElem in prop.Value.EnumerateArray())
            {
                var entry = new MtxEntry
                {
                    Name = itemElem.GetProperty("Name").GetString() ?? string.Empty,
                    Group = itemElem.TryGetProperty("Group", out var gp) ? gp.GetString() : null,
                    DisplayGroup = itemElem.TryGetProperty("DisplayGroup", out var dgp) ? dgp.GetString() : null,
                    IconDdsPath = itemElem.TryGetProperty("IconDdsPath", out var ic) ? ic.GetString() : null,
                    PreviewUrl = itemElem.TryGetProperty("PreviewUrl", out var pu) ? pu.GetString() : null,
                    SlotAttach = itemElem.TryGetProperty("SlotAttach", out var sa) ? sa.GetString() : null,
                    SlotAttachLabel = itemElem.TryGetProperty("SlotAttachLabel", out var sal) ? sal.GetString() : null,
                    ArmourSetFolder = itemElem.TryGetProperty("ArmourSetFolder", out var asf) ? asf.GetString() : null,
                    ArmourSetFilePrefix = itemElem.TryGetProperty("ArmourSetFilePrefix", out var asfp) ? asfp.GetString() : null,
                    IsArmourSetGlobal = itemElem.TryGetProperty("IsArmourSetGlobal", out var isg) && isg.GetBoolean(),
                    IsConstructionEffectPack = itemElem.TryGetProperty("IsConstructionEffectPack", out var isc) && isc.GetBoolean(),
                    InternalPaths = itemElem.TryGetProperty("InternalPaths", out var ip) && ip.ValueKind == JsonValueKind.Array
                        ? ip.EnumerateArray().Select(x => x.GetString()!).ToArray()
                        : null
                };

                // 分类归一化：将防具套装独立归为 ArmourSets
                if (entry.IsArmourSetGlobal)
                {
                    entry.Category = MtxCategory.ArmourSets;
                }
                else
                {
                    entry.Category = rawCategory switch
                    {
                        "Skills" => MtxCategory.Skills,
                        "Portals" => MtxCategory.Portals,
                        "Pets" => MtxCategory.Pets,
                        "Misc" => MtxCategory.Misc,
                        _ => MtxCategory.CharacterEffects
                    };
                }

                _allEntries.Add(entry);

                // 用「名称 + 套装目录」做复合键。
                // 205 款防具里有 2 组重名（Assassin Armour / Blood Guard Armour），
                // 旧的 _byName[name] = entry 是后写覆盖，导致：
                //   · 前端按名字取第一条（coremarakethassassin）
                //   · 后端按名字取最后一条（ezomyte）
                //   界面显示的和实际写进客户端的不是同一款，另有两款永远选不到。
                _byKey[BuildKey(entry.Name, entry.ArmourSetFolder)] = entry;
            }
        }
    }

    private static string BuildKey(string name, string? folder)
        => string.IsNullOrEmpty(folder) ? name : $"{name}\u0000{folder}";

    /// <summary>
    /// 按名称 + 套装目录精确定位条目。
    ///
    /// <paramref name="folder"/> 为空时退回「按名字取第一条」：调用方若不知道目录，
    /// 至少行为与旧版一致，不会因为找不到而整条丢失。
    /// </summary>
    public MtxEntry? FindByName(string name, string? folder = null)
    {
        if (_byKey.TryGetValue(BuildKey(name, folder), out var exact)) return exact;

        // 没给目录（或目录已变）：按名字取第一条，保证老配置仍可解析。
        return _allEntries.FirstOrDefault(
            e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 读取内嵌的鼠标指针位图数据。
    /// </summary>
    public static byte[]? ReadCursorBitmap(string packName, string fileName)
    {
        var asm = Assembly.GetExecutingAssembly();
        // 匹配逻辑名，如 FreiAtlas.Patcher.Resources.mtx.cursors.EternalCursor.default.bmp
        var resourcePath = $"FreiAtlas.Patcher.Resources.mtx.cursors.{packName}.{fileName}";
        using var stream = asm.GetManifestResourceStream(resourcePath);
        if (stream is null)
        {
            // 模糊查找匹配
            var match = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.Contains(packName, StringComparison.OrdinalIgnoreCase)
                                     && n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
            if (match is null) return null;
            using var altStream = asm.GetManifestResourceStream(match);
            if (altStream is null) return null;
            using var ms2 = new MemoryStream();
            altStream.CopyTo(ms2);
            return ms2.ToArray();
        }

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
