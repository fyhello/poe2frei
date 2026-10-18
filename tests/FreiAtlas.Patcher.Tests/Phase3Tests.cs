using System.Text;
using FreiAtlas.Patcher;

namespace FreiAtlas.Patcher.Tests;

public class Phase3Tests
{
    [Fact]
    public void MapBeam_GenerateBeamWrapperContent_ProducesValidStructureAndMaterials()
    {
        var farudinColor = MapBeamPatchService.GetColor("Farudin");
        string copyPath = "Metadata/Terrain/WorldMaps/Maps/Doodads/Pins/EF_SceneryChild_abcdef12.ao";

        string content = MapBeamPatchService.GenerateBeamWrapperContent(copyPath, farudinColor);

        Assert.Contains("extends \"Metadata/FmtParent\"", content);
        Assert.Contains(copyPath, content);
        Assert.Contains("VaalCitadelMap_pin_01.fmt", content);
        Assert.Contains("lightbeam_Farudin.mat:0", content);
        Assert.Contains("lightbeam_Farudin_Secondary.mat:0", content);
        Assert.Contains("MapCoin01_White.mat:0", content);
    }

    [Fact]
    public void MapBeam_Fnv1a32Hex8_ProducesConsistent8CharHex()
    {
        string path = "Metadata/Terrain/WorldMaps/Maps/Doodads/Maps/Castaway.ao";
        string hash1 = MapBeamPatchService.Fnv1a32Hex8(path);
        string hash2 = MapBeamPatchService.Fnv1a32Hex8(path);

        Assert.Equal(hash1, hash2);
        Assert.Equal(8, hash1.Length);

        string copyPath = MapBeamPatchService.GetCopyFilePath(path);
        Assert.StartsWith("Metadata/Terrain/WorldMaps/Maps/Doodads/Pins/EF_SceneryChild_", copyPath);
        Assert.EndsWith(".ao", copyPath);
    }

    [Fact]
    public void PricePatch_AppendAndCleanWorkflow_SuccessfullyModifiesAndRestores()
    {
        int rowCount = 2;
        int rowSize = 40;
        int stringBase = 4 + rowCount * rowSize;

        using var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(rowCount), 0, 4);

        byte[] emptyRow = new byte[rowSize];
        ms.Write(emptyRow, 0, emptyRow.Length);
        ms.Write(emptyRow, 0, emptyRow.Length);

        // 写入哨兵 0xBB * 8
        ms.Write(new byte[] { 187, 187, 187, 187, 187, 187, 187, 187 }, 0, 8);

        string meta0 = "Metadata/Items/Currency/CurrencyMirror";
        string name0 = "Mirror of Kalandra";
        uint meta0Offset = (uint)(ms.Length - stringBase);
        ms.Write(Encoding.Unicode.GetBytes(meta0));
        ms.Write(new byte[4]);

        uint name0Offset = (uint)(ms.Length - stringBase);
        ms.Write(Encoding.Unicode.GetBytes(name0));
        ms.Write(new byte[4]);

        string meta1 = "Metadata/Items/Currency/CurrencyDivine";
        string name1 = "Divine Orb";
        uint meta1Offset = (uint)(ms.Length - stringBase);
        ms.Write(Encoding.Unicode.GetBytes(meta1));
        ms.Write(new byte[4]);

        uint name1Offset = (uint)(ms.Length - stringBase);
        ms.Write(Encoding.Unicode.GetBytes(name1));
        ms.Write(new byte[4]);

        byte[] originalDat = ms.ToArray();
        Buffer.BlockCopy(BitConverter.GetBytes(meta0Offset), 0, originalDat, 4, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(name0Offset), 0, originalDat, 4 + 32, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(meta1Offset), 0, originalDat, 4 + rowSize, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(name1Offset), 0, originalDat, 4 + rowSize + 32, 4);

        // 1. 验证 Append 模式基于物品名称注入物价
        var items = PricePatchService.ScanBaseItemNames(originalDat);
        Assert.Equal(2, items.Count);

        var prices = new List<PriceRow>
        {
            new PriceRow("Mirror of Kalandra", "500d"),
            new PriceRow("Divine Orb", "400e")
        };

        var (replacements, warnings) = PricePatchService.BuildReplacements(
            items,
            prices,
            separator: " ",
            keepExistingPrice: false,
            mode: "append",
            patchSameNameDuplicates: true);

        Assert.Equal(2, replacements.Count);

        byte[] patchedDat = PricePatchService.ApplyReplacementsAppend(originalDat, replacements);
        Assert.True(patchedDat.Length > originalDat.Length);

        // 读取注入后的名称
        var namesAfterPatch = PricePatchService.ScanBaseItemNames(patchedDat);
        Assert.Equal(2, namesAfterPatch.Count);
        Assert.Equal("Mirror of Kalandra [500d]", namesAfterPatch[0].Name);
        Assert.Equal("Divine Orb [400e]", namesAfterPatch[1].Name);

        // 2. 验证 Clean 模式清洗后缀
        var (cleanReplacements, cleanWarnings) = PricePatchService.BuildReplacements(
            namesAfterPatch,
            [],
            separator: " ",
            keepExistingPrice: false,
            mode: "append",
            patchSameNameDuplicates: true);

        Assert.Equal(2, cleanReplacements.Count);
        byte[] cleanedDat = PricePatchService.ApplyReplacementsAppend(patchedDat, cleanReplacements);

        var namesAfterClean = PricePatchService.ScanBaseItemNames(cleanedDat);
        Assert.Equal(2, namesAfterClean.Count);
        Assert.Equal("Mirror of Kalandra", namesAfterClean[0].Name);
        Assert.Equal("Divine Orb", namesAfterClean[1].Name);
    }

    [Fact]
    public void ColorModOverrides_LoadsAuthoritativeJson_Successfully()
    {
        Assert.True(ColorModOverrides.IsLoaded, $"Overrides 未能成功加载: {ColorModOverrides.LoadError}");
        Assert.True(ColorModOverrides.BlockCount > 500, $"规则条数异常: {ColorModOverrides.BlockCount}");
        Assert.NotEmpty(ColorModOverrides.AllStatIds);

        // 验证常见词缀的 NamedColor 映射与 SimpleStats
        var redRgb = ColorModOverrides.Rgb("DA1");
        Assert.NotNull(redRgb);
        Assert.Equal(3, redRgb.Length);
        Assert.Equal(227, redRgb[0]);
        Assert.Equal(23, redRgb[1]);
        Assert.Equal(13, redRgb[2]);
    }

    [Fact]
    public void ColorModService_TransformColorMods_NoRedundantBracketsAndProtectsHoverTags()
    {
        // 构造包含多语言、引用说明和负抗性的真实 CSD 片段 (每块由 description + 统计名行 + 变体行组成)
        string sampleCsd =
            "description\n" +
            "\t1 additional_maximum_all_elemental_resistances_%\n" +
            "\t1\n" +
            "\t\t#\n" +
            "\t\t\"{0:+d}% 最大全元素抗性\"\n" +
            "description\n" +
            "\t1 monster_penetrate_elemental_resistances_%\n" +
            "\t1\n" +
            "\t\t#\n" +
            "\t\t\"怪物伤害穿透 {0}% [Keyword|元素抗性]\"\n";

        byte[] csdBytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(sampleCsd)).ToArray();

        var colorMap = new Dictionary<string, byte[]>
        {
            ["additional_maximum_all_elemental_resistances_%"] = [0, 255, 127],
            ["monster_penetrate_elemental_resistances_%"] = [227, 23, 13]
        };

        var styleMap = new Dictionary<string, ColorModStyle>
        {
            ["additional_maximum_all_elemental_resistances_%"] = ColorModStyle.Whole,
            ["monster_penetrate_elemental_resistances_%"] = ColorModStyle.Whole
        };

        byte[] transformed = ColorModService.TransformColorMods(
            "stat_descriptions.csd",
            csdBytes,
            colorMap,
            styleMap,
            null);

        string resultText = Encoding.Unicode.GetString(transformed, 2, transformed.Length - 2);

        // 1. 验证染色标签已注入
        Assert.Contains("<rgb(", resultText);

        // 2. 验证绝对不存在多余的四重大括号或字面括号泄漏
        Assert.DoesNotContain("{{{{", resultText);
        Assert.DoesNotContain("}}}}", resultText);

        // 3. 验证游戏原生的引用说明标签 [Keyword|元素抗性] 结构完好，未被截断破坏
        Assert.Contains("[Keyword|元素抗性]", resultText);
    }

    [Fact]
    public void ColorModService_PlannedBlock_AppliesAuthoritativeSlotPlan()
    {
        // 验证从 colormod_overrides.json 中权威定义的 BlockPlan (如 atlas_stat_descriptions.csd 的神圣石掉落)
        string atlasCsd =
            "description\r\n" +
            "\t1 map_currency_divine_stack_4_permillage_chance\r\n" +
            "\t1\r\n" +
            "\t\t# \"掉落的神圣石有额外堆叠\"\r\n" +
            "\tlang \"Simplified Chinese\"\r\n" +
            "\t\t# \"掉落的神圣石有额外堆叠\"\r\n";

        byte[] csdBytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(atlasCsd)).ToArray();

        var colorMap = new Dictionary<string, byte[]>
        {
            ["map_currency_divine_stack_4_permillage_chance"] = [231, 179, 37]
        };

        byte[] transformed = ColorModService.TransformColorMods(
            "atlas_stat_descriptions.csd",
            csdBytes,
            colorMap,
            null,
            null);

        string resultText = Encoding.Unicode.GetString(transformed, 2, transformed.Length - 2);

        // 验证权威规则只精确包裹“神圣石”，不破坏整句，并生成合规标签
        Assert.Contains("<rgb(231,179,37)>{{神圣石}}", resultText);
    }

    [Fact]
    public void PatchBackupManager_ComputeDirectoryHash_IsDeterministicAndSafe()
    {
        var mgr = new PatchBackupManager();
        var info1 = new GameModeInfo { IsValid = true, GameDirectory = @"C:\Games\Path of Exile 2", Mode = GameMode.GGPK };
        var info2 = new GameModeInfo { IsValid = true, GameDirectory = @"c:/games/path of exile 2/", Mode = GameMode.GGPK };
        var infoDiff = new GameModeInfo { IsValid = true, GameDirectory = @"D:\Games\PoE2_TW", Mode = GameMode.GGPK };

        string p1 = mgr.GetBackupFilePath(info1);
        string p2 = mgr.GetBackupFilePath(info2);
        string pDiff = mgr.GetBackupFilePath(infoDiff);

        // 规范化路径大小写与斜杠后，哈希应一致，实现同目录稳定命中
        Assert.Equal(p1, p2);
        // 不同游戏路径隔离存储，杜绝相互覆盖
        Assert.NotEqual(p1, pDiff);
    }

    [Fact]
    public void PatchBackupManager_CheckResourceLock_ReportsClearReasonOnLockedFile()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "frei_test_lock_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string testFile = Path.Combine(tempDir, "Content.ggpk");
        File.WriteAllBytes(testFile, [1, 2, 3, 4]);

        var info = new GameModeInfo { IsValid = true, GameDirectory = tempDir, Mode = GameMode.GGPK };

        try
        {
            // 未锁定时检测正常
            var (unlocked, _) = PatchBackupManager.CheckResourceLock(info);
            Assert.False(unlocked);

            // 模拟外部进程（如下载器或更新程序）独占锁住文件
            using (var lockStream = new FileStream(testFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var (isLocked, reason) = PatchBackupManager.CheckResourceLock(info);
                Assert.True(isLocked);
                Assert.NotNull(reason);
                Assert.Contains("占用", reason);
            }

            // 释放锁后恢复正常
            var (released, _) = PatchBackupManager.CheckResourceLock(info);
            Assert.False(released);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PatchBackupManager_Restore_WhenBackupMissing_ReturnsAccurateMissingMessage()
    {
        string emptyTemp = Path.Combine(Path.GetTempPath(), "frei_nobak_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyTemp);
        var info = new GameModeInfo { IsValid = true, GameDirectory = emptyTemp, Mode = GameMode.Bundles2 };

        var mgr = new PatchBackupManager(Path.Combine(emptyTemp, "empty_backups"));
        var result = mgr.Restore(info);

        Assert.False(result.Success);
        Assert.Contains("找不到纯净备份文件", result.Message);
    }

    [Fact]
    public void PatchSnapshot_CreateManualAndAutoSnapshots_RotatesAutoTo5AndKeepsManual()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "frei_snap_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string b2Dir = Path.Combine(tempDir, "Bundles2");
        Directory.CreateDirectory(b2Dir);
        string indexPath = Path.Combine(b2Dir, "_.index.bin");
        File.WriteAllBytes(indexPath, Encoding.UTF8.GetBytes("ORIGINAL_INDEX_DATA"));

        var info = new GameModeInfo { IsValid = true, GameDirectory = tempDir, Mode = GameMode.Bundles2 };
        string customBak = Path.Combine(tempDir, "backups");
        var mgr = new PatchBackupManager(customBak);

        try
        {
            // 1. 初始化官方底包
            var off = mgr.CreateSnapshot(info, PatchSnapshotType.Official, "🌟 官方出厂纯净底包", "官方纯净底包");
            Assert.True(off.Success);

            // 2. 创建 2 个手动快照
            var m1 = mgr.CreateSnapshot(info, PatchSnapshotType.Manual, "手动版本A", "描述A", "{\"key\":\"val1\"}");
            var m2 = mgr.CreateSnapshot(info, PatchSnapshotType.Manual, "手动版本B", "描述B", "{\"key\":\"val2\"}");
            Assert.True(m1.Success);
            Assert.True(m2.Success);

            // 3. 连续创建 7 个自动快照
            for (int i = 1; i <= 7; i++)
            {
                Thread.Sleep(10);
                var a = mgr.CreateSnapshot(info, PatchSnapshotType.Auto, $"自动快照_{i}", $"自动描述_{i}");
                Assert.True(a.Success);
            }

            var all = mgr.GetSnapshots(info);

            // 官方底包：1 个
            Assert.Single(all, s => s.Type == PatchSnapshotType.Official);
            // 手动备份：全部保留，共 2 个
            Assert.Equal(2, all.Count(s => s.Type == PatchSnapshotType.Manual));
            // 自动快照：严格轮转淘汰，仅保留最近 5 个 (3, 4, 5, 6, 7)
            var autos = all.Where(s => s.Type == PatchSnapshotType.Auto).ToList();
            Assert.Equal(5, autos.Count);
            Assert.Contains(autos, s => s.Name == "自动快照_7");
            Assert.Contains(autos, s => s.Name == "自动快照_3");
            Assert.DoesNotContain(autos, s => s.Name == "自动快照_1");
            Assert.DoesNotContain(autos, s => s.Name == "自动快照_2");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PatchSnapshot_OfficialSnapshot_IsProtectedFromDeletion()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "frei_snap_lock_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string b2Dir = Path.Combine(tempDir, "Bundles2");
        Directory.CreateDirectory(b2Dir);
        string indexPath = Path.Combine(b2Dir, "_.index.bin");
        File.WriteAllBytes(indexPath, Encoding.UTF8.GetBytes("CLEAN_BASE"));

        var info = new GameModeInfo { IsValid = true, GameDirectory = tempDir, Mode = GameMode.Bundles2 };
        var mgr = new PatchBackupManager(Path.Combine(tempDir, "backups"));

        try
        {
            var off = mgr.CreateSnapshot(info, PatchSnapshotType.Official, "🌟 官方出厂纯净底包", "官方纯净底包");
            Assert.True(off.Success);
            bool deleted = mgr.DeleteSnapshot(info, PatchBackupManager.OfficialSnapshotId, out string message);
            Assert.False(deleted);
            Assert.Contains("禁止删除", message);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PatchSnapshot_RestoreSnapshot_RestoresContentAndOutputsSettingsJson()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "frei_snap_restore_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string b2Dir = Path.Combine(tempDir, "Bundles2");
        Directory.CreateDirectory(b2Dir);
        string indexPath = Path.Combine(b2Dir, "_.index.bin");
        File.WriteAllBytes(indexPath, Encoding.UTF8.GetBytes("VERSION_1_CONTENT"));

        var info = new GameModeInfo { IsValid = true, GameDirectory = tempDir, Mode = GameMode.Bundles2 };
        var mgr = new PatchBackupManager(Path.Combine(tempDir, "backups"));

        try
        {
            const string expectedSettings = "{\"ColorModEnabled\":true,\"MapBeamEnabled\":false}";
            var createRes = mgr.CreateSnapshot(info, PatchSnapshotType.Manual, "快照V1", "测试备注", expectedSettings);
            Assert.True(createRes.Success);

            var snapshots = mgr.GetSnapshots(info);
            var v1Snapshot = snapshots.First(s => s.Name == "快照V1");

            // 模拟客户端文件被后续补丁修改
            File.WriteAllBytes(indexPath, Encoding.UTF8.GetBytes("VERSION_2_MODIFIED_CONTENT"));

            // 执行快照还原
            var restoreRes = mgr.RestoreSnapshot(info, v1Snapshot.Id, out string? restoredSettings);
            Assert.True(restoreRes.Success);
            Assert.Equal(expectedSettings, restoredSettings);

            // 验证客户端文件已被准确回滚为快照内容
            string contentAfterRestore = Encoding.UTF8.GetString(File.ReadAllBytes(indexPath));
            Assert.Equal("VERSION_1_CONTENT", contentAfterRestore);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Rumour_BuiltinRules_CorrectlyMatchesTraditionalAndSimplifiedRumours()
    {
        // 繁体中文匹配
        var (ruleTradNoRevive, textTradNoRevive) = RumourPatchService.MatchRule("沒東西可喝……", "", isChina: false);
        Assert.NotNull(ruleTradNoRevive);
        Assert.Equal("致命机制", ruleTradNoRevive.Category);
        Assert.Equal("#ef4444", ruleTradNoRevive.Color);
        Assert.Equal("[<rgb(227,158,255)>{無復活}]沒東西可喝……", textTradNoRevive);

        var (ruleTradBadMap, textTradBadMap) = RumourPatchService.MatchRule("荒涼又糟糕……", "", isChina: false);
        Assert.NotNull(ruleTradBadMap);
        Assert.Equal("贫瘠差图", ruleTradBadMap.Category);
        Assert.Equal("[差圖]荒涼又糟糕……", textTradBadMap);

        // 简体中文匹配
        var (ruleSimpCastaway, textSimpCastaway) = RumourPatchService.MatchRule("所有闪闪发光之物……", "", isChina: true);
        Assert.NotNull(ruleSimpCastaway);
        Assert.Equal("暗金神图", ruleSimpCastaway.Category);
        Assert.Equal("#06b6d4", ruleSimpCastaway.Color);
        Assert.Equal("[<rgb(14,186,255)>{颠沛领域}]所有闪闪发光之物……", textSimpCastaway);

        var (ruleSimpNoDrink, textSimpNoDrink) = RumourPatchService.MatchRule("没东西喝……", "", isChina: true);
        Assert.NotNull(ruleSimpNoDrink);
        Assert.Equal("[<rgb(227,158,255)>{无复活}]没东西喝……", textSimpNoDrink);
    }

    [Fact]
    public void Rumour_ApplyPatchAndVerify_CorrectlyModifiesPointerAtRowIndex()
    {
        int rowCount = 2;
        int rowSize = 243;
        int stringBase = 4 + rowCount * rowSize;

        using var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(rowCount), 0, 4);

        byte[] emptyRow = new byte[rowSize];
        ms.Write(emptyRow, 0, emptyRow.Length);
        ms.Write(emptyRow, 0, emptyRow.Length);

        // 写入哨兵 0xBB * 8
        ms.Write(new byte[] { 187, 187, 187, 187, 187, 187, 187, 187 }, 0, 8);

        // Row 0: MapName = "漂流者之所", RumourText = "閃光的未必是金……"
        string mapName0 = "漂流者之所";
        string rumour0 = "閃光的未必是金……";

        uint rumour0Offset = (uint)(ms.Length - stringBase);
        ms.Write(Encoding.Unicode.GetBytes(rumour0));
        ms.Write(new byte[4]);

        // Row 1: MapName = "廢棄礦井", RumourText = "沒東西可喝……"
        string rumour1 = "沒東西可喝……";

        uint rumour1Offset = (uint)(ms.Length - stringBase);
        ms.Write(Encoding.Unicode.GetBytes(rumour1));
        ms.Write(new byte[4]);

        byte[] originalDat = ms.ToArray();
        // 设置 Row 0 和 Row 1 的 RumourTextOffset
        Buffer.BlockCopy(BitConverter.GetBytes(rumour0Offset), 0, originalDat, 4 + 0 * rowSize + RumourPatchService.RumourTextOffset, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(rumour1Offset), 0, originalDat, 4 + 1 * rowSize + RumourPatchService.RumourTextOffset, 4);

        var waNames = new Dictionary<int, string>
        {
            [0] = mapName0
        };

        // 1. 扫描客户端传言
        var scanned = RumourPatchService.ParseFromBytes(originalDat, waNames, isChina: false);
        Assert.Equal(2, scanned.Count);
        Assert.Equal(0, scanned[0].RowIndex);
        Assert.Equal("漂流者之所", scanned[0].MapName);
        Assert.Equal("閃光的未必是金……", scanned[0].OriginalRumourText);
        Assert.Equal("暗金神图", scanned[0].Category);

        Assert.Equal(1, scanned[1].RowIndex);
        Assert.Equal("沒東西可喝……", scanned[1].OriginalRumourText);
        Assert.Equal("致命机制", scanned[1].Category);

        // 2. 闭环修改并写入（只启用 Row 1）
        scanned[0].Enabled = false;
        scanned[1].Enabled = true;
        scanned[1].RumourText = "[<rgb(227,158,255)>{無復活}]沒東西可喝……";

        byte[] patchedDat = RumourPatchService.ApplyRumourChanges(originalDat, scanned);
        Assert.True(patchedDat.Length > originalDat.Length);

        // 3. 回读校验 Row 1 已被精准修改，Row 0 保持不变
        var postScan = RumourPatchService.ParseFromBytes(patchedDat, waNames, isChina: false);
        Assert.Equal(2, postScan.Count);
        Assert.Equal("閃光的未必是金……", postScan[0].OriginalRumourText);
        Assert.Equal("[<rgb(227,158,255)>{無復活}]沒東西可喝……", postScan[1].OriginalRumourText);
    }
}
