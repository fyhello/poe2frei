using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 客户端语言判定。
///
/// 所有「该输出简体还是繁体」的判断都必须走 <see cref="GameModeInfo.IsSimplifiedChinese"/>。
/// 历史问题：用 IsChina（服务器归属）代替语言判断，导致国际服客户端打上汉化补丁后
/// 内容已是简体，却仍注入繁体前缀。
/// </summary>
public sealed class ClientLanguageTests
{
    [Theory]
    [InlineData("zh-CN")]
    [InlineData("zh-cn")]
    [InlineData("zh-Hans")]
    [InlineData("zh-hans")]
    [InlineData("sc")]
    [InlineData("SC")]
    public void 简体语言代码判定为简体(string code)
    {
        var info = new GameModeInfo { LanguageCode = code, IsChina = false };
        Assert.True(info.IsSimplifiedChinese);
    }

    [Theory]
    [InlineData("zh-TW")]
    [InlineData("zh-tw")]
    [InlineData("zh-Hant")]
    [InlineData("en")]
    [InlineData("ko")]
    public void 非简体语言代码判定为繁体(string code)
    {
        var info = new GameModeInfo { LanguageCode = code, IsChina = false };
        Assert.False(info.IsSimplifiedChinese);
    }

    [Fact]
    public void 国服无论配置如何都按简体处理()
    {
        // 国服客户端即使配置文件读不到或写成繁体，界面内容仍是简体
        var info = new GameModeInfo { LanguageCode = "zh-TW", IsChina = true };
        Assert.True(info.IsSimplifiedChinese);
    }

    [Fact]
    public void 国际服打汉化后按配置判定为简体()
    {
        // 回归：这正是用户遇到问题的场景——国际服 + 汉化，IsChina 仍为 false，
        // 但语言已切换为简体，必须输出简体前缀。
        var info = new GameModeInfo { LanguageCode = "zh-CN", IsChina = false };
        Assert.False(info.IsChina);
        Assert.True(info.IsSimplifiedChinese);
    }
}
