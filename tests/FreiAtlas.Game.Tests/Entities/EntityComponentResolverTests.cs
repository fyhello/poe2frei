using FreiAtlas.Game.Entities;
using FreiAtlas.Game.Tests.Memory;

namespace FreiAtlas.Game.Tests.Entities;

public sealed class EntityComponentResolverTests
{
    private static readonly nint Entity = 0x300000;
    private static readonly nint Details = 0x310000;
    private static readonly nint Lookup = 0x320000;
    private static readonly nint Components = 0x330000;
    private static readonly nint Entries = 0x340000;
    private static readonly nint Name = 0x350000;

    [Fact]
    public void TryResolve_MapsComponentNameAndCachesSuccessfulAddress()
    {
        var memory = CreateResolverMemory("Render", 0, 0x360000);
        var resolver = new EntityComponentResolver(memory);
        resolver.BeginSample(7);

        Assert.True(resolver.TryResolve(new RawEntityRef(12, Entity), "Render", out var address));
        Assert.Equal((nint)0x360000, address);

        var reads = memory.ReadOperationCount;
        Assert.True(resolver.TryResolve(new RawEntityRef(12, Entity), "Render", out address));
        Assert.Equal((nint)0x360000, address);
        Assert.Equal(reads, memory.ReadOperationCount);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void TryResolve_IgnoresInvalidComponentIndexes(int invalidIndex)
    {
        var memory = CreateResolverMemory("Render", invalidIndex, 0x360000);
        var resolver = new EntityComponentResolver(memory);
        resolver.BeginSample(7);

        Assert.True(resolver.TryResolve(new RawEntityRef(12, Entity), "Render", out var address));
        Assert.Equal(0, address);
    }

    [Fact]
    public void TryResolve_RejectsMoreThan256Components()
    {
        var memory = CreateResolverMemory("Render", 0, 0x360000);
        memory.WritePointer(Entity + 0x18, Components + (257 * IntPtr.Size));
        var resolver = new EntityComponentResolver(memory);
        resolver.BeginSample(7);

        Assert.False(resolver.TryResolve(new RawEntityRef(12, Entity), "Render", out _));
    }

    [Fact]
    public void TryResolve_RejectsUnreadableUtf8Name()
    {
        var memory = CreateResolverMemory("Render", 0, 0x360000);
        memory.FailRange(Name, 32);
        var resolver = new EntityComponentResolver(memory);
        resolver.BeginSample(7);

        Assert.False(resolver.TryResolve(new RawEntityRef(12, Entity), "Render", out _));
    }

    [Fact]
    public void TryResolve_EvictsCachedComponentsWhenAddressIsReusedByAnotherEntityId()
    {
        var memory = CreateResolverMemory("Render", 0, 0x360000);
        var resolver = new EntityComponentResolver(memory);
        resolver.BeginSample(7);
        Assert.True(resolver.TryResolve(new RawEntityRef(12, Entity), "Render", out _));

        memory.WritePointer(Components, 0x370000);

        Assert.True(resolver.TryResolve(new RawEntityRef(13, Entity), "Render", out var address));
        Assert.Equal((nint)0x370000, address);
    }

    [Fact]
    public void BeginSample_ClearsSuccessfulCacheWhenAreaSessionChanges()
    {
        var memory = CreateResolverMemory("Render", 0, 0x360000);
        var resolver = new EntityComponentResolver(memory);
        resolver.BeginSample(7);
        Assert.True(resolver.TryResolve(new RawEntityRef(12, Entity), "Render", out _));

        memory.WritePointer(Components, 0x370000);
        resolver.BeginSample(8);

        Assert.True(resolver.TryResolve(new RawEntityRef(12, Entity), "Render", out var address));
        Assert.Equal((nint)0x370000, address);
    }

    [Fact]
    public void BeginSample_RetriesZeroAddressCacheOnTheNextWorldSample()
    {
        var memory = CreateResolverMemory("Life", 0, 0x360000);
        var resolver = new EntityComponentResolver(memory);
        resolver.BeginSample(7);
        Assert.True(resolver.TryResolve(new RawEntityRef(12, Entity), "Render", out var missing));
        Assert.Equal(0, missing);

        memory.WriteUtf8(Name, "Render", 32);
        Assert.True(resolver.TryResolve(new RawEntityRef(12, Entity), "Render", out missing));
        Assert.Equal(0, missing);

        resolver.BeginSample(7);
        Assert.True(resolver.TryResolve(new RawEntityRef(12, Entity), "Render", out var address));
        Assert.Equal((nint)0x360000, address);
    }

    private static SyntheticProcessMemory CreateResolverMemory(
        string name,
        int index,
        nint componentAddress)
    {
        var memory = new SyntheticProcessMemory();
        memory.WritePointer(Entity + 0x08, Details);
        memory.WritePointer(Details + 0x28, Lookup);
        memory.WritePointer(Entity + 0x10, Components);
        memory.WritePointer(Entity + 0x18, Components + IntPtr.Size);
        memory.WritePointer(Components, componentAddress);
        memory.WritePointer(Lookup + 0x28, Entries);
        memory.WritePointer(Lookup + 0x30, Entries + 0x10);
        memory.WritePointer(Entries, Name);
        memory.WriteInt32(Entries + 0x08, index);
        memory.WriteUtf8(Name, name, 32);
        return memory;
    }
}
