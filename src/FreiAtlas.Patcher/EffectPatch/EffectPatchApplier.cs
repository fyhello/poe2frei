using System.IO;
using System.Text.RegularExpressions;
using LibBundle3;
using FreiAtlas.Core.Settings;
using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.EffectPatch;

/// <summary>
/// 特效补丁的应用服务：预览与写入。
///
/// 职责边界：本类负责真正改文件，含备份、语法校验、写入后验证。
/// 规则的定义在 <see cref="EffectPatchRules"/>，单条规则的内容变换在 <see cref="EffectRuleApplier"/>。
///
/// 设计上照搬 <see cref="SpellEffectBlocker"/> 的安全约束：
///   1. 写入前逐文件备份原始字节
///   2. 写入前逐文件语法校验，任一失败则整体中止
///   3. 只改索引里已存在的文件（不新增）
///   4. 写入由 PatchEngine 统一做读回验证
///
/// 规则按 <see cref="EffectActionExtensions.Priority"/> 升序执行：
/// ReplaceCode(20) 必须先于 RemoveObject(30)，否则先删键会让替换目标消失。
/// </summary>
public sealed class EffectPatchApplier
{
    private const string BackupGroup = "effect_patch";

    private static readonly string[] TargetExtensions =
        [".ot", ".aoc", ".ao", ".env", ".hlsl", ".epk", ".pet", ".trl", ".fxgraph", ".ffx"];

    /// <summary>单个文件处理上限，与扫描服务保持一致。</summary>
    private const int MaxFileBytes = 8 * 1024 * 1024;

    private readonly PatchEngine _engine;

    public EffectPatchApplier(PatchEngine? engine = null)
    {
        _engine = engine ?? new PatchEngine();
    }

    /// <summary>
    /// 预览：报告每个文件会被改成什么，不写入。
    /// </summary>
    public EffectApplyPreview Preview(
        GameModeInfo info,
        IEnumerable<string> ruleIds,
        Action<string>? progress = null)
    {
        if (!info.IsValid)
        {
            return EffectApplyPreview.Failure(info.ErrorMessage ?? "游戏客户端无效。");
        }

        var rules = ResolveRules(ruleIds, out var resolveError);
        if (rules.Count == 0)
        {
            return EffectApplyPreview.Failure(resolveError ?? "没有可用的规则。");
        }

        try
        {
            var plan = BuildPlan(info, rules, progress, out var failures);
            if (failures.Count > 0)
            {
                return EffectApplyPreview.Failure(string.Join("；", failures.Take(3)));
            }

            var items = plan
                .Select(p => new EffectApplyFile(
                    p.VirtualPath,
                    p.OriginalBytes?.Length ?? 0,
                    p.NewBytes.Length,
                    p.ChangedRules))
                .ToList();

            return new EffectApplyPreview(items, null);
        }
        catch (Exception ex)
        {
            return EffectApplyPreview.Failure($"预览失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 执行：按规则改文件。含备份、校验、写入后验证。
    /// </summary>
    public bool Apply(
        GameModeInfo info,
        IEnumerable<string> ruleIds,
        Action<string>? progress,
        out string message)
    {
        message = string.Empty;

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

        var rules = ResolveRules(ruleIds, out var resolveError);
        if (rules.Count == 0)
        {
            message = resolveError ?? "没有可用的规则。";
            return false;
        }

        try
        {
            var plan = BuildPlan(info, rules, progress, out var failures);
            if (failures.Count > 0)
            {
                message = $"有 {failures.Count} 个文件未通过校验，已中止：{string.Join("；", failures.Take(3))}";
                return false;
            }

            var existingBackups = PatchFileBackup.LoadOriginals(BackupGroup);
            var plannedPaths = new HashSet<string>(
                plan.Select(p => p.VirtualPath),
                StringComparer.OrdinalIgnoreCase);
            var restoredFiles = existingBackups
                .Where(orig => !plannedPaths.Contains(orig.VirtualPath))
                .ToList();

            if (plan.Count == 0 && restoredFiles.Count == 0)
            {
                message = "没有需要改动的文件（规则未命中，或补丁已应用）。";
                return false;
            }

            // 只有读到过原文件的那部分能逐文件备份。
            // 泥人贴图不读原文件，那部分还原依赖索引级快照（写入前已由
            // AutoSnapshotBeforePatch 生成），这里跳过即可。
            var backupable = plan
                .Where(p => p.OriginalBytes is not null)
                .ToDictionary(
                    p => p.VirtualPath,
                    p => p.OriginalBytes!,
                    StringComparer.OrdinalIgnoreCase);

            if (backupable.Count > 0)
            {
                progress?.Invoke($"备份 {backupable.Count} 个原始文件...");
                PatchFileBackup.SaveOriginals(
                    BackupGroup,
                    backupable.Keys,
                    backupable);
            }

            var files = plan
                .Select(p => (p.VirtualPath, p.NewBytes))
                .Concat(restoredFiles.Select(orig => (orig.VirtualPath, orig.Data)))
                .ToList();

            progress?.Invoke($"写入 {files.Count} 个文件...");
            if (!_engine.WriteGameFiles(info, files))
            {
                message = PatchEngine.LastWriteException?.Message ?? "写入失败。";
                return false;
            }

            if (restoredFiles.Count > 0)
            {
                PatchFileBackup.RemoveOriginals(
                    BackupGroup,
                    restoredFiles.Select(x => x.VirtualPath));
            }

            message = restoredFiles.Count > 0
                ? $"已应用 {rules.Count} 条规则，改动 {plan.Count} 个文件，还原 {restoredFiles.Count} 个文件。"
                : $"已应用 {rules.Count} 条规则，改动 {files.Count} 个文件。";
            return true;
        }
        catch (Exception ex)
        {
            message = $"应用失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 还原：把本模块改过的文件恢复成备份的原始内容。
    /// </summary>
    public bool Restore(GameModeInfo info, Action<string>? progress, out string message)
    {
        message = string.Empty;

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
            var originals = PatchFileBackup.LoadOriginals(BackupGroup);
            if (originals.Count == 0)
            {
                message = "没有可用的原始备份。";
                return false;
            }

            // character.ot 与 MTX 翻滚补丁共享：整文件覆盖会把 MTX 的注入一起抹掉。
            // 这类文件改为「只移除本补丁注入的行」，保留其他补丁的改动。
            var (files, surgicalCount) = BuildRestorePayload(info, originals, progress);

            if (files.Count == 0)
            {
                message = "没有需要还原的内容。";
                return false;
            }

            progress?.Invoke($"还原 {files.Count} 个文件...");
            if (!_engine.WriteGameFiles(info, files))
            {
                message = PatchEngine.LastWriteException?.Message ?? "还原写入失败。";
                return false;
            }

            PatchFileBackup.Clear(BackupGroup);

            message = surgicalCount > 0
                ? $"已还原 {files.Count} 个文件（其中 {surgicalCount} 个共享文件采用定点移除，保留其他补丁改动）。"
                : $"已还原 {files.Count} 个文件。";
            return true;
        }
        catch (Exception ex)
        {
            message = $"还原失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 构造还原写入内容。
    ///
    /// 普通文件直接写回原始备份；共享文件（如 character.ot）走定点移除，
    /// 只删本补丁注入的行，从而不破坏 MTX 等其他补丁的注入。
    /// </summary>
    private (List<(string VirtualPath, byte[] Data)> Files, int SurgicalCount)
        BuildRestorePayload(
            GameModeInfo info,
            List<(string VirtualPath, byte[] Data)> originals,
            Action<string>? progress)
    {
        var files = new List<(string VirtualPath, byte[] Data)>(originals.Count);
        int surgicalCount = 0;

        using var session = ClientIndexSession.Open(info);

        foreach (var (path, originalBytes) in originals)
        {
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
                    SharedFileRestore.ViewDistanceLine,
                    out var cleanedBytes,
                    out var restoreError))
            {
                progress?.Invoke($"{Path.GetFileName(path)} 定点还原失败，回退为整文件还原：{restoreError}");
                files.Add((path, originalBytes));
                continue;
            }

            if (cleanedBytes is null)
            {
                // 当前内容已无本补丁痕迹，无需写入
                continue;
            }

            files.Add((path, cleanedBytes));
            surgicalCount++;
        }

        return (files, surgicalCount);
    }


    // ── 内部 ──────────────────────────────────────────────

    private static List<EffectPatchRule> ResolveRules(IEnumerable<string> ruleIds, out string? error)
    {
        error = null;
        var wanted = new HashSet<string>(ruleIds, StringComparer.OrdinalIgnoreCase);

        // 检测是否有视距倍率调整规则（如 view_distance:2.5 或 view_distance）
        var customViewDistance = wanted.FirstOrDefault(id => id.StartsWith("view_distance", StringComparison.OrdinalIgnoreCase));
        EffectPatchRule? dynamicViewRule = null;
        if (customViewDistance is not null)
        {
            double ratio = 2.0;
            var parts = customViewDistance.Split(':');
            if (parts.Length > 1 && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedRatio))
            {
                ratio = parsedRatio;
            }
            dynamicViewRule = EffectPatchRules.CreateViewDistanceRule(ratio, customViewDistance);
            wanted.Remove(customViewDistance);
            wanted.Remove("view_distance");
        }

        // 检测是否有环境去黑暗与全局亮度调整规则（如 remove_dark:0.3、remove_dark:991 或 remove_dark）
        var customRemoveDark = wanted.FirstOrDefault(id =>
            string.Equals(id, "remove_dark", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("remove_dark:", StringComparison.OrdinalIgnoreCase));
        IReadOnlyList<EffectPatchRule>? dynamicDarkRules = null;
        if (customRemoveDark is not null)
        {
            double darkLevel = 1.0;
            var parts = customRemoveDark.Split(':');
            if (parts.Length > 1 && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedLevel))
            {
                darkLevel = parsedLevel;
            }
            dynamicDarkRules = EffectPatchRules.CreateRemoveDarkRules(darkLevel);
            wanted.RemoveWhere(id =>
                string.Equals(id, "remove_dark", StringComparison.OrdinalIgnoreCase)
                || id.StartsWith("remove_dark:", StringComparison.OrdinalIgnoreCase));
            foreach (var dr in dynamicDarkRules)
            {
                wanted.Remove(dr.Id);
            }
        }

        // 检测是否有小地图全开规则（如 minimap_visibility 或 minimap_color:...）
        var customMinimap = wanted.FirstOrDefault(id =>
            string.Equals(id, "minimap_visibility", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("minimap_color:", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("minimap_visibility:", StringComparison.OrdinalIgnoreCase));
        IReadOnlyList<EffectPatchRule>? dynamicMinimapRules = null;
        if (customMinimap is not null)
        {
            var colorSettings = ParseMinimapColorSettings(customMinimap);
            dynamicMinimapRules = EffectPatchRules.CreateMinimapRules(colorSettings);
            wanted.RemoveWhere(id =>
                string.Equals(id, "minimap_visibility", StringComparison.OrdinalIgnoreCase)
                || id.StartsWith("minimap_color:", StringComparison.OrdinalIgnoreCase)
                || id.StartsWith("minimap_visibility:", StringComparison.OrdinalIgnoreCase));
            foreach (var mr in dynamicMinimapRules)
            {
                wanted.Remove(mr.Id);
            }
        }

        // 兼容支持别名 remove_fog 与 remove_fog2 展开
        if (wanted.Remove("remove_fog"))
        {
            foreach (var r in EffectPatchRules.CreateRemoveFogRules()) wanted.Add(r.Id);
        }
        if (wanted.Remove("remove_fog2"))
        {
            foreach (var r in EffectPatchRules.CreateRemoveFog2Rules()) wanted.Add(r.Id);
        }

        var rules = EffectPatchRules.All.Where(r => wanted.Contains(r.Id)).ToList();
        if (dynamicViewRule is not null)
        {
            rules.Add(dynamicViewRule);
        }
        if (dynamicDarkRules is not null)
        {
            rules.AddRange(dynamicDarkRules);
        }
        if (dynamicMinimapRules is not null)
        {
            rules.AddRange(dynamicMinimapRules);
        }

        if (rules.Count == 0)
        {
            error = "没有匹配的规则 ID。";
            return rules;
        }

        var unsupported = rules.Where(r => r.IsUnsupported).Select(r => r.Id).ToList();
        if (unsupported.Count > 0)
        {
            error = $"以下规则在当前客户端不受支持：{string.Join(", ", unsupported)}";
            return [];
        }

        // 按优先级升序，同优先级保持定义顺序。
        return rules
            .OrderBy(r => r.Action.Priority())
            .ThenBy(r => EffectPatchRules.All.ToList().FindIndex(x => x.Id == r.Id))
            .ToList();
    }

    private static MinimapColorSettings ParseMinimapColorSettings(string input)
    {
        var idx = input.IndexOf(':');
        if (idx < 0 || idx == input.Length - 1)
        {
            return MinimapColorSettings.Default;
        }

        var payload = input[(idx + 1)..];
        var tokens = payload.Split([';', '_', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length >= 15)
        {
            try
            {
                var er = int.Parse(tokens[0], System.Globalization.CultureInfo.InvariantCulture);
                var eg = int.Parse(tokens[1], System.Globalization.CultureInfo.InvariantCulture);
                var eb = int.Parse(tokens[2], System.Globalization.CultureInfo.InvariantCulture);
                var ea = int.Parse(tokens[3], System.Globalization.CultureInfo.InvariantCulture);

                var br = int.Parse(tokens[4], System.Globalization.CultureInfo.InvariantCulture);
                var bg = int.Parse(tokens[5], System.Globalization.CultureInfo.InvariantCulture);
                var bb = int.Parse(tokens[6], System.Globalization.CultureInfo.InvariantCulture);
                var ba = int.Parse(tokens[7], System.Globalization.CultureInfo.InvariantCulture);

                var xr = int.Parse(tokens[8], System.Globalization.CultureInfo.InvariantCulture);
                var xg = int.Parse(tokens[9], System.Globalization.CultureInfo.InvariantCulture);
                var xb = int.Parse(tokens[10], System.Globalization.CultureInfo.InvariantCulture);
                var xa = int.Parse(tokens[11], System.Globalization.CultureInfo.InvariantCulture);

                var bglow = float.Parse(tokens[12], System.Globalization.CultureInfo.InvariantCulture);
                var xglow = float.Parse(tokens[13], System.Globalization.CultureInfo.InvariantCulture);
                var thresh = float.Parse(tokens[14], System.Globalization.CultureInfo.InvariantCulture);

                return new MinimapColorSettings(
                    er, eg, eb, ea,
                    br, bg, bb, ba,
                    xr, xg, xb, xa,
                    bglow, xglow, thresh);
            }
            catch
            {
                // 回退默认
            }
        }

        return MinimapColorSettings.Default;
    }

    private sealed class FilePlan
    {
        public required string VirtualPath { get; init; }

        /// <summary>
        /// 原始字节。文本文件在这里仍是原编码的字节。
        ///
        /// 只看路径的动作（泥人贴图）为 null —— 那类动作不读原文件，
        /// 也就拿不到原始内容，还原由索引级快照负责。
        /// </summary>
        public required byte[]? OriginalBytes { get; init; }

        /// <summary>修改后的字节。二进制动作直接给最终字节；文本动作已按原编码回写。</summary>
        public required byte[] NewBytes { get; init; }

        public required List<string> ChangedRules { get; init; }
    }

    /// <summary>
    /// 计算每个文件应用全部规则后的结果。
    /// </summary>
    private List<FilePlan> BuildPlan(
        GameModeInfo info,
        IReadOnlyList<EffectPatchRule> rules,
        Action<string>? progress,
        out List<string> failures)
    {
        failures = [];

        var compiled = rules
            .Select(rule => (Rule: rule, PathRegex: CompilePathRegex(rule)))
            .Where(x => x.PathRegex is not null)
            .ToList();

        progress?.Invoke("正在读取客户端索引...");

        // 索引只打开一次：路径枚举、内容读取、后续写入都复用它。
        // 之前按 300 个一批调用 ReadGameFiles，每批都会重新打开索引并 ParsePaths
        // （实测 426 万条路径解析一次约 19 秒），4 万个贴图会因此多花几十分钟。
        var session = ClientIndexSession.Open(info);
        if (session is null)
        {
            failures.Add("未能打开客户端索引。");
            return [];
        }

        try
        {
            return BuildPlanCore(session, compiled, progress, failures);
        }
        finally
        {
            session.Dispose();
        }
    }

    /// <summary>
    /// 在已打开的索引会话上完成规划。
    /// </summary>
    private static List<FilePlan> BuildPlanCore(
        ClientIndexSession session,
        List<(EffectPatchRule Rule, Regex? PathRegex)> compiled,
        Action<string>? progress,
        List<string> failures)
    {
        // 路径正则筛选：同一个文件可能被多条规则命中，按住路径聚合。
        var pathRules = new Dictionary<string, List<EffectPatchRule>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, record) in session.Records)
        {
            if (!TargetExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase))) continue;

            foreach (var (rule, pathRegex) in compiled)
            {
                if (!pathRegex!.IsMatch(path)) continue;
                if (!pathRules.TryGetValue(path, out var list))
                {
                    list = [];
                    pathRules[path] = list;
                }
                list.Add(rule);
            }
        }

        if (pathRules.Count == 0)
        {
            return [];
        }

        progress?.Invoke($"路径匹配到 {pathRules.Count} 个文件，正在处理...");

        var existingBackups = PatchFileBackup.LoadOriginals(BackupGroup)
            .ToDictionary(x => x.VirtualPath, x => x.Data, StringComparer.OrdinalIgnoreCase);

        var plan = new List<FilePlan>();
        var processed = 0;

        {
            foreach (var (path, fileRules) in pathRules)
            {
                processed++;
                if (processed % 500 == 0)
                {
                    progress?.Invoke($"处理文件 {processed}/{pathRules.Count}...");
                }

                // 只看路径的动作（泥人贴图）：替换字节完全由路径决定，读原文件没有意义。
                // 几万张图据此跳过读取，省下的是几十 GB 的磁盘与解压开销。
                var pathOnlyRules = fileRules.Where(r => EffectRuleApplier.IsPathOnlyAction(r.Action)).ToList();
                if (pathOnlyRules.Count > 0)
                {
                    var replaced = new List<string>();
                    byte[]? last = null;
                    foreach (var rule in pathOnlyRules)
                    {
                        var bytesForPath = EffectRuleApplier.ResolveByPath(path, rule);
                        if (bytesForPath is null) continue;
                        last = bytesForPath;
                        replaced.Add(rule.Id);
                    }

                    if (last is not null)
                    {
                        plan.Add(new FilePlan
                        {
                            VirtualPath = path,
                            OriginalBytes = null,
                            NewBytes = last,
                            ChangedRules = replaced
                        });
                    }
                    continue;
                }

                byte[]? bytes;
                if (existingBackups.TryGetValue(path, out var backedUpBytes) && backedUpBytes.Length > 0)
                {
                    bytes = backedUpBytes;
                }
                else
                {
                    try
                    {
                        bytes = session.ReadFile(path);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{Path.GetFileName(path)}: 读取失败 - {ex.Message}");
                        continue;
                    }
                }

                if (bytes is null || bytes.Length == 0 || bytes.Length > MaxFileBytes) continue;

                var encoding = EffectTextCodec.Detect(bytes, path);
                var original = EffectTextCodec.Decode(bytes, encoding);
                var current = original;
                var changedRules = new List<string>();

                foreach (var rule in fileRules)
                {
                    var outcome = EffectRuleApplier.Apply(current, rule);
                    if (outcome.Error is not null)
                    {
                        failures.Add($"{Path.GetFileName(path)} [{rule.Id}]: {outcome.Error}");
                        continue;
                    }
                    if (outcome.Changed)
                    {
                        current = outcome.Content;
                        changedRules.Add(rule.Id);
                    }
                }

                if (changedRules.Count == 0) continue;

                var validation = EffectSyntaxValidator.ValidateFor(current, path);
                if (!validation.IsValid)
                {
                    failures.Add($"{Path.GetFileName(path)}: {validation.Error}");
                    continue;
                }

                plan.Add(new FilePlan
                {
                    VirtualPath = path,
                    OriginalBytes = bytes,
                    NewBytes = EffectTextCodec.Encode(current, encoding),
                    ChangedRules = changedRules
                });
            }
        }

        progress?.Invoke("规划完成。");
        return plan;
    }

    private static Regex? CompilePathRegex(EffectPatchRule rule)
    {
        try
        {
            // 超时兜底灾难性回溯：规则里的 PathPattern 由内置规则表提供，
            // 但仍加超时，避免将来新增规则写出指数级回溯的模式后静默卡死。
            return new Regex(
                rule.PathPattern,
                RegexOptions.IgnoreCase | RegexOptions.Compiled,
                TimeSpan.FromSeconds(2));
        }
        catch
        {
            return null;
        }
    }

}

/// <summary>
/// 客户端索引会话：一次打开，供路径枚举与逐文件读取共用。
///
/// 存在的理由：索引解析（ParsePaths）在 426 万条路径的客户端上约需 19 秒。
/// 若按批次反复开关索引，几万个文件的补丁会因此多花几十分钟，
/// 而这些时间全部消耗在重复解析同一份索引上。
///
/// 生命周期：由调用方 using 释放；内部按需缓存 bundle 实例，
/// 同一 bundle 内的多个文件共享一次解压。
/// </summary>
internal sealed class ClientIndexSession : IDisposable
{
    private readonly LibBundledGGPK3.BundledGGPK? _ggpk;
    private readonly LibBundle3.Index? _index;
    private readonly LibBundle3.IBundleFactory? _factory;
    private readonly Dictionary<LibBundle3.Records.BundleRecord, LibBundle3.Bundle> _openBundles = [];

    private ClientIndexSession(
        LibBundledGGPK3.BundledGGPK? ggpk,
        LibBundle3.Index? index,
        LibBundle3.IBundleFactory? factory)
    {
        _ggpk = ggpk;
        _index = index;
        _factory = factory;
    }

    /// <summary>全部已解析路径与其记录的对应关系，路径统一为正斜杠。</summary>
    public IEnumerable<(string Path, LibBundle3.Records.FileRecord Record)> Records
    {
        get
        {
            var files = _ggpk?.Index.Files ?? _index?.Files;
            if (files is null) yield break;

            foreach (var record in files.Values)
            {
                var p = record.Path;
                if (string.IsNullOrEmpty(p)) continue;
                yield return (p.Replace('\\', '/'), record);
            }
        }
    }

    /// <summary>按路径读取文件内容；不存在时返回 null。</summary>
    public byte[]? ReadFile(string path)
    {
        var clean = path.Replace('\\', '/').TrimStart('/');
        var index = _ggpk?.Index ?? _index;
        if (index is null) return null;

        if (!index.TryGetFile(clean, out var record) || record is null) return null;

        // GGPK 模式：Index 内部自带 factory，直接无参读取即可。
        if (_factory is null) return record.Read().ToArray();

        // Bundles2 模式：同一 bundle 复用实例。
        // bundle 内部会缓存已解压的 chunk，反复 new 会把这些缓存全部丢掉。
        if (!_openBundles.TryGetValue(record.BundleRecord, out var bundle))
        {
            bundle = _factory.GetBundle(record.BundleRecord);
            _openBundles[record.BundleRecord] = bundle;
        }

        return record.Read(bundle).ToArray();
    }

    /// <summary>检查指定路径是否存在于索引中。</summary>
    public bool FileExists(string path)
    {
        var clean = path.Replace('\\', '/').TrimStart('/');
        var index = _ggpk?.Index ?? _index;
        return index is not null && index.TryGetFile(clean, out var record) && record is not null;
    }

    /// <summary>全部已解析的虚拟路径集合。</summary>
    public IEnumerable<string> AllFilePaths => Records.Select(r => r.Path);

    /// <summary>打开客户端索引；客户端无效或文件缺失时返回 null。</summary>
    public static ClientIndexSession? Open(GameModeInfo info)
    {
        if (info.Mode == GameMode.GGPK)
        {
            var ggpkPath = PatchEngine.GetGgpkPath(info);
            if (!File.Exists(ggpkPath)) return null;

            var ggpk = new LibBundledGGPK3.BundledGGPK(ggpkPath, parsePathsInIndex: false);
            ggpk.Index.ParsePaths();
            return new ClientIndexSession(ggpk, null, null);
        }

        var bundlesDir = PatchEngine.GetBundlesDirectory(info);
        var indexPath = PatchEngine.GetBundlesIndexPath(info);
        if (!File.Exists(indexPath)) return null;

        var factory = new DriveBundleFactory(bundlesDir);
        var index = new LibBundle3.Index(indexPath, parsePaths: false, factory);
        index.ParsePaths();
        return new ClientIndexSession(null, index, factory);
    }

    public void Dispose()
    {
        foreach (var bundle in _openBundles.Values)
        {
            bundle?.Dispose();
        }
        _openBundles.Clear();

        _index?.Dispose();
        ( _factory as IDisposable)?.Dispose();
        _ggpk?.Dispose();
    }
}

/// <summary>预览中的一个文件。</summary>
public sealed record EffectApplyFile(
    string VirtualPath,
    int OriginalLength,
    int NewLength,
    IReadOnlyList<string> ChangedRules);

/// <summary>应用预览结果。</summary>
public sealed record EffectApplyPreview(
    IReadOnlyList<EffectApplyFile> Files,
    string? Error)
{
    public static EffectApplyPreview Failure(string error) => new([], error);

    public bool CanApply => Error is null && Files.Count > 0;
}
