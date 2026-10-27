using System.IO;
using FreiAtlas.Patcher.EffectPatch;
using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.EffectPatch.HealthBar;

/// <summary>
/// 头顶血条补丁的应用与还原。
///
/// 与泥人共用同一套底层能力：
///   · <see cref="ClientIndexSession"/> 单次打开索引，避免反复 ParsePaths；
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
        // 血条要扫描全部 .ot 再写入，游戏占用资源时开跑会中途失败。
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

            // 还原点：同版本复用，跨版本重建。理由与泥人一致 ——
            // 每次应用都新建会堆出多份 115 MB 文件，且第二份起记的已是打过补丁的索引。
            progress?.Invoke("准备还原点...");
            var restorePoint = EnsureRestorePoint(info, progress, out var restoreError);
            if (string.IsNullOrEmpty(restorePoint))
            {
                message = $"无法准备还原点，已中止：{restoreError}";
                return false;
            }

            var files = plan.Select(p => (p.VirtualPath, p.NewBytes)).ToList();

            progress?.Invoke($"写入 {files.Count} 个文件...");
            if (!_engine.WriteGameFiles(info, files))
            {
                message = PatchEngine.LastWriteException?.Message ?? "写入失败。";
                return false;
            }

            WriteState(files.Count);

            // 本次写入来自血条自身，把还原点指纹同步到最新索引。
            // 否则同一版本内重复应用后，还原点会被判定为 Stale 而无法还原。
            _backupManager.RefreshSnapshotFingerprint(info, restorePoint);

            message = $"头顶血条已全部显示：改动 {files.Count} 个怪物定义文件。";
            return true;
        }
        catch (Exception ex)
        {
            message = $"应用失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 还原：回滚到应用前的索引。
    /// </summary>
    public bool Restore(GameModeInfo info, Action<string>? progress, out string message)
    {
        message = string.Empty;

        try
        {
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
                        $"血条还原点是 {point.GameVersion} 版本创建的，当前客户端是 {current}。" +
                        "用旧版本索引还原会让客户端无法启动，已拒绝。";
                    return false;
                }

                // 还原是回滚全量索引快照，若快照之后又打过其他补丁，回滚会把它们一起抹掉。
                // 先比对索引指纹，发现被改写过就拒绝。
                var staleness = _backupManager.CheckSnapshotStaleness(info, point.Id);
                if (staleness == SnapshotStaleness.Stale)
                {
                    message =
                        "血条还原点之后，客户端又被其他补丁改动过（物价、汉化、商城外观等）。\n" +
                        "此时还原血条会把那些改动一并抹掉，已中止。\n\n" +
                        "如需继续，请先单独还原那些补丁，或使用「恢复纯净客户端」后重新按需打补丁。";
                    return false;
                }

                progress?.Invoke("正在还原到血条应用前的索引...");
                var snap = _backupManager.RestoreSnapshot(info, point.Id, out var snapMessage, progress);
                if (!snap.Success)
                {
                    message = $"还原失败：{snapMessage}";
                    return false;
                }

                ClearState();
                message = "已还原到血条应用前的状态。";
                return true;
            }

            progress?.Invoke("正在还原到补丁前的索引...");
            var result = _backupManager.Restore(info, progress);
            if (!result.Success)
            {
                message = result.Message;
                return false;
            }

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

    /// <summary>
    /// 确保当前游戏版本有一份血条还原点。同版本复用，跨版本重建。
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

        foreach (var old in points)
        {
            progress?.Invoke($"游戏版本已变化（{old.GameVersion} → {currentVersion}），重建还原点...");
            _backupManager.DeleteSnapshot(info, old.Id, out _);
        }

        var created = _backupManager.CreateSnapshot(
            info: info,
            type: PatchSnapshotType.Manual,
            name: $"{RestorePointPrefix}（{currentVersion}）",
            description: "血条应用前记录的客户端索引；同一版本内反复应用都回滚到这一份",
            settingsJson: null,
            activeModules: null,
            logger: progress,
            indexBytes: null);

        if (!created.Success)
        {
            error = created.Message;
            return null;
        }

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

    private sealed class FilePlan
    {
        public required string VirtualPath { get; init; }
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

        using var session = ClientIndexSession.Open(info);
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
                NewBytes = EffectTextCodec.Encode(working, encoding)
            });
        }

        progress?.Invoke($"规划完成：命中 {plan.Count} 个文件。");
        return plan;
    }
}
