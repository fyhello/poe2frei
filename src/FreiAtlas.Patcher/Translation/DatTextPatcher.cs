using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FreiAtlas.Patcher.Core;
using FreiAtlas.Patcher.Modules.Price;

namespace FreiAtlas.Patcher.Translation;

/// <summary>
/// DAT 核心数据表文本列无损重写注入引擎：
/// 以国际服原版表为底模，仅对齐追加重写其文本列偏移指针，
/// 严格保持原表行数与二进制元数据结构 100% 不变，杜绝因行数或外键不匹配引起的客户端越界崩溃。
/// </summary>
public static class DatTextPatcher
{
    public sealed class FullDatPatchCatalog
    {
        [JsonPropertyName("Tables")]
        public Dictionary<string, TablePatchDefinition> Tables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("BaseItems")]
        public Dictionary<string, string> BaseItems { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class TablePatchDefinition
    {
        [JsonPropertyName("FileName")]
        public string FileName { get; set; } = "";

        [JsonPropertyName("RowSize")]
        public int RowSize { get; set; }

        [JsonPropertyName("RowPatches")]
        public List<RowCellPatch> RowPatches { get; set; } = [];
    }

    public sealed class RowCellPatch
    {
        [JsonPropertyName("Row")]
        public int Row { get; set; }

        [JsonPropertyName("Col")]
        public int Col { get; set; }

        [JsonPropertyName("Text")]
        public string Text { get; set; } = "";
    }

    private static FullDatPatchCatalog? _cachedCatalog;
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// 从嵌入资源加载预打包的官方简中全量词典数据（Gzip 压缩）
    /// </summary>
    public static FullDatPatchCatalog GetEmbeddedCatalog()
    {
        if (_cachedCatalog != null) return _cachedCatalog;

        var assembly = typeof(DatTextPatcher).Assembly;

        // 1. 优先加载嵌入的 Gzip 资源
        using (var stream = assembly.GetManifestResourceStream("FreiAtlas.Patcher.Resources.dat_translations.json.gz"))
        {
            if (stream != null)
            {
                var jsonBytes = GzipUtil.DecompressToBytes(stream, 64 * 1024 * 1024);
                _cachedCatalog = JsonSerializer.Deserialize<FullDatPatchCatalog>(jsonBytes, _jsonOptions) ?? new();
                return _cachedCatalog;
            }
        }

        // 2. 回退到输出目录中的 dat_translations.json.gz
        string fallbackGz = Path.Combine(AppContext.BaseDirectory, "Resources", "dat_translations.json.gz");
        if (File.Exists(fallbackGz))
        {
            using var fs = File.OpenRead(fallbackGz);
            var jsonBytes = GzipUtil.DecompressToBytes(fs, 64 * 1024 * 1024);
            _cachedCatalog = JsonSerializer.Deserialize<FullDatPatchCatalog>(jsonBytes, _jsonOptions) ?? new();
            return _cachedCatalog;
        }

        // 3. 回退到未压缩 json
        string fallbackJson = Path.Combine(AppContext.BaseDirectory, "Resources", "dat_translations.json");
        if (File.Exists(fallbackJson))
        {
            using var fs = File.OpenRead(fallbackJson);
            _cachedCatalog = JsonSerializer.Deserialize<FullDatPatchCatalog>(fs, _jsonOptions) ?? new();
            return _cachedCatalog;
        }

        throw new FileNotFoundException("未找到嵌入的 dat_translations.json.gz 官方简中全量翻译资源。");
    }

    /// <summary>
    /// 对任意行对齐或键对齐的数据表进行官方简中文本列注入
    /// </summary>
    public static (byte[] PatchedBytes, int PatchedCount) PatchTable(
        byte[] originalBytes,
        TablePatchDefinition def)
    {
        if (originalBytes.Length < 16 || def.RowPatches.Count == 0)
            return (originalBytes, 0);

        int rowCount = BitConverter.ToInt32(originalBytes, 0);
        if (rowCount <= 0) return (originalBytes, 0);

        int rowSize = def.RowSize;
        int stringBase = DatBinaryUtils.FindSentinel(originalBytes, rowCount);
        if (stringBase <= 4)
        {
            stringBase = 4 + rowCount * rowSize;
        }

        if (stringBase >= originalBytes.Length)
            return (originalBytes, 0);

        // 行尺寸必须与文件实际布局一致，否则指针位置会整体错位。
        //
        // def.RowSize 来自外部 schema，游戏版本更新后可能变化。文件头之后到
        // 字符串哨兵之间就是全部定长行，(stringBase - 4) / rowCount 即真实行
        // 尺寸。二者不等时若继续按 schema 写入，会把 i32 数值或 foreignrow
        // 索引当成指针覆盖掉，客户端读取越界。
        int actualRowSize = (stringBase - 4) / rowCount;
        if (actualRowSize != rowSize)
            return (originalBytes, 0);

        var replacements = new List<(int PointerPos, string NewText)>();
        foreach (var p in def.RowPatches)
        {
            if (p.Row < 0 || p.Row >= rowCount) continue;
            int ptrPos = 4 + p.Row * rowSize + p.Col;
            if (ptrPos + 8 <= stringBase)
            {
                // 运行期槽位安全断言守卫：确保原位置高 32 位为 0 且指针指向合法字符串区
                ulong origPtr = BitConverter.ToUInt64(originalBytes, ptrPos);
                uint high32 = (uint)((origPtr >> 32) & 0xFFFFFFFF);
                if (high32 == 0 && origPtr < (ulong)(originalBytes.Length - stringBase))
                {
                    replacements.Add((ptrPos, p.Text));
                }
            }
        }

        if (replacements.Count == 0)
            return (originalBytes, 0);

        byte[] patched = DatBinaryUtils.RewriteStringPointersAppend(originalBytes, stringBase, replacements);
        return (patched, replacements.Count);
    }

    /// <summary>
    /// 对 BaseItemTypes.datc64 进行官方简中名称注入（基于 MetadataPath 跨版本精确匹配）
    ///
    /// 与「物价注入」补丁合并：两者写的是同一列（Name），
    /// 若直接覆盖，先打物价后打汉化会把 <c>[150C]</c> 这类价格后缀抹掉。
    /// 因此这里先从旧名称里剥离出价格后缀，再用「官方简中名 + 原价格后缀」重写，
    /// 使两个补丁可以任意顺序应用且互不破坏。
    ///
    /// 客户端原始 BaseItemTypes 的名称不含价格后缀，
    /// 剥离逻辑对未打物价的客户端不产生任何改变（幂等）。
    /// </summary>
    public static (byte[] PatchedBytes, int PatchedCount) PatchBaseItemTypes(
        byte[] originalBytes,
        IReadOnlyDictionary<string, string>? customMap = null)
    {
        var catalog = customMap ?? GetEmbeddedCatalog().BaseItems;
        var layout = PricePatchService.DetectBaseItemLayout(originalBytes);
        var items = PricePatchService.ScanBaseItemNames(originalBytes);

        var replacements = new List<(int PointerPos, string NewText)>();
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.MetadataPath) ||
                !catalog.TryGetValue(item.MetadataPath, out string? zhName) ||
                string.IsNullOrWhiteSpace(zhName))
            {
                continue;
            }

            // 保留物价补丁已追加的价格后缀（如 " [150C]"）。
            // StripExistingPriceSuffix 返回去掉后缀的纯名称；用它和原名称比较即可取出后缀。
            string suffix = ExtractPriceSuffix(item.Name);
            string newName = suffix.Length == 0 ? zhName : zhName + suffix;

            if (newName != item.Name)
            {
                replacements.Add((item.NamePointerPos, newName));
            }
        }

        if (replacements.Count == 0)
        {
            return (originalBytes, 0);
        }

        byte[] patched = DatBinaryUtils.RewriteStringPointersAppend(originalBytes, layout.StringBase, replacements);
        return (patched, replacements.Count);
    }

    /// <summary>
    /// 从物品名里取出价格后缀部分（含分隔符），无后缀时返回空串。
    ///
    /// 借助 <see cref="PricePatchService.StripExistingPriceSuffix"/> 判定：
    /// 剥离后长度变短，说明尾部确有价格后缀，差值即为该后缀。
    /// </summary>
    private static string ExtractPriceSuffix(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;

        // 分隔符为空时由方法内部使用默认价格正则
        string stripped = PricePatchService.StripExistingPriceSuffix(name, string.Empty);
        if (stripped.Length >= name.Length) return string.Empty;

        return name.Substring(stripped.Length);
    }

}
