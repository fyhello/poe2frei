namespace FreiAtlas.Patcher.Core;

/// <summary>
/// 批量应用的写盘叠加层。
///
/// 各模块原本是「读盘 → 改 → 写盘」，串行跑 N 个模块就要重写 N 次整份索引
/// （GGPK 下每次约 110 MB，耗时以十秒计）。本类把中间写操作截留在内存里：
/// 后续模块读到的是前序模块的输出，全部跑完后一次性写盘。
///
/// 使用约定：
/// <list type="bullet">
/// <item>会话期间 <see cref="PatchEngine.WriteGameFiles"/> 只登记，不落盘；</item>
/// <item><see cref="PatchEngine.ReadGameFiles"/> 优先返回登记过的内容；</item>
/// <item>会话结束必须显式 <see cref="PatchEngine.CommitSession"/> 或 <see cref="PatchEngine.AbortSession"/>。</item>
/// </list>
/// </summary>
public sealed class PatchSession
{
    /// <summary>模块对同一路径的写入方式。</summary>
    public enum WriteMode
    {
        /// <summary>读原内容再改写，可与其它 Transform 叠加。</summary>
        Transform,

        /// <summary>整份替换，会丢弃此前对该路径的任何改动。</summary>
        Replace
    }

    /// <summary>叠加层容量上限。超过即中止，避免把整份客户端读进内存。</summary>
    public const long MaxPendingBytes = 512L * 1024 * 1024;

    private readonly Dictionary<string, byte[]> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WriteMode> _modes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _conflicts = [];

    // 会话期间允许新建的路径（例如光柱补丁新增的 .ao 文件）。
    // 合并写盘时必须把这份许可一起带给 WriteGameFilesCore，否则会被 FilterExisting 过滤掉。
    private readonly HashSet<string> _allowedNewPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>会话期间允许新建的路径集合。</summary>
    public IReadOnlySet<string> AllowedNewPaths => _allowedNewPaths;

    /// <summary>登记允许新建的路径。</summary>
    public void AllowNewPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path)) _allowedNewPaths.Add(path);
        }
    }

    /// <summary>会话是否仍然有效。冲突或超限会被标记为无效，调用方必须放弃提交。</summary>
    public bool IsValid { get; private set; } = true;

    /// <summary>登记待写入的文件（路径 → 最新内容）。</summary>
    public IReadOnlyDictionary<string, byte[]> Pending => _pending;

    /// <summary>检测到的路径冲突，供调用方说明失败原因。</summary>
    public IReadOnlyList<string> Conflicts => _conflicts;

    /// <summary>已登记内容的总字节数。</summary>
    public long TotalBytes { get; private set; }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    /// <summary>读取叠加层中的最新内容；未登记过返回 null。</summary>
    public byte[]? TryGet(string path)
        => _pending.TryGetValue(Normalize(path), out var data) ? data : null;

    /// <summary>
    /// 登记一次写入。
    ///
    /// 冲突判定：
    /// <list type="bullet">
    /// <item>同一路径出现两次 <see cref="WriteMode.Replace"/> —— 后写会静默丢弃前写，必须报错；</item>
    /// <item>路径已被 Replace 之后又被任何模块写入 —— 同理；</item>
    /// <item>路径已被 Transform 之后又来 Replace —— Replace 会丢掉前面的改动。</item>
    /// </list>
    /// Transform 叠加是允许的：后一个模块通过 <see cref="TryGet"/> 读到前一个的输出。
    /// </summary>
    public bool Stage(string path, byte[] data, WriteMode mode)
    {
        string key = Normalize(path);

        if (_modes.TryGetValue(key, out var existing))
        {
            if (existing == WriteMode.Replace || mode == WriteMode.Replace)
            {
                string detail = existing == WriteMode.Replace && mode == WriteMode.Replace
                    ? "两个模块都要整份替换该文件"
                    : existing == WriteMode.Replace
                        ? "该文件已被整份替换，后续模块不能再改写"
                        : "该文件已被其它模块改写，不能再整份替换";

                _conflicts.Add($"{key}：{detail}");
                IsValid = false;
                return false;
            }
        }

        if (_pending.TryGetValue(key, out var old))
        {
            TotalBytes -= old.Length;
        }

        _pending[key] = data;
        _modes[key] = mode;
        TotalBytes += data.Length;

        if (TotalBytes > MaxPendingBytes)
        {
            _conflicts.Add($"待写入内容超过 {MaxPendingBytes / (1024 * 1024)} MB 上限，已中止批量应用。");
            IsValid = false;
            return false;
        }

        return true;
    }
}
