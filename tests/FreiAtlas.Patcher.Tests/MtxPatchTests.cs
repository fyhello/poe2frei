using System.Text.RegularExpressions;
using FreiAtlas.Patcher.MtxPatch;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// PoE 2 全量商城外观特效 (MTX) 单元测试。
///
/// 覆盖：
///   1. 843 款特效元数据完整性与资源加载校验
///   2. 沙虫宠物体型缩放算法 (scale = 150)
///   3. 职业模型背饰/翅膀 AttachedAnimatedObject 注入与幂等性
///   4. 职业模型周身光效 ParticleEffects 注入与幂等性
///   5. 翻滚动作常驻 on_construction_complete 注入与幂等性
///   6. 全局防具时装 7 种属性底子长后缀优先匹配算法
///   7. 3 款鼠标指针 21 张 BMP 资源提取测试
/// </summary>
public class MtxPatchTests
{
    [Fact]
    public void MtxDatabase_LoadAllEntries_CompleteAndValid()
    {
        var db = MtxDatabase.Instance;
        Assert.NotNull(db);
        Assert.Equal(843, db.AllEntries.Count);

        // 分类数量校验
        int armourSets = db.AllEntries.Count(e => e.Category == MtxCategory.ArmourSets);
        int skills = db.AllEntries.Count(e => e.Category == MtxCategory.Skills);
        int portals = db.AllEntries.Count(e => e.Category == MtxCategory.Portals);
        int pets = db.AllEntries.Count(e => e.Category == MtxCategory.Pets);
        int charFx = db.AllEntries.Count(e => e.Category == MtxCategory.CharacterEffects);
        int misc = db.AllEntries.Count(e => e.Category == MtxCategory.Misc);

        Assert.Equal(205, armourSets);
        Assert.Equal(79, skills);
        Assert.Equal(245, portals);
        Assert.Equal(154, pets);
        Assert.Equal(146, charFx);
        Assert.Equal(14, misc);

        // 全部条目名称非空且唯一检索成功
        foreach (var entry in db.AllEntries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Name));
            var found = db.FindByName(entry.Name);
            Assert.NotNull(found);
            Assert.Equal(entry.Name, found.Name);
        }
    }

    [Fact]
    public void CursorPack_Bitmaps_ExistAndValidLength()
    {
        var cursorPacks = new[] { "EternalCursor", "KaruiCursor", "VaalCursor" };
        var testBmp = "contrast.bmp";

        foreach (var pack in cursorPacks)
        {
            var bytes = MtxDatabase.ReadCursorBitmap(pack, testBmp);
            Assert.NotNull(bytes);
            Assert.True(bytes!.Length > 1000, $"{pack} 的 {testBmp} 数据过小");
            // 校验 BMP 魔数 'BM'
            Assert.Equal((byte)'B', bytes[0]);
            Assert.Equal((byte)'M', bytes[1]);
        }
    }

    [Fact]
    public void PetScale_ReplacesExistingScale_OrInjectsNew()
    {
        // 场景 1：已有 scale = 20，替换为 150
        var input1 = "Positioned\n{\n\tobject_size = 20\n\tscale = 20\n\tblocking = false\n}\n";
        var res1 = MtxPatchApplier.ApplyPetScale(input1, 150);
        Assert.Contains("scale = 150", res1);
        Assert.DoesNotContain("scale = 20", res1);

        // 场景 2：有 Positioned 但无 scale，在内部注入
        var input2 = "Positioned\n{\n\tobject_size = 20\n\tblocking = false\n}\n";
        var res2 = MtxPatchApplier.ApplyPetScale(input2, 150);
        Assert.Contains("scale = 150", res2);
        Assert.Contains("Positioned", res2);

        // 场景 3：无 Positioned，在尾部追加完整节点
        var input3 = "version 3\nextends \"metadata/monsters/monster.ot\"\n";
        var res3 = MtxPatchApplier.ApplyPetScale(input3, 150);
        Assert.Contains("Positioned\n{\n\tscale = 150\n}", res3);
    }

    [Fact]
    public void CharacterAttachment_InjectAttachedAnimatedObject_Idempotent()
    {
        var initialAo = "version 2\nAttachedAnimatedObject\n{\n\tattached_object = \"<root> original.ao\"\n}\n";
        var injected = MtxPatchApplier.InjectAttachedAnimatedObject(initialAo, "aux_Back_attachment", "wings.ao");

        Assert.Contains("attached_object = \"aux_Back_attachment wings.ao\"", injected);
        Assert.Contains("attached_object = \"<root> original.ao\"", injected);

        // 幂等性：重复调用不产生重复行
        var injectedAgain = MtxPatchApplier.InjectAttachedAnimatedObject(injected, "aux_Back_attachment", "wings.ao");
        Assert.Equal(injected, injectedAgain);
    }

    [Fact]
    public void CharacterParticle_InjectParticleEffect_Idempotent()
    {
        var initialAo = "version 2\nclient\n{\n\tParticleEffects\n\t{\n\t\tcontinuous_effect = \"base orig.pet\"\n\t}\n}\n";
        var injected = MtxPatchApplier.InjectParticleEffect(initialAo, "bodyfx", "micro/aura.pet");

        Assert.Contains("continuous_effect = \"bodyfx micro/aura.pet\"", injected);
        Assert.Contains("continuous_effect = \"base orig.pet\"", injected);

        // 幂等性
        var injectedAgain = MtxPatchApplier.InjectParticleEffect(injected, "bodyfx", "micro/aura.pet");
        Assert.Equal(injected, injectedAgain);
    }

    [Fact]
    public void DodgeRoll_InjectOnConstructionComplete_Idempotent()
    {
        var initialOt = "client\n{\n\tBaseEvents\n\t{\n\t\ton_initial_position_set = { SetMaxZoomDistance(2.0); }\n\t\ton_construction_complete =\n\t\t{\n\t\t\tAddEffectPack( 'roll_default.epk' );\n\t\t}\n\t}\n}\n";
        var injected = MtxPatchApplier.InjectDodgeRollPack(initialOt, "roll_abyssal.epk");

        Assert.Contains("AddEffectPack( 'roll_abyssal.epk' );", injected);
        Assert.Contains("SetMaxZoomDistance(2.0);", injected); // 确保与视距调整互不破坏

        // 幂等性
        var injectedAgain = MtxPatchApplier.InjectDodgeRollPack(injected, "roll_abyssal.epk");
        Assert.Equal(injected, injectedAgain);
    }

    [Theory]
    [InlineData("bodyarmour_strdexint.ao", "strdexint")]
    [InlineData("bodyarmour_strdexint1.ao", "strdexint")]
    [InlineData("gloves_strdexfourb.ao", "strdex")]
    [InlineData("boots_dexint2.ao", "dexint")]
    [InlineData("helmet_strint.ao", "strint")]
    [InlineData("body_str4.ao", "str")]
    [InlineData("boots_dex.ao", "dex")]
    [InlineData("helmet_intb.ao", "int")]
    public void ArmourSuffix_LongestSuffixPriority_MatchesCorrectly(string fileName, string expectedSuffix)
    {
        var fn = Path.GetFileNameWithoutExtension(fileName).TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        if (fn.EndsWith("b", StringComparison.OrdinalIgnoreCase))
        {
            fn = fn[..^1];
        }
        if (fn.EndsWith("four", StringComparison.OrdinalIgnoreCase))
        {
            fn = fn[..^4];
        }

        string? matched = null;
        foreach (var suf in MtxDatabase.ArmourBaseSuffixes)
        {
            if (fn.EndsWith(suf, StringComparison.OrdinalIgnoreCase))
            {
                matched = suf;
                break;
            }
        }

        Assert.Equal(expectedSuffix, matched);
    }

    [Fact]
    public void MtxTranslation_All843Entries_TranslateWithoutCrashAndNonEmpty()
    {
        var db = MtxDatabase.Instance;
        Assert.Equal(843, db.AllEntries.Count);

        foreach (var entry in db.AllEntries)
        {
            var zh = entry.DisplayNameZh;
            Assert.False(string.IsNullOrWhiteSpace(zh), $"Entry {entry.Name} 翻译结果为空");
            // 校验是否至少包含中文字符
            bool hasChinese = zh.Any(c => c >= 0x4e00 && c <= 0x9fff);
            Assert.True(hasChinese, $"Entry {entry.Name} -> '{zh}' 未包含任何中文字符");
        }
    }

    [Theory]
    [InlineData("Abyssal Phoenix Pet", MtxCategory.Pets, "深渊", "凤凰", "宠物")]
    [InlineData("Abyss Core Portal", MtxCategory.Portals, "深渊", "核心", "传送门")]
    [InlineData("Dragon Hunter Wings", MtxCategory.CharacterEffects, "猎龙者", "背饰之翼", "")]
    [InlineData("Aurora Herald of Ice", MtxCategory.Skills, "极光", "冰霜之捷", "技能")]
    [InlineData("EternalCursor", MtxCategory.Misc, "永恒", "鼠标指针", "")]
    [InlineData("Cursed Pirate Armour", MtxCategory.ArmourSets, "海盗", "防具套装", "")]
    [InlineData("Crocolisk Werewolf Shapeshif", MtxCategory.Misc, "巨鳄", "狼人", "变身")]
    public void MtxTranslation_SampleEntries_TranslatesExpectedTerms(
        string englishName,
        MtxCategory category,
        string expected1,
        string expected2,
        string expected3)
    {
        var translated = MtxTranslationDictionary.Translate(englishName, category);
        Assert.False(string.IsNullOrWhiteSpace(translated));
        if (!string.IsNullOrEmpty(expected1)) Assert.Contains(expected1, translated);
        if (!string.IsNullOrEmpty(expected2)) Assert.Contains(expected2, translated);
        if (!string.IsNullOrEmpty(expected3)) Assert.Contains(expected3, translated);
    }

    [Fact]
    public void MtxPatch_Utf16LePreservation_PreservesBomAndCorrectEncoding()
    {
        // 模拟 PoE 2 官方 .ao 文件格式 (UTF-16LE 带 0xFF 0xFE BOM)
        var originalText = "version 3\r\nextends \"Metadata/Characters/Character\"\r\nAttachedAnimatedObject\r\n{\r\n}\r\n";
        var rawBytes = FreiAtlas.Patcher.EffectPatch.EffectTextCodec.Encode(
            originalText,
            FreiAtlas.Patcher.EffectPatch.EffectTextEncoding.Utf16LeWithBom);

        Assert.Equal((byte)0xFF, rawBytes[0]);
        Assert.Equal((byte)0xFE, rawBytes[1]);

        // 执行注入流程
        var encoding = FreiAtlas.Patcher.EffectPatch.EffectTextCodec.Detect(rawBytes, "metadata/characters/int/intfour.ao");
        Assert.Equal(FreiAtlas.Patcher.EffectPatch.EffectTextEncoding.Utf16LeWithBom, encoding);

        var decoded = FreiAtlas.Patcher.EffectPatch.EffectTextCodec.Decode(rawBytes, encoding);
        var modified = MtxPatchApplier.InjectAttachedAnimatedObject(decoded, "aux_Back_attachment", "micro/wings.ao");
        var encodedBack = FreiAtlas.Patcher.EffectPatch.EffectTextCodec.Encode(modified, encoding);

        // 验证写回结果严格保留 UTF-16LE BOM (绝无 0x7F79 罹 乱码)
        Assert.Equal((byte)0xFF, encodedBack[0]);
        Assert.Equal((byte)0xFE, encodedBack[1]);

        var roundtripText = FreiAtlas.Patcher.EffectPatch.EffectTextCodec.Decode(encodedBack, encoding);
        Assert.Contains("aux_Back_attachment micro/wings.ao", roundtripText);
        Assert.Contains("version 3", roundtripText);
    }

    [Fact]
    public void SkillEffects_WithEpk_DoNotMisclassifyAsDodgeRoll()
    {
        var db = MtxDatabase.Instance;
        var arc = db.FindByName("Darkshard Arc");
        var spark = db.FindByName("Darkshard Spark");
        var comet = db.FindByName("Abyssal Comet");

        Assert.NotNull(arc);
        Assert.NotNull(spark);
        Assert.NotNull(comet);

        Assert.Equal(MtxCategory.Skills, arc.Category);
        Assert.Equal(MtxCategory.Skills, spark.Category);
        Assert.Equal(MtxCategory.Skills, comet.Category);

        // Subtitle 应当是 "技能替换"，而不是 "翻滚动作常驻特效"
        Assert.Equal("技能替换", arc.Subtitle);
        Assert.Equal("技能替换", spark.Subtitle);
        Assert.Equal("技能替换", comet.Subtitle);

        // 验证分类拆分：它们必须进入 pairEntries，而不是 rollEntries
        var selectedEntries = new List<MtxEntry> { arc, spark, comet };

        var rollEntries = selectedEntries
            .Where(e => e.Category == MtxCategory.CharacterEffects && e.IsConstructionEffectPack)
            .ToList();
        var pairEntries = selectedEntries
            .Where(e => e.Category != MtxCategory.CharacterEffects && !e.IsArmourSetGlobal)
            .ToList();

        Assert.Empty(rollEntries);
        Assert.Equal(3, pairEntries.Count);
    }
}
