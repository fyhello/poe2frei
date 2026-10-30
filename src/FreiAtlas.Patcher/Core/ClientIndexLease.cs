using LibBundle3.Records;

namespace FreiAtlas.Patcher.Core;

/// <summary>
/// 客户端索引会话的借用凭证。
///
/// 模块通过 <see cref="PatchEngine.AcquireClientIndex"/> 借用引擎已经打开的索引，
/// 不再各自 <see cref="ClientIndexSession.Open"/>。借用期间会话由引擎持有，
/// <see cref="Dispose"/> 只归还借用，不关闭底层会话。
///
/// 读取一律经过 <see cref="PatchEngine"/> 的叠加层包装：批量会话中前一个模块
/// 尚未落盘的改动会优先返回，避免模块拿到旧内容。
/// </summary>
internal sealed class ClientIndexLease : IDisposable
{
    private PatchEngine? _owner;

    internal ClientIndexLease(PatchEngine owner, ClientIndexSession session)
    {
        _owner = owner;
        Session = session;
    }

    internal ClientIndexSession Session { get; }

    /// <summary>
    /// 全部已解析路径。会话期间只解析一次。
    ///
    /// 只包含「客户端索引里已存在」的路径，不包含本批次刚登记、尚未落盘的
    /// 新路径（<see cref="PatchSession.Pending"/>）。需要读取新路径内容的场景
    /// 请用 <see cref="ReadFile"/> / <see cref="FileExists"/>，它们会先查叠加层。
    /// </summary>
    public IEnumerable<(string Path, FileRecord Record)> Records
    {
        get
        {
            ThrowIfDisposed();
            return Owner.GetClientRecords(Session);
        }
    }

    public void EnsurePathsParsed()
    {
        ThrowIfDisposed();
        Session.EnsurePathsParsed();
    }

    /// <summary>读取文件；优先返回批量会话叠加层中的最新内容。</summary>
    public byte[]? ReadFile(string path)
        => Owner.ReadClientFile(Session, path);

    /// <summary>判断文件是否存在；叠加层中已登记的新内容也视为存在。</summary>
    public bool FileExists(string path)
        => Owner.FileExistsInClient(Session, path);

    public void Dispose()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        owner?.ReturnClientIndex();
    }

    private PatchEngine Owner
    {
        get
        {
            ThrowIfDisposed();
            return _owner!;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_owner is null)
        {
            throw new ObjectDisposedException(nameof(ClientIndexLease));
        }
    }
}
