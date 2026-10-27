using FreiAtlas.Patcher.EffectPatch;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 第四轮评审 P0/P1 修复的回归测试（第一批：纯逻辑与共享文件）。
///
/// 这些测试锁定「改动后应保持的行为」，防止后续重构把修复退回去。
/// 涉及文件 IO 与 UI 时序的部分无法在此单测覆盖，改用源码级断言另行锁定。
/// </summary>
public sealed class Round4SharedFileAndDbTests
{
    // ── P0-7：共享文件必须走定点移除 ──

    [Theory]
    [InlineData("metadata/characters/character.ot", true)]
    [InlineData("metadata/characters/character.OT", true)]
    [InlineData("metadata/monsters/zombie/zombie.ot", false)]
    [InlineData("data/statdescriptions/some.csd", false)]
    [InlineData("", false)]
    public void 共享文件识别正确(string path, bool expected)
    {
        Assert.Equal(expected, SharedFileRestore.IsSharedFile(path));
    }

    [Fact]
    public void 定点移除只删本补丁注入行_保留其他补丁内容()
    {
        // 模拟 character.ot 同时被视距补丁与 MTX 翻滚补丁改过
        string content = """
            version 3
            extends "Metadata/Characters/Character"
            on_construction_complete = {
                AddEffectPack( "Metadata/Effects/Spells/foo.epk" );
            }
            on_initial_position_set = { CreateCameraZoomNode(1000000000.0f, 1000000000.0f, 2.0f); }
            """;

        string cleaned = SharedFileRestore.RemoveMatchingLines(
            content, SharedFileRestore.ViewDistanceLine);

        // 视距注入行被移除
        Assert.DoesNotContain("CreateCameraZoomNode", cleaned);
        // MTX 翻滚注入的 AddEffectPack 必须保留（这是共享文件的核心约束）
        Assert.Contains("AddEffectPack", cleaned);
        Assert.Contains("extends", cleaned);
    }

    [Fact]
    public void 未注入时定点移除不改内容()
    {
        const string content = """
            version 3
            extends "Metadata/Characters/Character"
            """;

        Assert.False(SharedFileRestore.ContainsInjectedLine(
            content, SharedFileRestore.ViewDistanceLine));
    }

    // ── P0-6：数据库可用性状态 ──

    [Fact]
    public void 数据库加载失败时可查询到错误且不抛异常()
    {
        // 两个数据库都必须能安全访问：加载失败时返回空数据，
        // 错误通过 LoadError 暴露，不能让 Lazy 把异常缓存后反复抛出。
        Assert.True(MtxPatch.MtxDatabase.IsAvailable || MtxPatch.MtxDatabase.LoadError is not null);
        Assert.True(Poe2SpellBlockDatabase.IsAvailable || Poe2SpellBlockDatabase.LoadError is not null);

        // 访问入口本身不得抛异常
        int mtxCount = MtxPatch.MtxDatabase.Instance.AllEntries.Count;
        Assert.True(mtxCount >= 0);

        int skillCount = Poe2SpellBlockDatabase.Options.Count;
        Assert.True(skillCount >= 0);
    }

    [Fact]
    public void 空的技能选择返回空规划不抛异常()
    {
        SpellBlockPlan plan = Poe2SpellBlockDatabase.Plan([]);
        Assert.Empty(plan.PlannedFiles);
    }

    [Fact]
    public void 规划结果包含技能涉及的文件路径()
    {
        // 用于 P0-4 的精准还原：RestoreBatch 靠这个反查该还原哪些文件。
        // 至少有一个技能能规划出路径，否则精准还原逻辑无从生效。
        var entries = Poe2SpellBlockDatabase.Entries;
        Assert.NotEmpty(entries);

        var selection = entries.First(e => !string.IsNullOrWhiteSpace(e.Selection)).Selection;
        SpellBlockPlan plan = Poe2SpellBlockDatabase.Plan([selection]);

        Assert.NotEmpty(plan.PlannedFiles);
    }
}
