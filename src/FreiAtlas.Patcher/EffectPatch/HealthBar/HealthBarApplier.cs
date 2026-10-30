using System.IO;
using FreiAtlas.Patcher.EffectPatch;
using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.EffectPatch.HealthBar;

/// <summary>
/// 头顶血条补丁的应用与还原。
///
/// 与泥人共用同一套底层能力：
///   · <see cref="ClientIndexLease"/> 借用引擎索引会话，避免反复 ParsePaths；
///   · <see cref="PatchBackupManager"/> 建还原点，还原用索引级回滚（秒级）；
///   · <see cref="EffectSyntaxValidator"/> 写入前校验语法。
///
/// 改动量很小（实测 53 处、53 个文件），因此规划阶段直接读取候选文件内容，
/// 不做路径预筛 —— 需要读的只有那 53 个，其余 8,228 个 .ot 不含目标字段。
/// </summary>
public sealed class HealthBarApplier
{
    /// <summary>备份分组。与泥人分开，互不影响。</summary>
    private const string BackupGroup = "healthbar";

    /// <summary>泥人还原点同样按名字前缀识别，这里用独立前缀避免冲突。</summary>
    public const string RestorePointPrefix = "血条还原点";

    private const string StateFileName = "state.json";

    private readonly PatchEngine _engine;
    private readonly PatchBackupManager _backupManager;

    public HealthBarApplier(PatchEngine? engine = null, PatchBackupManager? backupManager = null)
    {
        _engine = engine ?? new PatchEngine();
        _backupManager = backupManager ?? new PatchBackupManager();
    }

    /// <summary>已应用档位与改动文件数。</summary>
    public readonly record struct AppliedState(bool Applied, int FileCount);

    /// <summary>读取当前应用状态。</summary>
    public static AppliedState ReadState()
    {
        var path = Path.Combine(PatchFileBackup.GetGroupDirectory(BackupGroup), StateFileName);
        if (!File.Exists(path)) return default;

        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path));
            if (node is null) return default;

            return new AppliedState(
                node["applied"]?.GetValue<bool>() ?? false,
                node["fileCount"]?.GetValue<int>() ?? 0);
        }
        catch
        {
            return default;
        }
    }

    private static void WriteState(int fileCount)
    {
        try
        {
            var dir = PatchFileBackup.GetGroupDirectory(BackupGroup);
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, StateFileName),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    applied = true,
                    fileCount,
                    savedAt = DateTime.Now
                }));
        }
        catch
        {
            // 状态只影响界面显示，写失败不阻断补丁。
        }
    }

    /// <summary>
    /// 清除持久化的应用状态标记。
    ///
    /// 全量还原客户端底包后必须调用，否则 state.json 仍记着 applied=true，
    /// 界面会一直显示「已开启」而实际客户端已回到纯净版本。
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

    /// <summary>是否已应用。</summary>
    public static bool HasBackup() => ReadState().Applied;

    /// <summary>已改动的文件数量。</summary>
    public static int PatchedFileCount() => ReadState().FileCount;

    /// <summary>
    /// 应用：把所有隐藏头顶血条的声明改为显示。
    /// </summary>
    public bool Apply(GameModeInfo info, Action<string>? progress, out string message)
    {
        message = string.Empty;

        // 资源锁：用 FileShare.None 独占打开客户端索引，比只查进程名可靠。
        var (isLocked, lockReason) = PatchBackupManager.CheckResourceLock(info);
        if (isLocked)
        {
            message = lockReason ?? "客户端资源被占用，无法写入。";
            return false;
        }

        try
        {
            progress?.Invoke("血条：正在扫描怪物定义...");

            var plan = BuildPlan(info, progress, out var failures);
            if (failures.Count > 0)
            {
                message = $"有 {failures.Count} 个文件未通过校验，已中止：{string.Join("；", failures.Take(3))}";
                return false;
            }

            if (plan.Count == 0)
            {
                message = "没有需要改动的文件（客户端已全部显示头顶血条）。";
                return false;
            }

            // 逐文件备份：只涉及几十个怪物定义文件，体积很小。
            //
            // 原先复用「全量索引快照 + 指纹校验」，导致血条无法单独关闭：
            // 用户之后打过任何其他补丁，索引指纹就变了，快照被判为 Stale，
            // 还原被永久拒绝。逐文件备份只记录这几十个文件的原件，
            // 与其他补丁互不干扰，也不受索引整体变化影响。
            // BuildPlan 已读到并校验过每个文件的原始字节，这里直接复用，
            // 不再第二次打开索引；Originals 为空的情况已在 BuildPlan 中拦截。
            var originals = plan.ToDictionary(
                p => p.VirtualPath,
                p => p.OriginalBytes,
                StringComparer.OrdinalIgnoreCase);

            PatchFileBackup.SaveOriginals(
                BackupGroup,
                originals.Keys,
                originals,
                gameVersion: PatchBackupManager.ReadGameVersion(info));

            var files = plan.Select(p => (p.VirtualPath, p.NewBytes)).ToList();

            progress?.Invoke($"写入 {files.Count} 个文件...");
            if (!_engine.WriteGameFiles(info, files))
            {
                message = PatchEngine.LastWriteException?.Message ?? "写入失败。";
                return false;
            }

            WriteState(files.Count);
            message = $"头顶血条已全部显示：改动 {files.Count} 个怪物定义文件。";
            return true;
        }
        catch (Exception ex)
        {
            message = $"应用失败：{ex.Message}";
            return false;
        }
    }

    public bool Restore(GameModeInfo info, Action<string>? progress, out string message)
    {
        message = string.Empty;

        var (isLocked, lockReason) = PatchBackupManager.CheckResourceLock(info);
        if (isLocked)
        {
            message = lockReason ?? "客户端资源被占用，无法写入还原。";
            return false;
        }

        try
        {
            // 版本校验：游戏更新后怪物定义可能已变，写回旧版本文件会出问题。
            string? mismatch = PatchFileBackup.CheckVersionMismatch(
                BackupGroup, PatchBackupManager.ReadGameVersion(info));
            if (mismatch is not null)
            {
                message = mismatch;
                return false;
            }

            var originals = PatchFileBackup.LoadOriginals(BackupGroup);
            if (originals.Count == 0)
            {
                message = PatchFileBackup.LastManifestError is null
                    ? "没有可用的血条原始备份。"
                    : $"读取血条原始备份失败：{PatchFileBackup.LastManifestError}";
                return false;
            }

            var files = originals.Select(kv => (kv.VirtualPath, kv.Data)).ToList();
            progress?.Invoke($"正在还原 {files.Count} 个文件至官方原版...");
            if (!_engine.WriteGameFiles(info, files))
            {
                message = PatchEngine.LastWriteException?.Message ?? "还原写入失败。";
                return false;
            }

            PatchFileBackup.RemoveOriginals(BackupGroup, files.Select(f => f.VirtualPath));
            ClearState();

            message = $"已还原 {files.Count} 个怪物定义文件为官方原版。";
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

        /// <summary>规划阶段读到的原始字节，用于写入前建立逐文件备份。</summary>
        public required byte[] OriginalBytes { get; init; }

        public required byte[] NewBytes { get; init; }
    }

    /// <summary>
    /// 扫描所有 .ot，把命中规则的写成新内容。
    ///
    /// 只读取含 mini_life_bar 的文件：先用一次索引遍历拿到候选路径，
    /// 再逐个读取，避免把 8,281 个 .ot 全部读进内存。
    /// </summary>
    private List<FilePlan> BuildPlan(
        GameModeInfo info,
        Action<string>? progress,
        out List<string> failures)
    {
        failures = [];
        var plan = new List<FilePlan>();

        using var session = _engine.AcquireClientIndex(info);
        if (session is null)
        {
            failures.Add("无法打开客户端索引");
            return plan;
        }

        var rules = HealthBarRules.All;
        int scanned = 0;

        foreach (var (path, _) in session.Records)
        {
            if (!HealthBarRules.MayApply(path)) continue;

            scanned++;
            if (scanned % 2000 == 0)
            {
                progress?.Invoke($"已扫描 {scanned} 个 .ot...");
            }

            byte[]? bytes;
            try
            {
                bytes = session.ReadFile(path);
            }
            catch
            {
                continue;
            }

            if (bytes is null || bytes.Length == 0) continue;

            var encoding = EffectTextCodec.Detect(bytes, path);
            string current;
            try
            {
                current = EffectTextCodec.Decode(bytes, encoding);
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrEmpty(current)) continue;

            // 快速排除：不含目标字段的文件直接跳过，不进入正则
            if (!current.Contains("mini_life_bar", StringComparison.Ordinal)) continue;

            string working = current;
            bool changed = false;

            foreach (var rule in rules)
            {
                var outcome = EffectRuleApplier.Apply(working, rule);
                if (outcome.Error is not null)
                {
                    failures.Add($"{Path.GetFileName(path)}: {outcome.Error}");
                    break;
                }
                if (outcome.Changed)
                {
                    working = outcome.Content;
                    changed = true;
                }
            }

            if (!changed) continue;

            var validation = EffectSyntaxValidator.ValidateFor(working, path);
            if (!validation.IsValid)
            {
                failures.Add($"{Path.GetFileName(path)}: {validation.Error}");
                continue;
            }

            plan.Add(new FilePlan
            {
                VirtualPath = path,
                OriginalBytes = bytes,
                NewBytes = EffectTextCodec.Encode(working, encoding)
            });
        }

        progress?.Invoke($"规划完成：命中 {plan.Count} 个文件。");
        return plan;
    }
}
