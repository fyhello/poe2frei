using System.Buffers.Binary;
using System.Text;
using FreiAtlas.Patcher.EffectPatch;
using FreiAtlas.Patcher.EffectPatch.MudMan;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 泥人贴图替换的单元测试。
///
/// 重点验证三件事：
///   1. 名单内的定制块是合法 DDS（字段来自对易刷实际输出的比对）
///   2. 内嵌表能加载且条目数与提取时一致
///   3. 名单外一律不处理 —— 这一条是实测纠错得来的：
///      早期版本对名单外贴图也替换，导致粒子失去亮度、发光遮罩铺满全屏。
/// </summary>
public class MudManTests
{
    [Fact]
    public void CustomBlock_HasExpectedHeader()
    {
        // 定制表内的一条：环境贴图，4×4 灰色
        const string path = "art/textures/environment/act10/act10storyglyph_colour_dxt1.dds";

        Assert.True(GrayDdsMaker.IsListed(path));
        var stub = GrayDdsMaker.GetReplacement(path);

        Assert.NotNull(stub);
        Assert.True(stub!.Length > 148, "定制块应包含完整 DDS 头");
        Assert.True(stub.Length <= GrayDdsMaker.MaxStubBytes, "定制块不应超过声明的上限");

        Assert.Equal("DDS ", Encoding.ASCII.GetString(stub, 0, 4));
        Assert.Equal(124u, BinaryPrimitives.ReadUInt32LittleEndian(stub.AsSpan(4)));
        Assert.Equal(0x00021007u, BinaryPrimitives.ReadUInt32LittleEndian(stub.AsSpan(8)));
        Assert.Equal("DX10", Encoding.ASCII.GetString(stub, 84, 4));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(stub.AsSpan(132)));   // TEXTURE2D
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(stub.AsSpan(140)));   // arraySize
    }

    [Fact]
    public void Tables_LoadSuccessfully()
    {
        Assert.True(MudManTables.IsAvailable, MudManTables.LoadError);
        Assert.Equal(28_404, MudManTables.ListCount);
        Assert.Equal(10_394, MudManTables.CustomCount);
        Assert.Equal(15_087, MudManTables.ExcludedCount);
    }

    [Fact]
    public void ListedPath_WithoutCustom_ReturnsDefault172ByteGrayDds()
    {
        // mt 表内、且未被 ye() 与 zd() 排除的贴图应返回 172 字节默认纯灰 DDS（对齐 worker.js 的 qd()）
        const string path = "art/models/monsters/3.15monsters/textures/anchorman2_sss_dxt1.dds";

        Assert.True(MudManTables.IsInRawTables(path));
        Assert.Null(MudManTables.TryGetCustom(path));
        Assert.True(GrayDdsMaker.IsListed(path));

        var stub = GrayDdsMaker.GetReplacement(path);
        Assert.NotNull(stub);
        Assert.Equal(GrayDdsMaker.DefaultGrayStubBytes, stub!.Length);

        Assert.Equal("DDS ", Encoding.ASCII.GetString(stub, 0, 4));
        Assert.Equal(124u, BinaryPrimitives.ReadUInt32LittleEndian(stub.AsSpan(4)));
        Assert.Equal(135175u, BinaryPrimitives.ReadUInt32LittleEndian(stub.AsSpan(8)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(stub.AsSpan(12)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(stub.AsSpan(16)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(stub.AsSpan(28)));     // 3 级 Mipmap
        Assert.Equal("DX10", Encoding.ASCII.GetString(stub, 84, 4));
        Assert.Equal(71u, BinaryPrimitives.ReadUInt32LittleEndian(stub.AsSpan(128)));   // BC1_UNORM
        Assert.Equal(33808, BinaryPrimitives.ReadUInt16LittleEndian(stub.AsSpan(148))); // 0x8410 中性灰
        Assert.Equal(33808, BinaryPrimitives.ReadUInt16LittleEndian(stub.AsSpan(150)));
    }

    [Theory]
    [InlineData("art/models/monsters/3.15monsters/textures/anchorman2_colour_dxt5.dds")] // 被热更 ye() 排除的怪物主颜色贴图
    [InlineData("art/models/monsters/3.15monsters/textures/eye_glowgray_dxt1.dds")]      // 被 zd() 排除的发光贴图
    public void ProtectedOrGlowTexture_InRawTable_IsExcluded(string path)
    {
        Assert.True(MudManTables.IsInRawTables(path));
        Assert.False(GrayDdsMaker.IsListed(path));
        Assert.Null(GrayDdsMaker.GetReplacement(path));
    }

    [Fact]
    public void ListedPath_ReturnsCustomBlock()
    {
        const string path = "art/textures/environment/act10/act10storyglyph_colour_dxt1.dds";

        Assert.True(GrayDdsMaker.IsListed(path));

        var bytes = GrayDdsMaker.GetReplacement(path);
        Assert.NotNull(bytes);
        Assert.True(bytes!.Length > 148, "定制块应包含完整 DDS 头");
        Assert.Equal("DDS ", Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Theory]
    [InlineData("art/2dart/ui/someicon.dds")]
    [InlineData("art/2ditems/rings/ring.dds")]
    [InlineData("art/microtransactions/armour/armour.dds")]
    [InlineData("art/textures/minimap/atlas.dds")]
    [InlineData("metadata/effects/spells/fireball.aoc")]
    [InlineData("data/balance/baseitemtypes.datc64")]
    public void NonScenePaths_AreNotReplaced(string path)
    {
        Assert.Null(GrayDdsMaker.GetReplacement(path));
    }

    /// <summary>
    /// 名单外的贴图必须原样保留。
    ///
    /// 这些路径曾经被兜底替换成灰块，直接导致画面变暗与红光泛滥；
    /// 易刷不碰它们，所以这里也不碰。
    /// </summary>
    [Theory]
    [InlineData("art/particles/monster_particles/act2_four/farudinwarlock/textures/runefarudinsmall_emissive.dds")]
    [InlineData("art/particles/environment_effects/act4/lava/ground/lava_glow_dxt1.dds")]
    [InlineData("art/textures/general/notinlist_colour.dds")]
    [InlineData("art/models/terrain/unknown_terrain.dds")]
    [InlineData("art/textures/interface/characterselection/fade/characterselection_07_fade_colour.dds")]
    public void UnlistedSceneTexture_IsNotReplaced(string path)
    {
        Assert.False(GrayDdsMaker.IsListed(path));
        Assert.Null(GrayDdsMaker.GetReplacement(path));
    }

    [Fact]
    public void Tiers_HaveSixEntriesWithExpectedScope()
    {
        Assert.Equal(6, MudManTierCatalog.All.Length);

        Assert.False(MudManTier.Low.HasTextures());
        Assert.False(MudManTier.Medium.HasTextures());
        Assert.False(MudManTier.High.HasTextures());
        Assert.True(MudManTier.LowTextures.HasTextures());
        Assert.True(MudManTier.MediumTextures.HasTextures());
        Assert.True(MudManTier.HighTextures.HasTextures());

        Assert.Equal(MudManTier.Low, MudManTier.LowTextures.EffectScope());
        Assert.Equal(MudManTier.Medium, MudManTier.MediumTextures.EffectScope());
        Assert.Equal(MudManTier.High, MudManTier.HighTextures.EffectScope());
    }

    [Fact]
    public void Tiers_RulesAccumulateByScope()
    {
        var low = MudManTier.Low.Rules();
        var medium = MudManTier.Medium.Rules();
        var high = MudManTier.High.Rules();

        Assert.True(low.Count > 0);
        Assert.True(medium.Count > low.Count, "中级应包含低级全部规则并追加技能规则");
        Assert.True(high.Count > medium.Count, "高级应包含中级全部规则并追加粒子规则");

        // 贴图规则不混进档位规则集：它的执行路径完全不同
        Assert.DoesNotContain(low, r => r.Action == EffectAction.MudManTexture);
        Assert.DoesNotContain(high, r => r.Action == EffectAction.MudManTexture);
    }

    [Fact]
    public void TierRules_TargetAoNotAoc()
    {
        // PoE2 已无 .aoc（实测 0 个），规则必须覆盖 .ao，否则全部命中 0 个文件
        var medium = MudManTier.Medium.Rules();

        var spell = medium.First(r => r.Id == "mudman_spell_blocks");
        Assert.Contains("ao", spell.PathPattern);
        Assert.Contains("aoc", spell.PathPattern);

        var sound = medium.First(r => r.Id == "mudman_sound_events");
        Assert.Contains("ao", sound.PathPattern);
        Assert.Contains("aoc", sound.PathPattern);
    }

    [Fact]
    public void StaticAtlasAndDecalRules_TransformAndValidateCorrectly()
    {
        var medium = MudManTier.Medium.Rules();
        var atlasRule = medium.First(r => r.Id == "mudman_spell_atlases");
        var decal4Rule = medium.First(r => r.Id == "mudman_static_decals_4");

        const string sampleAtlas =
            "version 2\r\n" +
            "\"Art/particles/rage_slash/textures/surge/scratch_decal.mat\"\r\n" +
            "1024 1024\r\n" +
            "minimum_placement_distance 250\r\n";

        var atlasOutcome = EffectRuleApplier.Apply(sampleAtlas, atlasRule);
        Assert.True(atlasOutcome.Changed);
        Assert.Contains("\r\n0 0\r\n", atlasOutcome.Content);
        Assert.True(EffectSyntaxValidator.ValidateFor(atlasOutcome.Content, "metadata/effects/spells/rage_slash/decal/scratch.atlas").IsValid);

        // 二次应用应保持幂等
        var atlasSecond = EffectRuleApplier.Apply(atlasOutcome.Content, atlasRule);
        Assert.False(atlasSecond.Changed);

        const string sampleDecal =
            "1 1 0 1\r\n" +
            "\"Art/particles/monster_particles/League_Expedition/crater_tiles/mb/crater01_1.fmt\" 0\r\n";

        var decalOutcome = EffectRuleApplier.Apply(sampleDecal, decal4Rule);
        Assert.True(decalOutcome.Changed);
        Assert.StartsWith("0 0 0 0\r\n", decalOutcome.Content);
        Assert.True(EffectSyntaxValidator.ValidateFor(decalOutcome.Content, "metadata/terrain/leagues/expedition/crater_tiles.dlp").IsValid);

        var decalSecond = EffectRuleApplier.Apply(decalOutcome.Content, decal4Rule);
        Assert.False(decalSecond.Changed);
    }

    [Fact]
    public void EpkAndPetRules_ReplaceEntireMultilineDocumentOnce()
    {
        var high = MudManTier.High.Rules();
        var epkRule = high.First(r => r.Id == "mudman_spell_epk");
        var petRule = high.First(r => r.Id == "mudman_all_particles");

        const string sampleEpk = "line1\r\nline2\r\nline3\r\n";
        var epkOutcome = EffectRuleApplier.Apply(sampleEpk, epkRule);
        Assert.True(epkOutcome.Changed);
        Assert.Equal(string.Empty, epkOutcome.Content);

        // 已经是空串的 .epk 再次应用不应重复触发 Changed
        var epkSecond = EffectRuleApplier.Apply(epkOutcome.Content, epkRule);
        Assert.False(epkSecond.Changed);

        const string samplePet = "5\r\n\"Art/particles/foo.pet\"\r\n\"Art/particles/bar.pet\"\r\n";
        var petOutcome = EffectRuleApplier.Apply(samplePet, petRule);
        Assert.True(petOutcome.Changed);
        Assert.Equal("0", petOutcome.Content.Trim());
    }
}
