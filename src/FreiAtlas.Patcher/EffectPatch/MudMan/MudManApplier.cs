using System.IO;
using System.Text.RegularExpressions;
using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.EffectPatch.MudMan;

/// <summary>
/// 泥人补丁的应用服务。
///
/// 与 <see cref="EffectPatchApplier"/> 的关系：完全独立，不共用规则集、
/// 不共用备份分组、不共用还原入口。两者可以并存 —— 用户可以既用
/// 「画面与环境优化」的细粒度开关，又用泥人的一键档位。
///
/// 与易刷的关键差异：易刷在内存里一次性改完再写盘，这里复用
/// <see cref="PatchEngine.WriteGameFiles"/>（拼包 + 写回 + 读回校验），
/// 因此具备写入后验证；代价是比易刷多一次打包。
/// 真正决定速度的是不读原图，这一点已经做到。
/// </summary>
public sealed class MudManApplier
{
    /// <summary>备份分组。与 effect_patch 分开，互不影响。</summary>
    private const string BackupGroup = "mudman";

    /// <summary>文本规则的目标扩展名（贴图不走这里）。</summary>
    private static readonly string[] TextExtensions =
        [".ot", ".ao", ".aoc", ".epk", ".pet", ".trl", ".atlas", ".dlp", ".ffx"];

    /// <summary>单个文本文件处理上限。</summary>
    private const int MaxTextFileBytes = DatBinaryUtils.MaxPatchFileBytes;

    private readonly PatchEngine _engine;
    private readonly PatchBackupManager _backupManager;

    public MudManApplier(PatchEngine? engine = null, PatchBackupManager? backupManager = null)
    {
        _engine = engine ?? new PatchEngine();
        _backupManager = backupManager ?? new PatchBackupManager();
    }

    /// <summary>
    /// 状态记录文件名。泥人不做逐文件备份，靠这个文件记录「应用过什么」，
    /// 供界面显示与还原判定使用。
    /// </summary>
    private const string StateFileName = "state.json";

    /// <summary>已应用档位与改动文件数。</summary>
    public readonly record struct AppliedState(bool Applied, int Tier, int FileCount);

    /// <summary>读取当前应用状态。</summary>
    public static AppliedState ReadState()
    {
        var path = Path.Combine(PatchFileBackup.GetGroupDirectory(BackupGroup), StateFileName);
        if (!File.Exists(path)) return default;

        try
        {
            var json = File.ReadAllText(path);
            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            if (node is null) return default;

            return new AppliedState(
                node["applied"]?.GetValue<bool>() ?? false,
                node["tier"]?.GetValue<int>() ?? -1,
                node["fileCount"]?.GetValue<int>() ?? 0);
        }
        catch
        {
            return default;
        }
    }

    private static void WriteState(int tier, int fileCount)
    {
        try
        {
            var dir = PatchFileBackup.GetGroupDirectory(BackupGroup);
            Directory.CreateDirectory(dir);
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                applied = true,
                tier,
                fileCount,
                savedAt = DateTime.Now
            });
            File.WriteAllText(Path.Combine(dir, StateFileName), json);
        }
        catch
        {
            // 状态文件只影响界面显示，写失败不阻断补丁本身。
        }
    }

    /// <summary>
    /// 清除持久化的应用状态标记。
    ///
    /// 全量还原客户端底包后必须调用：索引已被回滚成纯净版本，但 state.json
    /// 仍记着 applied=true 与旧档位，会导致再次应用同档位时被判定为「已应用、
    /// 无需重复写入」而直接跳过，用户再也无法打上泥人。
    /// </summary>
    public static void ClearState()
    {
        try
        {
            var dir = PatchFileBackup.GetGroupDirectory(BackupGroup);
            var path = Path.Combine(dir, StateFileName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 同上。
        }
    }

    /// <summary>是否已应用过。</summary>
    public static bool HasBackup() => ReadState().Applied;

    /// <summary>已改动的文件数量。</summary>
    public static int PatchedFileCount() => ReadState().FileCount;

    /// <summary>
    /// 应用指定档位。
    /// </summary>
    public bool Apply(
        GameModeInfo info,
        MudManTier tier,
        Action<string>? progress,
        out string message,
        bool refreshFingerprint = true)
    {
        message = string.Empty;

        if (tier.HasTextures() && !MudManTables.IsAvailable)
        {
            message = MudManTables.LoadError ?? "泥人贴图表不可用，无法执行贴图压缩。";
            return false;
        }

        // 资源锁：用 FileShare.None 独占打开客户端索引，比只查进程名可靠。
        // 泥人要写 2 万多个文件，若在游戏占用资源时开跑，会中途失败并留下半成品。
        var (isLocked, lockReason) = PatchBackupManager.CheckResourceLock(info);
        if (isLocked)
        {
            message = lockReason ?? "客户端资源被占用，无法写入。";
            return false;
        }

        try
        {
            // 1. 先准备还原点（同版本复用，跨版本重建）。
            //
            // 还原点必须是「同版本第一次应用前」的索引：玩家会反复换档位
            // （中级→高级→低级），每次应用前都新建快照会堆出几十份 115 MB 文件，
            // 而且第二份开始记的已经是「打过补丁的索引」，拿它还原等于还原到半补丁状态。
            // 游戏版本变了则重建：旧索引与新的 bundle 布局对不上，用它还原会让偏移错位。
            progress?.Invoke("准备还原点...");
            var snapshotId = EnsureRestorePoint(info, progress, out var restorePointError);
            if (string.IsNullOrEmpty(snapshotId))
            {
                // 建不了还原点就不要动客户端：没有可靠回退手段时写补丁，
                // 等于把用户锁死在「出问题只能验证游戏文件」的境地。
                message = $"无法准备还原点，已中止：{restorePointError}";
                return false;
            }

            // 2. 换档位前必须先回滚到还原点。
            //
            // 关键：BuildPlan 从「当前客户端索引」读取内容（ClientIndexSession.Open），
            // 不是从还原点读。若不先回滚，从高档位切低档位时读到的是已被高档位
            // 改过的索引 —— 低档位规则只覆盖它自己关心的文件，高档位写入的
            // 3.2 万张灰贴图与文本清理全部滞留在索引里，降档形同虚设。
            var currentState = ReadState();
            bool needRollback = currentState.Applied && currentState.Tier != (int)tier;

            if (needRollback)
            {
                progress?.Invoke($"检测到已应用档位 {currentState.Tier}，切换前先回滚到还原点...");
                var rollback = _backupManager.RestoreSnapshot(info, snapshotId, out _, progress);
                if (!rollback.Success)
                {
                    message = $"切换档位前回滚失败，已中止（避免新旧档位改动叠加）：{rollback.Message}";
                    return false;
                }
            }

            // 3. 基于（已回滚的）客户端索引规划改动
            var rules = tier.Rules();
            var withTextures = tier.HasTextures();

            progress?.Invoke($"泥人：{tier.DisplayName()}，开始规划...");

            var plan = BuildPlan(info, rules, withTextures, progress, out var failures);
            if (failures.Count > 0)
            {
                message = $"有 {failures.Count} 个文件未通过校验，已中止：{string.Join("；", failures.Take(3))}";
                return false;
            }

            if (plan.Count == 0)
            {
                // 刚回滚过的话客户端已回到干净状态，状态文件必须同步清掉，
                // 否则界面仍显示「已应用档位 X」而实际什么都没写。
                if (needRollback) ClearState();
                message = "没有需要改动的文件（规则未命中，或该档位已应用）。";
                return false;
            }

            // 不做逐文件备份：还原走索引级快照（见 Restore）。
            // 索引快照同时覆盖文本与贴图，逐文件再存一份是纯冗余，
            // 而 20,800 个文本文件 + 32,181 张原图的逐文件写入会额外
            // 拖慢应用并占用数十 GB 磁盘。
            var files = plan
                .Select(p => (p.VirtualPath, p.NewBytes))
                .ToList();

            var textureCount = plan.Count(p => p.IsTexture);
            progress?.Invoke(
                textureCount > 0
                    ? $"写入 {files.Count} 个文件（其中贴图 {textureCount} 张）..."
                    : $"写入 {files.Count} 个文件...");

            if (!_engine.WriteGameFiles(info, files))
            {
                message = PatchEngine.LastWriteException?.Message ?? "写入失败。";
                return false;
            }

            WriteState((int)tier, files.Count);

            // 本次写入来自泥人自身，把还原点指纹同步到最新索引。
            //
            // 注意：批处理（applyAllEffects）里泥人之后还会写特效/技能/血条，
            // 那会把索引再改一次，此刻刷新的指纹立刻作废，导致后续 Restore 被判 Stale
            // 而永远无法还原泥人。所以批处理调用方会传 refreshFingerprint: false，
            // 等整批写完后由调用方统一刷新（见 MainWindow.HandleApplyAllEffectsAsync）。
            if (refreshFingerprint)
            {
                _backupManager.RefreshSnapshotFingerprint(info, snapshotId);
            }

            message = textureCount > 0
                ? $"泥人 {tier.DisplayName()} 已应用：改动 {files.Count} 个文件，其中贴图 {textureCount} 张。"
                : $"泥人 {tier.DisplayName()} 已应用：改动 {files.Count} 个文件。";
            return true;
        }
        catch (Exception ex)
        {
            message = $"应用失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>泥人还原点固定使用的快照 id，便于跨版本定位与替换。</summary>
    /// <summary>泥人还原点的名字前缀。快照 id 由 PatchBackupManager 内部生成，只能按名前缀识别。</summary>
    public const string RestorePointPrefix = "泥人还原点";

    /// <summary>
    /// 还原因指纹失效需要用户二次确认时的消息前缀。
    /// UI 识别该前缀后弹确认框，用户同意则带 force 重发。
    /// </summary>
    public const string StaleRequiresConfirmPrefix = "[MUDMAN_STALE_CONFIRM]";

    /// <summary>
    /// 确保当前游戏版本有一份可用的泥人还原点，返回它的 id。
    ///
    /// 策略（同一版本复用，跨版本重建）：
    ///   · 已存在且版本号等于当前客户端 → 直接复用，不再新建
    ///   · 不存在，或版本号不同 → 删掉旧的，用当前索引新建
    ///
    /// 为什么不在每次应用前都新建：玩家会在中级/高级/低级之间反复切换，
    /// 每次都建会堆出几十份 115 MB 的文件；更严重的是第二份起记的已经是
    /// 「打过补丁的索引」，拿它还原只会回到半补丁状态，不是干净起点。
    ///
    /// 为什么版本变了必须重建：索引与 bundle 布局是按游戏版本排布的，
    /// 用旧索引还原会让所有偏移错位，客户端直接无法启动。
    /// </summary>
    private string? EnsureRestorePoint(GameModeInfo info, Action<string>? progress, out string error)
    {
        error = string.Empty;

        string currentVersion = PatchBackupManager.ReadGameVersion(info);
        if (string.IsNullOrEmpty(currentVersion))
        {
            error = "无法读取当前客户端版本，已中止：无法确认还原点是否适用。";
            return null;
        }

        var points = _backupManager.GetSnapshots(info)
            .Where(s => s.Name.StartsWith(RestorePointPrefix, StringComparison.Ordinal))
            .ToList();

        // 同版本已有 → 复用；多份时留最新，其余清掉（旧逻辑可能已堆了几份）。
        var matched = points
            .Where(s => string.Equals(s.GameVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.CreatedAt)
            .ToList();

        if (matched.Count > 0)
        {
            foreach (var stale in points.Where(s => !ReferenceEquals(s, matched[0])))
            {
                _backupManager.DeleteSnapshot(info, stale.Id, out _);
            }

            progress?.Invoke($"复用现有还原点（{currentVersion}）。");
            return matched[0].Id;
        }

        // 版本不符 → 全部删掉重建
        foreach (var old in points)
        {
            progress?.Invoke($"游戏版本已变化（{old.GameVersion} → {currentVersion}），重建还原点...");
            _backupManager.DeleteSnapshot(info, old.Id, out _);
        }

        var created = _backupManager.CreateSnapshot(
            info: info,
            type: PatchSnapshotType.Manual,
            name: $"{RestorePointPrefix}（{currentVersion}）",
            description: "泥人首次应用前记录的客户端索引；同一版本内反复应用都回滚到这一份",
            settingsJson: null,
            activeModules: null,
            logger: progress,
            indexBytes: null);

        if (!created.Success)
        {
            error = created.Message;
            return null;
        }

        // CreateSnapshot 的 id 由内部生成，按名字取回刚建的那一条。
        var point = _backupManager.GetSnapshots(info)
            .Where(s => s.Name.StartsWith(RestorePointPrefix, StringComparison.Ordinal))
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefault();

        if (point is null)
        {
            error = "还原点创建后未能定位其标识。";
            return null;
        }

        progress?.Invoke($"已创建还原点（{currentVersion}）。");
        return point.Id;
    }

    /// <summary>
    /// 还原泥人的全部改动。
    ///
    /// 走索引级还原，不逐文件写回：贴图的原图从来没有被删除，只是索引指向了
    /// 新的替换块。把索引换回补丁前的版本，几万张图会同时恢复。
    /// 实测：逐文件写回 20,800 个文件需要 375 秒，索引还原 2.3 秒。
    ///
    /// 还原目标优先用泥人自己的还原点，而不是全局「官方纯净底包」：
    /// 底包可能是旧游戏版本的，用它还原会让索引与资源布局错位，
    /// 客户端直接无法启动（实测 2026-10-22 一次误还原导致 12,481 个文件读取失败）。
    /// </summary>
    public bool Restore(GameModeInfo info, Action<string>? progress, out string message)
        => Restore(info, force: false, progress, out message);

    /// <summary>
    /// 还原泥人改动。
    ///
    /// <paramref name="force"/> 为 false 时，若还原点之后客户端又被其他补丁改过
    /// （指纹已变），返回带 <see cref="StaleRequiresConfirmPrefix"/> 前缀的提示，
    /// 由 UI 弹二次确认；用户确认后以 force: true 重发，才真正执行回滚。
    ///
    /// 为什么不一概拒绝：同版本内泥人换档会先回滚再重写，指纹必然变过，
    /// 一概拒绝等于让「还原泥人」永远失败。把决定权交给知情的用户，
    /// 同时在提示里讲清后果（其他补丁需要重新应用）。
    /// </summary>
    public bool Restore(
        GameModeInfo info,
        bool force,
        Action<string>? progress,
        out string message)
    {
        message = string.Empty;

        try
        {
            // 优先用泥人自己的还原点。还原后保留它 —— 同一版本内
            // 后续再应用仍然回滚到同一份干净索引。
            var point = _backupManager.GetSnapshots(info)
                .Where(s => s.Name.StartsWith(RestorePointPrefix, StringComparison.Ordinal))
                .OrderByDescending(s => s.CreatedAt)
                .FirstOrDefault();

            if (point is not null)
            {
                string current = PatchBackupManager.ReadGameVersion(info);
                if (!string.IsNullOrEmpty(current)
                    && !string.IsNullOrEmpty(point.GameVersion)
                    && !string.Equals(point.GameVersion, current, StringComparison.OrdinalIgnoreCase))
                {
                    message =
                        $"泥人还原点是 {point.GameVersion} 版本创建的，当前客户端是 {current}。" +
                        "用旧版本索引还原会让客户端无法启动，已拒绝。\n" +
                        "下次应用泥人时会按当前版本自动重建还原点。";
                    return false;
                }

                // 还原是回滚全量索引快照，若快照之后又打过其他补丁（物价/汉化/MTX 等），
                // 回滚会把它们一起抹掉。这里先比对索引指纹，发现被改写过就拒绝。
                var staleness = _backupManager.CheckSnapshotStaleness(info, point.Id);
                if (staleness == SnapshotStaleness.Stale && !force)
                {
                    // 交给 UI 二次确认：用户知情后可以继续，不再是死路。
                    message =
                        StaleRequiresConfirmPrefix +
                        "泥人还原点之后，客户端又被其他补丁改动过（物价、汉化、商城外观等）。\n\n" +
                        "继续还原会把客户端索引重置为「泥人首次应用前」的状态，" +
                        "那些补丁需要重新应用一次。\n\n是否继续？";
                    return false;
                }

                progress?.Invoke("正在还原到泥人还原点...");
                var snap = _backupManager.RestoreSnapshot(info, point.Id, out var snapMessage, progress);

                if (!snap.Success)
                {
                    message = $"还原失败：{snapMessage}";
                    return false;
                }

                PatchFileBackup.Clear(BackupGroup);
                ClearState();

                message = "已还原到泥人首次应用前的干净状态。";
                return true;
            }

            // 没有泥人还原点（旧版本应用过，或还原点被手动删除）：
            // 退回到版本校验过的官方底包还原。
            progress?.Invoke("正在还原到补丁前的索引...");
            var result = _backupManager.Restore(info, progress);

            if (!result.Success)
            {
                message = result.Message;
                return false;
            }

            PatchFileBackup.Clear(BackupGroup);
            ClearState();

            message = result.Message;
            return true;
        }
        catch (Exception ex)
        {
            message = $"还原失败：{ex.Message}";
            return false;
        }
    }

    private sealed class FilePlan
    {
        public required string VirtualPath { get; init; }

        public required byte[] NewBytes { get; init; }

        /// <summary>该条是否来自贴图规则。</summary>
        public required bool IsTexture { get; init; }
    }

    private List<FilePlan> BuildPlan(
        GameModeInfo info,
        IReadOnlyList<EffectPatchRule> rules,
        bool withTextures,
        Action<string>? progress,
        out List<string> failures)
    {
        failures = [];

        var compiled = rules
            .Select(rule => (Rule: rule, PathRegex: CompilePathRegex(rule)))
            .Where(x => x.PathRegex is not null)
            .ToList();

        progress?.Invoke("正在读取客户端索引...");
        var session = ClientIndexSession.Open(info);
        if (session is null)
        {
            failures.Add("未能打开客户端索引。");
            return [];
        }

        try
        {
            return BuildPlanCore(session, compiled, withTextures, progress, failures);
        }
        finally
        {
            session.Dispose();
        }
    }

    private static List<FilePlan> BuildPlanCore(
        ClientIndexSession session,
        List<(EffectPatchRule Rule, Regex? PathRegex)> compiled,
        bool withTextures,
        Action<string>? progress,
        List<string> failures)
    {
        var plan = new List<FilePlan>();
        var textureRule = withTextures ? MudManTextureRule() : null;

        // 一次遍历同时收集文本规则命中与贴图命中，避免对 426 万条路径走两遍。
        var textMatches = new Dictionary<string, List<EffectPatchRule>>(StringComparer.OrdinalIgnoreCase);
        var textureMatches = new List<string>();

        foreach (var (path, _) in session.Records)
        {
            if (textureRule is not null
                && path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)
                && GrayDdsMaker.IsListed(path))
            {
                textureMatches.Add(path);
                continue;
            }

            if (!TextExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase))) continue;

            foreach (var (rule, pathRegex) in compiled)
            {
                if (!pathRegex!.IsMatch(path)) continue;
                if (!textMatches.TryGetValue(path, out var list))
                {
                    list = [];
                    textMatches[path] = list;
                }
                list.Add(rule);
            }
        }

        // ── 贴图：只看路径，不读原文件 ──
        if (textureRule is not null && textureMatches.Count > 0)
        {
            progress?.Invoke($"贴图命中 {textureMatches.Count} 张，正在生成替换字节...");
            var done = 0;
            foreach (var path in textureMatches)
            {
                var replacement = EffectRuleApplier.ResolveByPath(path, textureRule);
                if (replacement is null) continue;

                plan.Add(new FilePlan
                {
                    VirtualPath = path,
                    NewBytes = replacement,
                    IsTexture = true
                });

                if (++done % 5000 == 0)
                {
                    progress?.Invoke($"已生成 {done}/{textureMatches.Count} 张...");
                }
            }
        }

        // ── 文本：读内容、按规则改、语法校验 ──
        if (textMatches.Count > 0)
        {
            progress?.Invoke($"文本文件命中 {textMatches.Count} 个，正在处理...");
            var processed = 0;

            foreach (var (path, fileRules) in textMatches)
            {
                processed++;
                if (processed % 500 == 0)
                {
                    progress?.Invoke($"处理文本 {processed}/{textMatches.Count}...");
                }

                byte[]? bytes;
                try
                {
                    bytes = session.ReadFile(path);
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(path)}: 读取失败 - {ex.Message}");
                    continue;
                }

                if (bytes is null || bytes.Length == 0 || bytes.Length > MaxTextFileBytes) continue;

                var encoding = EffectTextCodec.Detect(bytes, path);
                var current = EffectTextCodec.Decode(bytes, encoding);
                var changed = false;

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
                        changed = true;
                    }
                }

                if (!changed) continue;

                // 清空型改动（把整个 .epk 内容换成空串或空白）不需要语法校验：
                // 空文件没有结构可破坏，而校验器会把「内容为空」当成失败。
                // 仅在替换后仍有非空白内容时才执行语法校验，清空后的文件直接加入写入计划。
                if (!string.IsNullOrWhiteSpace(current))
                {
                    var validation = EffectSyntaxValidator.ValidateFor(current, path);
                    if (!validation.IsValid)
                    {
                        failures.Add($"{Path.GetFileName(path)}: {validation.Error}");
                        continue;
                    }
                }

                plan.Add(new FilePlan
                {
                    VirtualPath = path,
                    NewBytes = EffectTextCodec.Encode(current, encoding),
                    IsTexture = false
                });
            }
        }

        progress?.Invoke("规划完成。");
        return plan;
    }

    /// <summary>贴图规则。它不出现在档位规则集里，因为执行路径完全不同。</summary>
    private static EffectPatchRule MudManTextureRule() => new()
    {
        Id = "mudman_texture",
        Feature = "泥人-贴图",
        PathPattern = @".*\.dds$",
        Action = EffectAction.MudManTexture,
        TargetPattern = string.Empty,
        Description = "把贴图替换成极小尺寸的预置块，不读取原图。"
    };

    private static Regex? CompilePathRegex(EffectPatchRule rule)
    {
        try
        {
            // 同 EffectPatchApplier：加超时兜底灾难性回溯。
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
