using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.Translation;

/// <summary>
/// 官方简体中文翻译补丁结果报告
/// </summary>
public sealed class TranslationPatchResult
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public int CsdCount { get; set; }
    public int BaseItemsCount { get; set; }
    public int PatchedDatTablesCount { get; set; }
    public int PatchedDatCellsCount { get; set; }
    public List<string> Warnings { get; set; } = [];
}

/// <summary>
/// 纯官方简体中文翻译补丁编排服务：
/// 1. 严格解耦：完全独立于字体替换、物价注记与双语别名，支持按需独立安装与卸载；
/// 2. 分轨注入：
///    - CSD 属性描述：流式状态机智能改写原生简中标签 + 267 条国服官方规则精准补漏 + 废弃哨兵繁体兜底；
///    - DAT 核心数据表：以国际服表格为底模，全量追加重写 156 个数据表（天赋、NPC对话/台词、任务追踪、界面与固定属性标签、暗金风味故事、通货说明等）以及 BaseItemTypes 基础道具名称，原表行数与模型/音频指针 100% 不变；
/// 3. 全程受 PatchBackupManager 快照机制保护，支持无损秒级回滚。
/// </summary>
public sealed class TranslationPatchService
{
    private readonly PatchEngine _engine;
    private static Dictionary<string, Dictionary<string, string>>? _cachedMissingRules;

    public TranslationPatchService(PatchEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    /// <summary>
    /// 获取内置嵌入的 CSD 缺失规则库
    /// </summary>
    public static Dictionary<string, Dictionary<string, string>> GetEmbeddedMissingRules()
    {
        if (_cachedMissingRules != null) return _cachedMissingRules;

        var assembly = typeof(TranslationPatchService).Assembly;
        using var stream = assembly.GetManifestResourceStream("FreiAtlas.Patcher.Resources.csd_missing_patches.json");
        if (stream != null)
        {
            _cachedMissingRules = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream) ?? new();
            return _cachedMissingRules;
        }

        string fallbackPath = Path.Combine(AppContext.BaseDirectory, "Resources", "csd_missing_patches.json");
        if (File.Exists(fallbackPath))
        {
            using var fs = File.OpenRead(fallbackPath);
            _cachedMissingRules = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(fs) ?? new();
            return _cachedMissingRules;
        }

        return new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 执行官方简体中文翻译补丁全量注入
    /// </summary>
    public TranslationPatchResult ApplyTranslationPatch(GameModeInfo info, Action<string>? progress = null)
    {
        var result = new TranslationPatchResult();

        if (!info.IsValid)
        {
            result.ErrorMessage = "未找到有效的 PoE2 游戏目录或索引。";
            return result;
        }

        // 国服客户端原生即为简体中文，无需也不需要汉化。放行只会用内置词典覆盖
        // 官方译文，反而降低质量，因此在入口直接拒绝。
        if (info.IsChina)
        {
            result.ErrorMessage = "当前为国服客户端，原生已是简体中文，无需应用汉化补丁。";
            progress?.Invoke(result.ErrorMessage);
            return result;
        }

        try
        {
            progress?.Invoke("正在加载内置官方简中规则库与全量数据表词典...");
            var missingRules = GetEmbeddedMissingRules();
            var catalog = DatTextPatcher.GetEmbeddedCatalog();

            var filesToWrite = new List<(string VirtualPath, byte[] Data)>();

            // 1. 读取并改写国际服客户端原生的所有 CSD 文件（词缀与属性描述）
            progress?.Invoke("正在扫描客户端 CSD 词缀与属性描述文件...");
            var csdFiles = _engine.ReadGameFilesMatching(info, p => p.EndsWith(".csd", StringComparison.OrdinalIgnoreCase));

            if (csdFiles.Count == 0)
            {
                result.Warnings.Add("未在客户端中检索到任何 .csd 描述文件，跳过 CSD 改写。");
            }
            else
            {
                progress?.Invoke($"正在智能改写 {csdFiles.Count} 个 CSD 文件（原生改签 + 规则精准补漏）...");
                foreach (var (path, rawBytes) in csdFiles)
                {
                    string normPath = path.ToLowerInvariant();
                    missingRules.TryGetValue(normPath, out var fileMissingDict);

                    byte[] transformedBytes = CsdSmartPatchTransformer.Transform(rawBytes, path, fileMissingDict);
                    filesToWrite.Add((path, transformedBytes));
                }
                result.CsdCount = csdFiles.Count;
            }

            // 2 & 3. 批量读取 BaseItemTypes 与全部核心数据表（一次打开 GGPK，杜绝重复解析）
            progress?.Invoke($"正在批量读取客户端 {catalog.Tables.Count} 个核心数据表与底子文件...");
            // 兜底取繁体目录（国际服默认），随后按探测到的底子文件路径覆盖。
            string langDir = Poe2LanguageDetector.TraditionalChineseDirectory;
            if (!string.IsNullOrEmpty(info.BaseItemsPath))
            {
                var dir = VirtualPath.Normalize(Path.GetDirectoryName(info.BaseItemsPath));
                if (!string.IsNullOrEmpty(dir))
                {
                    langDir = dir;
                }
            }

            string baseItemPath = $"{langDir}/baseitemtypes.datc64";
            var targetPaths = new List<string>(catalog.Tables.Count + 1) { baseItemPath };
            foreach (var fn in catalog.Tables.Keys)
            {
                targetPaths.Add($"{langDir}/{fn}");
            }

            var loadedTables = _engine.ReadGameFiles(info, targetPaths);

            // 汉化的核心是整表重写，读不到表就无从改写。
            // 若把读取异常当成「客户端没有这些表」静默跳过，用户会看到「汉化完成」但界面仍是英文。
            var tablesReadEx = _engine.LastReadException;
            if (tablesReadEx is not null)
            {
                result.ErrorMessage = $"读取客户端核心数据表失败，已中止汉化注入：{tablesReadEx.Message}";
                return result;
            }

            // 注入 BaseItemTypes
            if (loadedTables.TryGetValue(baseItemPath, out var baseItemBytes) && baseItemBytes.Length > 0)
            {
                progress?.Invoke("正在注入 BaseItemTypes 基础道具名称文本列...");
                var (patchedBaseItems, patchedCount) = DatTextPatcher.PatchBaseItemTypes(baseItemBytes, catalog.BaseItems);
                if (patchedCount > 0)
                {
                    filesToWrite.Add((baseItemPath, patchedBaseItems));
                    result.BaseItemsCount = patchedCount;
                }
            }
            else
            {
                result.Warnings.Add($"未找到 {baseItemPath}，跳过基础道具名称汉化。");
            }

            // 批量注入核心数据表
            progress?.Invoke($"正在注入 {catalog.Tables.Count} 个官方简中核心数据表（天赋、NPC台词、任务、界面标签等）...");
            int patchedTableCount = 0;
            int totalPatchedCells = 0;

            foreach (var (fileName, tableDef) in catalog.Tables)
            {
                string tablePath = $"{langDir}/{fileName}";
                if (loadedTables.TryGetValue(tablePath, out var datBytes) && datBytes.Length > 0)
                {
                    var (patchedDat, cellCount) = DatTextPatcher.PatchTable(datBytes, tableDef);
                    if (cellCount > 0)
                    {
                        filesToWrite.Add((tablePath, patchedDat));
                        patchedTableCount++;
                        totalPatchedCells += cellCount;
                    }
                }
            }

            result.PatchedDatTablesCount = patchedTableCount;
            result.PatchedDatCellsCount = totalPatchedCells;

            // 4. 将补丁文件批量安全写入客户端
            if (filesToWrite.Count == 0)
            {
                result.ErrorMessage = "未找到任何可汉化的资源或改动为空。";
                return result;
            }

            progress?.Invoke($"正在将 {filesToWrite.Count} 个补丁文件安全打包写入客户端（受快照保护）...");
            bool writeSuccess = _engine.WriteGameFiles(info, filesToWrite);
            if (!writeSuccess)
            {
                result.ErrorMessage = _engine.LastWriteException?.Message ?? "写入游戏客户端补丁失败，请确认客户端未在运行且未被锁定。";
                return result;
            }

            result.Success = true;
            progress?.Invoke($"官方简中补丁全量注入成功！改写 {result.CsdCount} 个 CSD 词缀描述文件，注入 {result.BaseItemsCount} 个基础底子名称，全量汉化 {result.PatchedDatTablesCount} 个核心数据表（共 {result.PatchedDatCellsCount} 个文本单元格）。");
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = $"执行汉化补丁时发生异常：{ex.Message}";
            return result;
        }
    }
}
