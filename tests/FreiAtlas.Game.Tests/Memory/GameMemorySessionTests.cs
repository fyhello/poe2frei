using FreiAtlas.Core.Memory;
using FreiAtlas.Game.Memory;

namespace FreiAtlas.Game.Tests.Memory;

public sealed class GameMemorySessionTests
{
    [Fact]
    public void TryRefresh_ReadsAreaIdentityAndStartsSequenceAtOne()
    {
        using var fixture = RootMemoryBuilder.Create();
        var session = CreateSession(fixture.Memory);

        Assert.True(session.TryRefresh(out _, out var area, out _));
        Assert.Equal("MapCurrent", area.AreaCode);
        Assert.Equal(81, area.AreaLevel);
        Assert.Equal(0x12345678u, area.AreaHash);
        Assert.Equal(1, area.SessionSequence);
    }

    [Fact]
    public void TryRefresh_SameAreaKeepsSequence()
    {
        using var fixture = RootMemoryBuilder.Create();
        var session = CreateSession(fixture.Memory);

        Assert.True(session.TryRefresh(out _, out var first, out _));
        Assert.True(session.TryRefresh(out _, out var second, out _));
        Assert.Equal(first.SessionSequence, second.SessionSequence);
    }

    [Fact]
    public void TryRefresh_AreaAddressChangeIncrementsSequenceAndRaisesEvent()
    {
        using var fixture = RootMemoryBuilder.Create();
        var session = CreateSession(fixture.Memory);
        var observed = new List<long>();
        session.SessionChanged += observed.Add;
        Assert.True(session.TryRefresh(out _, out var first, out _));

        fixture.ChangeArea(0x600000, 0xABCDEF01, "MapNext");

        Assert.True(session.TryRefresh(out _, out var second, out _));
        Assert.Equal(first.SessionSequence + 1, second.SessionSequence);
        Assert.Equal("MapNext", second.AreaCode);
        Assert.Equal([1L, 2L], observed);
    }

    [Fact]
    public void TryRefresh_AreaHashChangeIncrementsSequence()
    {
        using var fixture = RootMemoryBuilder.Create();
        var session = CreateSession(fixture.Memory);
        Assert.True(session.TryRefresh(out _, out var first, out _));

        fixture.ChangeAreaHash(0xABCDEF01);

        Assert.True(session.TryRefresh(out _, out var second, out _));
        Assert.Equal(first.SessionSequence + 1, second.SessionSequence);
    }

    [Fact]
    public void TryRefresh_RootFailureDoesNotChangeSequenceOrSelectAnotherProcess()
    {
        using var fixture = RootMemoryBuilder.Create(processId: 30744);
        var session = CreateSession(fixture.Memory);
        Assert.Equal(30744, session.ProcessId);
        Assert.True(session.TryRefresh(out _, out var first, out _));
        fixture.InvalidateGameStateSlot();

        Assert.False(session.TryRefresh(out _, out _, out var diagnostics));
        Assert.Equal(first.SessionSequence, session.SessionSequence);
        Assert.Contains(diagnostics, item => item.Code == "root-candidate-invalid");
    }

    private static GameMemorySession CreateSession(IProcessMemory memory)
    {
        var profile = Poe2MemoryProfile.Current;
        return new GameMemorySession(
            memory,
            profile,
            new GameStateLocator(profile));
    }
}
