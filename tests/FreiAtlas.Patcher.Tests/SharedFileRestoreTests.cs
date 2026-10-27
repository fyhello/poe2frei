using System.Text;
using FreiAtlas.Patcher.EffectPatch;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 共享文件定点还原的单元测试。
///
/// 背景：<c>metadata/characters/character.ot</c> 会被两个补丁写入 ——
/// 视距调整注入 <c>on_initial_position_set</c>，
/// MTX 翻滚注入 <c>AddEffectPack</c>。
///
/// 两者各自独立备份，若还原时整文件覆盖写回，会把另一个补丁的改动一起抹掉。
/// 本测试锁定「只移除本补丁注入内容」的行为。
/// </summary>
public class SharedFileRestoreTests
{
    /// <summary>模拟客户端原始 character.ot 中与两处注入相关的最小结构。</summary>
    private const string BaseOt = """
        Positioned
        {
        	blocking = true
        	team = 1
        }

        client
        {
        	Render
        	{
        		display_text = "ObjectDisplayTextPlayer"
        	}
        }
        """;

    [Fact]
    public void RemoveMatchingLines_RemovesViewDistanceLine()
    {
        const string injected = """
            Positioned
            {
            	blocking = true
            	team = 1
            	on_initial_position_set = { CreateCameraZoomNode(1000000000.0f, 1000000000.0f, 2.0f); }
            }

            client
            {
            	Render
            	{
            		display_text = "ObjectDisplayTextPlayer"
            	}
            }
            """;

        string result = SharedFileRestore.RemoveMatchingLines(
            injected, SharedFileRestore.ViewDistanceLine);

        Assert.DoesNotContain("CreateCameraZoomNode", result);
        Assert.Contains("team = 1", result);
        Assert.Contains("display_text", result);
    }

    [Fact]
    public void RemoveMatchingLines_KeepsOtherPatchContent()
    {
        // 同时含视距与 MTX 两处注入
        const string both = """
            Positioned
            {
            	team = 1
            	on_initial_position_set = { CreateCameraZoomNode(1000000000.0f, 1000000000.0f, 2.0f); }
            }

            client
            {
            	BaseEvents
            	{
            		on_construction_complete =
            		{
            			AddEffectPack( 'Metadata/Effects/Test/roll.epk' );
            		}
            	}

            	Render
            	{
            		display_text = "ObjectDisplayTextPlayer"
            	}
            }
            """;

        string result = SharedFileRestore.RemoveMatchingLines(
            both, SharedFileRestore.ViewDistanceLine);

        Assert.DoesNotContain("CreateCameraZoomNode", result);
        Assert.Contains("AddEffectPack", result);   // MTX 必须保留
    }

    [Fact]
    public void RemoveMatchingLines_NoMatch_ReturnsUnchanged()
    {
        string result = SharedFileRestore.RemoveMatchingLines(
            BaseOt, SharedFileRestore.ViewDistanceLine);

        Assert.Equal(BaseOt, result);
    }

    [Fact]
    public void RemoveEmptyInjectedBlocks_RemovesBothLayers()
    {
        // AddEffectPack 被移除后留下的两层空壳
        const string shell = """
            client
            {
            	BaseEvents
            	{
            		on_construction_complete =
            		{
            		}
            	}

            	Render
            	{
            		display_text = "ObjectDisplayTextPlayer"
            	}
            }
            """;

        string result = SharedFileRestore.RemoveEmptyInjectedBlocks(shell);

        Assert.DoesNotContain("BaseEvents", result);
        Assert.DoesNotContain("on_construction_complete", result);
        Assert.Contains("Render", result);
        Assert.Contains("display_text", result);
    }

    [Fact]
    public void RemoveEmptyInjectedBlocks_KeepsNonEmptyBlocks()
    {
        const string nonEmpty = """
            client
            {
            	BaseEvents
            	{
            		on_construction_complete =
            		{
            			AddEffectPack( 'Metadata/Effects/Test/roll.epk' );
            		}
            	}
            }
            """;

        string result = SharedFileRestore.RemoveEmptyInjectedBlocks(nonEmpty);

        // 非空块必须原样保留
        Assert.Contains("BaseEvents", result);
        Assert.Contains("AddEffectPack", result);
    }

    /// <summary>
    /// 完整往返：注入两处 → 全部移除 → 应与原始内容一致。
    /// </summary>
    [Fact]
    public void RoundTrip_BothPatchesRemoved_RestoresOriginal()
    {
        const string injected = """
            Positioned
            {
            	blocking = true
            	team = 1
            	on_initial_position_set = { CreateCameraZoomNode(1000000000.0f, 1000000000.0f, 2.0f); }
            }

            client
            {
            	BaseEvents
            	{
            		on_construction_complete =
            		{
            			AddEffectPack( 'Metadata/Effects/Test/roll.epk' );
            		}
            	}

            	Render
            	{
            		display_text = "ObjectDisplayTextPlayer"
            	}
            }
            """;

        string step1 = SharedFileRestore.RemoveMatchingLines(
            injected, SharedFileRestore.ViewDistanceLine);
        string step2 = SharedFileRestore.RemoveMatchingLines(
            step1, SharedFileRestore.DodgeRollLine);
        string step3 = SharedFileRestore.RemoveEmptyInjectedBlocks(step2);

        string Normalize(string s) =>
            s.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

        Assert.Equal(Normalize(BaseOt), Normalize(step3));
    }

    [Fact]
    public void ContainsInjectedLine_DetectsViewDistance()
    {
        const string injected = """
            Positioned
            {
            	team = 1
            	on_initial_position_set = { CreateCameraZoomNode(1.0f, 1.0f, 2.0f); }
            }
            """;

        Assert.True(SharedFileRestore.ContainsInjectedLine(
            injected, SharedFileRestore.ViewDistanceLine));
        Assert.False(SharedFileRestore.ContainsInjectedLine(
            BaseOt, SharedFileRestore.ViewDistanceLine));
    }

    [Theory]
    [InlineData("metadata/characters/character.ot")]
    [InlineData("Metadata/Characters/Character.ot")]
    [InlineData("metadata\\characters\\character.ot")]
    public void IsSharedFile_RecognizesCharacterOt(string path)
    {
        Assert.True(SharedFileRestore.IsSharedFile(path));
    }

    [Theory]
    [InlineData("metadata/monsters/monster.ot")]
    [InlineData("metadata/characters/other.ot")]
    [InlineData("")]
    public void IsSharedFile_RejectsOtherPaths(string path)
    {
        Assert.False(SharedFileRestore.IsSharedFile(path));
    }

    /// <summary>
    /// 对称性：还原 MTX（移除翻滚）时，视距注入必须保留。
    /// 这是补齐 MTX 侧接入后的核心保障。
    /// </summary>
    [Fact]
    public void TryRestoreSharedFile_RemoveDodgeRoll_KeepsViewDistance()
    {
        const string both = """
            Positioned
            {
            	team = 1
            	on_initial_position_set = { CreateCameraZoomNode(1000000000.0f, 1000000000.0f, 2.0f); }
            }

            client
            {
            	BaseEvents
            	{
            		on_construction_complete =
            		{
            			AddEffectPack( 'Metadata/Effects/Test/roll.epk' );
            		}
            	}
            }
            """;

        byte[] input = Encoding.Unicode.GetBytes(both);
        bool ok = SharedFileRestore.TryRestoreSharedFile(
            input,
            "metadata/characters/character.ot",
            SharedFileRestore.DodgeRollLine,
            out var cleaned,
            out var error);

        Assert.True(ok, error);
        Assert.NotNull(cleaned);

        string text = Encoding.Unicode.GetString(cleaned!);
        Assert.DoesNotContain("AddEffectPack", text);      // MTX 已移除
        Assert.Contains("CreateCameraZoomNode", text);     // 视距必须保留
    }

    /// <summary>
    /// 对称性：还原视距时，MTX 翻滚注入必须保留。
    /// </summary>
    [Fact]
    public void TryRestoreSharedFile_RemoveViewDistance_KeepsDodgeRoll()
    {
        const string both = """
            Positioned
            {
            	team = 1
            	on_initial_position_set = { CreateCameraZoomNode(1000000000.0f, 1000000000.0f, 2.0f); }
            }

            client
            {
            	BaseEvents
            	{
            		on_construction_complete =
            		{
            			AddEffectPack( 'Metadata/Effects/Test/roll.epk' );
            		}
            	}
            }
            """;

        byte[] input = Encoding.Unicode.GetBytes(both);
        bool ok = SharedFileRestore.TryRestoreSharedFile(
            input,
            "metadata/characters/character.ot",
            SharedFileRestore.ViewDistanceLine,
            out var cleaned,
            out var error);

        Assert.True(ok, error);
        Assert.NotNull(cleaned);

        string text = Encoding.Unicode.GetString(cleaned!);
        Assert.DoesNotContain("CreateCameraZoomNode", text);   // 视距已移除
        Assert.Contains("AddEffectPack", text);                // MTX 必须保留
    }

    /// <summary>无本补丁痕迹时返回成功但无变更内容（cleaned 为 null）。</summary>
    [Fact]
    public void TryRestoreSharedFile_NoInjection_ReturnsNoChange()
    {
        byte[] input = Encoding.Unicode.GetBytes(BaseOt);
        bool ok = SharedFileRestore.TryRestoreSharedFile(
            input,
            "metadata/characters/character.ot",
            SharedFileRestore.DodgeRollLine,
            out var cleaned,
            out var error);

        Assert.True(ok, error);
        Assert.Null(cleaned);
    }
}
