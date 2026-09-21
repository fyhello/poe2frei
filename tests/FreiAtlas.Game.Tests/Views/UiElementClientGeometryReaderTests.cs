using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Memory;
using FreiAtlas.Game.Tests.Memory;
using FreiAtlas.Game.Views;

namespace FreiAtlas.Game.Tests.Views;

public sealed class UiElementClientGeometryReaderTests
{
    [Fact]
    public void TryReadRect_IncludesScrollViewportPositionAndAppliesScrollOnce()
    {
        using var memory = new SyntheticProcessMemory();
        var tree = new ExpeditionUiMemoryBuilder(memory);
        var viewport = tree.AddElement(
            relativePosition: new Vector2(40, 30),
            positionModifier: new Vector2(0, -70),
            scale: 1.5f,
            scaleIndex: 2);
        var row = tree.AddElement(relativePosition: new Vector2(21, 115), size: new Vector2(222, 21), scale: 1.5f, scaleIndex: 2, modifyPosition: true);
        tree.SetChildren(tree.Root, viewport);
        tree.SetChildren(viewport, row);

        var success = new UiElementClientGeometryReader(memory).TryReadRect(
            row,
            viewport,
            new Vector2(0, -70),
            new AreaUiRect(0, 0, 2560, 1600),
            out var bounds,
            out var diagnosticCode);

        Assert.True(success);
        Assert.Null(diagnosticCode);
        Assert.Equal(91.5f, bounds.X);
        Assert.Equal(112.5f, bounds.Y);
        Assert.Equal(333f, bounds.Width);
        Assert.Equal(31.5f, bounds.Height);
    }

    [Fact]
    public void TryReadRect_AppliesViewportScaleSelectedByScaleIndex()
    {
        using var memory = new SyntheticProcessMemory();
        var tree = new ExpeditionUiMemoryBuilder(memory);
        var row = tree.AddElement(
            relativePosition: new Vector2(100, 80),
            size: new Vector2(200, 100),
            scaleIndex: 2);
        tree.SetChildren(tree.Root, row);

        var success = new UiElementClientGeometryReader(memory).TryReadRect(
            row,
            0,
            Vector2.Zero,
            new AreaUiRect(0, 0, 1015, 628),
            out var bounds,
            out var diagnosticCode);

        Assert.True(success);
        Assert.Null(diagnosticCode);
        Assert.Equal(39.25f, bounds.X, 3);
        Assert.Equal(31.4f, bounds.Y, 3);
        Assert.Equal(78.5f, bounds.Width, 3);
        Assert.Equal(39.25f, bounds.Height, 3);
    }

    [Fact]
    public void TryReadRect_AppliesPositionModifierAndConvertsParentScaleIndex()
    {
        using var memory = new SyntheticProcessMemory();
        var profile = Poe2MemoryProfile.Current;
        var tree = new ExpeditionUiMemoryBuilder(memory, profile);
        var parent = tree.AddElement(relativePosition: new Vector2(100, 50), positionModifier: new Vector2(4, 3), scale: 2f, scaleIndex: 1);
        var child = tree.AddElement(
            flags: 1u << profile.UiElement.ModifyPositionBit,
            relativePosition: new Vector2(10, 5),
            positionModifier: new Vector2(900, 900),
            size: new Vector2(20, 10),
            scale: 1f,
            scaleIndex: 2);
        tree.SetChildren(tree.Root, parent);
        tree.SetChildren(parent, child);

        var success = new UiElementClientGeometryReader(memory, profile).TryReadRect(
            child,
            0,
            Vector2.Zero,
            new AreaUiRect(0, 0, 2560, 1600),
            out var bounds,
            out var diagnosticCode);

        Assert.True(success);
        Assert.Null(diagnosticCode);
        Assert.Equal(new AreaUiRect(218, 111, 20, 10), bounds);
    }

    [Fact]
    public void TryReadRect_ConvertsParentMultiplierWhenScaleIndicesMatch()
    {
        using var memory = new SyntheticProcessMemory();
        var tree = new ExpeditionUiMemoryBuilder(memory);
        var parent = tree.AddElement(relativePosition: new Vector2(100, 50), scale: 2f, scaleIndex: 2);
        var child = tree.AddElement(relativePosition: new Vector2(10, 5), size: new Vector2(20, 10), scale: 1f, scaleIndex: 2);
        tree.SetChildren(tree.Root, parent);
        tree.SetChildren(parent, child);

        Assert.True(new UiElementClientGeometryReader(memory).TryReadRect(
            child, 0, Vector2.Zero, new AreaUiRect(0, 0, 2560, 1600), out var bounds, out _));

        Assert.Equal(new AreaUiRect(210, 105, 20, 10), bounds);
    }

    [Fact]
    public void TryReadRect_ClipCanSuppressViewportScroll()
    {
        using var memory = new SyntheticProcessMemory();
        var tree = new ExpeditionUiMemoryBuilder(memory);
        var viewport = tree.AddElement(relativePosition: new Vector2(40, 30), positionModifier: new Vector2(0, -70));
        var container = tree.AddElement(relativePosition: new Vector2(20, 10), size: new Vector2(200, 100), modifyPosition: true);
        tree.SetChildren(tree.Root, viewport);
        tree.SetChildren(viewport, container);

        Assert.True(new UiElementClientGeometryReader(memory).TryReadRect(
            container, viewport, Vector2.Zero, new AreaUiRect(0, 0, 2560, 1600), out var bounds, out _));

        Assert.Equal(new AreaUiRect(60, 40, 200, 100), bounds);
    }

    [Fact]
    public void TryReadRect_RejectsParentCycleWithSpecificDiagnostic()
    {
        using var memory = new SyntheticProcessMemory();
        var tree = new ExpeditionUiMemoryBuilder(memory);
        var first = tree.AddElement();
        var second = tree.AddElement();
        tree.SetParent(first, second);
        tree.SetParent(second, first);

        var success = new UiElementClientGeometryReader(memory).TryReadRect(
            first,
            0,
            Vector2.Zero,
            new AreaUiRect(0, 0, 1920, 1080),
            out _,
            out var diagnosticCode);

        Assert.False(success);
        Assert.Equal("runeforge-ui-parent-cycle", diagnosticCode);
    }

    [Theory]
    [InlineData(0f, 20f)]
    [InlineData(float.NaN, 20f)]
    [InlineData(20f, float.PositiveInfinity)]
    public void TryReadRect_RejectsInvalidVectorSize(float width, float height)
    {
        using var memory = new SyntheticProcessMemory();
        var tree = new ExpeditionUiMemoryBuilder(memory);
        var element = tree.AddElement(size: new Vector2(width, height));

        var success = new UiElementClientGeometryReader(memory).TryReadRect(
            element,
            0,
            Vector2.Zero,
            new AreaUiRect(0, 0, 1920, 1080),
            out _,
            out var diagnosticCode);

        Assert.False(success);
        Assert.Equal("runeforge-ui-geometry-invalid", diagnosticCode);
    }
}
