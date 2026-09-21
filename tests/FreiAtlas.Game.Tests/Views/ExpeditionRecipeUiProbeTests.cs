using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Memory;
using FreiAtlas.Game.Tests.Memory;
using FreiAtlas.Game.Views;

namespace FreiAtlas.Game.Tests.Views;

public sealed class ExpeditionRecipeUiProbeTests
{
    private static readonly AreaUiRect ClientViewport = new(0, 0, 1920, 1080);

    [Fact]
    public void Probe_BacktracksPastWrongSiblingAndReadsRuneAndRewardQuantities()
    {
        using var memory = new SyntheticProcessMemory();
        var tree = new ExpeditionUiMemoryBuilder(memory);
        var first = tree.AddRow("1x Reward A", 4, new Vector2(0, 0));
        var second = tree.AddRow("2x Reward B", 3, new Vector2(0, 21));
        tree.AddRecipeBranch(true, [first, second], includeWrongSibling: true);

        var result = new ExpeditionRecipeUiProbe(memory).Probe(tree.InGameState, ClientViewport);

        Assert.Equal(ExpeditionRecipeUiProbeState.OpenCandidate, result.State);
        Assert.Equal([4, 3], result.Rows.Select(row => row.RuneCount));
        Assert.Equal([1, 2], result.Rows.Select(row => row.RewardQuantity));
        Assert.Empty(result.Diagnostics.Where(diagnostic => diagnostic.Severity == AreaDiagnosticSeverity.Warning));
    }

    [Fact]
    public void Probe_ReadsVisibleRowsFromLargeRecipeCatalog()
    {
        using var memory = new SyntheticProcessMemory();
        var tree = new ExpeditionUiMemoryBuilder(memory);
        var first = tree.AddRow("1x Reward A", 4, new Vector2(0, 0));
        var second = tree.AddRow("2x Reward B", 3, new Vector2(0, 21));
        var catalog = Enumerable.Range(0, 319)
            .Select(_ => tree.AddElement())
            .ToList();
        catalog.Insert(161, first);
        catalog.Insert(206, second);
        tree.AddRecipeBranch(true, catalog);

        var result = new ExpeditionRecipeUiProbe(memory).Probe(tree.InGameState, ClientViewport);

        Assert.True(
            result.State == ExpeditionRecipeUiProbeState.OpenCandidate,
            $"State={result.State}; diagnostics={string.Join(',', result.Diagnostics.Select(item => item.Code))}; rows={result.Rows.Length}");
        Assert.Equal([4, 3], result.Rows.Select(row => row.RuneCount));
        Assert.Equal([1, 2], result.Rows.Select(row => row.RewardQuantity));
    }

    [Fact]
    public void Probe_LargeRecipeCatalogReturnsClosedWhenGateIsHidden()
    {
        using var memory = new SyntheticProcessMemory();
        var tree = new ExpeditionUiMemoryBuilder(memory);
        var active = tree.AddRow("1x Reward A", 4, Vector2.Zero);
        var catalog = Enumerable.Range(0, 320)
            .Select(_ => tree.AddElement())
            .ToList();
        catalog.Insert(161, active);
        tree.AddRecipeBranch(false, catalog);

        var result = new ExpeditionRecipeUiProbe(memory).Probe(tree.InGameState, ClientViewport);

        Assert.Equal(ExpeditionRecipeUiProbeState.Closed, result.State);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void Probe_HiddenStructurallyValidGateReturnsClosedWithoutRowsOrWarnings()
    {
        using var memory = new SyntheticProcessMemory();
        var tree = new ExpeditionUiMemoryBuilder(memory);
        var row = tree.AddRow("1x Reward A", 4, Vector2.Zero);
        tree.AddRecipeBranch(false, [row]);

        var result = new ExpeditionRecipeUiProbe(memory).Probe(tree.InGameState, ClientViewport);

        Assert.Equal(ExpeditionRecipeUiProbeState.Closed, result.State);
        Assert.Empty(result.Rows);
        Assert.Empty(result.Diagnostics.Where(diagnostic => diagnostic.Severity == AreaDiagnosticSeverity.Warning));
    }

    [Fact]
    public void Probe_HiddenGateReturnsClosedAfterRecipeContainerIsCleared()
    {
        using var memory = new SyntheticProcessMemory();
        var tree = new ExpeditionUiMemoryBuilder(memory);
        tree.AddRecipeBranch(false, [tree.AddElement()]);

        var result = new ExpeditionRecipeUiProbe(memory).Probe(tree.InGameState, ClientViewport);

        Assert.Equal(ExpeditionRecipeUiProbeState.Closed, result.State);
        Assert.Empty(result.Rows);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Probe_AppliesPanelPositionAndViewportScrollToRowBounds()
    {
        using var memory = new SyntheticProcessMemory();
        var profile = Poe2MemoryProfile.Current;
        var tree = new ExpeditionUiMemoryBuilder(memory, profile);
        var row = tree.AddRow("1x Reward A", 4, new Vector2(21, 115));
        var branch = tree.AddRecipeBranch(true, [row], scrollOffset: new Vector2(0, -70));
        memory.WriteFloat(branch.Gate + profile.UiElement.ScaleOffset, 1.5f);
        memory.WriteFloat(branch.Viewport + profile.UiElement.ScaleOffset, 1.5f);
        memory.WriteFloat(branch.Container + profile.UiElement.ScaleOffset, 1.5f);
        memory.WriteFloat(row + profile.UiElement.ScaleOffset, 1.5f);

        var result = new ExpeditionRecipeUiProbe(memory).Probe(tree.InGameState, ClientViewport);

        var candidate = Assert.Single(result.Rows);
        Assert.Equal(31.5f, candidate.Bounds.Height);
        Assert.Equal(63f, candidate.Bounds.X);
        Assert.Equal(174f, candidate.Bounds.Y);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0x Reward")]
    [InlineData("2x")]
    public void Probe_InvalidRewardPrefixReturnsOpenUnverifiedWithNoRows(string rewardText)
    {
        using var memory = new SyntheticProcessMemory();
        var tree = new ExpeditionUiMemoryBuilder(memory);
        var row = tree.AddRow(rewardText, 4, Vector2.Zero);
        tree.AddRecipeBranch(true, [row]);

        var result = new ExpeditionRecipeUiProbe(memory).Probe(tree.InGameState, ClientViewport);

        Assert.Equal(ExpeditionRecipeUiProbeState.OpenUnverified, result.State);
        Assert.Empty(result.Rows);
    }

    [Theory]
    [InlineData("Reward")]
    [InlineData("\u50b3\u5947\u98fe\u54c1")]
    [InlineData("\u672a\u5207\u5272\u7684\u5bf6\u77f3")]
    public void Probe_UnprefixedRewardDefaultsQuantityToOne(string rewardText)
    {
        using var memory = new SyntheticProcessMemory();
        var tree = new ExpeditionUiMemoryBuilder(memory);
        var row = tree.AddRow(rewardText, 4, Vector2.Zero);
        tree.AddRecipeBranch(true, [row]);

        var result = new ExpeditionRecipeUiProbe(memory).Probe(tree.InGameState, ClientViewport);

        Assert.Equal(ExpeditionRecipeUiProbeState.OpenCandidate, result.State);
        Assert.Equal(1, Assert.Single(result.Rows).RewardQuantity);
    }

    [Fact]
    public void Probe_ReadsDynamicRowCountAndInlineAndHeapRewardStrings()
    {
        using var memory = new SyntheticProcessMemory();
        var tree = new ExpeditionUiMemoryBuilder(memory);
        var rows = new[]
        {
            tree.AddRow("1x A", 1, new Vector2(0, 42)),
            tree.AddRow("22x Long Reward Name", 2, new Vector2(0, 0)),
            tree.AddRow("3X Reward C", 3, new Vector2(0, 21))
        };
        tree.AddRecipeBranch(true, rows);

        var result = new ExpeditionRecipeUiProbe(memory).Probe(tree.InGameState, ClientViewport);

        Assert.Equal(ExpeditionRecipeUiProbeState.OpenCandidate, result.State);
        Assert.Equal(3, result.Rows.Length);
        Assert.Equal([22, 3, 1], result.Rows.Select(row => row.RewardQuantity));
        Assert.Equal([2, 3, 1], result.Rows.Select(row => row.RuneCount));
    }

    [Fact]
    public void Probe_ParentCycleMakesWholeGroupOpenUnverified()
    {
        using var memory = new SyntheticProcessMemory();
        var tree = new ExpeditionUiMemoryBuilder(memory);
        var row = tree.AddRow("1x Reward", 2, Vector2.Zero);
        var branch = tree.AddRecipeBranch(true, [row]);
        tree.SetParent(branch.Container, row);
        tree.SetParent(row, branch.Container);

        var result = new ExpeditionRecipeUiProbe(memory).Probe(tree.InGameState, ClientViewport);

        Assert.Equal(ExpeditionRecipeUiProbeState.OpenUnverified, result.State);
        Assert.Empty(result.Rows);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "runeforge-ui-parent-cycle");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Probe_DuplicateOrInvalidRowGeometryRejectsWholeGroup(bool duplicatePosition, bool invalidSize)
    {
        using var memory = new SyntheticProcessMemory();
        var tree = new ExpeditionUiMemoryBuilder(memory);
        var first = tree.AddRow("1x Reward A", 2, Vector2.Zero);
        var second = tree.AddRow("2x Reward B", 3, duplicatePosition ? Vector2.Zero : new Vector2(0, 21));
        if (invalidSize) tree.WriteSize(second, new Vector2(222, 0));
        tree.AddRecipeBranch(true, [first, second]);

        var result = new ExpeditionRecipeUiProbe(memory).Probe(tree.InGameState, ClientViewport);

        Assert.Equal(ExpeditionRecipeUiProbeState.OpenUnverified, result.State);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void Probe_NonFiniteScrollReturnsOpenUnverified()
    {
        using var memory = new SyntheticProcessMemory();
        var tree = new ExpeditionUiMemoryBuilder(memory);
        var row = tree.AddRow("1x Reward", 2, Vector2.Zero);
        tree.AddRecipeBranch(true, [row], scrollOffset: new Vector2(float.NaN, 0));

        var result = new ExpeditionRecipeUiProbe(memory).Probe(tree.InGameState, ClientViewport);

        Assert.Equal(ExpeditionRecipeUiProbeState.OpenUnverified, result.State);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void Probe_InvalidUiRootReturnsUnavailable()
    {
        using var memory = new SyntheticProcessMemory();
        const nint inGameState = 0x900000;

        var result = new ExpeditionRecipeUiProbe(memory).Probe(inGameState, ClientViewport);

        Assert.Equal(ExpeditionRecipeUiProbeState.Unavailable, result.State);
        Assert.Empty(result.Rows);
    }
}
