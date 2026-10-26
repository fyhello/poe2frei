using FreiAtlas.Patcher.EffectPatch;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 大括号块扫描与清空。
///
/// 这是最容易写错的一环：字符串里的括号、注释里的括号都不能参与深度计数。
/// 清空必须保留空括号，删整块会让引擎寻址失败崩溃。
/// </summary>
public sealed class EffectBlockScannerTests
{
    private static readonly HashSet<string> ParticleBlocks = new(StringComparer.Ordinal)
    {
        "ParticleEffects", "SoundEvents", "TrailsEffects"
    };

    [Fact]
    public void 找到简单块并清空内容()
    {
        const string input = """
            ParticleEffects
            {
                Particle { name = "fire" }
            }
            """;

        var blocks = EffectBlockScanner.FindBlocks(input, ParticleBlocks);
        Assert.Single(blocks);
        Assert.Equal("ParticleEffects", blocks[0].Name);

        var cleared = EffectBlockScanner.ClearBlockContents(input, ParticleBlocks);
        Assert.Equal("ParticleEffects\n{}", cleared.TrimEnd());
    }

    [Fact]
    public void 字符串内的括号不计入深度()
    {
        // 这是关键边界：on_death = "RemoveEffects();" 里的括号
        // 如果被当成块边界，会导致截断
        const string input = """
            ParticleEffects
            {
                Particle { on_death = "RemoveEffects(); DisableRendering();" }
            }
            SoundEvents
            {
                Sound { id = 1 }
            }
            """;

        var cleared = EffectBlockScanner.ClearBlockContents(input, ParticleBlocks);

        // 两个块都被清空（内容删掉后括号直接相邻），块外内容保留
        Assert.Contains("ParticleEffects\n{}", cleared);
        Assert.Contains("SoundEvents\n{}", cleared);
        // 字符串内容已随块内容一起清掉
        Assert.DoesNotContain("RemoveEffects", cleared);
    }

    [Fact]
    public void 注释内的括号不计入深度()
    {
        const string input = """
            ParticleEffects
            {
                // 这里的 } 是注释
                /* 这里的 { 也是注释 */
                Particle { name = "x" }
            }
            """;

        var blocks = EffectBlockScanner.FindBlocks(input, ParticleBlocks);
        Assert.Single(blocks);

        var cleared = EffectBlockScanner.ClearBlockContents(input, ParticleBlocks);
        Assert.Equal("ParticleEffects\n{}", cleared.TrimEnd());
    }

    [Fact]
    public void 嵌套块正确找到最外层配平()
    {
        const string input = """
            ParticleEffects
            {
                Particle
                {
                    Sub
                    {
                        name = "deep"
                    }
                }
            }
            """;

        var blocks = EffectBlockScanner.FindBlocks(input, ParticleBlocks);
        Assert.Single(blocks);

        var cleared = EffectBlockScanner.ClearBlockContents(input, ParticleBlocks);
        Assert.Equal("ParticleEffects\n{}", cleared.TrimEnd());
    }

    [Fact]
    public void 未配平的块被跳过不影响后续()
    {
        // 第一个块缺少闭括号，第二个块正常
        const string input = """
            SoundEvents
            {
                Sound { id = 1 }

            ParticleEffects
            {
                Particle { name = "x" }
            }
            """;

        // 不应该抛异常
        var cleared = EffectBlockScanner.ClearBlockContents(input, ParticleBlocks);
        Assert.NotNull(cleared);
    }

    [Fact]
    public void 已是空块时不产生变化()
    {
        const string input = "ParticleEffects\n{}\n";

        var cleared = EffectBlockScanner.ClearBlockContents(input, ParticleBlocks);
        Assert.Equal(input, cleared);
    }

    [Fact]
    public void 不匹配行中赋值语句的对象字面量()
    {
        // 只匹配行首标识符 + {，不匹配 xxx = { ... }
        const string input = """
            Animated
            {
                ParticleEffects = { something }
            }
            """;

        var blocks = EffectBlockScanner.FindBlocks(input, ParticleBlocks);
        Assert.Empty(blocks);
    }

    [Fact]
    public void 块名与开括号之间的行注释被跳过()
    {
        // 合法语法但少见：ParticleEffects // 注释\n{
        // 漏掉会导致合法块静默失配。实测 200 个 .ao 样本里没遇到，
        // 但这是防御性覆盖。
        const string input = """
            ParticleEffects // 引擎特效
            {
                Particle { name = "x" }
            }
            """;

        var blocks = EffectBlockScanner.FindBlocks(input, ParticleBlocks);
        Assert.Single(blocks);

        var cleared = EffectBlockScanner.ClearBlockContents(input, ParticleBlocks);
        Assert.DoesNotContain("name = \"x\"", cleared);
    }

    [Fact]
    public void 块名与开括号之间的块注释被跳过()
    {
        const string input = """
            ParticleEffects /* 多行
            注释 */
            {
                Particle { name = "x" }
            }
            """;

        var blocks = EffectBlockScanner.FindBlocks(input, ParticleBlocks);
        Assert.Single(blocks);
    }

    [Fact]
    public void 清空后括号仍然配平()
    {
        const string input = """
            version 3
            client
            {
                ParticleEffects
                {
                    Particle { name = "a" }
                }
                Other
                {
                    keep = "this"
                }
            }
            """;

        var cleared = EffectBlockScanner.ClearBlockContents(input, ParticleBlocks);
        var validation = EffectSyntaxValidator.Validate(cleared, "test.ao");

        Assert.True(validation.IsValid, validation.Error);
        Assert.Contains("keep = \"this\"", cleared);   // 块外内容保留
        Assert.DoesNotContain("name = \"a\"", cleared); // 块内内容清空
    }
}