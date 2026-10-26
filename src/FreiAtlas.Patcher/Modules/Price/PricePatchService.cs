using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.Modules.Price;

/// <summary>
/// 物品物价注入行
/// </summary>
public sealed class PriceRow
{
    public string MetadataPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Price { get; set; } = string.Empty;
    public string NewName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;

    public PriceRow() { }

    public PriceRow(string name, string price, string metadataPath = "", string category = "")
    {
        Name = name;
        Price = price;
        MetadataPath = metadataPath;
        Category = category;
    }
}

/// <summary>
/// 客户端原生掉落物价直接注入服务（针对 BaseItemTypes.datc64）
/// 负责直接在地上掉落物名称后追加市场参考物价（如 [15C]、[2.5D]）
/// </summary>
public sealed class PricePatchService
{
    public const int WordsRowSize = 64;
    public const int WordsEnNameOffset = 4;
    public const int WordsDisplayNameOffset = 48;

    public sealed class WordItemName
    {
        public int RowIndex { get; init; }
        public string EnName { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public int DisplayOffset { get; init; }
        public int DisplayPointerPos { get; init; }
        public int DisplayStart { get; init; }
        public int DisplayEnd { get; init; }
    }

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
        public string Category { get; init; } = string.Empty;
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

    /// <summary>
    /// 物价注入预览明细项，用于在前端列表中清晰展示
    /// </summary>
    public sealed class PricePreviewItem
    {
        public int RowIndex { get; set; }
        public string OriginalName { get; set; } = string.Empty;
        public string Price { get; set; } = string.Empty;
        public string NewName { get; set; } = string.Empty;
        public string MetadataPath { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public bool Compacted { get; set; }
    }

    /// <summary>只读预览结果：读取真实客户端 dat 后匹配出的计划统计与明细列表，不写文件。</summary>
    public sealed class PricePatchPreview
    {
        public string Token { get; set; } = Guid.NewGuid().ToString("N");
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public string TargetPath { get; set; } = string.Empty;
        public int CandidateCount { get; set; }
        public int MatchedCount { get; set; }
        public int FullTextNames { get; set; }
        public int CompactedNames { get; set; }
        public int UnmatchedCount => Math.Max(0, CandidateCount - MatchedCount);
        public string ClientSha256 { get; set; } = string.Empty;
        public string MetadataSignature { get; set; } = string.Empty;
        public string PriceRowsHash { get; set; } = string.Empty;
        public List<string> Warnings { get; set; } = [];
        public List<PricePreviewItem> Items { get; set; } = [];
        public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    private static readonly byte[] MetadataMarker = Encoding.Unicode.GetBytes("Metadata/Items/");
    private static readonly Regex PriceRe = new(@"^\s*\[?([0-9]+(?:\.[0-9]+)?)([A-Za-z]+)\]?\s*$", RegexOptions.Compiled);
    private const string PriceSuffixPattern = @"\[?(?:★\s*)?(?:<1|[0-9]+(?:\.[0-9]+)?)[cCeEdD]\]?";

    /// <summary>
    /// 英文名到 Metadata 路径的映射。
    /// 数据位于 <see cref="PriceNameMaps"/>，此处转发以兼容既有调用点。
    /// </summary>
    public static Dictionary<string, string> EnglishToMetadataMap => PriceNameMaps.EnglishToMetadataMap;

    /// <summary>
    /// 英文名到中文名的映射。
    /// 数据位于 <see cref="PriceNameMaps"/>，此处转发以兼容既有调用点。
    /// </summary>
    public static Dictionary<string, string> EnglishToChineseMap => PriceNameMaps.EnglishToChineseMap;

    private static readonly Dictionary<char, char> TradToSimpCharMap = new()
    {
        ['聖'] = '圣', ['鏡'] = '镜', ['寶'] = '宝', ['體'] = '体',
        ['術'] = '术', ['階'] = '阶', ['導'] = '导', ['圖'] = '图',
        ['連'] = '连', ['煉'] = '炼', ['點'] = '点', ['數'] = '数',
        ['極'] = '极', ['終'] = '终', ['開'] = '开', ['關'] = '关',
        ['斷'] = '断', ['擴'] = '扩', ['張'] = '张', ['強'] = '强',
        ['屬'] = '属', ['靈'] = '灵', ['魂'] = '魂', ['擊'] = '击',
        ['電'] = '电', ['傷'] = '伤', ['護'] = '护', ['閃'] = '闪',
        ['避'] = '避', ['異'] = '异', ['遠'] = '远', ['頂'] = '顶',
        ['級'] = '级', ['藥'] = '药', ['劑'] = '剂', ['銘'] = '铭',
        ['傳'] = '传', ['雙'] = '双', ['與'] = '与', ['無'] = '无',
        ['庫'] = '库', ['殘'] = '残', ['熾'] = '炽', ['熱'] = '热',
        ['劇'] = '剧', ['滅'] = '灭'
    };

    public static string NormalizeSimplified(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var chars = text.ToCharArray();
        bool changed = false;
        for (int i = 0; i < chars.Length; i++)
        {
            if (TradToSimpCharMap.TryGetValue(chars[i], out char simp))
            {
                chars[i] = simp;
                changed = true;
            }
        }
        return changed ? new string(chars) : text;
    }

    private readonly PatchEngine _engine;

    public PricePatchService(PatchEngine engine)
    {
        _engine = engine;
    }

    private byte[]? ReadBaseItemsFile(GameModeInfo info, out string actualPath)
        => _engine.ReadFirstAvailableFile(info, [
            info.BaseItemsPath,
            "data/balance/traditional chinese/baseitemtypes.datc64",
            "data/balance/simplified chinese/baseitemtypes.datc64",
            "data/balance/chinese/baseitemtypes.datc64",
            "data/traditional chinese/baseitemtypes.datc64",
            "data/simplified chinese/baseitemtypes.datc64",
            "data/balance/baseitemtypes.datc64",
            "data/baseitemtypes.datc64"
        ], out actualPath);

    public List<BaseItemName> ScanClientBaseItems(GameModeInfo info)
    {
        var bytes = ReadBaseItemsFile(info, out _);
        if (bytes == null || bytes.Length == 0) return [];
        return ScanBaseItemNames(bytes);
    }

    public Dictionary<string, BaseItemName>? BuildEnglishToLocalBaseItemMap(
        GameModeInfo info,
        List<BaseItemName> localEntries,
        string separator = " ")
    {
        try
        {
            string[] enCandidatePaths = [
                "data/balance/baseitemtypes.datc64",
                "data/baseitemtypes.datc64"
            ];

            byte[]? enBytes = null;
            foreach (var p in enCandidatePaths)
            {
                enBytes = _engine.ReadGameFile(info, p, out _);
                if (enBytes != null && enBytes.Length > 0) break;
            }

            if (enBytes == null || enBytes.Length == 0) return null;

            var enEntries = ScanBaseItemNames(enBytes);
            var localByMeta = new Dictionary<string, BaseItemName>(StringComparer.OrdinalIgnoreCase);
            foreach (var loc in localEntries)
            {
                if (!string.IsNullOrEmpty(loc.MetadataPath))
                {
                    localByMeta[loc.MetadataPath] = loc;
                }
            }

            var map = new Dictionary<string, BaseItemName>(StringComparer.OrdinalIgnoreCase);
            foreach (var en in enEntries)
            {
                string enClean = StripExistingPriceSuffix(en.Name, separator);
                if (!string.IsNullOrEmpty(enClean) && localByMeta.TryGetValue(en.MetadataPath, out var locMatch))
                {
                    map[enClean] = locMatch;
                }
            }

            return map.Count > 0 ? map : null;
        }
        catch
        {
            return null;
        }
    }

    private byte[]? ReadWordsFile(GameModeInfo info, out string actualPath)
        => _engine.ReadFirstAvailableFile(info, [
            info.WordsPath,
            "data/balance/traditional chinese/words.datc64",
            "data/balance/simplified chinese/words.datc64",
            "data/balance/chinese/words.datc64",
            "data/traditional chinese/words.datc64",
            "data/simplified chinese/words.datc64",
            "data/balance/words.datc64",
            "data/words.datc64"
        ], out actualPath);


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
    /// 只读预览：读取真实客户端 dat、扫描真实行并构建匹配计划，不创建备份、不写文件。
    /// </summary>
    public PricePatchPreview PreviewPatch(
        GameModeInfo info,
        List<PriceRow> priceRows,
        string mode = "append",
        string separator = " ",
        bool keepExistingPrice = false,
        bool patchSameNameDuplicates = true,
        Action<string>? progress = null)
    {
        var preview = new PricePatchPreview { CandidateCount = priceRows.Count };

        try
        {
            progress?.Invoke("正在读取客户端 BaseItemTypes 数据表...");
            byte[]? rawBytes = ReadBaseItemsFile(info, out string targetPath);
            if (rawBytes == null || rawBytes.Length == 0)
            {
                preview.ErrorMessage = "未在客户端中找到匹配的 BaseItemTypes 数据表文件。";
                return preview;
            }

            preview.TargetPath = targetPath;
            preview.ClientSha256 = ComputeSha256(rawBytes);
            preview.MetadataSignature = ComputeMetadataSignature(rawBytes);
            preview.PriceRowsHash = ComputePriceRowsHash(priceRows);

            // 预览必须基于客户端当前内容，与随后真正写入时使用的数据源保持一致。
            // 若这里改用历史基线，用户看到的会是过期名称，与实际写入结果不符。
            byte[] sourceBytes = keepExistingPrice ? rawBytes : StripAllPriceSuffixes(rawBytes, separator);

            progress?.Invoke("正在扫描客户端基础物品并匹配价格...");
            var entries = ScanBaseItemNames(sourceBytes);
            var enMap = BuildEnglishToLocalBaseItemMap(info, entries, separator);
            var (replacements, warnings) = BuildReplacements(
                entries,
                priceRows,
                separator,
                keepExistingPrice,
                mode,
                patchSameNameDuplicates,
                enMap);

            // 2. 扫描 Words.datc64（暗金传奇装备）
            progress?.Invoke("正在读取客户端 Words 数据表（暗金装备）...");
            byte[]? wordsRawBytes = ReadWordsFile(info, out string wordsTargetPath);
            List<NameReplacement> wordsReplacements = [];
            if (wordsRawBytes != null && wordsRawBytes.Length > 0)
            {
                // 同 BaseItemTypes：预览与写入都基于当前内容
                byte[] wordsSourceBytes = keepExistingPrice
                    ? wordsRawBytes
                    : StripAllWordsPriceSuffixes(wordsRawBytes, separator);

                var wordsEntries = ScanWordNames(wordsSourceBytes);
                var (wReplacements, wWarnings) = BuildWordReplacements(
                    wordsEntries,
                    priceRows,
                    separator,
                    keepExistingPrice);
                wordsReplacements = wReplacements;
                warnings.AddRange(wWarnings);
            }

            int totalMatched = replacements.Count + wordsReplacements.Count;
            preview.MatchedCount = totalMatched;
            preview.FullTextNames = replacements.Count(r => !r.Compacted) + wordsReplacements.Count(r => !r.Compacted);
            preview.CompactedNames = replacements.Count(r => r.Compacted) + wordsReplacements.Count(r => r.Compacted);
            preview.Warnings.AddRange(warnings);

            var previewItems = new List<PricePreviewItem>(totalMatched);
            previewItems.AddRange(replacements.Select(r => new PricePreviewItem
            {
                RowIndex = r.RowIndex,
                OriginalName = r.OldName,
                Price = r.Price,
                NewName = r.FittedName,
                MetadataPath = r.MetadataPath,
                Category = r.Category,
                Compacted = r.Compacted
            }));
            previewItems.AddRange(wordsReplacements.Select(r => new PricePreviewItem
            {
                RowIndex = r.RowIndex,
                OriginalName = r.OldName,
                Price = r.Price,
                NewName = r.FittedName,
                MetadataPath = r.MetadataPath,
                Category = string.IsNullOrWhiteSpace(r.Category) ? "Unique" : r.Category,
                Compacted = r.Compacted
            }));

            preview.Items = previewItems;
            preview.Success = true;
            progress?.Invoke($"预览完成：已匹配 {totalMatched} 个客户端物品（基础通货 {replacements.Count}，暗金装备 {wordsReplacements.Count}）。");
        }
        catch (Exception ex)
        {
            preview.ErrorMessage = ex.Message;
            progress?.Invoke($"预览失败：{ex.Message}");
        }

        return preview;
    }

    private static string ComputePriceRowsHash(IEnumerable<PriceRow> priceRows)
    {
        string text = string.Join(
            '\n',
            priceRows
                .OrderBy(row => row.MetadataPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Name, StringComparer.Ordinal)
                .ThenBy(row => row.Price, StringComparer.Ordinal)
                .Select(row => $"{row.MetadataPath}|{row.Name}|{row.Price}|{row.NewName}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static string ComputeSha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>
    /// 应用客户端物价直接注入补丁（协同处理 BaseItemTypes 与 Words 表）
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
            // 1. 定位并注入 BaseItemTypes 数据表（通货、符文、宝石）
            progress?.Invoke("正在定位游戏 BaseItemTypes 数据表...");
            byte[]? rawBytes = ReadBaseItemsFile(info, out string targetPath);
            if (rawBytes == null || rawBytes.Length == 0)
            {
                result.ErrorMessage = "未在客户端中找到匹配的 BaseItemTypes 数据表文件。";
                progress?.Invoke(result.ErrorMessage);
                return result;
            }

            // 注入源始终是【客户端当前内容】剥离价格后缀后的结果。
            // 不使用任何历史基线快照：它会带入汉化前的旧名称、旧数值，
            // 把其他补丁的改动一并抹掉。回滚由内存中的 rawBytes 承担。
            byte[] sourceBytes = keepExistingPrice ? rawBytes : StripAllPriceSuffixes(rawBytes, separator);
            var entries = ScanBaseItemNames(sourceBytes);
            var enMap = BuildEnglishToLocalBaseItemMap(info, entries, separator);
            var (replacements, warnings) = BuildReplacements(entries, priceRows, separator, keepExistingPrice, mode, patchSameNameDuplicates, enMap);
            result.Warnings.AddRange(warnings);

            int basePatchedCount = 0;
            if (replacements.Count > 0)
            {
                progress?.Invoke($"正在为 {replacements.Count} 个基础物品注入地面参考物价（模式：{mode}）...");
                byte[] patchedBytes = mode == "append"
                    ? ApplyReplacementsAppend(sourceBytes, replacements)
                    : ApplyReplacementsFixed(sourceBytes, replacements);

                if (!_engine.WriteGameFiles(info, [(targetPath, patchedBytes)]))
                {
                    result.ErrorMessage = "写入 BaseItemTypes 物价补丁文件失败：" + (PatchEngine.LastWriteException?.Message ?? "未知原因");
                    progress?.Invoke(result.ErrorMessage);
                    return result;
                }

                byte[]? verifyBytes = _engine.ReadGameFile(info, targetPath, out _);
                if (verifyBytes == null || !VerifyPatchedNames(verifyBytes, replacements))
                {
                    string restoreMessage = RestorePreviousContent(info, targetPath, rawBytes);
                    result.ErrorMessage = "BaseItemTypes 物价写入后校验失败，已尝试还原备份。" + restoreMessage;
                    progress?.Invoke(result.ErrorMessage);
                    return result;
                }

                basePatchedCount = replacements.Count;
            }

            // 2. 定位并注入 Words 数据表（全量暗金传奇装备）
            int wordsPatchedCount = 0;
            byte[]? wordsRawBytes = ReadWordsFile(info, out string wordsTargetPath);
            if (wordsRawBytes != null && wordsRawBytes.Length > 0)
            {
                // 同 BaseItemTypes：注入源来自当前内容，不使用历史基线
                byte[] wordsSourceBytes = keepExistingPrice ? wordsRawBytes : StripAllWordsPriceSuffixes(wordsRawBytes, separator);
                var wordsEntries = ScanWordNames(wordsSourceBytes);
                var (wordsReplacements, wordsWarnings) = BuildWordReplacements(wordsEntries, priceRows, separator, keepExistingPrice);
                result.Warnings.AddRange(wordsWarnings);

                if (wordsReplacements.Count > 0)
                {
                    progress?.Invoke($"正在为 {wordsReplacements.Count} 个暗金装备注入地面参考物价...");
                    byte[] wordsPatchedBytes = ApplyWordReplacementsAppend(wordsSourceBytes, wordsReplacements);

                    if (!_engine.WriteGameFiles(info, [(wordsTargetPath, wordsPatchedBytes)]))
                    {
                        result.ErrorMessage = "写入 Words 暗金装备物价补丁失败：" + (PatchEngine.LastWriteException?.Message ?? "未知原因");
                        progress?.Invoke(result.ErrorMessage);
                        return result;
                    }

                    byte[]? verifyWords = _engine.ReadGameFile(info, wordsTargetPath, out _);
                    if (verifyWords == null || !VerifyPatchedWordNames(verifyWords, wordsReplacements))
                    {
                        string restoreMessage = RestorePreviousContent(info, wordsTargetPath, wordsRawBytes);
                        result.ErrorMessage = "Words 暗金物价写入后校验失败，已尝试还原备份。" + restoreMessage;
                        progress?.Invoke(result.ErrorMessage);
                        return result;
                    }

                    wordsPatchedCount = wordsReplacements.Count;
                }
            }

            int totalPatched = basePatchedCount + wordsPatchedCount;
            result.Success = true;
            result.PatchedNames = totalPatched;
            result.FullTextNames = replacements.Count(r => !r.Compacted);
            result.CompactedNames = replacements.Count(r => r.Compacted);
            result.SourceSize = rawBytes.Length + (wordsRawBytes?.Length ?? 0);
            result.PatchedSize = result.SourceSize;
            progress?.Invoke($"客户端物价注入完成！（基础物品 {basePatchedCount}，暗金装备 {wordsPatchedCount}，共 {totalPatched} 个）");
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            progress?.Invoke($"应用物价补丁异常：{ex.Message}");
        }

        return result;
    }

    private static bool VerifyPatchedNames(byte[] bytes, List<NameReplacement> replacements)
    {
        var rows = ScanBaseItemNames(bytes).ToDictionary(item => item.RowIndex);
        foreach (var replacement in replacements)
        {
            if (!rows.TryGetValue(replacement.RowIndex, out var row)
                || !string.Equals(row.Name, replacement.FittedName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 写入后校验失败时，回滚到【本次写入前的实际内容】。
    ///
    /// 不能回滚到历史基线：基线可能是汉化或游戏更新之前的状态，写回去会连带抹掉
    /// 这些改动。回滚的正确目标永远是「刚才那次写入之前客户端长什么样」。
    /// </summary>
    private string RestorePreviousContent(GameModeInfo info, string targetPath, byte[] previousBytes)
    {
        try
        {
            return _engine.WriteGameFiles(info, [(targetPath, previousBytes)])
                ? "已回滚到写入前状态。"
                : "回滚失败，请使用客户端校验或备份快照恢复。";
        }
        catch (Exception exception)
        {
            return "回滚异常：" + exception.Message;
        }
    }

    /// <summary>
    /// 清除所有已注入的物价后缀（双表协同）。
    ///
    /// 只从【当前客户端内容】剥离价格后缀，不写回任何历史基线——清洗的语义是
    /// 「去掉当前文件里的价格标记」，客户端其余内容（名称、数值、其他补丁的改动）
    /// 必须原样保留。写回历史备份会把汉化、游戏更新等改动一并抹掉。
    /// </summary>
    public PricePatchResult CleanPatch(GameModeInfo info, string separator = " ", Action<string>? progress = null)
    {
        var result = new PricePatchResult();

        try
        {
            int totalCleaned = 0;

            // 1. 清洗 BaseItemTypes
            progress?.Invoke("正在定位 BaseItemTypes 数据表以准备清洗物价...");
            byte[]? rawBytes = ReadBaseItemsFile(info, out string targetPath);
            if (rawBytes != null && rawBytes.Length > 0 && ContainsPriceSuffix(rawBytes))
            {
                progress?.Invoke("正在剥离 BaseItemTypes 旧物价后缀...");
                byte[] cleaned = StripAllPriceSuffixes(rawBytes, separator);
                if (_engine.WriteGameFiles(info, [(targetPath, cleaned)]))
                {
                    totalCleaned++;
                }
            }

            // 2. 清洗 Words (暗金装备)
            byte[]? wordsRawBytes = ReadWordsFile(info, out string wordsTargetPath);
            if (wordsRawBytes != null && wordsRawBytes.Length > 0 && ContainsWordsPriceSuffix(wordsRawBytes))
            {
                progress?.Invoke("正在剥离 Words 表暗金装备物价后缀...");
                byte[] cleanedWords = StripAllWordsPriceSuffixes(wordsRawBytes, separator);
                if (_engine.WriteGameFiles(info, [(wordsTargetPath, cleanedWords)]))
                {
                    totalCleaned++;
                }
            }

            result.Success = true;
            result.PatchedNames = totalCleaned;
            progress?.Invoke(totalCleaned > 0
                ? "客户端物价后缀清洗成功，已彻底还原纯净原版名称！"
                : "当前客户端未检测到物价后缀，无需清洗。");
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            progress?.Invoke($"清洗物价异常：{ex.Message}");
        }

        return result;
    }

    /// <summary>判断 dat 中是否已有价格后缀。</summary>
    public static bool ContainsPriceSuffix(byte[] data)
    {
        try
        {
            return ScanBaseItemNames(data)
                .Any(item => StripExistingPriceSuffix(item.Name, "") != item.Name);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>计算不包含名称后缀的结构指纹，用于判断游戏更新后旧备份是否仍然适用。</summary>
    public static string ComputeMetadataSignature(byte[] data)
    {
        var builder = new StringBuilder();
        foreach (var item in ScanBaseItemNames(data))
        {
            builder.Append(item.RowIndex)
                .Append(':')
                .Append(item.MetadataPath)
                .Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }



    /// <summary>剥离 dat 中所有已存在的价格后缀，得到干净基线。</summary>
    public static byte[] StripAllPriceSuffixes(byte[] data, string separator = "")
    {
        var entries = ScanBaseItemNames(data);
        var (replacements, _) = BuildReplacements(
            entries,
            [],
            separator,
            keepExistingPrice: false,
            "append",
            patchSameNameDuplicates: true);
        return replacements.Count == 0 ? data : ApplyReplacementsAppend(data, replacements);
    }

    /// <summary>判断 Words.datc64 中是否已有价格后缀。</summary>
    public static bool ContainsWordsPriceSuffix(byte[] data)
    {
        try
        {
            return ScanWordNames(data)
                .Any(item => StripExistingPriceSuffix(item.DisplayName, "") != item.DisplayName);
        }
        catch
        {
            return true;
        }
    }


    /// <summary>剥离 Words.datc64 中所有已存在的价格后缀，得到干净基线。</summary>
    public static byte[] StripAllWordsPriceSuffixes(byte[] data, string separator = "")
    {
        var entries = ScanWordNames(data);
        var (replacements, _) = BuildWordReplacements(
            entries,
            [],
            separator,
            keepExistingPrice: false);
        return replacements.Count == 0 ? data : ApplyWordReplacementsAppend(data, replacements);
    }

    public static DatLayout DetectWordsLayout(byte[] data)
    {
        if (data.Length < 4 + WordsRowSize)
        {
            throw new InvalidDataException("Words.datc64 文件太小。");
        }

        int rowCount = BitConverter.ToInt32(data, 0);
        if (rowCount <= 0)
        {
            throw new InvalidDataException($"Words.datc64 行数无效：{rowCount}。");
        }

        int stringBase = 4 + rowCount * WordsRowSize;
        if (stringBase >= data.Length)
        {
            throw new InvalidDataException("Words.datc64 字符串基址超出文件范围。");
        }

        return new DatLayout(rowCount, WordsRowSize, stringBase);
    }

    public static List<WordItemName> ScanWordNames(byte[] data)
    {
        var layout = DetectWordsLayout(data);
        var list = new List<WordItemName>(layout.RowCount);

        for (int i = 0; i < layout.RowCount; i++)
        {
            int rowStart = 4 + i * layout.RowSize;
            if (rowStart + layout.RowSize > data.Length) break;

            // 指针槽是 8 字节；只读低 4 字节在偏移超过 4GB 时会解出错误位置
            ulong enOffset = BitConverter.ToUInt64(data, rowStart + WordsEnNameOffset);
            int displayPtrPos = rowStart + WordsDisplayNameOffset;
            ulong displayOffset = BitConverter.ToUInt64(data, displayPtrPos);

            if (enOffset == 0 || enOffset == DatBinaryUtils.SentinelValue
                || displayOffset == 0 || displayOffset == DatBinaryUtils.SentinelValue)
            {
                continue;
            }

            long enStartL = layout.StringBase + (long)enOffset;
            long displayStartL = layout.StringBase + (long)displayOffset;

            if (enStartL >= 0 && enStartL < data.Length - 1 && displayStartL >= 0 && displayStartL < data.Length - 1)
            {
                int enStart = (int)enStartL;
                int displayStart = (int)displayStartL;
                string enName = ReadUtf16LeZ(data, enStart);
                string displayName = ReadUtf16LeZ(data, displayStart);
                int displayEnd = displayStart + displayName.Length * 2;

                if (!string.IsNullOrEmpty(enName) && !string.IsNullOrEmpty(displayName))
                {
                    list.Add(new WordItemName
                    {
                        RowIndex = i,
                        EnName = enName,
                        DisplayName = displayName,
                        DisplayOffset = (int)displayOffset,
                        DisplayPointerPos = displayPtrPos,
                        DisplayStart = displayStart,
                        DisplayEnd = displayEnd
                    });
                }
            }
        }

        return list;
    }

    public static string NormalizeAlphanumeric(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString();
    }

    public static (List<NameReplacement> Replacements, List<string> Warnings) BuildWordReplacements(
        List<WordItemName> words,
        List<PriceRow> priceRows,
        string separator,
        bool keepExistingPrice)
    {
        var enMap = new Dictionary<string, WordItemName>(StringComparer.OrdinalIgnoreCase);
        var displayMap = new Dictionary<string, WordItemName>(StringComparer.Ordinal);
        var displaySimpMap = new Dictionary<string, WordItemName>(StringComparer.Ordinal);

        foreach (var w in words)
        {
            string normEn = NormalizeAlphanumeric(w.EnName);
            if (!string.IsNullOrEmpty(normEn) && !enMap.ContainsKey(normEn))
            {
                enMap[normEn] = w;
            }

            string cleanDisplay = StripExistingPriceSuffix(w.DisplayName, separator);
            if (!string.IsNullOrEmpty(cleanDisplay) && !displayMap.ContainsKey(cleanDisplay))
            {
                displayMap[cleanDisplay] = w;
            }

            string simpDisplay = NormalizeSimplified(cleanDisplay);
            if (!string.IsNullOrEmpty(simpDisplay) && !displaySimpMap.ContainsKey(simpDisplay))
            {
                displaySimpMap[simpDisplay] = w;
            }
        }

        var replacements = new List<NameReplacement>();
        var warnings = new List<string>();
        var handledRowIndices = new HashSet<int>();

        foreach (var pr in priceRows)
        {
            if (string.IsNullOrEmpty(pr.Price) && string.IsNullOrEmpty(pr.NewName)) continue;
            if (string.IsNullOrWhiteSpace(pr.Name)) continue;

            WordItemName? target = null;

            // 1. 英文名称归一化对齐（例如 "Mageblood", "Headhunter"）
            string normQuery = NormalizeAlphanumeric(pr.Name);
            if (!string.IsNullOrEmpty(normQuery) && enMap.TryGetValue(normQuery, out var byEn))
            {
                target = byEn;
            }

            // 2. 显示名中文直接对齐（如 "法师之血"）
            if (target == null && displayMap.TryGetValue(pr.Name, out var byDisplay))
            {
                target = byDisplay;
            }

            // 3. 繁简归一化对齐（如 "法師之血" 与 "法师之血"）
            if (target == null)
            {
                string simpQuery = NormalizeSimplified(pr.Name);
                if (displaySimpMap.TryGetValue(simpQuery, out var bySimp))
                {
                    target = bySimp;
                }
            }

            if (target == null) continue;
            if (!handledRowIndices.Add(target.RowIndex)) continue;

            string clean = StripExistingPriceSuffix(target.DisplayName, separator);
            string priceText = pr.Price.Trim();
            if (!priceText.StartsWith('[') && !string.IsNullOrEmpty(priceText))
            {
                priceText = $"[{priceText}]";
            }

            string requested = !string.IsNullOrEmpty(pr.NewName)
                ? pr.NewName
                : $"{clean}{separator}{priceText}";

            replacements.Add(new NameReplacement
            {
                RowIndex = target.RowIndex,
                NameStart = target.DisplayStart,
                NameEnd = target.DisplayEnd,
                NameOffset = target.DisplayOffset,
                NamePointerPos = target.DisplayPointerPos,
                OldName = target.DisplayName,
                RequestedName = requested,
                FittedName = requested,
                MetadataPath = "Unique/" + target.EnName,
                Category = string.IsNullOrWhiteSpace(pr.Category) ? "Unique" : pr.Category,
                Price = pr.Price
            });
        }

        // 若需要清除未匹配条目的现有价格后缀
        if (!keepExistingPrice)
        {
            foreach (var w in words)
            {
                if (handledRowIndices.Contains(w.RowIndex)) continue;
                string clean = StripExistingPriceSuffix(w.DisplayName, separator);
                if (clean != w.DisplayName)
                {
                    handledRowIndices.Add(w.RowIndex);
                    replacements.Add(new NameReplacement
                    {
                        RowIndex = w.RowIndex,
                        NameStart = w.DisplayStart,
                        NameEnd = w.DisplayEnd,
                        NameOffset = w.DisplayOffset,
                        NamePointerPos = w.DisplayPointerPos,
                        OldName = w.DisplayName,
                        RequestedName = clean,
                        FittedName = clean,
                        MetadataPath = "Unique/" + w.EnName,
                        Price = string.Empty
                    });
                }
            }
        }

        return (replacements, warnings);
    }

    public static byte[] ApplyWordReplacementsAppend(byte[] data, List<NameReplacement> replacements)
    {
        var layout = DetectWordsLayout(data);

        // 与 ApplyReplacementsAppend 同理，委托给统一的追加重写实现。
        // 名称未变动的条目保持原指针不动。
        var toWrite = replacements
            .Where(r => !string.Equals(r.FittedName, r.OldName, StringComparison.Ordinal))
            .Select(r => (PointerPos: r.NamePointerPos, NewText: r.FittedName));

        return DatBinaryUtils.RewriteStringPointersAppend(data, layout.StringBase, toWrite);
    }

    private static bool VerifyPatchedWordNames(byte[] bytes, List<NameReplacement> replacements)
    {
        var rows = ScanWordNames(bytes).ToDictionary(item => item.RowIndex);
        foreach (var replacement in replacements)
        {
            if (!rows.TryGetValue(replacement.RowIndex, out var row)
                || !string.Equals(row.DisplayName, replacement.FittedName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
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
        int sentinel = DatBinaryUtils.FindSentinel(data, rowCount);
        if (sentinel > 0)
        {
            int rowSize = (sentinel - 4) / rowCount;
            if (rowSize > 32)
            {
                return new DatLayout(rowCount, rowSize, sentinel);
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

            // 指针槽是 8 字节；只读低 4 字节在偏移超过 4GB 时会解出错误位置
            ulong metaOffset = BitConverter.ToUInt64(data, rowOffset);
            int namePtrPos = rowOffset + 32;
            if (namePtrPos + 8 > data.Length) break;

            ulong nameOffset = BitConverter.ToUInt64(data, namePtrPos);
            if (metaOffset == 0 || metaOffset == DatBinaryUtils.SentinelValue
                || nameOffset == 0 || nameOffset == DatBinaryUtils.SentinelValue)
            {
                continue;
            }

            long metaStart = layout.StringBase + (long)metaOffset;
            long nameStart = layout.StringBase + (long)nameOffset;

            if (metaStart >= 0 && metaStart < data.Length - 1 && nameStart >= 0 && nameStart < data.Length - 1)
            {
                string meta = ReadUtf16LeZ(data, (int)metaStart);
                string name = ReadUtf16LeZ(data, (int)nameStart);
                int nameEnd = (int)nameStart + name.Length * 2;

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
                        NameStart = (int)nameStart,
                        NameEnd = nameEnd
                    });
                }
            }
        }

        return list;
    }

    public static string NormalizeLookupKey(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString();
    }

    public static (List<NameReplacement> Replacements, List<string> Warnings) BuildReplacements(
        List<BaseItemName> entries,
        List<PriceRow> priceRows,
        string separator,
        bool keepExistingPrice,
        string mode,
        bool patchSameNameDuplicates,
        IReadOnlyDictionary<string, BaseItemName>? enNameToLocalMap = null)
    {
        var metaMap = new Dictionary<string, BaseItemName>(StringComparer.OrdinalIgnoreCase);
        var nameMap = new Dictionary<string, List<BaseItemName>>(StringComparer.Ordinal);

        static void AddToNameMap(Dictionary<string, List<BaseItemName>> map, string key, BaseItemName item)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            if (!map.TryGetValue(key, out var list))
            {
                list = [];
                map[key] = list;
            }
            if (!list.Contains(item))
            {
                list.Add(item);
            }
        }

        foreach (var entry in entries)
        {
            metaMap[entry.MetadataPath] = entry;
            string clean = StripExistingPriceSuffix(entry.Name, separator);
            AddToNameMap(nameMap, clean, entry);
            string simplified = NormalizeSimplified(clean);
            if (!string.Equals(clean, simplified, StringComparison.Ordinal))
            {
                AddToNameMap(nameMap, simplified, entry);
            }
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
            }

            // 1. 若通过客户端提取的双语映射表找到了该英文名对应的基底物品，直接采用
            if (targets.Count == 0 && !string.IsNullOrEmpty(pr.Name) && enNameToLocalMap != null)
            {
                if (enNameToLocalMap.TryGetValue(pr.Name, out var enMatch))
                {
                    targets.Add(enMatch);
                }
                else
                {
                    string normEn = NormalizeLookupKey(pr.Name);
                    foreach (var pair in enNameToLocalMap)
                    {
                        if (NormalizeLookupKey(pair.Key) == normEn)
                        {
                            targets.Add(pair.Value);
                            break;
                        }
                    }
                }
            }

            if (targets.Count == 0 && !string.IsNullOrEmpty(pr.Name))
            {
                // 2. 直接名称匹配
                if (nameMap.TryGetValue(pr.Name, out var byName))
                {
                    targets = byName;
                }
                // 3. 繁简归一化匹配 (例如繁中客户端 "神聖石" 与输入 "神圣石" 自动对齐)
                else if (nameMap.TryGetValue(NormalizeSimplified(pr.Name), out var bySimp))
                {
                    targets = bySimp;
                }
                // 4. 英文名对照转官方中文名匹配
                else if (EnglishToChineseMap.TryGetValue(pr.Name, out var mappedZh))
                {
                    if (nameMap.TryGetValue(mappedZh, out var byZh))
                    {
                        targets = byZh;
                    }
                    else if (nameMap.TryGetValue(NormalizeSimplified(mappedZh), out var byZhSimp))
                    {
                        targets = byZhSimp;
                    }
                }
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
                    Category = InferCategory(pr.Category, item.MetadataPath),
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

        // 委托给统一的追加重写实现（原为各模块各一份副本，现已收敛）。
        // 名称未变动的条目保持原指针：既不追加新字符串，也不重写指针槽，
        // 避免无谓的索引膨胀。
        var toWrite = replacements
            .Where(r => !string.Equals(r.FittedName, r.OldName, StringComparison.Ordinal))
            .Select(r => (PointerPos: r.NamePointerPos, NewText: r.FittedName));

        return DatBinaryUtils.RewriteStringPointersAppend(data, layout.StringBase, toWrite);
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

        // 该方法对每个物品名调用一次，物价补丁动辄数千条，
        // 每次都 Regex.Replace(字符串模式) 会重复解析同一个模式。
        // 按分隔符缓存已编译实例，同时带上超时兜底灾难性回溯。
        var regex = PriceSuffixRegexCache.GetOrAdd(
            separator ?? string.Empty,
            static sep => new Regex(
                string.IsNullOrEmpty(sep)
                    ? PriceSuffixPattern
                    : $@"(?:{Regex.Escape(sep)})?{PriceSuffixPattern}",
                RegexOptions.Compiled,
                TimeSpan.FromSeconds(2)));

        return regex.Replace(name, string.Empty).Trim();
    }

    /// <summary>按分隔符缓存「剥离已有物价后缀」的正则实例。</summary>
    private static readonly ConcurrentDictionary<string, Regex> PriceSuffixRegexCache = new(StringComparer.Ordinal);

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

    private static string ReadUtf16LeZ(byte[] data, int start, int maxChars = 500) =>
        DatBinaryUtils.ReadUtf16LeZ(data, start, maxChars);

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

    public static string InferCategory(string category, string metadataPath)
    {
        if (!string.IsNullOrWhiteSpace(category))
        {
            return category;
        }

        if (string.IsNullOrWhiteSpace(metadataPath))
        {
            return "Currency";
        }

        string lower = metadataPath.ToLowerInvariant();
        if (lower.Contains("lineage")) return "LineageSupportGems";
        if (lower.Contains("gem")) return "UncutGems";
        if (lower.Contains("rune")) return "Runes";
        if (lower.Contains("soulcore")) return "SoulCores";
        if (lower.Contains("expedition")) return "Expedition";
        if (lower.Contains("verisium")) return "Verisium";
        if (lower.Contains("fragment")) return "Fragments";
        if (lower.Contains("essence")) return "Essences";
        if (lower.Contains("idol")) return "Idols";
        if (lower.Contains("abyss")) return "Abyss";
        if (lower.Contains("delirium")) return "Delirium";
        if (lower.Contains("ritual") || lower.Contains("omen")) return "Ritual";
        if (lower.Contains("breach")) return "Breach";
        if (lower.Contains("unique")) return "Unique";
        return "Currency";
    }
}
