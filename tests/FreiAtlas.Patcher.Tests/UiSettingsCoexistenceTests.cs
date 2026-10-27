using System.Text;
using FreiAtlas.Patcher.Modules.Font;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 字体补丁与 UI 染色补丁对 <c>metadata/ui/uisettings*.xml</c> 的协同测试。
///
/// 背景：字体补丁曾把语言覆盖文件（繁中 / 国服 / 韩文）写成 196 字节空继承代理，
/// 理由是「杜绝语言覆盖冲突」。但那会连带清掉覆盖文件自带的全部内容：
///   · uisettings.traditional chinese.xml —— 64 个 Font + WordsPerMinute 等数值
///   · uisettings.tencent.xml —— 3 个 Colour + 70 个 Font + 4 个 InstalledFont + 2 个 FallbackFont
/// 而这些是游戏为该语言专门配置的字体与排版。
///
/// 同时空代理对 UI 染色也没有帮助：染色改的是主文件 uisettings.xml 的 MarkupColours 段，
/// 覆盖文件通过 inherits 继承主文件，本来就不冲突。
///
/// 本测试锁定：字体处理语言覆盖文件时保留非字体内容，且与染色互不破坏。
/// </summary>
public class UiSettingsCoexistenceTests
{
    /// <summary>模拟繁中覆盖文件：含 Number 与 Font，无 Colour。</summary>
    private const string TraditionalChineseSample = """
        <?xml version="1.0"?>

        <Props id="PathOfExile" inherits="Metadata/UI/UISettings.xml">

        	<Number id="WordsPerMinute"                         value="25"/>
        	<Number id="CharactersPerMinute"                    value="250"/>

        	<Font id="Normal"        typeface="Noto Sans CJK TC"           size="38"/>
        	<Font id="Small"         typeface="Noto Sans CJK TC"           size="35"/>
        	<Font id="Large"         typeface="Noto Sans CJK TC"           size="54"/>
        </Props>
        """;

    /// <summary>模拟国服覆盖文件：含 Colour、InstalledFont、FallbackFont、Font。</summary>
    private const string TencentSample = """
        <?xml version="1.0"?>
        <Props id="PathOfExile" inherits="Metadata/UI/UISettings.xml">
        	<InstalledFont id="FZFWD" typeface="FZFWZhuZiAOldMinchoD_POE2 D" value="Art/2DArt/Fonts/FZFWZhuZiAOldMincho-D.ttf"/>
        	<FallbackFont id="CJK" ranges="CJK" fonts="FZFWZhuZiAOldMinchoD_POE2 D,Microsoft YaHei"/>
        	<Font id="Normal"        typeface="FZFWZhuZiAOldMinchoD_POE2 D"           size="36"/>
        	<Font id="Small"         typeface="FZFWZhuZiAOldMinchoD_POE2 D"           size="34"/>
        	<Colour id="fire" value="150,0,0"/>
        </Props>
        """;

    /// <summary>
    /// 模拟主文件：MarkupColours 是独立 Props 段（染色只在这个段里改色）。
    /// </summary>
    private const string MainSample = """
        <?xml version="1.0"?>
        <Props id="PathOfExile">
        	<Fonts>
        		<Font id="Normal"        typeface="Fontin"           size="32"/>
        		<Font id="Small"         typeface="Fontin"           size="28"/>
        	</Fonts>
        </Props>
        <Props id="MarkupColours">
        	<Colour id="default"              value="127,127,127"/>
        	<Colour id="fire"                 value="150,0,0"/>
        	<Colour id="cold"                 value="54,100,146"/>
        </Props>
        """;

    private static byte[] Encode(string text) => Encoding.Unicode.GetBytes(text);

    private static string Decode(byte[] bytes) => Encoding.Unicode.GetString(bytes);

    private static int CountOf(string text, string pattern)
        => System.Text.RegularExpressions.Regex.Matches(text, pattern).Count;

    [Fact]
    public void ProcessUiSettingsCore_PreservesNonFontContent_TraditionalChinese()
    {
        byte[] result = FontPatchService.ProcessUiSettingsCore(
            Encode(TraditionalChineseSample), "Microsoft YaHei", 2, null)!;

        string text = Decode(result);

        // 非字体内容必须原样保留
        Assert.Equal(
            CountOf(TraditionalChineseSample, @"<Number\s+id="),
            CountOf(text, @"<Number\s+id="));
        Assert.Contains(@"<Number id=""WordsPerMinute""", text);
        Assert.Contains(@"<Number id=""CharactersPerMinute""", text);

        // 字体数量不变，但字体族已替换
        Assert.Equal(
            CountOf(TraditionalChineseSample, @"<Font\s+id="),
            CountOf(text, @"<Font\s+id="));
        Assert.Contains("Microsoft YaHei", text);
        Assert.DoesNotContain("Noto Sans CJK TC", text);
    }

    [Fact]
    public void ProcessUiSettingsCore_PreservesNonFontContent_Tencent()
    {
        byte[] result = FontPatchService.ProcessUiSettingsCore(
            Encode(TencentSample), "Microsoft YaHei", 2, null)!;

        string text = Decode(result);

        // Colour / InstalledFont / FallbackFont 一个都不能少
        Assert.Equal(
            CountOf(TencentSample, @"<Colour\s+id="),
            CountOf(text, @"<Colour\s+id="));
        Assert.Equal(
            CountOf(TencentSample, @"<InstalledFont\s+id="),
            CountOf(text, @"<InstalledFont\s+id="));
        Assert.Equal(
            CountOf(TencentSample, @"<FallbackFont\s+id="),
            CountOf(text, @"<FallbackFont\s+id="));

        Assert.Contains(@"id=""FZFWD""", text);
        Assert.Contains(@"id=""CJK""", text);
        Assert.Contains("Microsoft YaHei", text);
    }

    /// <summary>
    /// 字体处理不应改动 MarkupColours 段（染色负责的区域）。
    /// </summary>
    [Fact]
    public void ProcessUiSettingsCore_DoesNotTouchMarkupColours()
    {
        byte[] result = FontPatchService.ProcessUiSettingsCore(
            Encode(MainSample), "Microsoft YaHei", 2, null)!;

        string text = Decode(result);

        Assert.Contains(@"<Colour id=""default""", text);
        Assert.Contains("127,127,127", text);
        Assert.Contains(@"<Colour id=""fire""", text);
        Assert.Contains("150,0,0", text);
        Assert.Contains(@"<Colour id=""cold""", text);
        Assert.Contains("54,100,146", text);
    }

    /// <summary>语言覆盖文件清单应覆盖繁中、国服、韩文三类。</summary>
    [Fact]
    public void LanguageOverridePaths_ContainsExpectedEntries()
    {
        var paths = FontPatchService.LanguageOverridePaths;

        Assert.Contains("metadata/ui/uisettings.traditional chinese.xml", paths);
        Assert.Contains("metadata/ui/uisettings.tencent.xml", paths);
        Assert.Contains("metadata/ui/uisettings.korean.xml", paths);
    }
}
