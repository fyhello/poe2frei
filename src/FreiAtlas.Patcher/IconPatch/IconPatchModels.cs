namespace FreiAtlas.Patcher.IconPatch;

/// <summary>
/// 一条图标替换条目。
///
/// 路径来自内置的图标包索引，是唯一权威来源——它必须与游戏客户端里的虚拟路径
/// 完全一致，否则写不进去。可用性在扫描阶段通过客户端索引校验后确定。
/// </summary>
public sealed class IconPatchEntry
{
    /// <summary>游戏内虚拟路径，例如 art/2ditems/currency/abyss/ancientclavicle.dds。</summary>
    public required string VirtualPath { get; init; }

    public required string FileName { get; init; }

    /// <summary>通货 / 血脉宝石 / 终局地图 / 维金 / 其他。</summary>
    public required string Category { get; init; }

    /// <summary>分类序号，用于前端标签页筛选。</summary>
    public required int GroupIndex { get; init; }

    /// <summary>当前客户端中该文件的原始大小。0 表示客户端不存在该文件。</summary>
    public int OriginalSize { get; set; }

    /// <summary>替换文件大小。</summary>
    public required int ReplacementSize { get; init; }

    /// <summary>缩略图在雪碧图中的像素坐标。</summary>
    public required int SpriteX { get; init; }

    public required int SpriteY { get; init; }

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// 图标扫描结果。
///
/// <see cref="SkippedCount"/> 大于 0 说明内置图标包里有当前客户端不存在的路径，
/// 通常是游戏版本差异所致，这些条目已被剔除，不会参与写入。
/// </summary>
public sealed record IconPatchScanResult(
    IReadOnlyList<IconPatchEntry> Entries,
    int SkippedCount,
    string? Error)
{
    public static IconPatchScanResult Failure(string error) => new([], 0, error);
}

/// <summary>雪碧图元数据，随扫描结果一起下发，供前端做 CSS 定位。</summary>
public sealed record IconSpriteInfo(int CellSize, int Columns);

/// <summary>
/// 图标还原结果。
///
/// <see cref="RestoredCount"/> 为 0 且 <see cref="Message"/> 提到「没有可还原的备份」时，
/// 说明客户端图标仍处于被替换状态但没有备份可用，此时只能引导用户走全量还原底包。
/// </summary>
public sealed record IconPatchRestoreResult(
    bool Success,
    int RestoredCount,
    string Message)
{
    public static IconPatchRestoreResult Ok(int restoredCount, string message)
        => new(true, restoredCount, message);

    public static IconPatchRestoreResult Failure(string message)
        => new(false, 0, message);

    public static IconPatchRestoreResult NoBackup(string message)
        => new(false, 0, message);
}
