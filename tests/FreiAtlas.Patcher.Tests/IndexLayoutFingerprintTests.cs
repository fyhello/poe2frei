using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 索引布局指纹：只在构建戳缺失/不一致时作为兜底判据。
/// 这里锁定「同一客户端连续计算必须稳定」，避免把正常客户端误判成布局变化。
/// </summary>
public sealed class IndexLayoutFingerprintTests
{
    [Theory]
    [InlineData(GameMode.GGPK)]
    [InlineData(GameMode.Bundles2)]
    public void 损坏索引校验失败后不占用文件(GameMode mode)
    {
        string directory = Path.Combine(Path.GetTempPath(), "frei_layout_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "Bundles2"));
        var info = new GameModeInfo { IsValid = true, GameDirectory = directory, Mode = mode };
        string path = mode == GameMode.GGPK
            ? Path.Combine(directory, "Content.ggpk")
            : Path.Combine(directory, "Bundles2", "_.index.bin");
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            Assert.Null(IndexLayoutFingerprint.ComputeCurrent(info));
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(4, exclusive.Length);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private const string RealGameDir =
        @"E:\PSAutoRecover\ui\rood\Grinding Gear Games\Path of Exile 2";

    [Fact]
    [Trait("Category", "RequiresGameClient")]
    public void 当前客户端布局指纹稳定且非空()
    {
        if (!Directory.Exists(RealGameDir)) return;

        var info = new GameModeInfo
        {
            IsValid = true,
            GameDirectory = RealGameDir,
            Mode = GameMode.GGPK
        };

        string? first = IndexLayoutFingerprint.ComputeCurrent(info);
        string? second = IndexLayoutFingerprint.ComputeCurrent(info);

        Assert.False(string.IsNullOrEmpty(first));
        Assert.Equal(first, second);
    }
}
