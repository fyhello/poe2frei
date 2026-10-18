using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace FreiAtlas.Patcher;

public sealed record FontItem(string DisplayName, string TypefaceName);

public sealed class FontEntryInfo
{
    public string Id { get; set; } = string.Empty;
    public string Typeface { get; set; } = string.Empty;
    public int Size { get; set; }
}

public sealed class FontSettingsInfo
{
    public string FilePath { get; set; } = string.Empty;
    public string CurrentTypeface { get; set; } = string.Empty;
    public List<FontEntryInfo> Fonts { get; set; } = [];
    public List<string> FallbackFontsCJK { get; set; } = [];
    public List<string> FallbackFontsAny { get; set; } = [];
}

public sealed class FontPatchService
{
    private static IReadOnlyList<FontItem>? _cachedInstalledFonts;
    private static readonly object _fontsLock = new();

    public static IReadOnlyList<FontItem> GetInstalledFonts()
    {
        if (_cachedInstalledFonts != null)
        {
            return _cachedInstalledFonts;
        }

        lock (_fontsLock)
        {
            if (_cachedInstalledFonts != null)
            {
                return _cachedInstalledFonts;
            }

            var list = new List<FontItem>();
            try
            {
                var query = Fonts.SystemFontFamilies
                    .Select(ff =>
                    {
                        var enName = ff.FamilyNames.Values.FirstOrDefault();
                        if (string.IsNullOrWhiteSpace(enName))
                        {
                            enName = ff.Source;
                        }
                        var zhName = GetChineseFamilyName(ff);
                        var displayName = string.IsNullOrEmpty(zhName) ? enName : $"{zhName} ({enName})";
                        return new FontItem(displayName, enName);
                    })
                    .Where(f => !string.IsNullOrWhiteSpace(f.TypefaceName))
                    .DistinctBy(f => f.TypefaceName, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(f => f.DisplayName, StringComparer.CurrentCultureIgnoreCase);

                list.AddRange(query);

                if (!list.Any(f => string.Equals(f.TypefaceName, "Fontin", StringComparison.OrdinalIgnoreCase)))
                {
                    list.Insert(0, new FontItem("Fontin (游戏原版)", "Fontin"));
                }
            }
            catch
            {
                list.Add(new FontItem("Fontin (游戏原版)", "Fontin"));
                list.Add(new FontItem("微软雅黑 (Microsoft YaHei)", "Microsoft YaHei"));
                list.Add(new FontItem("黑体 (SimHei)", "SimHei"));
            }

            _cachedInstalledFonts = list.AsReadOnly();
            return _cachedInstalledFonts;
        }
    }

    private static string? GetChineseFamilyName(FontFamily family)
    {
        string? text = null;
        string? text2 = null;
        foreach (var pair in family.FamilyNames)
        {
            var text4 = pair.Value;
            if (string.IsNullOrWhiteSpace(text4))
            {
                continue;
            }
            string text5;
            try
            {
                text5 = pair.Key.IetfLanguageTag.ToLowerInvariant();
            }
            catch
            {
                continue;
            }
            if (text5.StartsWith("zh", StringComparison.Ordinal))
            {
                text2 ??= text4;
                if ((text5.StartsWith("zh-hans", StringComparison.Ordinal) ||
                     text5.StartsWith("zh-cn", StringComparison.Ordinal) ||
                     text5.StartsWith("zh-sg", StringComparison.Ordinal)) && text == null)
                {
                    text = text4;
                }
            }
        }
        return text ?? text2;
    }

    private static readonly string[] UiSettingPaths =
    [
        "metadata/ui/uisettings.xml",
        "metadata/ui/uisettings.console.xml",
        "metadata/ui/uisettings.tencent.xml",
        "metadata/ui/uisettings.tencent.console.xml",
        "metadata/ui/uisettings.traditional chinese.xml",
        "metadata/ui/uisettings_controller.xml",
        "metadata/ui/uisettings_controller_steamdeck.xml",
        "metadata/ui/uisettings_steamdeck.xml"
    ];

    public static readonly HashSet<string> LanguageOverridePaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "metadata/ui/uisettings.traditional chinese.xml",
        "metadata/ui/uisettings.tencent.xml",
        "metadata/ui/uisettings.korean.xml"
    };

    public static readonly Dictionary<string, int> MatureFontBaselineMap = new(StringComparer.Ordinal)
    {
        // 通用基础核心 (成熟中文字体最佳比例)
        ["Normal"] = 28,
        ["Small"] = 22,
        ["Large"] = 45,
        ["SmallNormal"] = 29,
        ["LargeNormal"] = 29,
        ["VerySmall"] = 24,
        ["NormalSC"] = 33,
        ["SmallSC"] = 28,
        ["LargeSC"] = 45,
        ["SmallNormalSC"] = 29,
        ["LargeNormalSC"] = 39,
        ["VerySmallSC"] = 24,
        ["SharpSmall"] = 26,
        ["SharpNormal"] = 33,

        // 手写体 / Quikhand 系列
        ["QuikhandSmall"] = 23,
        ["QuikhandSmallNormal"] = 26,
        ["QuikhandNormal"] = 30,
        ["QuikhandLargeNormal"] = 36,
        ["QuikhandLarge"] = 42,

        // 掉落物 / 物品堆叠 / 标签系列
        ["ItemPopupTitle"] = 39,
        ["ItemStackCountLabel"] = 29,
        ["ItemStackCountLabelSmall"] = 24,
        ["ItemStackCountLabelQuad"] = 18,
        ["ItemStackLevelLabel"] = 22,
        ["ItemStackLevelLabelQuad"] = 17,
        ["WorldItemDescriptionLabel"] = 33,
        ["UnifiedCraftingOptionName"] = 30,

        // 窗口标题与大字顶栏 (成熟补丁深度防爆框微调)
        ["Title"] = 39,
        ["CompletionNotificationTitle"] = 50,
        ["CompletionNotificationDescription"] = 64,
        ["ExpeditionWindowSectionHeader"] = 40,
        ["ExpeditionMapPinNameLabel"] = 34,
        ["ExpeditionMapPinSubNameLabel"] = 26,
        ["KiracMissionNPCHeader"] = 40,
        ["LargeOP"] = 46,
        ["LargeMediumOP"] = 41,
        ["LargeNormalOP"] = 37,
        ["NormalOP"] = 32,
        ["SmallNormalOP"] = 30,
        ["ExtraSmallOP"] = 55,
        ["ExtraNormalOP"] = 64,
        ["ExtraLargeOP"] = 73,

        // 微小角标、状态与计时器 (防重叠)
        ["TinySmallSC"] = 18,
        ["TinyNormalSC"] = 23,
        ["TinySC"] = 23,
        ["TinySmall"] = 23,
        ["Tiny"] = 23,
        ["PriceLabel"] = 28,
        ["PurchaseButtonLabel"] = 28,
        ["RewardClaimedLabel"] = 24,
        ["UltimatumTimerLabel"] = 30,
        ["UltimatumVoteCountLabel"] = 28,
        ["UltimatumProgressBarLabel"] = 33,
        ["TrackNameTitle"] = 33,
        ["ProgressBarTitle"] = 33,
        ["WarpToVaultButtonLabel"] = 33,
        ["Reminder"] = 32,
        ["Marketing"] = 39
    };

    public static readonly Dictionary<string, int> FontPresetSizes = MatureFontBaselineMap;

    public enum FontSemanticCategory
    {
        GeneralText,
        DropItem,
        WindowTitle,
        TinyBadge
    }

    public static FontSemanticCategory GetFontCategory(string fontId)
    {
        if (string.IsNullOrEmpty(fontId)) return FontSemanticCategory.GeneralText;

        // 1. 掉落物与物品类
        if (fontId.StartsWith("ItemStack", StringComparison.OrdinalIgnoreCase) ||
            fontId.StartsWith("WorldItem", StringComparison.OrdinalIgnoreCase) ||
            fontId.Equals("ItemPopupTitle", StringComparison.OrdinalIgnoreCase))
        {
            return FontSemanticCategory.DropItem;
        }

        // 2. 窗口标题与大字
        if (fontId.Contains("Title", StringComparison.OrdinalIgnoreCase) ||
            fontId.Contains("Header", StringComparison.OrdinalIgnoreCase) ||
            fontId.EndsWith("OP", StringComparison.OrdinalIgnoreCase) ||
            fontId.Contains("Notification", StringComparison.OrdinalIgnoreCase))
        {
            return FontSemanticCategory.WindowTitle;
        }

        // 3. 微小角标与数值
        if (fontId.StartsWith("Tiny", StringComparison.OrdinalIgnoreCase) ||
            fontId.Contains("Price", StringComparison.OrdinalIgnoreCase) ||
            fontId.Contains("Timer", StringComparison.OrdinalIgnoreCase) ||
            fontId.Contains("Vote", StringComparison.OrdinalIgnoreCase) ||
            fontId.Contains("Reward", StringComparison.OrdinalIgnoreCase))
        {
            return FontSemanticCategory.TinyBadge;
        }

        return FontSemanticCategory.GeneralText;
    }

    private readonly PatchEngine _engine;

    public FontPatchService(PatchEngine engine)
    {
        _engine = engine;
    }

    public FontSettingsInfo? ScanFontSettings(GameModeInfo info)
    {
        foreach (var path in UiSettingPaths)
        {
            var bytes = _engine.ReadGameFile(info, path, out var actualPath);
            if (bytes == null || bytes.Length == 0) continue;

            var res = ParseFontSettings(bytes, actualPath);
            if (res != null && res.Fonts.Count > 0)
            {
                return res;
            }
        }
        return null;
    }

    public static FontSettingsInfo? ParseFontSettings(byte[] bytes, string actualPath)
    {
        string content = Encoding.Unicode.GetString(bytes);
        var lines = content.Split(["\r\n", "\n"], StringSplitOptions.None);
        var res = new FontSettingsInfo { FilePath = actualPath };

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith("<Font") && line.Contains("typeface"))
            {
                var idMatch = Regex.Match(line, "id=\"([^\"]+)\"");
                var typeMatch = Regex.Match(line, "typeface=\"([^\"]+)\"");
                var sizeMatch = Regex.Match(line, "size=\"(\\d+)\"");

                var entry = new FontEntryInfo
                {
                    Id = idMatch.Success ? idMatch.Groups[1].Value : "",
                    Typeface = typeMatch.Success ? typeMatch.Groups[1].Value : "",
                    Size = sizeMatch.Success && int.TryParse(sizeMatch.Groups[1].Value, out var s) ? s : 0
                };
                res.Fonts.Add(entry);
                if (string.IsNullOrEmpty(res.CurrentTypeface) && !string.IsNullOrEmpty(entry.Typeface))
                {
                    res.CurrentTypeface = entry.Typeface;
                }
            }
            else if (line.StartsWith("<FallbackFont"))
            {
                var fontsMatch = Regex.Match(line, "fonts=\"([^\"]+)\"");
                if (fontsMatch.Success)
                {
                    var fontList = fontsMatch.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    if (line.Contains("ranges=\"CJK\""))
                    {
                        res.FallbackFontsCJK = fontList;
                    }
                    else if (line.Contains("ranges=\"Any\""))
                    {
                        res.FallbackFontsAny = fontList;
                    }
                }
            }
        }

        return res;
    }

    public bool ApplyFont(
        GameModeInfo info,
        string fontFamily,
        int fontSizeDelta,
        bool applyPresetScales = false,
        bool isBold = false,
        int dropItemSizeDelta = 0,
        int generalTextSizeDelta = 0,
        int titleSizeDelta = 0,
        int tinyBadgeSizeDelta = 0,
        Action<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(fontFamily) && fontSizeDelta == 0 && !applyPresetScales && !isBold &&
            dropItemSizeDelta == 0 && generalTextSizeDelta == 0 && titleSizeDelta == 0 && tinyBadgeSizeDelta == 0)
        {
            progress?.Invoke("未指定字体、字号偏移或加粗选项");
            return false;
        }

        progress?.Invoke("正在读取游戏 UI 设置文件...");
        var filesToReplace = new List<(string VirtualPath, byte[] Data)>();

        const string proxyXml = "<?xml version=\"1.0\"?>\r\n<Props id=\"PathOfExile\" inherits=\"Metadata/UI/UISettings.xml\">\r\n</Props>\r\n";
        byte[] proxyBytes = UiColorService.EncodeUtf16Bom(proxyXml);

        foreach (var path in UiSettingPaths)
        {
            var bytes = _engine.ReadGameFile(info, path, out var actualPath);
            if (bytes == null || bytes.Length == 0) continue;

            // 若属于繁体或国服覆盖文件，写入标准 196 字节空继承代理，彻底杜绝语言覆盖冲突
            if (LanguageOverridePaths.Contains(path))
            {
                progress?.Invoke($"正在协同空继承代理 {actualPath} (清除语言覆盖冲突)...");
                filesToReplace.Add((actualPath, proxyBytes));
                continue;
            }

            progress?.Invoke($"正在处理 {actualPath}...");
            var modified = ProcessUiSettingsCore(
                bytes,
                fontFamily,
                fontSizeDelta,
                applyPresetScales ? MatureFontBaselineMap : null,
                isBold,
                dropItemSizeDelta,
                generalTextSizeDelta,
                titleSizeDelta,
                tinyBadgeSizeDelta);

            if (modified != null)
            {
                filesToReplace.Add((actualPath, modified));
            }
        }

        if (filesToReplace.Count == 0)
        {
            progress?.Invoke("未在客户端中找到匹配的字体配置文件（metadata/ui/uisettings*.xml）");
            return false;
        }

        progress?.Invoke($"正在写入 {filesToReplace.Count} 个修改后的 UI 设置文件...");
        var success = _engine.WriteGameFiles(info, filesToReplace);
        progress?.Invoke(success ? $"字体补丁应用成功！（已修改 {filesToReplace.Count} 个文件）" : "写入补丁失败。");
        return success;
    }

    public static byte[]? ProcessUiSettings(
        byte[] bytes,
        string fontFamily,
        int fontSizeDelta,
        Dictionary<string, int>? presetSizes = null,
        bool isBold = false,
        int dropItemSizeDelta = 0,
        int generalTextSizeDelta = 0,
        int titleSizeDelta = 0,
        int tinyBadgeSizeDelta = 0)
    {
        return ProcessUiSettingsCore(
            bytes,
            fontFamily,
            fontSizeDelta,
            presetSizes,
            isBold,
            dropItemSizeDelta,
            generalTextSizeDelta,
            titleSizeDelta,
            tinyBadgeSizeDelta);
    }

    /// <summary>
    /// 处理 UI 字体、成熟基准字号、细分分类偏移量与原生加粗 (weight="700")
    /// </summary>
    public static byte[]? ProcessUiSettingsCore(
        byte[] bytes,
        string fontFamily,
        int fontSizeDelta,
        Dictionary<string, int>? presetSizes,
        bool isBold = false,
        int dropItemSizeDelta = 0,
        int generalTextSizeDelta = 0,
        int titleSizeDelta = 0,
        int tinyBadgeSizeDelta = 0)
    {
        Encoding unicode = Encoding.Unicode;
        string[] array = unicode.GetString(bytes).Split(["\r\n", "\n"], StringSplitOptions.None);
        bool flag = false;

        for (int i = 0; i < array.Length; i++)
        {
            string text = array[i];
            string text2 = text.TrimStart();

            if (text2.StartsWith("<Font ") || text2.StartsWith("<Font\t"))
            {
                Match match = Regex.Match(text, "id=\"([^\"]+)\"");
                string fontId = match.Success ? match.Groups[1].Value : "";

                // 1. 字体名称替换（若节点包含 typeface 属性）
                if (!string.IsNullOrWhiteSpace(fontFamily) && text.Contains("typeface=\""))
                {
                    string text4 = Regex.Replace(text, "typeface=\".*?\"", "typeface=\"" + fontFamily + "\"");
                    if (text4 != text)
                    {
                        text = text4;
                        flag = true;
                    }
                }

                // 2. 字号调整（支持成熟基准尺寸 + 全局偏移 + 细分分类偏移）
                Match match2 = Regex.Match(text, "size=\"(\\d+)\"");
                if (match2.Success && int.TryParse(match2.Groups[1].Value, out var curSize))
                {
                    int baseSize = curSize;
                    if (presetSizes != null && !string.IsNullOrEmpty(fontId) && presetSizes.TryGetValue(fontId, out var targetSize))
                    {
                        baseSize = targetSize;
                    }

                    var category = GetFontCategory(fontId);
                    int categoryDelta = category switch
                    {
                        FontSemanticCategory.DropItem => dropItemSizeDelta,
                        FontSemanticCategory.WindowTitle => titleSizeDelta,
                        FontSemanticCategory.TinyBadge => tinyBadgeSizeDelta,
                        _ => generalTextSizeDelta
                    };

                    int newSize = Math.Max(8, baseSize + fontSizeDelta + categoryDelta);
                    string text5 = Regex.Replace(text, "size=\"\\d+\"", $"size=\"{newSize}\"");
                    if (text5 != text)
                    {
                        text = text5;
                        flag = true;
                    }
                }

                // 3. 加粗注入（官方属性 weight="700" 与 bold="true"）
                if (isBold)
                {
                    if (text.Contains("weight=\""))
                    {
                        string textW = Regex.Replace(text, "weight=\"[^\"]*\"", "weight=\"700\"");
                        if (textW != text) { text = textW; flag = true; }
                    }
                    else
                    {
                        string textW = Regex.Replace(text, "(<Font\\b[^>]*?)(/?>)", "$1 weight=\"700\"$2");
                        if (textW != text) { text = textW; flag = true; }
                    }

                    if (text.Contains("bold=\""))
                    {
                        string textB = Regex.Replace(text, "bold=\"[^\"]*\"", "bold=\"true\"");
                        if (textB != text) { text = textB; flag = true; }
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(fontFamily) && text2.StartsWith("<FallbackFont"))
            {
                Match fontsMatch = Regex.Match(text, "fonts=\"([^\"]*)\"");
                if (fontsMatch.Success)
                {
                    string origFonts = fontsMatch.Groups[1].Value;
                    var parts = origFonts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    parts.RemoveAll(p => string.Equals(p, fontFamily, StringComparison.OrdinalIgnoreCase));
                    parts.Insert(0, fontFamily);
                    string newFontsAttr = $"fonts=\"{string.Join(",", parts)}\"";
                    string newText = text.Substring(0, fontsMatch.Index) + newFontsAttr + text.Substring(fontsMatch.Index + fontsMatch.Length);
                    if (newText != text)
                    {
                        text = newText;
                        flag = true;
                    }
                }
            }

            array[i] = text;
        }

        if (!flag)
        {
            return null;
        }

        return UiColorService.EncodeUtf16Bom(string.Join("\r\n", array));
    }
}
