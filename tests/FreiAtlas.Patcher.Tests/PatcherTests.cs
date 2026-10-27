using System.Text;
using FreiAtlas.Patcher;
using FreiAtlas.Patcher.Core;
using FreiAtlas.Patcher.Modules.ColorMod;
using FreiAtlas.Patcher.Modules.Price;
using FreiAtlas.Patcher.Modules.Font;
using FreiAtlas.Patcher.Modules.Name;
using FreiAtlas.Patcher.Modules.Rumour;

namespace FreiAtlas.Patcher.Tests;

public class PatcherTests
{
    [Fact]
    public void ProcessUiSettings_ReplacesFontFamilyAndAdjustsSize()
    {
        string sampleXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <UiSettings>
                <Font id="Normal" typeface="Fontin" size="28" />
                <Font id="Small" typeface="Fontin" size="22" />
                <FallbackFont ranges="CJK" fonts="Noto Sans CJK TC" />
                <FallbackFont ranges="Any" fonts="Microsoft Sans Serif" />
            </UiSettings>
            """;

        byte[] inputBytes = Encoding.Unicode.GetBytes(sampleXml);
        byte[]? result = FontPatchService.ProcessUiSettings(
            inputBytes,
            fontFamily: "Microsoft YaHei",
            fontSizeDelta: 4,
            presetSizes: null);

        Assert.NotNull(result);

        // Strip 2-byte BOM
        string modifiedXml = Encoding.Unicode.GetString(result, 2, result.Length - 2);

        Assert.Contains("typeface=\"Microsoft YaHei\"", modifiedXml);
        Assert.Contains("size=\"32\"", modifiedXml); // 28 + 4
        Assert.Contains("size=\"26\"", modifiedXml); // 22 + 4
        Assert.Contains("fonts=\"Microsoft YaHei,Noto Sans CJK TC\"", modifiedXml);
        Assert.Contains("fonts=\"Microsoft YaHei,Microsoft Sans Serif\"", modifiedXml);
    }

    [Fact]
    public void ProcessUiSettings_AdjustsInheritedFontSizesAndInjectsBold()
    {
        string sampleXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <UiSettings>
                <!-- Font Weight Normal 400 -->
                <!-- Font Weight Bold 700 -->
                <Font id="Normal" typeface="Fontin" size="28" weight="400" />
                <Font id="WorldItemLabel" inherits="Normal" size="20" />
                <Font id="MenuTitle" typeface="Fontin" size="30" bold="false" />
                <FallbackFont ranges="CJK" fonts="Noto Sans CJK TC" />
            </UiSettings>
            """;

        byte[] inputBytes = Encoding.Unicode.GetBytes(sampleXml);
        byte[]? result = FontPatchService.ProcessUiSettings(
            inputBytes,
            fontFamily: "Microsoft YaHei",
            fontSizeDelta: 3,
            isBold: true,
            presetSizes: null);

        Assert.NotNull(result);

        string modifiedXml = Encoding.Unicode.GetString(result, 2, result.Length - 2);

        // 1. 验证常规带 typeface 的节点 size 调整: 28 + 3 = 31
        Assert.Contains("size=\"31\"", modifiedXml);

        // 2. 验证纯继承型节点(不带 typeface)的 size 调整: 20 + 3 = 23
        Assert.Contains("size=\"23\"", modifiedXml);

        // 3. 验证 weight 属性注入与更新为 700
        Assert.Contains("weight=\"700\"", modifiedXml);
        Assert.DoesNotContain("weight=\"400\"", modifiedXml);

        // 4. 验证 bold 属性注入与更新为 true
        Assert.Contains("bold=\"true\"", modifiedXml);
        Assert.DoesNotContain("bold=\"false\"", modifiedXml);
    }

    [Fact]
    public void ProcessUiSettings_SupportsCategoryDeltas_And_MatureBaselineMap()
    {
        string sampleXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <UiSettings>
                <Font id="Normal" typeface="Fontin" size="35" />
                <Font id="Title" typeface="OptimusPrincepsSemiBold" size="60" />
                <Font id="WorldItemDescriptionLabel" inherits="NormalSC" size="35" />
                <Font id="TinySmallSC" typeface="Fontin Smallcaps" size="25" />
                <FallbackFont ranges="CJK" fonts="Noto Sans CJK TC" />
            </UiSettings>
            """;

        byte[] inputBytes = Encoding.Unicode.GetBytes(sampleXml);

        // 使用成熟基准比例 + 掉落物 +3, 标题 -2, 微小角标 +1, 全局 +1
        byte[]? result = FontPatchService.ProcessUiSettings(
            inputBytes,
            fontFamily: "Microsoft YaHei",
            fontSizeDelta: 1, // 全局偏移 +1
            presetSizes: FontPatchService.MatureFontBaselineMap,
            isBold: false,
            dropItemSizeDelta: 3,     // 掉落物独立 +3
            generalTextSizeDelta: 0,  // 正文 0
            titleSizeDelta: -2,       // 标题防爆框 -2
            tinyBadgeSizeDelta: 1);   // 微小角标 +1

        Assert.NotNull(result);
        string modifiedXml = Encoding.Unicode.GetString(result, 2, result.Length - 2);

        // 1. Normal (GeneralText): MatureBase(28) + Global(1) + Gen(0) = 29
        Assert.Contains("<Font id=\"Normal\" typeface=\"Microsoft YaHei\" size=\"29\"", modifiedXml);

        // 2. WorldItemDescriptionLabel (DropItem): MatureBase(33) + Global(1) + Drop(3) = 37
        Assert.Contains("<Font id=\"WorldItemDescriptionLabel\" inherits=\"NormalSC\" size=\"37\"", modifiedXml);

        // 3. Title (WindowTitle): MatureBase(39) + Global(1) + Title(-2) = 38 (从原版60号极大收敛防爆框)
        Assert.Contains("<Font id=\"Title\" typeface=\"Microsoft YaHei\" size=\"38\"", modifiedXml);

        // 4. TinySmallSC (TinyBadge): MatureBase(18) + Global(1) + Tiny(1) = 20
        Assert.Contains("<Font id=\"TinySmallSC\" typeface=\"Microsoft YaHei\" size=\"20\"", modifiedXml);
    }

    [Fact]
    public void RumourPatchService_GetDefaultMemo_FindsMappedText()
    {
        string memoSimplified = RumourPatchService.GetDefaultMemo("所有闪闪发光之物……", isSimplified: true);
        Assert.Contains("颠沛领域", memoSimplified);

        string memoTraditional = RumourPatchService.GetDefaultMemo("閃光的未必是金……", isSimplified: false);
        Assert.Contains("漂流者之所", memoTraditional);
    }

    [Fact]
    public void GameInstallDetector_EvaluatesDirectoryAccurately()
    {
        var invalidInfo = GameInstallDetector.EvaluateDirectory(@"C:\non_existent_poe2_path_xyz_123");
        Assert.False(invalidInfo.IsValid);

        string realGameDir = @"E:\PSAutoRecover\ui\rood\Grinding Gear Games\Path of Exile 2";
        if (Directory.Exists(realGameDir))
        {
            var validInfo = GameInstallDetector.EvaluateDirectory(realGameDir);
            Assert.True(validInfo.IsValid);
            Assert.Equal(GameMode.GGPK, validInfo.Mode);
        }
    }

    [Fact]
    public void ReadRealGgpkFile_WhenPresent_Succeeds()
    {
        string realGameDir = @"E:\PSAutoRecover\ui\rood\Grinding Gear Games\Path of Exile 2";
        if (!Directory.Exists(realGameDir)) return;

        var info = GameInstallDetector.EvaluateDirectory(realGameDir);
        var patcher = new PatcherFacade();

        // 验证小写输入也可以通过大小写容错机制成功读取
        var uiBytesLower = patcher.Engine.ReadGameFile(info, "metadata/ui/uisettings.xml");
        if (uiBytesLower == null) return;
        Assert.NotEmpty(uiBytesLower);

        // 验证规范大小写路径直接读取
        var uiBytesExact = patcher.Engine.ReadGameFile(info, "Metadata/UI/uisettings.xml");
        Assert.NotNull(uiBytesExact);
        Assert.NotEmpty(uiBytesExact);

        var egBytes = patcher.Engine.ReadGameFile(info, "Data/Balance/EndgameMaps.datc64");
        Assert.NotNull(egBytes);
        Assert.NotEmpty(egBytes);

        var waBytes = patcher.Engine.ReadGameFile(info, "Data/Balance/WorldAreas.datc64");
        Assert.NotNull(waBytes);
        Assert.NotEmpty(waBytes);
    }

    [Fact]
    public void ProcessRealGgpkData_Succeeds()
    {
        string realGameDir = @"E:\PSAutoRecover\ui\rood\Grinding Gear Games\Path of Exile 2";
        if (!Directory.Exists(realGameDir)) return;

        var info = GameInstallDetector.EvaluateDirectory(realGameDir);
        var patcher = new PatcherFacade();

        // 1. 验证从真实 GGPK 提取 uisettings 并应用字体
        var uiBytes = patcher.Engine.ReadGameFile(info, "metadata/ui/uisettings.xml");
        if (uiBytes == null) return;

        var fontResult = FontPatchService.ProcessUiSettings(
            uiBytes,
            fontFamily: "Microsoft YaHei",
            fontSizeDelta: 2,
            presetSizes: null);
        Assert.NotNull(fontResult);
        string fontXml = Encoding.Unicode.GetString(fontResult, 2, fontResult.Length - 2);
        Assert.Contains("Microsoft YaHei", fontXml);


        // 3. 验证从真实 GGPK 提取 EndgameMaps 并解析传言条目
        // 依次尝试 繁体中文、简体中文 与 根目录路径
        byte[]? egBytes = null;
        bool isChina = false;
        string[] testPaths =
        [
            "data/balance/traditional chinese/endgamemaps.datc64",
            "data/balance/simplified chinese/endgamemaps.datc64",
            "Data/Balance/EndgameMaps.datc64"
        ];

        foreach (var p in testPaths)
        {
            egBytes = patcher.Engine.ReadGameFile(info, p);
            if (egBytes != null && egBytes.Length > 0)
            {
                isChina = p.Contains("simplified", StringComparison.OrdinalIgnoreCase);
                break;
            }
        }
        Assert.NotNull(egBytes);

        var rumourItems = RumourPatchService.ParseFromBytes(egBytes, new Dictionary<int, string>(), isChina);
        Assert.NotEmpty(rumourItems);

        // 验证至少能匹配到一个默认传言并成功执行 ApplyRumourChanges
        int matchCount = 0;
        foreach (var item in rumourItems)
        {
            if (!string.IsNullOrEmpty(item.Memo))
            {
                item.Enabled = true;
                item.Memo = item.OriginalRumourText + " [Test]";
                item.RumourText = item.Memo;
                matchCount++;
            }
        }
        Assert.True(matchCount > 0, "应能从真实 EndgameMaps 数据中匹配到至少一条传言");

        var modifiedEgData = RumourPatchService.ApplyRumourChanges(egBytes, rumourItems);
        Assert.NotNull(modifiedEgData);
        Assert.True(modifiedEgData.Length > egBytes.Length, "动态字符串表追加后文件大小应增加");
    }

    [Fact]
    public void GetInstalledFonts_ReturnsSystemFontsWithChineseNames()
    {
        var fonts = FontPatchService.GetInstalledFonts();
        Assert.NotEmpty(fonts);

        // 验证列表中含有 DisplayName 与 TypefaceName
        Assert.All(fonts, f =>
        {
            Assert.False(string.IsNullOrWhiteSpace(f.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(f.TypefaceName));
        });

        // 验证包含了 Fontin (游戏原版)
        Assert.Contains(fonts, f => string.Equals(f.TypefaceName, "Fontin", StringComparison.OrdinalIgnoreCase));

        // 验证至少有一个常见中文字体（如微软雅黑或黑体）被正确解析显示了友好中文名
        Assert.Contains(fonts, f => f.DisplayName.Contains("微软雅黑") || f.DisplayName.Contains("黑体") || f.DisplayName.Contains("宋体") || f.DisplayName.Contains("楷体"));
    }

    [Fact]
    public void NamePatchService_StripNamePatchSuffix_WorksCorrectly()
    {
        Assert.Equal("秘术保险箱", NamePatchService.StripNamePatchSuffix("秘术保险箱[法系装备]"));
        Assert.Equal("黄金失落宝匣", NamePatchService.StripNamePatchSuffix("<rgb(166,90,255)>{黄金失落宝匣[时迭珠宝]}"));
        Assert.Equal("阿曼娜姆的虚空", NamePatchService.StripNamePatchSuffix("<rgb(227,23,13)>{￥￥￥}阿曼娜姆的虚空<rgb(227,23,13)>{￥￥￥}"));
        Assert.Equal("深渊密箱", NamePatchService.StripNamePatchSuffix("<rgb(211,50,247)>{■■深渊密箱■■}"));
    }

    [Fact]
    public void NamePatchService_GetDefaultMemo_MatchesPresets()
    {
        string memoChest = NamePatchService.GetDefaultMemo("秘术保险箱", isSimplified: true, NamePatchKind.Chests);
        Assert.Equal("秘术保险箱[法系装备]", memoChest);
    }

    [Fact]
    public void ColorModService_ParseBlocks_And_BuildEntries()
    {
        // 构造一段典型的 csd 格式文本（例如包含 base_cannot_have_life_leeched_from）
        string sampleCsd = 
            "description\r\n" +
            "\t1 base_cannot_have_life_leeched_from\r\n" +
            "\t1\r\n" +
            "\t\t1 # \"Cannot have Life Leeched from you\"\r\n" +
            "\tlang \"Simplified Chinese\"\r\n" +
            "\t\t1 # \"无法从你身上偷取生命\"\r\n";

        var blocks = ColorModDefaults.ParseDescriptionBlocks(sampleCsd, "Simplified Chinese");
        Assert.NotEmpty(blocks);
        var block = blocks.First();
        Assert.Equal("无法从你身上偷取生命", block.DisplayName);
        Assert.Contains("base_cannot_have_life_leeched_from", block.StatIds);

        var entries = ColorModDefaults.BuildEntriesFromBlocks(blocks, "通用");
        Assert.NotEmpty(entries);
        var entry = entries.First();
        Assert.True(entry.Enabled);
        Assert.True(entry.IsDanger);
        Assert.Equal(ColorModDefaults.ColorPink, entry.Color);
    }

    [Fact]
    public void PricePatchService_CompactPriceVariants_GeneratesProperCandidates()
    {
        var variants1 = PricePatchService.CompactPriceVariants("15c");
        Assert.Contains("[15c]", variants1);

        var variants2 = PricePatchService.CompactPriceVariants("0.5d");
        Assert.Contains("[.5d]", variants2);

        var variants3 = PricePatchService.CompactPriceVariants("20.0c");
        Assert.Contains("[20c]", variants3);
    }

    [Fact]
    public void PricePatchService_StripExistingPriceSuffix_CleansSuffix()
    {
        Assert.Equal("崇高石", PricePatchService.StripExistingPriceSuffix("崇高石 [15C]", " "));
        Assert.Equal("神圣石", PricePatchService.StripExistingPriceSuffix("神圣石[2.5D]", ""));
        Assert.Equal("魔符", PricePatchService.StripExistingPriceSuffix("魔符 [<1d]", " "));
        Assert.Equal("法师之血", PricePatchService.StripExistingPriceSuffix("法师之血 [★ 280D]", " "));
    }

    [Fact]
    public void PricePatchService_MultiLanguageAndSimplifiedNormalization_MatchesProperly()
    {
        var entries = new List<PricePatchService.BaseItemName>
        {
            new() { RowIndex = 0, MetadataPath = "Metadata/Items/Currency/CurrencyDuplicate", Name = "卡兰德的魔镜", NameStart = 10, NameEnd = 22 },
            new() { RowIndex = 1, MetadataPath = "Metadata/Items/Currency/CurrencyModValues", Name = "神聖石", NameStart = 30, NameEnd = 36 },
            new() { RowIndex = 2, MetadataPath = "Metadata/Items/Gems/SkillGem", Name = "未切割的技能宝石", NameStart = 50, NameEnd = 66 }
        };

        var priceRows = new List<PriceRow>
        {
            // 1. 英文 DisplayName 通过 EnglishToMetadataMap 命中
            new() { Name = "Mirror of Kalandra", Price = "280D" },
            // 2. 简体名称输入命中繁体客户端条目 "神聖石"
            new() { Name = "神圣石", Price = "400E" },
            // 3. 英文 DisplayName 通过 EnglishToChineseMap 命中 "未切割的技能宝石"
            new() { Name = "Uncut Skill Gem", Price = "1.5D" }
        };

        var (replacements, _) = PricePatchService.BuildReplacements(entries, priceRows, " ", keepExistingPrice: false, "append", true);
        Assert.Equal(3, replacements.Count);
        Assert.Contains(replacements, r => r.OldName == "卡兰德的魔镜" && r.RequestedName == "卡兰德的魔镜 [280D]");
        Assert.Contains(replacements, r => r.OldName == "神聖石" && r.RequestedName == "神聖石 [400E]");
        Assert.Contains(replacements, r => r.OldName == "未切割的技能宝石" && r.RequestedName == "未切割的技能宝石 [1.5D]");
    }

    [Fact]
    public void PricePatchService_SyntheticDat_Append_And_CleanPatch()
    {
        // 构造合成的 BaseItemTypes.datc64 结构
        // rowCount = 2, rowSize = 40 (满足 > 32)
        int rowCount = 2;
        int rowSize = 40;
        int stringBase = 4 + rowCount * rowSize; // 哨兵起始位置即为 StringBase

        using var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(rowCount), 0, 4);

        // 初始化行数据（填充 0）
        byte[] emptyRow = new byte[rowSize];
        ms.Write(emptyRow, 0, emptyRow.Length);
        ms.Write(emptyRow, 0, emptyRow.Length);

        // 写入哨兵 0xBB * 8
        ms.Write(new byte[] { 187, 187, 187, 187, 187, 187, 187, 187 }, 0, 8);

        // 在 stringBase 后写入字符串
        // 第 0 行：MetadataPath="Metadata/Items/Currency/CurrencyExalted", Name="崇高石"
        string meta0 = "Metadata/Items/Currency/CurrencyExalted";
        string name0 = "崇高石";
        uint meta0Offset = (uint)(ms.Length - stringBase);
        ms.Write(Encoding.Unicode.GetBytes(meta0));
        ms.Write(new byte[4]); // \0\0\0\0

        uint name0Offset = (uint)(ms.Length - stringBase);
        ms.Write(Encoding.Unicode.GetBytes(name0));
        ms.Write(new byte[4]);

        // 第 1 行：MetadataPath="Metadata/Items/Currency/CurrencyDivine", Name="神圣石"
        string meta1 = "Metadata/Items/Currency/CurrencyDivine";
        string name1 = "神圣石";
        uint meta1Offset = (uint)(ms.Length - stringBase);
        ms.Write(Encoding.Unicode.GetBytes(meta1));
        ms.Write(new byte[4]);

        uint name1Offset = (uint)(ms.Length - stringBase);
        ms.Write(Encoding.Unicode.GetBytes(name1));
        ms.Write(new byte[4]);

        byte[] datBytes = ms.ToArray();

        // 回填第 0 行指针：偏移 4 填 meta0Offset，偏移 4+32 填 name0Offset
        Buffer.BlockCopy(BitConverter.GetBytes(meta0Offset), 0, datBytes, 4, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(name0Offset), 0, datBytes, 4 + 32, 4);

        // 回填第 1 行指针：偏移 4+40 填 meta1Offset，偏移 4+40+32 填 name1Offset
        Buffer.BlockCopy(BitConverter.GetBytes(meta1Offset), 0, datBytes, 4 + rowSize, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(name1Offset), 0, datBytes, 4 + rowSize + 32, 4);

        // 验证扫描
        var items = PricePatchService.ScanBaseItemNames(datBytes);
        Assert.Equal(2, items.Count);
        Assert.Equal("崇高石", items[0].Name);
        Assert.Equal("神圣石", items[1].Name);

        // 测试注入物价
        var priceRows = new List<PriceRow>
        {
            new() { MetadataPath = meta0, Price = "15c" },
            new() { MetadataPath = meta1, Price = "2.5d" }
        };

        var (replacements, _) = PricePatchService.BuildReplacements(items, priceRows, " ", keepExistingPrice: false, "append", true);
        Assert.Equal(2, replacements.Count);

        byte[] patchedDat = PricePatchService.ApplyReplacementsAppend(datBytes, replacements);
        var patchedItems = PricePatchService.ScanBaseItemNames(patchedDat);
        Assert.Equal(2, patchedItems.Count);
        Assert.Equal("崇高石 [15c]", patchedItems[0].Name);
        Assert.Equal("神圣石 [2.5d]", patchedItems[1].Name);

        // 测试物价清洗
        var (cleanReplacements, _) = PricePatchService.BuildReplacements(patchedItems, [], " ", keepExistingPrice: false, "append", true);
        Assert.Equal(2, cleanReplacements.Count);
        byte[] cleanedDat = PricePatchService.ApplyReplacementsAppend(patchedDat, cleanReplacements);
        var cleanedItems = PricePatchService.ScanBaseItemNames(cleanedDat);
        Assert.Equal("崇高石", cleanedItems[0].Name);
        Assert.Equal("神圣石", cleanedItems[1].Name);
    }

    [Fact]
    public void EndToEnd_ColorModPipeline_Verification()
    {
        // 1. 语言探测器
        string lang = Poe2LanguageDetector.GetTargetCsdLanguage(isChinaServer: false);
        Assert.NotNull(lang);

        // 2. 词缀预设与离线词库提取
        var entries = ColorModService.GetBuiltinEntries();
        if (entries.Count > 0)
        {
            Assert.True(entries.Count > 1000);
            var sample = entries.FirstOrDefault(e => e.Enabled && e.DisplayName.Length > 0);
            Assert.NotNull(sample);
            Assert.DoesNotContain("{0}", sample.DisplayName);

            // 验证高危物理反伤词缀被正确判定为 Enabled 与 IsDanger
            var dangerReflect = entries.FirstOrDefault(e => e.StatId == "map_monsters_reflect_%_physical_damage");
            Assert.NotNull(dangerReflect);
            Assert.True(dangerReflect.Enabled);
            Assert.True(dangerReflect.IsDanger);

            // 验证普通词缀默认保持未启用（不胡乱染色，保持原版）
            var normalMod = entries.FirstOrDefault(e => !ColorModDefaults.DefaultColorMap.ContainsKey(e.StatId));
            if (normalMod != null)
            {
                Assert.False(normalMod.Enabled);
            }
        }

        // 3. 门面 facade 调用
        var facade = new PatcherFacade();
        var scanned = facade.ScanClientDictionary();
        Assert.NotNull(scanned);
    }

    [Fact]
    public void ChestSemanticRule_Matches_Simplified_Traditional_And_English()
    {
        // 繁中
        var ruleTrad = NamePatchService.MatchChestRule("黃金奧術師貯藏箱");
        Assert.NotNull(ruleTrad);
        Assert.Equal("通货", ruleTrad.Category);

        // 简中
        var ruleSimp = NamePatchService.MatchChestRule("黄金奥术师的宝匣");
        Assert.NotNull(ruleSimp);
        Assert.Equal("通货", ruleSimp.Category);

        // 英文
        var ruleEn = NamePatchService.MatchChestRule("Golden Arcanist's Strongbox");
        Assert.NotNull(ruleEn);
        Assert.Equal("通货", ruleEn.Category);

        // 珠宝
        var ruleJewel = NamePatchService.MatchChestRule("至尊黃金皇家貯藏箱");
        Assert.NotNull(ruleJewel);
        Assert.Equal("珠宝", ruleJewel.Category);

        // 光谱
        var ruleSpectrum = NamePatchService.MatchChestRule("黄金聚光宝匣");
        Assert.NotNull(ruleSpectrum);
        Assert.Equal("光谱珠宝", ruleSpectrum.Category);
        Assert.Equal("255,0,0", ruleSpectrum.ColorRgb);
    }

    [Fact]
    public void PricePatchService_StripExistingPriceSuffix_DoesNotStripNonPriceBrackets()
    {
        Assert.Equal("物品 [ABC]", PricePatchService.StripExistingPriceSuffix("物品 [ABC]", " "));
        Assert.Equal("物品 [ABC]", PricePatchService.StripExistingPriceSuffix("物品 [ABC] [15e]", " "));
    }

    [Fact]
    public void PricePatchService_Maps_English_To_Metadata()
    {
        Assert.True(PricePatchService.EnglishToMetadataMap.ContainsKey("Divine Orb"));
        Assert.Equal("Metadata/Items/Currency/CurrencyModValues", PricePatchService.EnglishToMetadataMap["Divine Orb"]);

        Assert.True(PricePatchService.EnglishToMetadataMap.ContainsKey("Mirror of Kalandra"));
        Assert.Equal("Metadata/Items/Currency/CurrencyDuplicate", PricePatchService.EnglishToMetadataMap["Mirror of Kalandra"]);
    }

    [Fact]
    public void PricePatchService_BuildReplacements_PopulatesDetailedFields()
    {
        var entries = new List<PricePatchService.BaseItemName>
        {
            new() { RowIndex = 0, MetadataPath = "Metadata/Items/Currency/CurrencyModValues", Name = "神圣石", NameStart = 0, NameEnd = 6 }
        };
        var priceRows = new List<PriceRow>
        {
            new("神圣石", "400E", "Metadata/Items/Currency/CurrencyModValues")
        };

        var (replacements, _) = PricePatchService.BuildReplacements(entries, priceRows, " ", keepExistingPrice: false, "append", true);
        Assert.Single(replacements);
        var item = replacements[0];
        Assert.Equal("神圣石", item.OldName);
        Assert.Equal("400E", item.Price);
        Assert.Equal("神圣石 [400E]", item.FittedName);
        Assert.False(item.Compacted);
    }

    private static byte[] BuildSyntheticBaseItemsDat(
        string metadataPath = "Metadata/Items/Currency/CurrencyExalted",
        string name = "崇高石")
    {
        const int rowCount = 1;
        const int rowSize = 40;
        const int stringBase = 4 + rowCount * rowSize;

        using var stream = new MemoryStream();
        stream.Write(BitConverter.GetBytes(rowCount));
        stream.Write(new byte[rowSize]);
        stream.Write(new byte[] { 187, 187, 187, 187, 187, 187, 187, 187 });

        uint metadataOffset = (uint)(stream.Length - stringBase);
        stream.Write(Encoding.Unicode.GetBytes(metadataPath));
        stream.Write(new byte[4]);

        uint nameOffset = (uint)(stream.Length - stringBase);
        stream.Write(Encoding.Unicode.GetBytes(name));
        stream.Write(new byte[4]);

        byte[] bytes = stream.ToArray();
        // 指针槽是 8 字节，只写低 4 字节会让高 4 字节保持为 0，读取时才是正确的偏移
        Buffer.BlockCopy(BitConverter.GetBytes((ulong)metadataOffset), 0, bytes, 4, 8);
        Buffer.BlockCopy(BitConverter.GetBytes((ulong)nameOffset), 0, bytes, 4 + 32, 8);
        return bytes;
    }
}
