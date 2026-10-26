using System.Reflection;
using FreiAtlas.Patcher.EffectPatch;
using FreiAtlas.Patcher.EffectPatch.HealthBar;
using FreiAtlas.Patcher.EffectPatch.MudMan;
using FreiAtlas.Patcher.IconPatch;
using FreiAtlas.Patcher.MtxPatch;
using FreiAtlas.Patcher.Translation;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 补丁状态与数据结构的回归锁定。
///
/// 对应第二轮评审发现的缺陷：状态标记未清理导致的永久死锁、数据表行尺寸
/// 失配时的错位写入、图标模块缺失的备份还原能力。
/// </summary>
public sealed class PatchStateIntegrityTests
{
    /// <summary>
    /// 全量还原后必须能清掉泥人/血条的状态标记。
    ///
    /// 修复前这两个方法是 private 且还原流程从不调用它们，导致索引已回滚成
    /// 纯净版本、state.json 却仍记着 applied=true：再次应用同一档位时会被
    /// 判定为「已应用、无需写入」而直接跳过，用户再也打不上补丁。
    /// </summary>
    [Fact]
    public void 泥人与血条的状态清理入口必须对外可见()
    {
        var mudMan = typeof(MudManApplier).GetMethod(
            "ClearState", BindingFlags.Public | BindingFlags.Static);
        var healthBar = typeof(HealthBarApplier).GetMethod(
            "ClearState", BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(mudMan);
        Assert.NotNull(healthBar);
    }

    /// <summary>
    /// 全量还原时必须清理全部模块的逐文件备份分组。
    ///
    /// 早期实现只清了 effect_patch 和 mtx_patch_v1，漏掉 icon_patch_v1 与
    /// spell_block_v2。备份目录残留会让 IsIconPatchApplied() 这类「按目录是否
    /// 有文件判断」的状态查询继续返回 true，与已回滚的纯净客户端脱节。
    ///
    /// 清理方通过公开常量引用分组名，这里锁定三个「按目录判断状态」的模块
    /// 常量确实对外可见且取值稳定 —— 常量一旦私有化或改名，清理方就会编译失败。
    /// </summary>
    [Fact]
    public void 清理方引用的备份分组常量必须对外可见()
    {
        Assert.Equal("icon_patch_v1", IconPatchService.BackupGroup);
        Assert.Equal("spell_block_v2", SpellEffectBlocker.BackupGroup);
        Assert.Equal("mtx_patch_v1", MtxPatchApplier.BackupGroup);
    }

    /// <summary>
    /// 恢复默认补丁设置时不得清空用户已选的游戏目录。
    ///
    /// <see cref="FreiAtlas.Core.Settings.ClientPatchSettings.Default"/> 的
    /// CustomGameDirectory 是 null，整体覆盖会让非默认安装路径的用户
    /// 被迫重新选择客户端。
    /// </summary>
    [Fact]
    public void 默认设置可保留自定义游戏目录()
    {
        var defaults = FreiAtlas.Core.Settings.ClientPatchSettings.Default;
        Assert.Null(defaults.CustomGameDirectory);

        const string userDir = @"D:\Games\Path of Exile 2";
        var merged = defaults with { CustomGameDirectory = userDir };

        Assert.Equal(userDir, merged.CustomGameDirectory);
        // 其余补丁状态确实被复位
        Assert.Equal(-1, merged.MudManTier);
        Assert.False(merged.HealthBarEnabled);
        Assert.False(merged.IconPatchEnabled);
    }

    /// <summary>
    /// 图标模块必须提供备份分组与还原方法。
    ///
    /// 修复前该模块直接覆盖客户端 DDS、无任何备份，取消勾选只把内存开关置为
    /// false，客户端图标仍是替换后的版本（假关闭）。
    /// </summary>
    [Fact]
    public void 图标模块必须具备备份分组与还原方法()
    {
        Assert.Equal("icon_patch_v1", IconPatchService.BackupGroup);
        Assert.NotNull(typeof(IconPatchService).GetMethod("Restore"));
        Assert.NotNull(typeof(IconPatchService).GetMethod(
            "HasBackup", BindingFlags.Public | BindingFlags.Static));
    }

    /// <summary>
    /// 数据表行尺寸与文件实际布局不一致时必须放弃写入。
    ///
    /// 构造 rowCount=2、schema 声明 RowSize=16 但实际行区只有 8 字节/行的表：
    /// 真实落点是 (哨兵位置 - 4) / 2 = 8。二者不等时若继续按 schema 写指针，
    /// 会覆盖到 i32 数值或 foreignrow 索引上，客户端读取越界。
    /// </summary>
    [Fact]
    public void 行尺寸与文件布局不符时不得写入()
    {
        const int rowCount = 2;
        const int actualRowSize = 8;
        int stringBase = 4 + rowCount * actualRowSize;

        var buffer = new byte[stringBase + 0xBBBB];
        BitConverter.GetBytes(rowCount).CopyTo(buffer, 0);
        for (int i = stringBase; i < buffer.Length; i++) buffer[i] = 0xBB;

        var def = new DatTextPatcher.TablePatchDefinition
        {
            FileName = "test.datc64",
            RowSize = 16,
            RowPatches = [new DatTextPatcher.RowCellPatch { Row = 0, Col = 0, Text = "测试文本" }]
        };

        var (patched, count) = DatTextPatcher.PatchTable(buffer, def);

        Assert.Equal(0, count);
        Assert.Same(buffer, patched);
    }

    /// <summary>
    /// 行尺寸一致时正常注入，确认新增校验没有误伤合法表。
    /// </summary>
    [Fact]
    public void 行尺寸一致时正常注入()
    {
        const int rowCount = 2;
        const int rowSize = 8;
        int stringBase = 4 + rowCount * rowSize;

        var buffer = new byte[stringBase + 0xBBBB];
        BitConverter.GetBytes(rowCount).CopyTo(buffer, 0);
        for (int i = stringBase; i < buffer.Length; i++) buffer[i] = 0xBB;
        // 第一行第 0 列是合法指针：高 32 位为 0，偏移落在字符串区内
        BitConverter.GetBytes(0UL).CopyTo(buffer, 4);

        var def = new DatTextPatcher.TablePatchDefinition
        {
            FileName = "test.datc64",
            RowSize = rowSize,
            RowPatches = [new DatTextPatcher.RowCellPatch { Row = 0, Col = 0, Text = "测试" }]
        };

        var (patched, count) = DatTextPatcher.PatchTable(buffer, def);

        Assert.Equal(1, count);
        Assert.NotSame(buffer, patched);
    }
}
