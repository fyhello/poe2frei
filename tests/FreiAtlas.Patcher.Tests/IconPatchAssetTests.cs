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
    public void 雪碧图字节可读取且为WebP容器()
    {
        var sheet = IconPatchService.SpriteSheetBytes;

        Assert.NotNull(sheet);
        Assert.True(sheet!.Length > 0);
        Assert.Equal((byte)'R', sheet[0]);
        Assert.Equal((byte)'I', sheet[1]);
        Assert.Equal((byte)'F', sheet[2]);
        Assert.Equal((byte)'F', sheet[3]);
        Assert.Equal((byte)'W', sheet[8]);
        Assert.Equal((byte)'E', sheet[9]);
        Assert.Equal((byte)'B', sheet[10]);
        Assert.Equal((byte)'P', sheet[11]);
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
        bool ok = new IconPatchService().Apply(InvalidClient(), [], null, out var message);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    [Fact]
    public void 客户端无效时写入被拒绝且不抛异常()
    {
        bool ok = new IconPatchService().Apply(
            InvalidClient(),
            ["art/2ditems/currency/abyss/ancientclavicle.dds"],
            null,
            out var message);

        Assert.False(ok);
        Assert.Contains("未检测到", message);
    }
}