using FreiAtlas.Patcher.IconPatch;
using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 图标资源的公开契约。
///
/// 图标路径是唯一权威来源，必须与游戏客户端里的虚拟路径完全一致；分类规则
/// 与 POE2小助手保持一致，避免同一图标在两边落到不同标签页。
/// </summary>
public sealed class IconPatchAssetTests
{
    private static GameModeInfo InvalidClient() => new()
    {
        GameDirectory = @"Z:\does-not-exist",
        Mode = GameMode.GGPK,
        IsValid = false,
        ErrorMessage = "未检测到游戏客户端"
    };

    [Fact]
    public void 内置图标资源可用()
    {
        Assert.True(IconPatchService.IsAvailable, IconPatchService.LoadError);
    }

    [Fact]
    public void 雪碧图元数据有效()
    {
        var sprite = IconPatchService.SpriteInfo;

        Assert.Equal(48, sprite.CellSize);
        Assert.Equal(32, sprite.Columns);
    }

    [Fact]
    public void 雪碧图随Web目录分发且为WebP容器()
    {
        // 雪碧图源文件只保留一份：构建时复制到 Settings 的 Web 目录供 patcher.js 引用，
        // 不再同时嵌入 Patcher.dll，避免同一份 1.2 MB 资源随包两次。
        string sheetPath = Path.Combine(
            FindRepositoryRoot(), "src", "FreiAtlas.Patcher", "Resources", "iconpatch", "icons.sheet.webp");

        Assert.True(File.Exists(sheetPath), $"雪碧图缺失：{sheetPath}");

        byte[] sheet = File.ReadAllBytes(sheetPath);
        Assert.True(sheet.Length > 0);
        Assert.Equal((byte)'R', sheet[0]);
        Assert.Equal((byte)'I', sheet[1]);
        Assert.Equal((byte)'F', sheet[2]);
        Assert.Equal((byte)'F', sheet[3]);
        Assert.Equal((byte)'W', sheet[8]);
        Assert.Equal((byte)'E', sheet[9]);
        Assert.Equal((byte)'B', sheet[10]);
        Assert.Equal((byte)'P', sheet[11]);
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FreiAtlas.Settings", "Web")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("未能从测试输出目录向上找到仓库根目录。");
    }

    [Fact]
    public void 扫描无效客户端时返回错误而不是抛出异常()
    {
        var result = new IconPatchService().Scan(InvalidClient());

        Assert.NotNull(result.Error);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void 写入空列表被拒绝()
    {
        bool ok = new IconPatchService().Apply(InvalidClient(), [], previousPaths: null, progress: null, out var message);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    [Fact]
    public void 客户端无效时写入被拒绝且不抛异常()
    {
        bool ok = new IconPatchService().Apply(
            InvalidClient(),
            ["art/2ditems/currency/abyss/ancientclavicle.dds"],
            previousPaths: null,
            progress: null,
            out var message);

        Assert.False(ok);
        Assert.Contains("未检测到", message);
    }
}