using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;

using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.EffectPatch.MudMan;

/// <summary>
/// 泥人贴图表：从内嵌资源加载三份数据。
///
/// 数据来源是对易刷能力包（modules/eni/3.0.58/worker.js 与热更脚本 remote_ni.js）的完整提取：
///   · 名单表（mt）28,404 条 —— 使用 172 字节默认纯灰 DDS 块（qd()）的路径
///   · 定制表（ua）10,394 条 —— 路径 → 预置 DDS 字节（十六进制），
///     原图各自的色调被压进 4×4~16×4 的小块里，保留场景大致明暗与色调
///   · 保护排除表（ye + k）15,087 条 —— 热更规则中明确排除的怪物、机制与 UI 贴图
///
/// 加载策略：首次访问时惰性解压，之后常驻。
/// </summary>
public static class MudManTables
{
    private const string ResourceName = "FreiAtlas.Patcher.Resources.mudman.graytables.gz";
    private const string Magic = "MUDMAN1";

    private static readonly Lazy<Tables> Loaded = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>定制表条目数。</summary>
    public static int CustomCount => Loaded.Value.Custom.Count;

    /// <summary>名单表条目数。</summary>
    public static int ListCount => Loaded.Value.Listed.Count;

    /// <summary>热更规则排除表条目数。</summary>
    public static int ExcludedCount => Loaded.Value.Excluded.Count;

    /// <summary>数据是否可用。资源缺失或损坏时为 false。</summary>
    public static bool IsAvailable => Loaded.Value.Ok;

    /// <summary>加载失败原因；成功时为 null。</summary>
    public static string? LoadError => Loaded.Value.Error;

    /// <summary>该路径是否在任一张原始表内（未扣除排除表）。</summary>
    public static bool IsInRawTables(string virtualPath)
    {
        var key = Normalize(virtualPath);
        var t = Loaded.Value;
        return t.Custom.ContainsKey(key) || t.Listed.Contains(key);
    }

    /// <summary>该路径是否在热更保护排除表内。</summary>
    public static bool IsExcluded(string virtualPath)
    {
        var key = Normalize(virtualPath);
        return Loaded.Value.Excluded.Contains(key);
    }

    /// <summary>该路径是否在泥人有效名单内（在 mt 或 ua 表中，且未被排除表拦截）。</summary>
    public static bool IsListed(string virtualPath)
    {
        var key = Normalize(virtualPath);
        var t = Loaded.Value;
        if (t.Excluded.Contains(key)) return false;
        return t.Custom.ContainsKey(key) || t.Listed.Contains(key);
    }

    /// <summary>取定制块；不在定制表内返回 null。</summary>
    public static byte[]? TryGetCustom(string virtualPath)
    {
        var key = Normalize(virtualPath);
        return Loaded.Value.Custom.TryGetValue(key, out var bytes) ? bytes : null;
    }

    private static string Normalize(string path) =>
        VirtualPath.Normalize(path).ToLowerInvariant();

    private static Tables Load()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                return Tables.Fail($"内嵌资源缺失：{ResourceName}");
            }

            using var reader = new StringReader(GzipUtil.DecompressToString(stream, 32 * 1024 * 1024));

            var magic = reader.ReadLine();
            if (magic != Magic)
            {
                return Tables.Fail($"表头不符：{magic ?? "<空>"}");
            }

            var custom = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var listed = new HashSet<string>(StringComparer.Ordinal);
            var excluded = new HashSet<string>(StringComparer.Ordinal);

            if (!TryReadCount(reader, out var listCount))
            {
                return Tables.Fail("名单表条目数解析失败。");
            }

            for (int i = 0; i < listCount; i++)
            {
                var line = reader.ReadLine();
                if (string.IsNullOrEmpty(line)) continue;
                listed.Add(Normalize(line));
            }

            if (!TryReadCount(reader, out var customCount))
            {
                return Tables.Fail("定制表条目数解析失败。");
            }

            for (int i = 0; i < customCount; i++)
            {
                var line = reader.ReadLine();
                if (string.IsNullOrEmpty(line)) continue;

                var tab = line.IndexOf('\t');
                if (tab <= 0 || tab + 1 >= line.Length) continue;

                var path = Normalize(line[..tab]);
                var hex = line[(tab + 1)..];

                if (!TryParseHex(hex, out var bytes)) continue;
                custom[path] = bytes;
            }

            // 读取可选的第三段：热更保护排除表
            if (TryReadCount(reader, out var excludedCount))
            {
                for (int i = 0; i < excludedCount; i++)
                {
                    var line = reader.ReadLine();
                    if (string.IsNullOrEmpty(line)) continue;
                    excluded.Add(Normalize(line));
                }
            }

            return new Tables(custom, listed, excluded, true, null);
        }
        catch (Exception ex)
        {
            return Tables.Fail($"加载泥人贴图表失败：{ex.Message}");
        }
    }

    private static bool TryReadCount(TextReader reader, out int count)
    {
        count = 0;
        var line = reader.ReadLine();
        return line is not null && int.TryParse(line, out count) && count >= 0;
    }

    private static bool TryParseHex(string hex, out byte[] bytes)
    {
        bytes = [];
        if (hex.Length == 0 || (hex.Length & 1) != 0) return false;

        var result = new byte[hex.Length / 2];
        for (int i = 0; i < result.Length; i++)
        {
            int hi = HexVal(hex[i * 2]);
            int lo = HexVal(hex[i * 2 + 1]);
            if (hi < 0 || lo < 0) return false;
            result[i] = (byte)((hi << 4) | lo);
        }

        bytes = result;
        return true;
    }

    private static int HexVal(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1
    };

    private sealed class Tables(
        Dictionary<string, byte[]> custom,
        HashSet<string> listed,
        HashSet<string> excluded,
        bool ok,
        string? error)
    {
        public Dictionary<string, byte[]> Custom { get; } = custom;
        public HashSet<string> Listed { get; } = listed;
        public HashSet<string> Excluded { get; } = excluded;
        public bool Ok { get; } = ok;
        public string? Error { get; } = error;

        public static Tables Fail(string error) =>
            new(new Dictionary<string, byte[]>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal),
                false,
                error);
    }
}
