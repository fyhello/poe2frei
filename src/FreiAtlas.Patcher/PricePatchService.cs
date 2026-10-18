using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FreiAtlas.Patcher;

/// <summary>
/// 物品物价注入行
/// </summary>
public sealed class PriceRow
{
    public string MetadataPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Price { get; set; } = string.Empty;
    public string NewName { get; set; } = string.Empty;

    public PriceRow() { }

    public PriceRow(string name, string price, string metadataPath = "")
    {
        Name = name;
        Price = price;
        MetadataPath = metadataPath;
    }
}

/// <summary>
/// 客户端原生掉落物价直接注入服务（针对 BaseItemTypes.datc64）
/// 负责直接在地上掉落物名称后追加市场参考物价（如 [15C]、[2.5D]）
/// </summary>
public sealed class PricePatchService
{
    public sealed record DatLayout(int RowCount, int RowSize, int StringBase);

    public sealed class BaseItemName
    {
        public int RowIndex { get; init; }
        public string MetadataPath { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public int NameOffset { get; init; }
        public int NamePointerPos { get; init; }
        public int NameStart { get; init; }
        public int NameEnd { get; init; }
    }

    public sealed class NameReplacement
    {
        public int RowIndex { get; init; }
        public int NameStart { get; init; }
        public int NameEnd { get; init; }
        public int NameOffset { get; init; }
        public int NamePointerPos { get; init; }
        public string OldName { get; init; } = string.Empty;
        public string RequestedName { get; init; } = string.Empty;
        public string FittedName { get; init; } = string.Empty;
        public string MetadataPath { get; init; } = string.Empty;
        public string Price { get; init; } = string.Empty;
        public int SlotChars => (NameEnd - NameStart) / 2;
        public bool Compacted => !string.Equals(RequestedName, FittedName, StringComparison.Ordinal);
    }

    public sealed class PricePatchResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public int PatchedNames { get; set; }
        public int FullTextNames { get; set; }
        public int CompactedNames { get; set; }
        public int SourceSize { get; set; }
        public int PatchedSize { get; set; }
        public List<string> Warnings { get; set; } = [];
    }

    private static readonly byte[] MetadataMarker = Encoding.Unicode.GetBytes("Metadata/Items/");
    private static readonly Regex PriceRe = new(@"^\s*\[?([0-9]+(?:\.[0-9]+)?)([A-Za-z]+)\]?\s*$", RegexOptions.Compiled);
    private const string PriceSuffixPattern = @"\[?(?:<1|[0-9]+(?:\.[0-9]+)?)[a-zA-Z]+\]?";

    public static readonly Dictionary<string, string> EnglishToMetadataMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Mirror of Kalandra"] = "Metadata/Items/Currency/CurrencyDuplicate",
        ["Divine Orb"] = "Metadata/Items/Currency/CurrencyModValues",
        ["Exalted Orb"] = "Metadata/Items/Currency/CurrencyAddModToRare",
        ["Chaos Orb"] = "Metadata/Items/Currency/CurrencyRerollRare",
        ["Perfect Jeweller's Orb"] = "Metadata/Items/Currency/CurrencySocketCount6",
        ["Greater Jeweller's Orb"] = "Metadata/Items/Currency/CurrencySocketCount5",
        ["Jeweller's Orb"] = "Metadata/Items/Currency/CurrencyRerollSocketNumbers",
        ["Orb of Annulment"] = "Metadata/Items/Currency/CurrencyRemoveMod",
        ["Orb of Chance"] = "Metadata/Items/Currency/CurrencyUpgradeRandomly",
        ["Orb of Alchemy"] = "Metadata/Items/Currency/CurrencyUpgradeToRare",
        ["Regal Orb"] = "Metadata/Items/Currency/CurrencyUpgradeMagicToRare",
        ["Vaal Orb"] = "Metadata/Items/Currency/CurrencyVaal",
        ["Gemcutter's Prism"] = "Metadata/Items/Currency/CurrencyGemQuality",
        ["Glassblower's Bauble"] = "Metadata/Items/Currency/CurrencyFlaskQuality",
        ["Blacksmith's Whetstone"] = "Metadata/Items/Currency/CurrencyWeaponQuality",
        ["Armourer's Scrap"] = "Metadata/Items/Currency/CurrencyArmourQuality",
        ["Orb of Transmutation"] = "Metadata/Items/Currency/CurrencyUpgradeToMagic",
        ["Orb of Augmentation"] = "Metadata/Items/Currency/CurrencyAddModToMagic",
        ["Orb of Alteration"] = "Metadata/Items/Currency/CurrencyRerollMagic",
        ["Scroll of Wisdom"] = "Metadata/Items/Currency/CurrencyIdentification",
        ["Portal Scroll"] = "Metadata/Items/Currency/CurrencyPortal",
        ["Artificer's Orb"] = "Metadata/Items/Currency/CurrencyItemRerollSocketColours",
        ["Lesser Jeweller's Orb"] = "Metadata/Items/Currency/CurrencySocketCount3"
    };

    private readonly PatchEngine _engine;

    public PricePatchService(PatchEngine engine)
    {
        _engine = engine;
    }

    private byte[]? ReadBaseItemsFile(GameModeInfo info, out string actualPath)
    {
        string[] candidatePaths = [
            info.BaseItemsPath,
            "data/balance/traditional chinese/baseitemtypes.datc64",
            "data/balance/simplified chinese/baseitemtypes.datc64",
            "data/balance/chinese/baseitemtypes.datc64",
            "data/traditional chinese/baseitemtypes.datc64",
            "data/simplified chinese/baseitemtypes.datc64",
            "data/balance/baseitemtypes.datc64",
            "data/baseitemtypes.datc64"
        ];

        actualPath = string.Empty;
        foreach (var p in candidatePaths.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var bytes = _engine.ReadGameFile(info, p, out actualPath);
            if (bytes != null && bytes.Length > 0)
            {
                return bytes;
            }
        }
        return null;
    }

    public List<BaseItemName> ScanClientBaseItems(GameModeInfo info)
    {
        var bytes = ReadBaseItemsFile(info, out _);
        if (bytes == null || bytes.Length == 0) return [];
        return ScanBaseItemNames(bytes);
    }

    /// <summary>
    /// 从 CSV 文件中读取物价行（格式支持 metadata_path, name, price, new_name）
    /// </summary>
    public static List<PriceRow> LoadPriceRowsFromCsv(string csvPath)
    {
        var list = new List<PriceRow>();
        if (!File.Exists(csvPath)) return list;

        string text = File.ReadAllText(csvPath, Encoding.UTF8).TrimStart('\ufeff');
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0) return list;

        var header = ParseCsvLine(lines[0]);
        int idxMeta = -1, idxName = -1, idxPrice = -1, idxNewName = -1;

        for (int i = 0; i < header.Count; i++)
        {
            string col = header[i].Trim();
            if (col.Equals("metadata_path", StringComparison.OrdinalIgnoreCase)) idxMeta = i;
            else if (col.Equals("name", StringComparison.OrdinalIgnoreCase)) idxName = i;
            else if (col.Equals("price", StringComparison.OrdinalIgnoreCase)) idxPrice = i;
            else if (col.Equals("new_name", StringComparison.OrdinalIgnoreCase)) idxNewName = i;
        }

        for (int j = 1; j < lines.Length; j++)
        {
            if (string.IsNullOrWhiteSpace(lines[j])) continue;
            var fields = ParseCsvLine(lines[j]);
            if (fields.Count == 0) continue;

            string GetCol(int colIdx) => (colIdx >= 0 && colIdx < fields.Count) ? fields[colIdx].Trim() : string.Empty;

            var row = new PriceRow
            {
                MetadataPath = GetCol(idxMeta),
                Name = GetCol(idxName),
                Price = GetCol(idxPrice),
                NewName = GetCol(idxNewName)
            };

            if (!string.IsNullOrEmpty(row.MetadataPath) || !string.IsNullOrEmpty(row.Name) ||
                !string.IsNullOrEmpty(row.Price) || !string.IsNullOrEmpty(row.NewName))
            {
                list.Add(row);
            }
        }

        return list;
    }

    /// <summary>
    /// 应用客户端物价直接注入补丁
    /// </summary>
    public PricePatchResult ApplyPatch(
        GameModeInfo info,
        List<PriceRow> priceRows,
        string mode = "append",
        string separator = " ",
        bool keepExistingPrice = false,
        bool patchSameNameDuplicates = true,
        Action<string>? progress = null)
    {
        var result = new PricePatchResult();

        try
        {
            progress?.Invoke("正在定位游戏 BaseItemTypes 数据表...");
            byte[]? rawBytes = ReadBaseItemsFile(info, out string targetPath);
            if (rawBytes == null || rawBytes.Length == 0)
            {
                result.ErrorMessage = "未在客户端中找到匹配的 BaseItemTypes 数据表文件。";
                progress?.Invoke(result.ErrorMessage);
                return result;
            }

            progress?.Invoke($"已读取 {targetPath}，正在解析条目...");
            var entries = ScanBaseItemNames(rawBytes);
            progress?.Invoke($"已解析 {entries.Count} 个基础物品条目，正在构建物价映射...");

            var (replacements, warnings) = BuildReplacements(entries, priceRows, separator, keepExistingPrice, mode, patchSameNameDuplicates);
            result.Warnings = warnings;

            if (replacements.Count == 0)
            {
                progress?.Invoke("未匹配到需要注入物价的物品，跳过写入。");
                result.Success = true;
                return result;
            }

            progress?.Invoke($"正在为 {replacements.Count} 个物品注入地面参考物价（模式：{mode}）...");
            byte[] patchedBytes = mode == "append"
                ? ApplyReplacementsAppend(rawBytes, replacements)
                : ApplyReplacementsFixed(rawBytes, replacements);

            bool ok = _engine.WriteGameFiles(info, [(targetPath, patchedBytes)]);
            if (ok)
            {
                result.Success = true;
                result.PatchedNames = replacements.Count;
                result.FullTextNames = replacements.Count(r => !r.Compacted);
                result.CompactedNames = replacements.Count(r => r.Compacted);
                result.SourceSize = rawBytes.Length;
                result.PatchedSize = patchedBytes.Length;
                progress?.Invoke($"客户端物价补丁应用成功！（已注入 {replacements.Count} 个物品物价）");
            }
            else
            {
                result.ErrorMessage = "写入物价补丁文件失败。";
                progress?.Invoke(result.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            progress?.Invoke($"应用物价补丁异常：{ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// 清除所有已注入的物价后缀，恢复纯净名称
    /// </summary>
    public PricePatchResult CleanPatch(GameModeInfo info, string separator = " ", Action<string>? progress = null)
    {
        var result = new PricePatchResult();

        try
        {
            progress?.Invoke("正在定位 BaseItemTypes 数据表以准备清洗物价...");
            byte[]? rawBytes = ReadBaseItemsFile(info, out string targetPath);
            if (rawBytes == null || rawBytes.Length == 0)
            {
                result.ErrorMessage = "未在客户端中找到匹配的 BaseItemTypes 数据表文件。";
                progress?.Invoke(result.ErrorMessage);
                return result;
            }

            var entries = ScanBaseItemNames(rawBytes);
            var (replacements, warnings) = BuildReplacements(entries, [], separator, keepExistingPrice: false, "append", patchSameNameDuplicates: true);
            result.Warnings = warnings;

            if (replacements.Count == 0)
            {
                progress?.Invoke("数据表中未发现物价后缀，无需清洗。");
                result.Success = true;
                return result;
            }

            progress?.Invoke($"正在清洗 {replacements.Count} 个物品的物价后缀并恢复纯净名称...");
            byte[] cleanedBytes = ApplyReplacementsAppend(rawBytes, replacements);

            bool ok = _engine.WriteGameFiles(info, [(targetPath, cleanedBytes)]);
            if (ok)
            {
                result.Success = true;
                result.PatchedNames = replacements.Count;
                result.SourceSize = rawBytes.Length;
                result.PatchedSize = cleanedBytes.Length;
                progress?.Invoke($"物价后缀清洗成功！（已还原 {replacements.Count} 个物品）");
            }
            else
            {
                result.ErrorMessage = "写入清洗文件失败。";
                progress?.Invoke(result.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            progress?.Invoke($"清洗物价异常：{ex.Message}");
        }

        return result;
    }

    public static DatLayout DetectBaseItemLayout(byte[] data)
    {
        if (data.Length < 16)
        {
            throw new InvalidDataException("BaseItemTypes.datc64 文件太小。");
        }

        int rowCount = BitConverter.ToInt32(data, 0);
        if (rowCount <= 0)
        {
            throw new InvalidDataException($"BaseItemTypes.datc64 行数无效：{rowCount}。");
        }

        int firstPointer = BitConverter.ToInt32(data, 4);
        int metadataPos = IndexOfBytes(data, MetadataMarker);

        if (metadataPos >= 0)
        {
            int stringBase = metadataPos - firstPointer;
            if (stringBase > 4 && stringBase < data.Length)
            {
                int dataBytes = stringBase - 4;
                if (dataBytes % rowCount == 0)
                {
                    int rowSize = dataBytes / rowCount;
                    if (rowSize > 32)
                    {
                        return new DatLayout(rowCount, rowSize, stringBase);
                    }
                }
            }
        }

        // 回退查找哨兵
        for (int i = 4; i <= data.Length - 8; i++)
        {
            if (data[i] == 187 && data[i + 1] == 187 && data[i + 2] == 187 && data[i + 3] == 187 &&
                data[i + 4] == 187 && data[i + 5] == 187 && data[i + 6] == 187 && data[i + 7] == 187)
            {
                int dataBytes = i - 4;
                if (dataBytes % rowCount == 0)
                {
                    int rowSize = dataBytes / rowCount;
                    if (rowSize > 32)
                    {
                        return new DatLayout(rowCount, rowSize, i);
                    }
                }
            }
        }

        throw new InvalidDataException("无法探测 BaseItemTypes.datc64 的行尺寸与字符串基址。");
    }

    public static List<BaseItemName> ScanBaseItemNames(byte[] data)
    {
        var layout = DetectBaseItemLayout(data);
        var list = new List<BaseItemName>(layout.RowCount);

        for (int i = 0; i < layout.RowCount; i++)
        {
            int rowOffset = 4 + i * layout.RowSize;
            if (rowOffset + layout.RowSize > data.Length) break;

            uint metaOffset = BitConverter.ToUInt32(data, rowOffset);
            int namePtrPos = rowOffset + 32;
            if (namePtrPos + 4 > data.Length) break;

            uint nameOffset = BitConverter.ToUInt32(data, namePtrPos);
            if (metaOffset == 0 || metaOffset == 4278124286u || nameOffset == 0 || nameOffset == 4278124286u)
            {
                continue;
            }

            int metaStart = layout.StringBase + (int)metaOffset;
            int nameStart = layout.StringBase + (int)nameOffset;

            if (metaStart >= 0 && metaStart < data.Length - 1 && nameStart >= 0 && nameStart < data.Length - 1)
            {
                string meta = ReadUtf16LeZ(data, metaStart);
                string name = ReadUtf16LeZ(data, nameStart);
                int nameEnd = nameStart + name.Length * 2;

                if (meta.StartsWith("Metadata/Items/", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(name) && !name.StartsWith("Metadata/", StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(new BaseItemName
                    {
                        RowIndex = i,
                        MetadataPath = meta,
                        Name = name,
                        NameOffset = (int)nameOffset,
                        NamePointerPos = namePtrPos,
                        NameStart = nameStart,
                        NameEnd = nameEnd
                    });
                }
            }
        }

        return list;
    }

    public static (List<NameReplacement> Replacements, List<string> Warnings) BuildReplacements(
        List<BaseItemName> entries,
        List<PriceRow> priceRows,
        string separator,
        bool keepExistingPrice,
        string mode,
        bool patchSameNameDuplicates)
    {
        var metaMap = new Dictionary<string, BaseItemName>(StringComparer.OrdinalIgnoreCase);
        var nameMap = new Dictionary<string, List<BaseItemName>>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            metaMap[entry.MetadataPath] = entry;
            string clean = StripExistingPriceSuffix(entry.Name, separator);
            if (!nameMap.TryGetValue(clean, out var list))
            {
                list = [];
                nameMap[clean] = list;
            }
            list.Add(entry);
        }

        var replacements = new List<NameReplacement>();
        var warnings = new List<string>();
        var handledRowIndices = new HashSet<int>();

        for (int i = 0; i < priceRows.Count; i++)
        {
            var pr = priceRows[i];
            if (string.IsNullOrEmpty(pr.Price) && string.IsNullOrEmpty(pr.NewName)) continue;

            BaseItemName? target = null;
            List<BaseItemName> targets = [];

            string metaLookup = pr.MetadataPath;
            if (string.IsNullOrEmpty(metaLookup) && !string.IsNullOrEmpty(pr.Name))
            {
                if (EnglishToMetadataMap.TryGetValue(pr.Name, out var mappedMeta))
                {
                    metaLookup = mappedMeta;
                }
            }

            if (!string.IsNullOrEmpty(metaLookup))
            {
                if (metaMap.TryGetValue(metaLookup, out target))
                {
                    targets.Add(target);
                }
                else if (!string.IsNullOrEmpty(pr.Name) && nameMap.TryGetValue(pr.Name, out var byName))
                {
                    targets = byName;
                }
            }
            else if (!string.IsNullOrEmpty(pr.Name) && nameMap.TryGetValue(pr.Name, out var byName))
            {
                targets = byName;
            }

            if (targets.Count == 0) continue;

            foreach (var item in targets)
            {
                if (!handledRowIndices.Add(item.RowIndex)) continue;

                string clean = StripExistingPriceSuffix(item.Name, separator);
                string priceText = pr.Price.Trim();
                if (!priceText.StartsWith('[') && !string.IsNullOrEmpty(priceText))
                {
                    priceText = $"[{priceText}]";
                }

                string requested = !string.IsNullOrEmpty(pr.NewName)
                    ? pr.NewName
                    : $"{clean}{separator}{priceText}";

                int slotChars = (item.NameEnd - item.NameStart) / 2;
                string fitted = mode == "append" ? requested : FitNameWithPrice(clean, requested, pr.Price, separator, slotChars);

                replacements.Add(new NameReplacement
                {
                    RowIndex = item.RowIndex,
                    NameStart = item.NameStart,
                    NameEnd = item.NameEnd,
                    NameOffset = item.NameOffset,
                    NamePointerPos = item.NamePointerPos,
                    OldName = item.Name,
                    RequestedName = requested,
                    FittedName = fitted,
                    MetadataPath = item.MetadataPath,
                    Price = pr.Price
                });
            }
        }

        // 若需要清除未匹配条目的现有价格后缀
        if (!keepExistingPrice)
        {
            foreach (var entry in entries)
            {
                if (handledRowIndices.Contains(entry.RowIndex)) continue;
                string clean = StripExistingPriceSuffix(entry.Name, separator);
                if (clean != entry.Name)
                {
                    handledRowIndices.Add(entry.RowIndex);
                    replacements.Add(new NameReplacement
                    {
                        RowIndex = entry.RowIndex,
                        NameStart = entry.NameStart,
                        NameEnd = entry.NameEnd,
                        NameOffset = entry.NameOffset,
                        NamePointerPos = entry.NamePointerPos,
                        OldName = entry.Name,
                        RequestedName = clean,
                        FittedName = clean,
                        MetadataPath = entry.MetadataPath,
                        Price = string.Empty
                    });
                }
            }
        }

        return (replacements, warnings);
    }

    public static byte[] ApplyReplacementsAppend(byte[] data, List<NameReplacement> replacements)
    {
        var layout = DetectBaseItemLayout(data);
        using var memoryStream = new MemoryStream();
        memoryStream.Write(data, 0, data.Length);
        var dedup = new Dictionary<string, uint>(StringComparer.Ordinal);

        foreach (var r in replacements.OrderBy(x => x.RowIndex))
        {
            uint stringOffset;
            if (r.FittedName == r.OldName)
            {
                stringOffset = (uint)r.NameOffset;
            }
            else if (dedup.TryGetValue(r.FittedName, out uint cached))
            {
                stringOffset = cached;
            }
            else
            {
                if ((memoryStream.Length - layout.StringBase) % 2 != 0L)
                {
                    memoryStream.WriteByte(0);
                }

                stringOffset = (uint)(memoryStream.Length - layout.StringBase);
                byte[] bytes = Encoding.Unicode.GetBytes(r.FittedName);
                memoryStream.Write(bytes, 0, bytes.Length);
                memoryStream.WriteByte(0);
                memoryStream.WriteByte(0);
                memoryStream.WriteByte(0);
                memoryStream.WriteByte(0);
                dedup[r.FittedName] = stringOffset;
            }

            byte[] pointerBytes = BitConverter.GetBytes(stringOffset);
            long curPos = memoryStream.Position;
            memoryStream.Position = r.NamePointerPos;
            memoryStream.Write(pointerBytes, 0, 4);
            memoryStream.Position = curPos;
        }

        return memoryStream.ToArray();
    }

    public static byte[] ApplyReplacementsFixed(byte[] data, List<NameReplacement> replacements)
    {
        byte[] copy = (byte[])data.Clone();
        foreach (var r in replacements.OrderByDescending(x => x.NameStart))
        {
            byte[] oldBytes = Encoding.Unicode.GetBytes(r.OldName);
            byte[] newBytes = Encoding.Unicode.GetBytes(r.FittedName);
            if (newBytes.Length > oldBytes.Length)
            {
                throw new InvalidOperationException($"Fixed 模式下文本长度超出槽位限制：{r.FittedName} > {r.OldName}");
            }

            Buffer.BlockCopy(newBytes, 0, copy, r.NameStart, newBytes.Length);
            for (int i = newBytes.Length; i < oldBytes.Length; i++)
            {
                copy[r.NameStart + i] = 0;
            }
        }
        return copy;
    }

    public static string StripExistingPriceSuffix(string name, string separator)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        string pattern = string.IsNullOrEmpty(separator)
            ? PriceSuffixPattern
            : $@"(?:{Regex.Escape(separator)})?{PriceSuffixPattern}";
        return Regex.Replace(name, pattern, string.Empty).Trim();
    }

    private static string FitNameWithPrice(string baseName, string requestedName, string price, string separator, int maxChars)
    {
        if (requestedName.Length <= maxChars) return requestedName;

        var variants = CompactPriceVariants(price);
        foreach (var v in variants)
        {
            string candidate = $"{baseName}{separator}{v}";
            if (candidate.Length <= maxChars) return candidate;
        }

        foreach (var v in variants)
        {
            int remaining = maxChars - separator.Length - v.Length;
            if (remaining > 0 && remaining <= baseName.Length)
            {
                return $"{baseName[..remaining]}{separator}{v}";
            }
        }

        return baseName.Length <= maxChars ? baseName : baseName[..maxChars];
    }

    public static List<string> CompactPriceVariants(string price)
    {
        string text = price.Trim();
        var list = new List<string> { text };
        var match = PriceRe.Match(text);
        if (!match.Success) return list;

        string numPart = match.Groups[1].Value;
        string unitPart = match.Groups[2].Value;
        string cleanNum = numPart.Contains('.') ? numPart.TrimEnd('0').TrimEnd('.') : numPart;

        list.Add($"[{cleanNum}{unitPart}]");
        if (cleanNum.StartsWith("0."))
        {
            list.Add($"[{cleanNum[1..]}{unitPart}]");
        }

        if (decimal.TryParse(numPart, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal val))
        {
            if (val >= 10m)
            {
                list.Add($"[{(int)Math.Round(val, 0, MidpointRounding.AwayFromZero)}{unitPart}]");
            }
            else if (val > 0m && val < 1m)
            {
                list.Add($"[<1{unitPart}]");
            }
        }

        return list.Distinct(StringComparer.Ordinal).ToList();
    }

    private static List<string> ParseCsvLine(string line)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    list.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }
        }

        list.Add(sb.ToString());
        return list;
    }

    private static string ReadUtf16LeZ(byte[] data, int start, int maxChars = 500)
    {
        var sb = new StringBuilder(64);
        for (int i = start; i + 1 < data.Length && sb.Length < maxChars; i += 2)
        {
            int c = data[i] | (data[i + 1] << 8);
            if (c == 0) break;
            sb.Append((char)c);
        }
        return sb.ToString();
    }

    private static int IndexOfBytes(byte[] haystack, byte[] needle, int start = 0)
    {
        if (needle.Length == 0 || haystack.Length - start < needle.Length) return -1;
        for (int i = start; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return i;
        }
        return -1;
    }
}
