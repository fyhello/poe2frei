using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using FreiAtlas.Patcher.EffectPatch;
using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.MtxPatch;

/// <summary>
/// PoE 2 全量商城外观特效 (MTX) 应用器。
///
/// 具备 5 大核心替换与注入能力：
///   1. 成对文件直接替换（技能特效、传送门外观、杂项变身）
///   2. 沙虫宠物替换与体型等比缩放 (150%)
///   3. 205 款全身防具时装对全职业 7 种属性底子的长后缀优先覆盖
///   4. 翅膀/背饰/周身光效挂载注入到 PoE 2 专属八大职业模型
///   5. 翻滚动作常驻特效注入到 character.ot 并在取消时无损移除
///   6. 鼠标指针 21 张高清位图替换
///
/// 严格依赖 PatchFileBackup 机制保证首次干净备份与 100% 无损还原闭环。
/// </summary>
public sealed class MtxPatchApplier
{
    public const string BackupGroup = "mtx_patch_v1";

    private readonly PatchEngine _engine;

    public MtxPatchApplier(PatchEngine? engine = null)
    {
        _engine = engine ?? new PatchEngine();
    }

    /// <summary>
    /// 检查当前是否已应用 MTX 补丁。
    /// </summary>
    public bool HasAppliedBackup()
    {
        return PatchFileBackup.ListOriginals(BackupGroup).Count > 0;
    }

    /// <summary>
    /// 获取已备份/已修改的文件数量。
    /// </summary>
    public int GetModifiedFileCount()
    {
        return PatchFileBackup.ListOriginals(BackupGroup).Count;
    }

    /// <summary>
    /// 应用选中的 MTX 特效方案。
    /// </summary>
    public MtxApplyResult Apply(
        GameModeInfo info,
        MtxPatchOptions options,
        Action<string>? progress = null)
    {
        // 商城是整批里最慢的模块：按「规划 / 备份读取 / 写盘」分段计时。
        var mtxSw = System.Diagnostics.Stopwatch.StartNew();

        if (!info.IsValid)
        {
            return MtxApplyResult.Failure(info.ErrorMessage ?? "游戏客户端路径无效。");
        }

        var (isLocked, lockReason) = PatchBackupManager.CheckResourceLock(info);
        if (isLocked)
        {
            return MtxApplyResult.Failure(lockReason ?? "游戏正在运行或客户端资源被占用，请先完全退出游戏。");
        }

        try
        {
            // 1. 预先加载已有备份，作为干净基底
            var existingBackups = PatchFileBackup.LoadOriginals(BackupGroup)
                .ToDictionary(x => x.VirtualPath, x => x.Data, StringComparer.OrdinalIgnoreCase);

            var db = MtxDatabase.Instance;
            var plannedFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var freshOriginals = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            List<string> newBackupTargets = [];

            // ----------------------------------------------------
            // 在独立会话作用域内完成数据计算、提取与原件读取
            // ----------------------------------------------------
            progress?.Invoke("正在连接游戏客户端索引...");
            using (var session = ClientIndexSession.Open(info))
            {
                if (session is null)
                {
                    return MtxApplyResult.Failure("无法打开游戏客户端索引，请检查游戏路径与文件完整性。");
                }

                // 辅助函数：优先读取原件备份，否则从客户端读取
                byte[]? ReadBaseBytes(string virtualPath)
                {
                    // 共享文件（character.ot）：特效模块会往同一文件注入视距行。
                    // 若拿本模块私有备份当基底，视距注入在基底里不存在，写回后就被抹掉。
                    // 因此这里必须读客户端当前内容，只剥掉本模块自己的翻滚注入，
                    // 保留对方的视距注入。
                    if (SharedFileRestore.IsSharedFile(virtualPath) && session.FileExists(virtualPath))
                    {
                        var current = session.ReadFile(virtualPath);
                        if (current is { Length: > 0 })
                        {
                            try
                            {
                                var enc = EffectTextCodec.Detect(current, virtualPath);
                                var text = EffectTextCodec.Decode(current, enc);
                                var stripped = SharedFileRestore.RemoveMatchingLines(
                                    text, SharedFileRestore.DodgeRollLine);
                                if (!string.Equals(stripped, text, StringComparison.Ordinal))
                                {
                                    return EffectTextCodec.Encode(stripped, enc);
                                }
                            }
                            catch
                            {
                                // 剥离失败时退回当前内容：防护重复的逻辑在 InjectDodgeRollPack 里。
                            }
                        }
                        return current;
                    }

                    if (existingBackups.TryGetValue(virtualPath, out var backed))
                    {
                        return backed;
                    }
                    if (session.FileExists(virtualPath))
                    {
                        return session.ReadFile(virtualPath);
                    }
                    return null;
                }

                // 模式 1 & 4 & 5: 处理普通选中的条目 (技能、杂项、翻滚、翅膀光效)
                var selectedEntries = options.SelectedNames
                    .Select(name => db.FindByName(name))
                    .Where(e => e is not null)
                    .Cast<MtxEntry>()
                    .ToList();

                // 拆分出翻滚特效、角色附着挂载特效和成对替换特效
                // 注意：只有角色特效大类且带有 IsConstructionEffectPack 的条目才是翻滚常驻动作注入；
                // 技能大类的条目（如含有受击 epk 的电弧与电球）必须全部作为技能成对文件直接替换。
                var rollEntries = selectedEntries
                    .Where(e => e.Category == MtxCategory.CharacterEffects && e.IsConstructionEffectPack)
                    .ToList();
                var charFxEntries = selectedEntries
                    .Where(e => e.Category == MtxCategory.CharacterEffects && !e.IsConstructionEffectPack && !e.IsArmourSetGlobal)
                    .ToList();
                var pairEntries = selectedEntries
                    .Where(e => e.Category != MtxCategory.CharacterEffects && !e.IsArmourSetGlobal)
                    .ToList();

                // 1.1 处理成对替换 (Skills / Misc)
                progress?.Invoke("正在处理技能与杂项外观替换...");
                foreach (var entry in pairEntries)
                {
                    if (entry.InternalPaths is null || entry.InternalPaths.Length < 2) continue;
                    // 检查是否为指针包
                    if (entry.Name.EndsWith("Cursor", StringComparison.OrdinalIgnoreCase))
                    {
                        ApplyCursorPack(entry.Name, plannedFiles);
                        continue;
                    }

                    // 处理普通成对路径: 跳过第一个可能存在的头标记 (如 SKILL_FLEXIBLE)
                    int startIdx = (entry.InternalPaths[0].StartsWith("SKILL_", StringComparison.OrdinalIgnoreCase) ||
                                    entry.InternalPaths[0].StartsWith("PORTAL_", StringComparison.OrdinalIgnoreCase) ||
                                    entry.InternalPaths[0].StartsWith("PET_", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;

                    for (int i = startIdx; i + 1 < entry.InternalPaths.Length; i += 2)
                    {
                        var srcPath = entry.InternalPaths[i].Trim();
                        var dstPath = entry.InternalPaths[i + 1].Trim();
                        if (string.IsNullOrWhiteSpace(srcPath) || string.IsNullOrWhiteSpace(dstPath)) continue;

                        // 源文件字节
                        var srcBytes = session.FileExists(srcPath) ? session.ReadFile(srcPath) : null;
                        if (srcBytes is not null && session.FileExists(dstPath))
                        {
                            plannedFiles[dstPath] = srcBytes;
                        }
                    }
                }

                // 1.2 处理传送门外观 (Portals, 单选或来自 SelectedPortalName)
                if (!string.IsNullOrWhiteSpace(options.SelectedPortalName))
                {
                    var portalEntry = db.FindByName(options.SelectedPortalName);
                    if (portalEntry is not null && portalEntry.InternalPaths is not null)
                    {
                        progress?.Invoke($"正在应用传送门外观: {portalEntry.Name}...");
                        int startIdx = portalEntry.InternalPaths.Length > 0 && portalEntry.InternalPaths[0].StartsWith("PORTAL_", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                        for (int i = startIdx; i + 1 < portalEntry.InternalPaths.Length; i += 2)
                        {
                            var srcPath = portalEntry.InternalPaths[i].Trim();
                            var dstPath = portalEntry.InternalPaths[i + 1].Trim();
                            if (string.IsNullOrWhiteSpace(srcPath) || string.IsNullOrWhiteSpace(dstPath)) continue;

                            var srcBytes = session.FileExists(srcPath) ? session.ReadFile(srcPath) : null;
                            if (srcBytes is not null && session.FileExists(dstPath))
                            {
                                plannedFiles[dstPath] = srcBytes;
                            }
                        }
                    }
                }

                // 模式 2: 沙虫宠物外观替换与 150% 体型缩放 (Pets)
                if (!string.IsNullOrWhiteSpace(options.SelectedPetName))
                {
                    var petEntry = db.FindByName(options.SelectedPetName);
                    if (petEntry is not null && petEntry.InternalPaths is not null)
                    {
                        progress?.Invoke($"正在应用宠物外观: {petEntry.Name} (含 150% 体型缩放)...");
                        int startIdx = petEntry.InternalPaths.Length > 0 && petEntry.InternalPaths[0].StartsWith("PET_", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                        for (int i = startIdx; i + 1 < petEntry.InternalPaths.Length; i += 2)
                        {
                            var srcPath = petEntry.InternalPaths[i].Trim();
                            var dstPath = petEntry.InternalPaths[i + 1].Trim();
                            if (string.IsNullOrWhiteSpace(srcPath) || string.IsNullOrWhiteSpace(dstPath)) continue;

                            if (!session.FileExists(srcPath) || !session.FileExists(dstPath)) continue;

                            var srcBytes = session.ReadFile(srcPath);
                            if (srcBytes is null) continue;

                            // 特判：如果目标是沙虫 basic.ot，进行体型缩放（严格保持原编码，通常为 UTF-16LE 带 BOM）
                            if (dstPath.EndsWith("weta/basic.ot", StringComparison.OrdinalIgnoreCase))
                            {
                                var encoding = EffectTextCodec.Detect(srcBytes, dstPath);
                                var otText = EffectTextCodec.Decode(srcBytes, encoding);
                                var scaledOt = ApplyPetScale(otText, 150);
                                plannedFiles[dstPath] = EffectTextCodec.Encode(scaledOt, encoding);
                            }
                            else
                            {
                                plannedFiles[dstPath] = srcBytes;
                            }
                        }
                    }
                }

                // 模式 3: 205 款防具时装覆盖全职业 7 种属性底子 (ArmourSets)
                if (!string.IsNullOrWhiteSpace(options.SelectedArmourSetName))
                {
                    var armourEntry = db.FindByName(options.SelectedArmourSetName, options.SelectedArmourSetFolder);
                    if (armourEntry is not null && !string.IsNullOrWhiteSpace(armourEntry.ArmourSetFolder))
                    {
                        progress?.Invoke($"正在计算全局防具套装覆盖: {armourEntry.Name}...");
                        ApplyArmourSet(session, armourEntry, plannedFiles);
                    }
                }

                // 模式 4: 翅膀/背饰/周身光效挂载到职业模型
                if (charFxEntries.Count > 0)
                {
                    progress?.Invoke("正在注入角色背饰与周身特效到职业模型...");
                    ApplyCharacterAttachments(session, charFxEntries, options.SelectedClassCode, ReadBaseBytes, plannedFiles);
                }

                // 模式 5: 翻滚动作常驻特效注入到 character.ot
                if (rollEntries.Count > 0)
                {
                    progress?.Invoke("正在注入翻滚常驻动作特效...");
                    ApplyDodgeRollEffects(rollEntries, ReadBaseBytes, plannedFiles);
                }

                progress?.Invoke($"[耗时] 商城规划: {mtxSw.ElapsedMilliseconds} ms");
                mtxSw.Restart();

                // 保存本次新涉及文件的首次原件备份（在 session 关闭前完成读取）
                newBackupTargets = plannedFiles.Keys
                    .Where(p => !existingBackups.ContainsKey(p))
                    .ToList();

                if (newBackupTargets.Count > 0)
                {
                    progress?.Invoke($"正在为 {newBackupTargets.Count} 个游戏文件建立首次原件备份...");

                    // 这是商城模块的主要耗时点：上万次「按路径读文件 + 解压」。
                    // GGPK 模式下 ReadFile 不共享可变状态，可安全并行；上限 4 线程，
                    // 避免同时解压过多 chunk 造成内存峰值。任一线程异常则回退串行。
                    var parallelOk = true;
                    try
                    {
                        var bag = new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>(
                            StringComparer.OrdinalIgnoreCase);
                        System.Threading.Tasks.Parallel.ForEach(
                            newBackupTargets,
                            new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 4 },
                            path =>
                            {
                                if (!session.FileExists(path)) return;
                                var data = session.ReadFile(path);
                                if (data is not null) bag[path] = data;
                            });

                        foreach (var kv in bag) freshOriginals[kv.Key] = kv.Value;
                    }
                    catch (Exception parallelEx)
                    {
                        // 并行读取失败不影响正确性：退回逐文件串行。
                        parallelOk = false;
                        freshOriginals.Clear();
                        progress?.Invoke("并行读取失败，回退串行：" + parallelEx.Message);
                    }

                    if (!parallelOk)
                    {
                        foreach (var path in newBackupTargets)
                        {
                            if (session.FileExists(path))
                            {
                                var data = session.ReadFile(path);
                                if (data is not null)
                                {
                                    freshOriginals[path] = data;
                                }
                            }
                        }
                    }
                }
            } // <--- 关键：此处 session 彻底释放并 Dispose，完全解除对 Content.ggpk 的文件占用！

            progress?.Invoke($"[耗时] 商城备份读取: {mtxSw.ElapsedMilliseconds} ms");
            mtxSw.Restart();

            // ----------------------------------------------------
            // 备份、差量还原与最终写入闭环（独立无句柄占用阶段）
            // ----------------------------------------------------
            if (newBackupTargets.Count > 0 && freshOriginals.Count > 0)
            {
                PatchFileBackup.SaveOriginals(BackupGroup, newBackupTargets, freshOriginals);
            }

            var plannedPaths = new HashSet<string>(plannedFiles.Keys, StringComparer.OrdinalIgnoreCase);

            // 找出此前备份过、但本次已不再修改的文件（需恢复为原件）
            var restoredFiles = existingBackups
                .Where(kv => !plannedPaths.Contains(kv.Key))
                .Select(kv => (VirtualPath: kv.Key, Data: kv.Value))
                .ToList();

            if (plannedFiles.Count == 0 && restoredFiles.Count == 0)
            {
                return MtxApplyResult.Ok(0, "没有需要改动的文件（未选择任何外观，或当前已处于该状态）。");
            }

            // 规范化路径并合并写入任务：规划修改的文件 + 恢复原件的文件
            var buildSw = System.Diagnostics.Stopwatch.StartNew();
            var restorePayload = BuildRestorePayload(info, restoredFiles, progress);
            long restoreMs = buildSw.ElapsedMilliseconds;
            var filesToWrite = plannedFiles
                .Select(kv => (VirtualPath: kv.Key.Replace('\\', '/').Trim().TrimStart('/'), Data: kv.Value))
                .Concat(restorePayload)
                .ToList();
            progress?.Invoke($"[耗时] 商城构造写入列表: {buildSw.ElapsedMilliseconds} ms (计划 {plannedFiles.Count} 个, 恢复 {restoredFiles.Count} 个, 其中恢复读取 {restoreMs} ms)");
            mtxSw.Restart();

            progress?.Invoke($"正在向游戏客户端写入 {filesToWrite.Count} 个文件 (含 {restoredFiles.Count} 个恢复原件)...");
            var writeSuccess = _engine.WriteGameFiles(info, filesToWrite);
            progress?.Invoke($"[耗时] 商城会话暂存: {mtxSw.ElapsedMilliseconds} ms");
            if (!writeSuccess)
            {
                var reason = PatchEngine.LastWriteException?.Message ?? "客户端索引替换未生效或校验不一致";
                return MtxApplyResult.Failure($"写入游戏客户端文件失败：{reason}");
            }

            // 从备份清单中移除已恢复原件的文件
            if (restoredFiles.Count > 0)
            {
                PatchFileBackup.RemoveOriginals(BackupGroup, restoredFiles.Select(x => x.VirtualPath));
            }

            int activeCount = plannedFiles.Count;
            return MtxApplyResult.Ok(activeCount, $"成功应用外观特效替换！共修改 {activeCount} 个文件，恢复 {restoredFiles.Count} 个文件。");
        }
        catch (Exception ex)
        {
            return MtxApplyResult.Failure($"应用外观特效异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 还原所有通过本模块修改的文件，恢复客户端至 100% 原始状态。
    /// </summary>
    public MtxApplyResult Restore(GameModeInfo info, Action<string>? progress = null)
    {
        if (!info.IsValid)
        {
            return MtxApplyResult.Failure(info.ErrorMessage ?? "游戏客户端路径无效。");
        }

        var (isLocked, lockReason) = PatchBackupManager.CheckResourceLock(info);
        if (isLocked)
        {
            return MtxApplyResult.Failure(lockReason ?? "游戏正在运行或客户端资源被占用，请先完全退出游戏。");
        }

        try
        {
            var originals = PatchFileBackup.LoadOriginals(BackupGroup);
            if (originals.Count == 0)
            {
                return MtxApplyResult.Ok(0, "当前没有已备份的商城外观修改，无需还原。");
            }

            progress?.Invoke($"正在还原 {originals.Count} 个文件至原始状态...");
            var filesToWrite = BuildRestorePayload(
                info,
                originals.Select(x => (VirtualPath: x.VirtualPath, Data: x.Data)).ToList(),
                progress).ToList();
            var writeSuccess = _engine.WriteGameFiles(info, filesToWrite);
            if (!writeSuccess)
            {
                var reason = PatchEngine.LastWriteException?.Message ?? "客户端索引替换未生效或校验不一致";
                return MtxApplyResult.Failure($"写入还原数据失败：{reason}");
            }

            PatchFileBackup.Clear(BackupGroup);
            return MtxApplyResult.Ok(originals.Count, $"成功还原 {originals.Count} 个游戏外观文件至纯净状态！");
        }
        catch (Exception ex)
        {
            return MtxApplyResult.Failure($"还原外观特效异常: {ex.Message}");
        }
    }

    // =========================================================================
    // 内部实现细节与替换算法
    // =========================================================================

    /// <summary>
    /// 释放内嵌的 21 张鼠标指针位图。
    /// </summary>
    private static void ApplyCursorPack(string packName, Dictionary<string, byte[]> plannedFiles)
    {
        var cursorFiles = new[]
        {
            "contrast.bmp", "contrast_click.bmp", "contrast_hover.bmp",
            "contrastgreenarmour.bmp", "contrastgreenarmour_click.bmp", "contrastgreenarmour_hover.bmp",
            "contrastgreenarmourinv.bmp", "contrastgreenarmourinv_click.bmp", "contrastgreenarmourinv_hover.bmp",
            "contrastinvert.bmp", "contrastinvert_click.bmp", "contrastinvert_hover.bmp",
            "contrastpinkarmour.bmp", "contrastpinkarmour_click.bmp", "contrastpinkarmour_hover.bmp",
            "contrastpinkarmourinv.bmp", "contrastpinkarmourinv_click.bmp", "contrastpinkarmourinv_hover.bmp",
            "default.bmp", "default_click.bmp", "default_hover.bmp"
        };

        foreach (var file in cursorFiles)
        {
            var bytes = MtxDatabase.ReadCursorBitmap(packName, file);
            if (bytes is not null && bytes.Length > 0)
            {
                var targetPath = $"art/2dart/cursors/{file}";
                plannedFiles[targetPath] = bytes;
            }
        }
    }

    /// <summary>
    /// 沙虫宠物体型缩放（将 scale 设定为指定比例，默认 150）。
    /// </summary>
    public static string ApplyPetScale(string otText, int scale = 150)
    {
        // 如果已经有 scale = xxx，替换它
        if (Regex.IsMatch(otText, @"scale\s*=\s*\d+"))
        {
            return Regex.Replace(otText, @"scale\s*=\s*\d+", $"scale = {scale}");
        }

        // 如果有 Positioned 节点，在其内部注入 scale = 150
        var posMatch = Regex.Match(otText, @"Positioned\s*\{");
        if (posMatch.Success)
        {
            int insertIndex = posMatch.Index + posMatch.Length;
            return otText.Insert(insertIndex, $"\n\tscale = {scale}\n");
        }

        // 否则在末尾追加 Positioned 节点
        return otText + $"\n\nPositioned\n{{\n\tscale = {scale}\n}}\n";
    }

    /// <summary>
    /// 全局防具时装对全职业全底子的长后缀覆盖算法。
    /// </summary>
    private static void ApplyArmourSet(
        ClientIndexSession session,
        MtxEntry armourEntry,
        Dictionary<string, byte[]> plannedFiles)
    {
        var folder = armourEntry.ArmourSetFolder!.Trim().Replace('\\', '/').Trim('/');

        // 后续只用到 .ao 路径：先过滤再收集，避免把 426 万条全量物化到 HashSet
        //（那是数 GB 的字符串与哈希开销，实测是防具套装覆盖的主要耗时之一）。
        var allPaths = new List<string>();
        foreach (var (path, _) in session.Records)
        {
            if (path.EndsWith(".ao", StringComparison.OrdinalIgnoreCase)) allPaths.Add(path);
        }

        foreach (var (root, part) in MtxDatabase.ArmourRoots)
        {
            var srcDir = $"{root}{folder}/";
            // 寻找源目录下的所有 .ao
            var srcAoFiles = allPaths
                .Where(p => p.StartsWith(srcDir, StringComparison.OrdinalIgnoreCase) && p.EndsWith(".ao", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (srcAoFiles.Count == 0) continue;

            // 预读取并按后缀缓存源时装数据
            var srcDataBySuffix = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            byte[]? fallbackData = null;

            foreach (var suf in MtxDatabase.ArmourBaseSuffixes)
            {
                var matchSrc = srcAoFiles.FirstOrDefault(p =>
                {
                    var fn = Path.GetFileNameWithoutExtension(p);
                    return fn.EndsWith(suf, StringComparison.OrdinalIgnoreCase) || fn.EndsWith($"{suf}b", StringComparison.OrdinalIgnoreCase);
                });

                if (matchSrc is not null)
                {
                    var data = session.ReadFile(matchSrc);
                    if (data is not null)
                    {
                        srcDataBySuffix[suf] = data;
                        fallbackData ??= data;
                    }
                }
            }

            fallbackData ??= session.ReadFile(srcAoFiles[0]);
            if (fallbackData is null) continue;

            // 扫描客户端该部位的所有目标基础防具 .ao
            var targetAos = allPaths
                .Where(p => p.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                            && p.EndsWith(".ao", StringComparison.OrdinalIgnoreCase)
                            && !p.StartsWith(srcDir, StringComparison.OrdinalIgnoreCase)
                            && !p.Contains("controllerheld", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var targetPath in targetAos)
            {
                var fn = Path.GetFileNameWithoutExtension(targetPath).TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
                if (fn.EndsWith("b", StringComparison.OrdinalIgnoreCase))
                {
                    fn = fn[..^1];
                }
                if (fn.EndsWith("four", StringComparison.OrdinalIgnoreCase))
                {
                    fn = fn[..^4];
                }

                // 按长后缀优先匹配 7 种底子
                string? matchedSuffix = null;
                foreach (var suf in MtxDatabase.ArmourBaseSuffixes)
                {
                    if (fn.EndsWith(suf, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedSuffix = suf;
                        break;
                    }
                }

                if (matchedSuffix is null) continue;

                // 取得最佳对应的时装数据
                if (!srcDataBySuffix.TryGetValue(matchedSuffix, out var chosenData))
                {
                    chosenData = fallbackData;
                }

                if (chosenData is not null)
                {
                    plannedFiles[targetPath] = chosenData;
                }
            }
        }
    }

    /// <summary>
    /// 注入背饰、翅膀和周身光效至选定的职业模型 .ao 文件。
    /// </summary>
    private static void ApplyCharacterAttachments(
        ClientIndexSession session,
        List<MtxEntry> entries,
        string selectedClassCode,
        Func<string, byte[]?> readBaseBytes,
        Dictionary<string, byte[]> plannedFiles)
    {
        // 确定要修改的职业模型
        var targetModels = new List<string>();
        if (selectedClassCode.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            targetModels.AddRange(MtxDatabase.SupportedClasses
                .Where(c => !c.Code.Equals("all", StringComparison.OrdinalIgnoreCase))
                .Select(c => c.ModelPath));
        }
        else
        {
            var single = MtxDatabase.SupportedClasses.FirstOrDefault(c => c.Code.Equals(selectedClassCode, StringComparison.OrdinalIgnoreCase));
            if (single is not null && !single.Code.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                targetModels.Add(single.ModelPath);
            }
            else
            {
                targetModels.Add("metadata/characters/int/intfour.ao"); // 默认女巫
            }
        }

        foreach (var modelPath in targetModels)
        {
            if (!session.FileExists(modelPath)) continue;

            var baseBytes = readBaseBytes(modelPath);
            if (baseBytes is null) continue;

            var encoding = EffectTextCodec.Detect(baseBytes, modelPath);
            var aoText = EffectTextCodec.Decode(baseBytes, encoding);
            var modifiedAo = aoText;

            foreach (var entry in entries)
            {
                if (entry.InternalPaths is null || entry.InternalPaths.Length == 0) continue;

                var slot = !string.IsNullOrWhiteSpace(entry.SlotAttach) ? entry.SlotAttach : "<root>";
                int startIdx = (entry.InternalPaths[0].StartsWith("SKILL_", StringComparison.OrdinalIgnoreCase) ||
                                entry.InternalPaths[0].StartsWith("PORTAL_", StringComparison.OrdinalIgnoreCase) ||
                                entry.InternalPaths[0].StartsWith("PET_", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;

                for (int i = startIdx; i < entry.InternalPaths.Length; i++)
                {
                    var raw = entry.InternalPaths[i].Trim();
                    if (string.IsNullOrWhiteSpace(raw)) continue;

                    if (raw.EndsWith(".ao", StringComparison.OrdinalIgnoreCase))
                    {
                        modifiedAo = InjectAttachedAnimatedObject(modifiedAo, slot, raw);
                    }
                    else if (raw.Contains(".pet", StringComparison.OrdinalIgnoreCase))
                    {
                        // 可能是 "name, path.pet" 或直接 "path.pet"
                        string effectName = "body";
                        string petPath = raw;
                        var commaIdx = raw.IndexOf(',');
                        if (commaIdx >= 0)
                        {
                            effectName = raw[..commaIdx].Trim();
                            petPath = raw[(commaIdx + 1)..].Trim();
                        }
                        else
                        {
                            var fn = Path.GetFileNameWithoutExtension(raw);
                            if (!string.IsNullOrWhiteSpace(fn)) effectName = fn;
                        }

                        modifiedAo = InjectParticleEffect(modifiedAo, effectName, petPath);
                    }
                }
            }

            if (!string.Equals(modifiedAo, aoText, StringComparison.Ordinal))
            {
                plannedFiles[modelPath] = EffectTextCodec.Encode(modifiedAo, encoding);
            }
        }
    }

    /// <summary>
    /// 在 .ao 文件中注入 AttachedAnimatedObject 挂载。
    /// </summary>
    public static string InjectAttachedAnimatedObject(string aoText, string slot, string effectPath)
    {
        var line = $"\tattached_object = \"{slot} {effectPath}\"\n";
        if (aoText.Contains(line.Trim())) return aoText; // 避免重复注入

        var match = Regex.Match(aoText, @"AttachedAnimatedObject\s*\{");
        if (match.Success)
        {
            int insertIndex = match.Index + match.Length;
            return aoText.Insert(insertIndex, "\n" + line);
        }

        // 没有 AttachedAnimatedObject 块，追加到文件根层级
        return aoText + $"\n\nAttachedAnimatedObject\n{{\n{line}}}\n";
    }

    /// <summary>
    /// 在 .ao 文件中注入 ParticleEffects 连续粒子。
    /// </summary>
    public static string InjectParticleEffect(string aoText, string effectName, string petPath)
    {
        var line = $"\t\tcontinuous_effect = \"{effectName} {petPath}\"\n";
        if (aoText.Contains(line.Trim())) return aoText;

        // 寻找 ParticleEffects {
        var match = Regex.Match(aoText, @"ParticleEffects\s*\{");
        if (match.Success)
        {
            int insertIndex = match.Index + match.Length;
            return aoText.Insert(insertIndex, "\n" + line);
        }

        // 寻找 client {
        var clientMatch = Regex.Match(aoText, @"client\s*\{");
        if (clientMatch.Success)
        {
            int insertIndex = clientMatch.Index + clientMatch.Length;
            var block = $"\n\tParticleEffects\n\t{{\n{line}\t}}\n";
            return aoText.Insert(insertIndex, block);
        }

        // 根层级创建
        return aoText + $"\n\nclient\n{{\n\tParticleEffects\n\t{{\n{line}\t}}\n}}\n";
    }

    /// <summary>
    /// 注入翻滚动作常驻特效至 character.ot。
    /// </summary>
    /// <summary>
    /// 构造「恢复原件」的写入内容。
    ///
    /// 普通文件直接写回原始备份；共享文件（character.ot）走定点移除，
    /// 只删本模块注入的 AddEffectPack 行，从而不破坏视距调整补丁的注入。
    ///
    /// 与 EffectPatchApplier 侧的实现对称：两侧共用 SharedFileRestore，
    /// 任一侧还原都不会再抹掉另一侧的改动。
    /// </summary>
    private List<(string VirtualPath, byte[] Data)> BuildRestorePayload(
        GameModeInfo info,
        List<(string VirtualPath, byte[] Data)> restoredFiles,
        Action<string>? progress)
    {
        var files = new List<(string VirtualPath, byte[] Data)>(restoredFiles.Count);

        ClientIndexSession? session = null;
        try
        {
            session = ClientIndexSession.Open(info);

            foreach (var (rawPath, originalBytes) in restoredFiles)
            {
                string path = rawPath.Replace('\\', '/').Trim().TrimStart('/');

                if (!SharedFileRestore.IsSharedFile(path) || session is null)
                {
                    files.Add((path, originalBytes));
                    continue;
                }

                byte[]? currentBytes;
                try
                {
                    currentBytes = session.ReadFile(path);
                }
                catch
                {
                    files.Add((path, originalBytes));
                    continue;
                }

                if (currentBytes is null || currentBytes.Length == 0)
                {
                    files.Add((path, originalBytes));
                    continue;
                }

                if (!SharedFileRestore.TryRestoreSharedFile(
                        currentBytes,
                        path,
                        SharedFileRestore.DodgeRollLine,
                        out var cleanedBytes,
                        out var restoreError))
                {
                    progress?.Invoke($"{Path.GetFileName(path)} 定点还原失败，回退为整文件还原：{restoreError}");
                    files.Add((path, originalBytes));
                    continue;
                }

                if (cleanedBytes is null)
                {
                    // 当前内容已无本模块注入痕迹，无需写入
                    continue;
                }

                files.Add((path, cleanedBytes));
            }
        }
        finally
        {
            session?.Dispose();
        }

        return files;
    }

    private static void ApplyDodgeRollEffects(
        List<MtxEntry> rollEntries,
        Func<string, byte[]?> readBaseBytes,
        Dictionary<string, byte[]> plannedFiles)
    {
        const string charOtPath = "metadata/characters/character.ot";
        var baseBytes = readBaseBytes(charOtPath);
        if (baseBytes is null) return;

        var encoding = EffectTextCodec.Detect(baseBytes, charOtPath);
        var otText = EffectTextCodec.Decode(baseBytes, encoding);
        var modifiedOt = otText;

        foreach (var entry in rollEntries)
        {
            if (entry.InternalPaths is null || entry.InternalPaths.Length == 0) continue;

            // 提取 .epk 路径
            var epkPath = entry.InternalPaths
                .Select(p => p.Trim())
                .FirstOrDefault(p => p.EndsWith(".epk", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(epkPath))
            {
                modifiedOt = InjectDodgeRollPack(modifiedOt, epkPath);
            }
        }

        if (!string.Equals(modifiedOt, otText, StringComparison.Ordinal))
        {
            plannedFiles[charOtPath] = EffectTextCodec.Encode(modifiedOt, encoding);
        }
    }

    /// <summary>
    /// 在 character.ot 中注入 AddEffectPack 到 on_construction_complete。
    /// </summary>
    public static string InjectDodgeRollPack(string otText, string epkPath)
    {
        var line = $"AddEffectPack( '{epkPath}' );";
        if (otText.Contains(line)) return otText; // 避免重复

        // 寻找 on_construction_complete = { 或 on_construction_complete =\n{
        var occMatch = Regex.Match(otText, @"on_construction_complete\s*=\s*\{");
        if (occMatch.Success)
        {
            int insertIndex = occMatch.Index + occMatch.Length;
            return otText.Insert(insertIndex, $"\n\t\t\t{line}\n");
        }

        // 寻找 BaseEvents {
        var baseEventsMatch = Regex.Match(otText, @"BaseEvents\s*\{");
        if (baseEventsMatch.Success)
        {
            int insertIndex = baseEventsMatch.Index + baseEventsMatch.Length;
            var block = $"\n\t\ton_construction_complete =\n\t\t{{\n\t\t\t{line}\n\t\t}}\n";
            return otText.Insert(insertIndex, block);
        }

        // 寻找 client {
        var clientMatch = Regex.Match(otText, @"client\s*\{");
        if (clientMatch.Success)
        {
            int insertIndex = clientMatch.Index + clientMatch.Length;
            var block = $"\n\tBaseEvents\n\t{{\n\t\ton_construction_complete =\n\t\t{{\n\t\t\t{line}\n\t\t}}\n\t}}\n";
            return otText.Insert(insertIndex, block);
        }

        // 根层级创建
        return otText + $"\n\nclient\n{{\n\tBaseEvents\n\t{{\n\t\ton_construction_complete =\n\t\t{{\n\t\t\t{line}\n\t\t}}\n\t}}\n}}\n";
    }
}

/// <summary>
/// MTX 应用/还原结果对象。
/// </summary>
public sealed record MtxApplyResult(
    bool Success,
    int ModifiedCount,
    string Message)
{
    public static MtxApplyResult Ok(int count, string msg) => new(true, count, msg);
    public static MtxApplyResult Failure(string error) => new(false, 0, error);
}
