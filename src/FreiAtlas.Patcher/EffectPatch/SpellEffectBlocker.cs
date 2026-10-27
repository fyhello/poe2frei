using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using LibBundle3;
using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.EffectPatch;

/// <summary>
/// PoE2 人物技能与装备特效屏蔽执行器。
///
/// 具备 110 项技能与装备特效的精细化规则执行、首原件基底重算、
/// 取消勾选自动还原、幂等防重复写盘以及全量清理闭环。
/// </summary>
public sealed class SpellEffectBlocker
{
    /// <summary>逐文件备份分组标识。全量还原纯净客户端时需要据此清理。</summary>
    public const string BackupGroup = "spell_block_v2";

    private readonly PatchEngine _engine;

    public SpellEffectBlocker(PatchEngine? engine = null)
    {
        _engine = engine ?? new PatchEngine();
    }

    /// <summary>
    /// 批量应用技能与装备特效屏蔽。
    /// 始终以 PatchFileBackup 保存的首次干净原件为计算基底；
    /// 本次未命中的已备份文件自动写回原件并清理备份条目。
    /// </summary>
    public bool ApplyBatch(
        GameModeInfo info,
        IEnumerable<string> spellIds,
        bool includeAudio,
        Action<string>? progress,
        out string message)
    {
        message = string.Empty;

        if (!info.IsValid)
        {
            message = info.ErrorMessage ?? "游戏客户端无效。";
            return false;
        }

        var spellList = spellIds
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (spellList.Count == 0)
        {
            message = "未指定任何需要屏蔽的技能。";
            return false;
        }

        var (isLocked, lockReason) = PatchBackupManager.CheckResourceLock(info);
        if (isLocked)
        {
            message = lockReason ?? "客户端资源被占用，无法写入。";
            return false;
        }

        try
        {
            progress?.Invoke("正在规划技能与装备特效屏蔽规则...");
            var plan = Poe2SpellBlockDatabase.Plan(spellList, includeAudio);

            progress?.Invoke("正在读取客户端索引...");
            var allClientPaths = EnumerateClientPaths(info);
            if (allClientPaths.Count == 0)
            {
                message = "未能从客户端读取到任何文件路径。";
                return false;
            }

            var allClientPathsSet = new HashSet<string>(allClientPaths, StringComparer.OrdinalIgnoreCase);

            // 1. 汇集所有需要处理的虚拟路径及其对应的 PlannedFileAction
            var targetActions = new Dictionary<string, PlannedFileAction>(StringComparer.OrdinalIgnoreCase);

            // A. 精确匹配
            foreach (var (path, action) in plan.PlannedFiles)
            {
                if (allClientPathsSet.Contains(path))
                {
                    targetActions[path] = action;
                }
            }

            // B. 前 50 项关键字对未入库文件的兜底匹配
            if (plan.FallbackKeywords.Count > 0)
            {
                var fallbackPattern = new Regex(
                    @"^metadata/effects/.*(?:" + string.Join("|", plan.FallbackKeywords) + @").*\.(ao|aoc|epk)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(2));

                var fallbackBlocks = includeAudio
                    ? SpellBlockPlan.DefaultFallbackBlocks.Split('|')
                    : SpellBlockPlan.DefaultFallbackBlocksNoAudio.Split('|');

                var defaultAoAction = new PlannedFileAction
                {
                    Kind = PlannedActionKind.ClearBlockAndHideSegments,
                    Blocks = fallbackBlocks
                };
                var defaultEpkAction = new PlannedFileAction
                {
                    Kind = PlannedActionKind.Empty
                };

                foreach (var path in allClientPaths)
                {
                    if (targetActions.ContainsKey(path)) continue;
                    if (plan.ExactAoAocEpkPaths.Contains(path)) continue;

                    if (fallbackPattern.IsMatch(path))
                    {
                        var ext = SpellEffectContentCleaner.GetExtension(path);
                        if (ext is ".ao" or ".aoc")
                        {
                            targetActions[path] = defaultAoAction;
                        }
                        else if (ext is ".epk")
                        {
                            targetActions[path] = defaultEpkAction;
                        }
                    }
                }
            }

            // 2. 加载已有备份（干净官方原件字典）
            // 读取异常不阻断应用（未备份的目标会从客户端现读），但必须让用户知道。
            var existingBackups = LoadAllExistingBackups(out string? backupReadError);
            if (backupReadError is not null)
            {
                progress?.Invoke($"读取已有技能备份时出错，将重新从客户端读取原件：{backupReadError}");
            }

            // 找出哪些目标文件尚未备份，需从客户端读取原件写入备份
            var unbackedTargets = targetActions.Keys
                .Where(p => !existingBackups.ContainsKey(p))
                .ToList();

            if (unbackedTargets.Count > 0)
            {
                progress?.Invoke($"读取并备份 {unbackedTargets.Count} 个新目标原件...");
                var newOriginals = _engine.ReadGameFiles(info, unbackedTargets);

                // 立即快照异常：LastReadException 是静态字段，后续任何读取都可能覆盖它。
                var readEx = PatchEngine.LastReadException;
                if (readEx is not null)
                {
                    // 读不到原件时绝不能继续 SaveOriginals —— 那会把空数据当成原始备份存下去，
                    // 后续还原就会把空内容写回客户端，原件永久丢失。
                    message = $"读取待备份原件失败，已中止以免污染备份：{readEx.Message}";
                    return false;
                }

                PatchFileBackup.SaveOriginals(BackupGroup, unbackedTargets, newOriginals);
                foreach (var (vp, data) in newOriginals)
                {
                    existingBackups[vp] = data;
                }
            }

            // 3. 计算每个目标文件的新字节内容
            var filesToWrite = new List<(string VirtualPath, byte[] Data)>();
            var failures = new List<string>();

            // 先从客户端读取当前已存在文件的最新状态，用于幂等比较（避免重复写盘）
            var currentClientFiles = _engine.ReadGameFiles(info, targetActions.Keys);

            // 此处读取失败不影响正确性：读不到当前状态时下面会退化为「不跳过、直接写入」，
            // 但必须让用户知道本次写入不是基于最新的客户端状态。
            var currentReadEx = PatchEngine.LastReadException;
            if (currentReadEx is not null)
            {
                progress?.Invoke($"警告：读取客户端当前状态失败，本次将不做幂等跳过：{currentReadEx.Message}");
            }

            foreach (var (path, action) in targetActions)
            {
                if (!existingBackups.TryGetValue(path, out var baseBytes) || baseBytes.Length == 0)
                {
                    continue;
                }

                var encoding = EffectTextCodec.Detect(baseBytes, path);
                var baseText = EffectTextCodec.Decode(baseBytes, encoding);
                var cleanedText = SpellEffectContentCleaner.CleanWithPlan(path, baseText, action);

                if (cleanedText is null) continue;

                var (isValid, error) = Validate(path, cleanedText);
                if (!isValid)
                {
                    failures.Add($"{Path.GetFileName(path)}: {error}");
                    continue;
                }

                var newBytes = EffectTextCodec.Encode(cleanedText, encoding);

                // 幂等判断：如果客户端当前字节已经等于新字节，跳过写入
                if (currentClientFiles.TryGetValue(path, out var curBytes) && curBytes.SequenceEqual(newBytes))
                {
                    continue;
                }

                filesToWrite.Add((path, newBytes));
            }

            if (failures.Count > 0)
            {
                message = $"有 {failures.Count} 个文件未通过语法校验，已中止写入：{string.Join("；", failures.Take(3))}";
                return false;
            }

            // 4. 找出需要自动还原的文件（存在于已有备份中，但本次不再被任何已选技能命中）
            var plannedPathSet = new HashSet<string>(targetActions.Keys, StringComparer.OrdinalIgnoreCase);
            var restoredFiles = existingBackups
                .Where(kv => !plannedPathSet.Contains(kv.Key))
                .Select(kv => (VirtualPath: kv.Key, Data: kv.Value))
                .ToList();

            // 若无需改动且无待还原项，幂等成功返回
            if (filesToWrite.Count == 0 && restoredFiles.Count == 0)
            {
                message = $"已保持 {spellList.Count} 项技能与装备特效屏蔽生效（文件无需改动）。";
                return true;
            }

            var allWriteFiles = filesToWrite.Concat(restoredFiles).ToList();

            progress?.Invoke($"正在写入客户端（改动 {filesToWrite.Count} 个，还原 {restoredFiles.Count} 个）...");
            if (!_engine.WriteGameFiles(info, allWriteFiles))
            {
                message = PatchEngine.LastWriteException?.Message ?? "写入客户端失败。";
                return false;
            }

            // 从备份清单中移除已成功写回原件的条目
            if (restoredFiles.Count > 0)
            {
                PatchFileBackup.RemoveOriginals(BackupGroup, restoredFiles.Select(x => x.VirtualPath));
            }

            message = restoredFiles.Count > 0
                ? $"已成功应用 {spellList.Count} 项技能特效屏蔽，更新 {filesToWrite.Count} 个文件，恢复 {restoredFiles.Count} 个文件。"
                : $"已成功应用 {spellList.Count} 项技能特效屏蔽（共更新 {filesToWrite.Count} 个文件）。";
            return true;
        }
        catch (Exception ex)
        {
            message = $"技能特效屏蔽失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 批量还原技能与装备特效。
    /// 将 spell_block_v2（及旧版单技能分组）的所有原始文件写回，并彻底清空备份目录。
    /// </summary>
    public bool RestoreBatch(
        GameModeInfo info,
        IEnumerable<string> spellIds,
        Action<string>? progress,
        out string message)
    {
        message = string.Empty;

        var spellList = spellIds
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (spellList.Count == 0)
        {
            message = "未指定任何需要还原的技能。";
            return false;
        }

        if (!info.IsValid)
        {
            message = info.ErrorMessage ?? "游戏客户端无效。";
            return false;
        }

        var (isLocked, lockReason) = PatchBackupManager.CheckResourceLock(info);
        if (isLocked)
        {
            message = lockReason ?? "客户端资源被占用，无法写入。";
            return false;
        }

        try
        {
            var allOriginals = LoadAllExistingBackups(out string? originalsReadError);
            if (allOriginals.Count == 0)
            {
                // 区分「确实没有备份」与「备份读不了」：后者用户需要去查磁盘权限，
                // 而不是以为自己的备份凭空消失了。
                message = originalsReadError is null
                    ? "没有可用的技能原始备份。"
                    : $"读取技能原始备份失败：{originalsReadError}";
                return false;
            }

            var files = allOriginals.Select(kv => (kv.Key, kv.Value)).ToList();
            progress?.Invoke($"正在还原 {files.Count} 个文件至官方原版...");
            if (!_engine.WriteGameFiles(info, files))
            {
                message = PatchEngine.LastWriteException?.Message ?? "还原写入失败。";
                return false;
            }

            // 清空备份记录
            PatchFileBackup.Clear(BackupGroup);
            PatchFileBackup.Clear("lightning_spark");

            message = $"已成功还原全部技能与装备特效（共 {files.Count} 个文件）。";
            return true;
        }
        catch (Exception ex)
        {
            message = $"还原失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 加载当前所有已存在的技能备份原件（合并 spell_block_v2 与旧版单技能分组）。
    /// </summary>
    /// <param name="readError">
    /// 读取失败的说明。为 null 表示读取过程没有异常（备份可能确实不存在）。
    /// 调用方必须区分「没有备份」与「备份目录读不了」—— 前者是正常情况，
    /// 后者会让用户误以为备份丢了。
    /// </param>
    private static Dictionary<string, byte[]> LoadAllExistingBackups(out string? readError)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>(2);

        // 1. 旧版单技能备份（如 lightning_spark）
        try
        {
            foreach (var (vp, data) in PatchFileBackup.LoadOriginals("lightning_spark"))
            {
                result[vp] = data;
            }
        }
        catch (Exception ex)
        {
            errors.Add($"旧版单技能备份组（lightning_spark）：{ex.Message}");
        }

        // 2. spell_block_v2 统一备份组
        try
        {
            foreach (var (vp, data) in PatchFileBackup.LoadOriginals(BackupGroup))
            {
                result[vp] = data;
            }
        }
        catch (Exception ex)
        {
            errors.Add($"技能屏蔽备份组（{BackupGroup}）：{ex.Message}");
        }

        readError = errors.Count > 0 ? string.Join("；", errors) : null;
        return result;
    }

    private static (bool IsValid, string? Error) Validate(string virtualPath, string cleaned)
    {
        var ext = SpellEffectContentCleaner.GetExtension(virtualPath);
        switch (ext)
        {
            case ".ao":
            case ".aoc":
            case ".ot":
                var r = EffectSyntaxValidator.Validate(cleaned, virtualPath);
                return (r.IsValid, r.Error);

            case ".epk":
                return cleaned == " " ? (true, null) : (false, "清理结果不是预期的占位值");

            case ".pet":
            case ".trl":
                return cleaned == "0" ? (true, null) : (false, "清理结果不是预期的占位值");

            default:
                return (true, null);
        }
    }

    private static List<string> EnumerateClientPaths(GameModeInfo info)
    {
        var paths = new List<string>();

        if (info.Mode == GameMode.GGPK)
        {
            var ggpkPath = PatchEngine.GetGgpkPath(info);
            if (!File.Exists(ggpkPath)) return paths;

            using var ggpk = new LibBundledGGPK3.BundledGGPK(ggpkPath, parsePathsInIndex: false);
            ggpk.Index.ParsePaths();
            foreach (var record in ggpk.Index.Files.Values)
            {
                var p = record.Path;
                if (!string.IsNullOrEmpty(p)) paths.Add(p.Replace('\\', '/'));
            }
            return paths;
        }

        var bundlesDir = PatchEngine.GetBundlesDirectory(info);
        var indexPath = PatchEngine.GetBundlesIndexPath(info);
        if (!File.Exists(indexPath)) return paths;

        var factory = new DriveBundleFactory(bundlesDir);
        try
        {
            using var index = new LibBundle3.Index(indexPath, parsePaths: false, factory);
            index.ParsePaths();
            foreach (var record in index.Files.Values)
            {
                var p = record.Path;
                if (!string.IsNullOrEmpty(p)) paths.Add(p.Replace('\\', '/'));
            }
        }
        finally
        {
            (factory as IDisposable)?.Dispose();
        }
        return paths;
    }
}
