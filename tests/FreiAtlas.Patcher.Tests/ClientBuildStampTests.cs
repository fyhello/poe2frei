using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 客户端构建戳：用 Authenticode 证书表判断「是不是同一个客户端构建」，
/// 不再依赖会滚动、会缺失的日志版本号。
/// </summary>
public sealed class ClientBuildStampTests
{
    private const string RealGameDir =
        @"E:\PSAutoRecover\ui\rood\Grinding Gear Games\Path of Exile 2";

    [Fact]
    [Trait("Category", "RequiresGameClient")]
    public void 真实客户端_证书表构建戳稳定且可解析签名()
    {
        if (!Directory.Exists(RealGameDir)) return;

        var info = new GameModeInfo
        {
            IsValid = true,
            GameDirectory = RealGameDir,
            Mode = GameMode.GGPK
        };

        var first = ClientBuildStamp.Compute(info);
        var second = ClientBuildStamp.Compute(info);

        Assert.StartsWith("sig:", first.Stamp);
        Assert.Equal(first.Stamp, second.Stamp);
        Assert.NotNull(first.SignedAt);
        Assert.Contains("Grinding Gear", first.Signer ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 无签名客户端_回退到根文件列表且能感知变化()
    {
        string dir = Path.Combine(Path.GetTempPath(), "frei_stamp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            string exe = Path.Combine(dir, "PathOfExile.exe");
            File.WriteAllBytes(exe, [1, 2, 3]);

            var info = new GameModeInfo { IsValid = true, GameDirectory = dir, Mode = GameMode.GGPK };
            var first = ClientBuildStamp.Compute(info);
            Assert.StartsWith("files:", first.Stamp);

            File.WriteAllBytes(exe, [1, 2, 3, 4]);
            File.SetLastWriteTimeUtc(exe, DateTime.UtcNow.AddMinutes(1));

            var second = ClientBuildStamp.Compute(info);
            Assert.NotEqual(first.Stamp, second.Stamp);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void 非PE文件不会被当成签名()
    {
        string path = Path.Combine(Path.GetTempPath(), "frei_notpe_" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(path, "not a portable executable");

        try
        {
            Assert.Null(ClientBuildStamp.TryReadCertificateTable(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
