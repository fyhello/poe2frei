namespace FreiAtlas.Patcher.Core;

/// <summary>
/// 客户端虚拟路径的唯一规范化入口。
///
/// 为什么必须统一：同一个路径在不同模块里被各自 <c>Replace('\\','/')</c> 处理，
/// 有的 TrimStart('/')、有的额外 Trim()、有的转小写。归一化结果不一致时，
/// <see cref="PatchSession"/> 的叠加层字典会按不同 key 存放同一份内容，
/// 后一个模块读不到前一个模块刚写入的版本，于是「算出无变化」并静默跳过。
///
/// 规则（全模块唯一）：
///   1. 反斜杠统一成正斜杠；
///   2. 去掉首尾空白（用户输入与配置里可能带空格）；
///   3. 去掉前导斜杠（索引里的路径不带前导 '/'）；
///   4. 不做大小写转换 —— 大小写由各调用点的比较器决定（OrdinalIgnoreCase 或 NameHash）。
/// </summary>
public static class VirtualPath
{
    /// <summary>把任意写法规范化为索引使用的相对虚拟路径。</summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        return path.Replace('\\', '/').Trim().TrimStart('/');
    }

    /// <summary>
    /// 判断路径是否可以安全地作为相对路径使用。
    ///
    /// 拒绝：空路径、盘符（<c>C:</c>）、UNC（<c>\\server</c>）、
    /// 以 '/' 开头的绝对路径、以及任何 <c>..</c> 段。
    /// 用于备份目录名、Zip 条目名这类会被文件系统/归档器解释的入口。
    /// </summary>
    public static bool IsSafeRelative(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string normalized = path.Replace('\\', '/');

        // 盘符或 UNC：C: / //server
        if (normalized.Length >= 2 && normalized[1] == ':') return false;
        if (normalized.StartsWith("//", StringComparison.Ordinal)) return false;
        if (normalized.StartsWith('/')) return false;

        foreach (var segment in normalized.Split('/'))
        {
            if (segment == "..") return false;
        }

        return true;
    }
}
