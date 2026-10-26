using FreiAtlas.Patcher.Core;
using FreiAtlas.Patcher.Modules.Price;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 数据表路径常量与正则超时。
///
/// 语言目录此前以字面量散落在 6 个文件、30 余处，改一处漏一处会导致
/// 简繁客户端读到错误的表。这里锁定常量的取值与互相关系。
/// </summary>
public sealed class PathConstantTests
{
    /// <summary>
    /// 目录常量必须与实际客户端路径完全一致 —— 拼错会静默读不到表。
    /// </summary>
    [Fact]
    public void 语言目录常量取值正确()
    {
        Assert.Equal("data/balance", Poe2LanguageDetector.BalanceRoot);
        Assert.Equal("data/balance/traditional chinese", Poe2LanguageDetector.TraditionalChineseDirectory);
        Assert.Equal("data/balance/simplified chinese", Poe2LanguageDetector.SimplifiedChineseDirectory);
    }

    /// <summary>
    /// 底子文件路径必须由目录常量拼出，避免目录与全路径各改一半。
    /// </summary>
    [Fact]
    public void 底子文件路径与目录常量一致()
    {
        Assert.Equal(
            $"{Poe2LanguageDetector.TraditionalChineseDirectory}/baseitemtypes.datc64",
            Poe2LanguageDetector.TraditionalChineseBaseItemsPath);

        Assert.Equal(
            $"{Poe2LanguageDetector.SimplifiedChineseDirectory}/baseitemtypes.datc64",
            Poe2LanguageDetector.SimplifiedChineseBaseItemsPath);
    }

    /// <summary>
    /// 已知底子文件列表必须包含简繁两种，供语言探测逐个尝试。
    /// </summary>
    [Fact]
    public void 已知底子文件列表包含简繁两种()
    {
        var paths = Poe2LanguageDetector.KnownBaseItemsPaths;

        Assert.Contains(Poe2LanguageDetector.TraditionalChineseBaseItemsPath, paths);
        Assert.Contains(Poe2LanguageDetector.SimplifiedChineseBaseItemsPath, paths);
    }

    /// <summary>
    /// 剥离物价后缀必须幂等：对已剥离的名称再调用一次不改变结果。
    /// 同时验证带缓存的实现没有把不同分隔符的结果串掉。
    /// </summary>
    [Fact]
    public void 剥离物价后缀幂等且分隔符互不干扰()
    {
        const string withPrice = "法师之血 重革腰带 [280D]";

        string once = PricePatchService.StripExistingPriceSuffix(withPrice, " ");
        string twice = PricePatchService.StripExistingPriceSuffix(once, " ");

        Assert.Equal(once, twice);
        Assert.DoesNotContain("[280D]", once);

        // 换一个分隔符仍应正确剥离，说明缓存按 key 隔离
        string withPipe = "法师之血 重革腰带|[280D]";
        string strippedPipe = PricePatchService.StripExistingPriceSuffix(withPipe, "|");
        Assert.DoesNotContain("[280D]", strippedPipe);
    }
}
