using FreiAtlas.Core.Memory;
using FreiAtlas.Game.Memory;

namespace FreiAtlas.Game.Tests.Memory;

public sealed class GameStateLocatorTests
{
    [Fact]
    public void TryResolve_UsesValidatedCurrentState()
    {
        using var fixture = RootMemoryBuilder.Create();
        var locator = new GameStateLocator(Poe2MemoryProfile.Current);

        Assert.True(locator.TryResolve(fixture.Memory, out var root, out var diagnostics));
        Assert.Equal((nint)RootMemoryBuilder.SlotAddress, root.GameStateSlot);
        Assert.Equal((nint)RootMemoryBuilder.InGameStateAddress, root.InGameState);
        Assert.Equal((nint)RootMemoryBuilder.AreaAddress, root.AreaInstance);
        Assert.Equal((nint)RootMemoryBuilder.PlayerAddress, root.LocalPlayer);
        Assert.DoesNotContain(diagnostics, item => item.Severity == Core.Area.AreaDiagnosticSeverity.Error);
    }

    [Fact]
    public void TryResolve_WhenCurrentStateInvalid_UsesValidatedFallbackSlot()
    {
        using var fixture = RootMemoryBuilder.Create(
            currentStateValid: false,
            validFallbackSlot: 7);
        var locator = new GameStateLocator(Poe2MemoryProfile.Current);

        Assert.True(locator.TryResolve(fixture.Memory, out var root, out var diagnostics));
        Assert.Equal((nint)RootMemoryBuilder.AreaAddress, root.AreaInstance);
        Assert.Contains(diagnostics, item => item.Code == "root-fallback-slot");
    }

    [Fact]
    public void TryResolve_WhenFirstAobIsInvalid_UsesValidatedLaterMatch()
    {
        using var fixture = RootMemoryBuilder.Create();
        fixture.AddInvalidAobReference(0x100100, 0x111000);
        var locator = new GameStateLocator(Poe2MemoryProfile.Current);

        Assert.True(locator.TryResolve(fixture.Memory, out var root, out _));
        Assert.Equal((nint)RootMemoryBuilder.SlotAddress, root.GameStateSlot);
    }

    [Fact]
    public void TryResolve_DoesNotReuseCachedSlotForDifferentProcess()
    {
        using var first = RootMemoryBuilder.Create(processId: 7928);
        var locator = new GameStateLocator(Poe2MemoryProfile.Current);
        Assert.True(locator.TryResolve(first.Memory, out _, out _));

        using var second = new SyntheticProcessMemory(processId: 9001);
        Assert.False(locator.TryResolve(second, out _, out var diagnostics));
        Assert.Contains(diagnostics, item => item.Code == "root-aob-not-found");
    }

    [Fact]
    public void TryResolve_WhenSlotBecomesInvalid_ReportsFailure()
    {
        using var fixture = RootMemoryBuilder.Create();
        var locator = new GameStateLocator(Poe2MemoryProfile.Current);
        Assert.True(locator.TryResolve(fixture.Memory, out _, out _));
        fixture.InvalidateGameStateSlot();

        Assert.False(locator.TryResolve(fixture.Memory, out _, out var diagnostics));
        Assert.Contains(diagnostics, item => item.Code == "root-candidate-invalid");
    }

    [Fact]
    public void TryResolve_RequiresModuleLayout()
    {
        using var memory = new MemoryWithoutLayout();
        var locator = new GameStateLocator(Poe2MemoryProfile.Current);

        Assert.False(locator.TryResolve(memory, out _, out var diagnostics));
        Assert.Contains(diagnostics, item => item.Code == "root-layout-unavailable");
    }

    private sealed class MemoryWithoutLayout : IProcessMemory
    {
        public int ProcessId => 12;
        public bool TryRead(nint address, Span<byte> destination) => false;
        public bool TryReadInt32(nint address, out int value) { value = 0; return false; }
        public bool TryReadInt64(nint address, out long value) { value = 0; return false; }
        public bool TryReadFloat(nint address, out float value) { value = 0; return false; }
        public bool TryReadPointer(nint address, out nint value) { value = 0; return false; }
        public bool TryReadUtf16(nint address, int maxChars, out string? value) { value = null; return false; }
        public void Dispose() { }
    }
}
