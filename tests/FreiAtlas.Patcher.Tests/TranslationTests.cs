using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FreiAtlas.Patcher.Core;
using FreiAtlas.Patcher.Translation;
using Xunit;

namespace FreiAtlas.Patcher.Tests;

public class TranslationTests
{
    [Fact]
    public void CsdSmartPatchTransformer_RuleB1_ConvertsNativeScToTcAndPreservesLanguageOrder()
    {
        string csdSample = """
            description
            	1 base_fire_damage_%
            	1
            		#% increased Fire Damage
            	lang "Russian"
            	1
            		#% повышение урона от огня
            	lang "Traditional Chinese"
            	1
            		#% 增加火焰傷害
            	lang "German"
            	1
            		#% erhöhter Feuerschaden
            	lang "Simplified Chinese"
            	1
            		#% 增加火焰伤害
            	lang "Portuguese"
            	1
            		#% de Dano de Fogo aumentado
            """;

        byte[] inputBytes = Encoding.Unicode.GetBytes(csdSample);
        byte[] resultBytes = CsdSmartPatchTransformer.Transform(inputBytes, "metadata/statdescriptions/fire_damage.csd");
        string resultText = Encoding.Unicode.GetString(resultBytes);

        // 简中内容应在 Traditional Chinese 原位输出
        Assert.Contains("lang \"Traditional Chinese\"", resultText);
        Assert.Contains("#% 增加火焰伤害", resultText);
        // 原繁中内容应被丢弃
        Assert.DoesNotContain("#% 增加火焰傷害", resultText);
        // 原版 Simplified Chinese 标签应被移除避免重复
        Assert.DoesNotContain("lang \"Simplified Chinese\"", resultText);

        // 验证原版语言顺序保持不变：Russian -> Traditional Chinese -> German -> Portuguese
        int idxRu = resultText.IndexOf("lang \"Russian\"");
        int idxTc = resultText.IndexOf("lang \"Traditional Chinese\"");
        int idxDe = resultText.IndexOf("lang \"German\"");
        int idxPt = resultText.IndexOf("lang \"Portuguese\"");

        Assert.True(idxRu < idxTc, "Russian 必须在 Traditional Chinese 之前");
        Assert.True(idxTc < idxDe, "Traditional Chinese 必须在 German 之前");
        Assert.True(idxDe < idxPt, "German 必须在 Portuguese 之前");
    }

    [Fact]
    public void CsdSmartPatchTransformer_RuleB2_InjectsMissingScFromRulesWithProperHeader()
    {
        string csdSample = """
            description
            	1 special_missing_stat
            	1
            		# "Special stat"
            	lang "Russian"
            	1
            		# "Спец стат"
            	lang "Traditional Chinese"
            	1
            		# "未翻譯之繁體詞綴"
            """;

        var missingRules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["1 special_missing_stat"] = "1\r\n\t\t# \"精准注入之官方简体词缀\""
        };

        byte[] inputBytes = Encoding.Unicode.GetBytes(csdSample);
        byte[] resultBytes = CsdSmartPatchTransformer.Transform(
            inputBytes,
            "metadata/statdescriptions/test.csd",
            missingRules);
        string resultText = Encoding.Unicode.GetString(resultBytes);

        // 验证必须有 lang 标头，且后接规则行，杜绝裸数字
        Assert.Contains("lang \"Traditional Chinese\"", resultText);
        Assert.Contains("# \"精准注入之官方简体词缀\"", resultText);
        Assert.DoesNotContain("未翻譯之繁體詞綴", resultText);

        // 确认结构：lang "Traditional Chinese" 后面紧跟 \t1
        int langIdx = resultText.IndexOf("lang \"Traditional Chinese\"");
        string afterLang = resultText.Substring(langIdx);
        Assert.StartsWith("lang \"Traditional Chinese\"\r\n\t1\r\n", afterLang);
    }

    [Fact]
    public void CsdSmartPatchTransformer_RuleB3_RetainsTcWhenBothSidesMissing()
    {
        string csdSample = """
            description
            	1 dummy_expedition_sentinel_test
            	1
            		# "Dummy"
            	lang "Traditional Chinese"
            	1
            		# "廢棄哨兵繁體原文兜底"
            """;

        byte[] inputBytes = Encoding.Unicode.GetBytes(csdSample);
        byte[] resultBytes = CsdSmartPatchTransformer.Transform(inputBytes, "metadata/statdescriptions/sentinel.csd");
        string resultText = Encoding.Unicode.GetString(resultBytes);

        // 两端均未翻译时安全保留繁体原样
        Assert.Contains("lang \"Traditional Chinese\"", resultText);
        Assert.Contains("廢棄哨兵繁體原文兜底", resultText);
    }

    [Fact]
    public void CsdSmartPatchTransformer_HandlesBomAndNoDescriptionBlocks()
    {
        string csdSample = "\uFEFF" + """
            description
            	1 first_stat_with_bom
            	1
            		# "First stat"
            	lang "Traditional Chinese"
            	1
            		# "首個繁體"
            	lang "Simplified Chinese"
            	1
            		# "首个简体"

            no_description level
            no_description item_drop_slots

            description
            	1 second_stat
            	1
            		# "Second stat"
            	lang "Traditional Chinese"
            	1
            		# "第二個繁體"
            	lang "Simplified Chinese"
            	1
            		# "第二个简体"
            """;

        byte[] inputBytes = Encoding.Unicode.GetBytes(csdSample);
        byte[] resultBytes = CsdSmartPatchTransformer.Transform(inputBytes, "test.csd");
        string resultText = Encoding.Unicode.GetString(resultBytes);

        // 第 1 块必须成功改签
        Assert.Contains("# \"首个简体\"", resultText);
        Assert.DoesNotContain("# \"首個繁體\"", resultText);

        // no_description 必须完整保留
        Assert.Contains("no_description level", resultText);
        Assert.Contains("no_description item_drop_slots", resultText);

        // 第 2 块也必须成功改签
        Assert.Contains("# \"第二个简体\"", resultText);
        Assert.DoesNotContain("# \"第二個繁體\"", resultText);

        // 输出仍然具备 BOM
        Assert.StartsWith("\uFEFF", resultText);
    }

    [Fact]
    public void DatTextPatcher_GetEmbeddedCatalog_LoadsSuccessfully()
    {
        var catalog = DatTextPatcher.GetEmbeddedCatalog();
        Assert.NotNull(catalog);
        Assert.True(catalog.BaseItems.Count > 4000, $"BaseItems 数量异常: {catalog.BaseItems.Count}");
        Assert.True(catalog.Tables.Count >= 150, $"Tables 数量异常: {catalog.Tables.Count}");
        Assert.True(catalog.Tables.ContainsKey("clientstrings.datc64"), "应包含 clientstrings.datc64");
        Assert.True(catalog.Tables.ContainsKey("passiveskills.datc64"), "应包含 passiveskills.datc64");
        Assert.True(catalog.Tables.ContainsKey("npctextaudio.datc64"), "应包含 npctextaudio.datc64");
        Assert.True(catalog.Tables.ContainsKey("queststates.datc64"), "应包含 queststates.datc64");
        Assert.True(catalog.Tables.ContainsKey("activeskills.datc64"), "应包含 activeskills.datc64");
        Assert.True(catalog.Tables.ContainsKey("currencyitems.datc64"), "应包含 currencyitems.datc64");
        Assert.True(catalog.Tables.ContainsKey("characterpanelstats.datc64"), "应包含 characterpanelstats.datc64");

        // 验证代表性条目
        Assert.Equal("神圣石", catalog.BaseItems["Metadata/Items/Currency/CurrencyModValues"]);
        Assert.Equal("卡兰德的魔镜", catalog.BaseItems["Metadata/Items/Currency/CurrencyDuplicate"]);
    }

    [Fact]
    public void TranslationPatchService_GetEmbeddedMissingRules_LoadsSuccessfully()
    {
        var rules = TranslationPatchService.GetEmbeddedMissingRules();
        Assert.NotNull(rules);
        int totalRules = rules.Values.Sum(v => v.Count);
        Assert.True(totalRules >= 260, $"缺失规则总数不足 260: {totalRules}");
    }

    [Fact]
    public void DatTextPatcher_RewriteStringPointersAppend_MaintainsDataAndRedirectsPointers()
    {
        int rowCount = 1;
        int rowSize = 36;
        int stringBase = 4 + rowCount * rowSize;

        byte[] initialData;
        using (var ms = new MemoryStream())
        {
            ms.Write(BitConverter.GetBytes(rowCount), 0, 4);
            var rowBytes = new byte[rowSize];
            BitConverter.GetBytes((uint)0).CopyTo(rowBytes, 32);
            ms.Write(rowBytes, 0, rowSize);

            byte[] oldStr = Encoding.Unicode.GetBytes("OldName");
            ms.Write(oldStr, 0, oldStr.Length);
            ms.Write([0, 0, 0, 0], 0, 4);

            initialData = ms.ToArray();
        }

        int pointerPos = 4 + 32;
        var replacements = new List<(int PointerPos, string NewText)>
        {
            (pointerPos, "全新简体中文名称")
        };

        byte[] patchedData = DatBinaryUtils.RewriteStringPointersAppend(initialData, stringBase, replacements);

        int patchedRows = BitConverter.ToInt32(patchedData, 0);
        Assert.Equal(1, patchedRows);

        uint newOffset = BitConverter.ToUInt32(patchedData, pointerPos);
        Assert.True(newOffset > 0, "新指针偏移应当大于 0");

        int newStringPos = stringBase + (int)newOffset;
        Assert.True(newStringPos < patchedData.Length);
        string newString = ReadUtf16(patchedData, newStringPos);
        Assert.Equal("全新简体中文名称", newString);
    }

    private static string ReadUtf16(byte[] data, int start)
    {
        int end = start;
        while (end < data.Length - 1 && !(data[end] == 0 && data[end + 1] == 0))
        {
            end += 2;
        }
        return Encoding.Unicode.GetString(data, start, end - start);
    }
}
