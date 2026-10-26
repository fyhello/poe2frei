using FreiAtlas.Core.Settings;
using FreiAtlas.Patcher;
using FreiAtlas.Patcher.EffectPatch;
using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.Tests;

public sealed class SpellEffectBatchTests
{
    [Fact]
    public void ApplyBatch在客户端无效时返回失败()
    {
        var blocker = new SpellEffectBlocker();
        var invalidInfo = new GameModeInfo { IsValid = false, ErrorMessage = "路径无效" };

        bool ok = blocker.ApplyBatch(invalidInfo, ["lightning_spark"], true, null, out var message);

        Assert.False(ok);
        Assert.Contains("路径无效", message);
    }

    [Fact]
    public void ApplyBatch在列表为空时返回拒绝()
    {
        var blocker = new SpellEffectBlocker();
        var info = new GameModeInfo { IsValid = true, Mode = GameMode.GGPK, GameDirectory = @"C:\Fake" };

        bool ok = blocker.ApplyBatch(info, [], true, null, out var message);

        Assert.False(ok);
        Assert.Equal("未指定任何需要屏蔽的技能。", message);
    }

    [Fact]
    public void RestoreBatch在列表为空时返回拒绝()
    {
        var blocker = new SpellEffectBlocker();
        var info = new GameModeInfo { IsValid = true, Mode = GameMode.GGPK, GameDirectory = @"C:\Fake" };

        bool ok = blocker.RestoreBatch(info, [], null, out var message);

        Assert.False(ok);
        Assert.Equal("未指定任何需要还原的技能。", message);
    }

    [Fact]
    public void ClientPatchSettings能够正常持久化新字段()
    {
        var settings = ClientPatchSettings.Default with
        {
            EffectPatchRuleIds = ["minimap_visibility", "view_distance"],
            BlockedSpellIds = ["lightning_spark"],
            SpellBlockIncludeAudio = false
        };

        Assert.NotNull(settings.EffectPatchRuleIds);
        Assert.Equal(2, settings.EffectPatchRuleIds.Count);
        Assert.Contains("minimap_visibility", settings.EffectPatchRuleIds);

        Assert.NotNull(settings.BlockedSpellIds);
        Assert.Single(settings.BlockedSpellIds);
        Assert.Equal("lightning_spark", settings.BlockedSpellIds[0]);

        Assert.False(settings.SpellBlockIncludeAudio);
    }

    [Fact]
    public void TryMigrateLegacy_旧目录存在时迁移到新目录并清理旧目录()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "FreiAtlasTest_" + Guid.NewGuid().ToString("N"));
        var legacyDir = Path.Combine(tempRoot, "spell_backup");
        var targetDir = Path.Combine(tempRoot, "patch_file_backup");

        try
        {
            var spellDir = Path.Combine(legacyDir, "test_spell");
            Directory.CreateDirectory(spellDir);
            File.WriteAllText(Path.Combine(spellDir, "manifest.json"), "{}");
            File.WriteAllText(Path.Combine(spellDir, "sample.pet"), "0");

            PatchFileBackup.TryMigrateLegacy(legacyDir, targetDir);

            // 目标目录应完整存在文件
            var destSpellDir = Path.Combine(targetDir, "test_spell");
            Assert.True(Directory.Exists(destSpellDir));
            Assert.True(File.Exists(Path.Combine(destSpellDir, "manifest.json")));
            Assert.True(File.Exists(Path.Combine(destSpellDir, "sample.pet")));

            // 旧目录由于空了应被自动清理
            Assert.False(Directory.Exists(legacyDir));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [Fact]
    public void TryMigrateLegacy_目标目录已有文件时不覆盖()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "FreiAtlasTest_" + Guid.NewGuid().ToString("N"));
        var legacyDir = Path.Combine(tempRoot, "spell_backup");
        var targetDir = Path.Combine(tempRoot, "patch_file_backup");

        try
        {
            var legacySpell = Path.Combine(legacyDir, "test_spell");
            Directory.CreateDirectory(legacySpell);
            File.WriteAllText(Path.Combine(legacySpell, "conflict.txt"), "legacy_data");
            File.WriteAllText(Path.Combine(legacySpell, "new_file.txt"), "legacy_new");

            var targetSpell = Path.Combine(targetDir, "test_spell");
            Directory.CreateDirectory(targetSpell);
            File.WriteAllText(Path.Combine(targetSpell, "conflict.txt"), "target_data");

            PatchFileBackup.TryMigrateLegacy(legacyDir, targetDir);

            // 目标目录 conflict.txt 应保持原有 target_data，不被覆盖
            Assert.Equal("target_data", File.ReadAllText(Path.Combine(targetSpell, "conflict.txt")));
            // 目标目录新文件应当已迁移过来
            Assert.Equal("legacy_new", File.ReadAllText(Path.Combine(targetSpell, "new_file.txt")));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [Fact]
    public void TryMigrateLegacy_旧目录不存在时安全通过()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "FreiAtlasTest_" + Guid.NewGuid().ToString("N"));
        var legacyDir = Path.Combine(tempRoot, "non_existent");
        var targetDir = Path.Combine(tempRoot, "patch_file_backup");

        // 不应抛出异常
        var ex = Record.Exception(() => PatchFileBackup.TryMigrateLegacy(legacyDir, targetDir));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData(1.5, "1.5f")]
    [InlineData(2.0, "2.0f")]
    [InlineData(2.5, "2.5f")]
    [InlineData(3.0, "3.0f")]
    public void CreateViewDistanceRule_生成正确倍率并支持热重写(double ratio, string expectedStr)
    {
        var rule = EffectPatchRules.CreateViewDistanceRule(ratio);
        Assert.Contains(expectedStr, rule.Replacement);
        Assert.Contains(expectedStr, rule.AppliedSentinel);

        // 1. 在原始文件内容上应用
        string rawOt = "character\n{\n\tteam = 1\n}\n";
        var outcome1 = EffectRuleApplier.Apply(rawOt, rule);
        Assert.True(outcome1.Changed);
        Assert.Contains(expectedStr, outcome1.Content);

        // 2. 相同倍率重复应用应幂等跳过
        var outcome2 = EffectRuleApplier.Apply(outcome1.Content, rule);
        Assert.False(outcome2.Changed);

        // 3. 切换为另一倍率（例如从当前倍率切换为 2.8x）
        var ruleNew = EffectPatchRules.CreateViewDistanceRule(2.8);
        var outcomeSwitch = EffectRuleApplier.Apply(outcome1.Content, ruleNew);
        Assert.True(outcomeSwitch.Changed);
        Assert.Contains("2.8f", outcomeSwitch.Content);
        Assert.DoesNotContain(expectedStr, outcomeSwitch.Content);
    }

    [Fact]
    public void Poe2SpellBlockDatabase_内嵌资源正常加载且包含110项选项与105个分类()
    {
        var options = Poe2SpellBlockDatabase.Options;
        var entries = Poe2SpellBlockDatabase.Entries;
        var pathRecords = Poe2SpellBlockDatabase.GlobalPathRecords;
        var exactPaths = Poe2SpellBlockDatabase.ExactAoAocEpkPaths;

        Assert.NotNull(options);
        Assert.Equal(110, options.Count);
        Assert.Contains(options, o => o.Name == "电球" && o.Value == "lightning_spark");
        Assert.Contains(options, o => o.Name == "彗星落（保留球体）" && o.Value == "poe2_skill:Comet_With_Ball");
        Assert.Contains(options, o => o.Name == "寒冰弹（保留球体）" && o.Value == "poe2_skill:Frostbolt_With_Ball");
        Assert.Contains(options, o => o.Name == "冰霜射击" && o.Value == "lightning_arrow|bow_ice_shot");

        Assert.NotNull(entries);
        Assert.Equal(105, entries.Count);

        Assert.NotNull(pathRecords);
        Assert.True(pathRecords.Count > 1000);

        Assert.NotNull(exactPaths);
        Assert.True(exactPaths.Count > 500);
    }

    [Fact]
    public void Poe2SpellBlockDatabase_Plan_彗星与寒冰弹保留球体互斥生效()
    {
        // 1. 普通彗星全清：包含彗星球体粒子 cometbody.pet
        var planAll = Poe2SpellBlockDatabase.Plan(["cold_comet"], true);
        Assert.NotEmpty(planAll.PlannedFiles);
        Assert.Contains("metadata/effects/spells/cold_comet/fx/cometbody.pet", planAll.PlannedFiles.Keys);

        // 2. 勾选了保留球体版本：排除 cometbody.pet
        var planWithBall = Poe2SpellBlockDatabase.Plan(["poe2_skill:Comet_With_Ball"], true);
        Assert.NotEmpty(planWithBall.PlannedFiles);
        Assert.DoesNotContain("metadata/effects/spells/cold_comet/fx/cometbody.pet", planWithBall.PlannedFiles.Keys);

        // 3. 同时勾选两者时，互斥消解排除 s_skill@Comet，保留球体生效
        var planBoth = Poe2SpellBlockDatabase.Plan(["cold_comet", "poe2_skill:Comet_With_Ball"], true);
        Assert.NotEmpty(planBoth.PlannedFiles);
        Assert.DoesNotContain("metadata/effects/spells/cold_comet/fx/cometbody.pet", planBoth.PlannedFiles.Keys);

        // 寒冰弹同理：勾选保留球体时不屏蔽投射物主体粒子
        var planFrostboltBoth = Poe2SpellBlockDatabase.Plan(["poe2_skill:Frostbolt", "poe2_skill:Frostbolt_With_Ball"], true);
        Assert.NotEmpty(planFrostboltBoth.PlannedFiles);
    }

    [Fact]
    public void Poe2SpellBlockDatabase_Plan_冰霜射击纠偏不包含闪电箭矢()
    {
        var plan = Poe2SpellBlockDatabase.Plan(["lightning_arrow|bow_ice_shot"], true);
        Assert.Contains("bow_ice_shot", plan.FallbackKeywords);
        Assert.DoesNotContain("lightning_arrow", plan.FallbackKeywords);
    }

    [Fact]
    public void SpellEffectContentCleaner_CleanSkinMeshHideSegments_正确注入隐藏段()
    {
        string sampleAo = """
        Version 2
        client
        {
        	SkinMesh
        	{
        		mesh_name = "art/models/effects/ice.sm"
        	}
        }
        """;

        string cleaned = SpellEffectContentCleaner.CleanSkinMeshHideSegments(sampleAo, ["0", "1"]);
        Assert.Contains("hide_segments = \"0,1\"", cleaned);
        Assert.Contains("mesh_name = \"art/models/effects/ice.sm\"", cleaned);

        // 重复应用幂等
        string cleanedAgain = SpellEffectContentCleaner.CleanSkinMeshHideSegments(cleaned, ["0", "1"]);
        Assert.Equal(cleaned, cleanedAgain);
    }

    [Fact]
    public void SpellEffectContentCleaner_CleanOtDisableRendering_正确注入disable_rendering()
    {
        string sampleOt = """
        Version 1
        client
        {
        	Render
        	{
        		name = "wolf_pack"
        	}
        }
        """;

        string cleaned = SpellEffectContentCleaner.CleanOtDisableRendering(sampleOt);
        Assert.Contains("disable_rendering = true", cleaned);
        Assert.Contains("name = \"wolf_pack\"", cleaned);

        // 重复应用幂等
        string cleanedAgain = SpellEffectContentCleaner.CleanOtDisableRendering(cleaned);
        Assert.Equal(cleaned, cleanedAgain);
    }

    [Fact]
    public void SpellEffectContentCleaner_CleanWithPlan_支持各类动作()
    {
        // 1. Empty 动作（对齐易刷返回单空格 " " 避免文件为 0 字节）
        var emptyAction = new PlannedFileAction { Kind = PlannedActionKind.Empty };
        var emptyResult = SpellEffectContentCleaner.CleanWithPlan("test.epk", "any content", emptyAction);
        Assert.Equal(" ", emptyResult);

        // 2. HideSegments 动作
        var hideAction = new PlannedFileAction
        {
            Kind = PlannedActionKind.ClearBlockAndHideSegments,
            HideSegments = ["2", "3"]
        };
        string ao = "client\n{\n\tSkinMesh\n\t{\n\t\tname = 1\n\t}\n}";
        var hideResult = SpellEffectContentCleaner.CleanWithPlan("test.ao", ao, hideAction);
        Assert.Contains("hide_segments = \"2,3\"", hideResult);
    }
}
