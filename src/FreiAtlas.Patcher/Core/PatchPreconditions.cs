namespace FreiAtlas.Patcher.Core;

/// <summary>
/// 补丁执行前的前置条件判定。
/// </summary>
public static class PatchPreconditions
{
    /// <summary>
    /// 物价数据允许的最大陈旧时间。
    ///
    /// 过期价格表会把上一轮赛季的价格写进客户端，比不注入更有害，
    /// 因此按硬性上限判定，由调用方决定中止整批任务并提示用户刷新。
    /// </summary>
    public static readonly TimeSpan PriceDataMaxAge = TimeSpan.FromHours(24);

    /// <summary>
    /// 判断物价快照是否仍可用于注入。
    /// </summary>
    /// <param name="fetchedAtUtc">快照拉取时间（UTC）。</param>
    /// <param name="nowUtc">当前时间（UTC）。</param>
    /// <param name="maxAge">允许的最大陈旧时间。</param>
    /// <returns>可用于注入返回 true。</returns>
    public static bool IsPriceDataFresh(DateTimeOffset fetchedAtUtc, DateTimeOffset nowUtc, TimeSpan maxAge)
    {
        // MinValue 表示快照没有记录拉取时间，无法证明它新鲜。
        if (fetchedAtUtc == DateTimeOffset.MinValue) return false;

        var age = nowUtc - fetchedAtUtc;

        // 时间戳落在未来通常是系统时钟偏差，按「刚拉取」处理，
        // 否则会因时钟问题误拦正常数据。
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;

        return age <= maxAge;
    }
}
