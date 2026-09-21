using FreiAtlas.Core.Memory;
using FreiAtlas.Core.Player;
using FreiAtlas.Game.Memory;

namespace FreiAtlas.Game.Tests.Memory;

public sealed class PlayerVitalsReaderTests
{
    [Fact]
    public void Read_UsesLocalPlayerLifeAndAllThreePools()
    {
        using var fixture = new VitalsFixture();
        var snapshot = new PlayerVitalsReader(fixture.Memory).Read();
        Assert.True(snapshot.IsValid, snapshot.Error);
        Assert.Equal(new VitalPool(1278, 1704, 0, 2500), snapshot.Health);
        Assert.Equal(1278, snapshot.Health!.AvailableMaximum);
        Assert.Equal(new VitalPool(2896, 2896, 0, 0), snapshot.Mana);
        Assert.Equal(new VitalPool(321, 500, 0, 0), snapshot.EnergyShield);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("health-read")]
    [InlineData("maximum")]
    [InlineData("current")]
    [InlineData("reservation")]
    public void InvalidHealthOrOwner_ClearsWholeSnapshot(string failure)
    {
        using var fixture = new VitalsFixture();
        var reader = new PlayerVitalsReader(fixture.Memory);
        Assert.True(reader.Read().IsValid);
        var profile = Poe2MemoryProfile.Current;
        switch (failure)
        {
            case "owner": fixture.Memory.WritePointer(VitalsFixture.Life + profile.Life.OwnerOffset, 0x600000); break;
            case "health-read": fixture.Memory.FailRange(VitalsFixture.Life + profile.Life.HealthOffset, 1); break;
            case "maximum": fixture.WritePool(profile.Life.HealthOffset, new(1, 0, 0, 0)); break;
            case "current": fixture.WritePool(profile.Life.HealthOffset, new(1001, 1000, 0, 0)); break;
            case "reservation": fixture.WritePool(profile.Life.HealthOffset, new(1, 1000, 1, 10000)); break;
        }
        var result = reader.Read();
        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
        Assert.Null(result.Health);
        Assert.Null(result.Mana);
        Assert.Null(result.EnergyShield);
    }

    [Fact]
    public void MissingManaAndShield_DoNotBecomeZeroOrInvalidateHealth()
    {
        using var fixture = new VitalsFixture();
        fixture.Memory.FailRange(VitalsFixture.Life + Poe2MemoryProfile.Current.Life.ManaOffset, 1);
        fixture.Memory.FailRange(VitalsFixture.Life + Poe2MemoryProfile.Current.Life.EnergyShieldOffset, 1);
        var result = new PlayerVitalsReader(fixture.Memory).Read();
        Assert.True(result.IsValid, result.Error);
        Assert.Null(result.Mana);
        Assert.Null(result.EnergyShield);
    }

    [Fact]
    public void AreaChange_ChangesPublishedSequence()
    {
        using var fixture = new VitalsFixture();
        var reader = new PlayerVitalsReader(fixture.Memory);
        var before = reader.Read();
        fixture.Root.ChangeAreaHash(0x87654321);
        var after = reader.Read();
        Assert.True(after.IsValid, after.Error);
        Assert.True(after.SessionSequence > before.SessionSequence);
    }

    [Fact]
    public void AreaChangesDuringPoolRead_RejectsMixedFrame()
    {
        using var fixture = new VitalsFixture();
        using var memory = new ObservedMemory(fixture.Memory);
        memory.OnRead = address =>
        {
            if (address == VitalsFixture.Life + Poe2MemoryProfile.Current.Life.HealthOffset)
                fixture.Root.ChangeAreaHash(0x87654321);
        };
        var result = new PlayerVitalsReader(memory).Read();
        Assert.False(result.IsValid);
        Assert.Equal("区域切换中", result.Error);
    }

    [Fact]
    public async Task MemoryService_RunsWithoutOverlayAndClearsSelectionAndDisposes()
    {
        using var fixture = new VitalsFixture();
        using var memory = new ObservedMemory(fixture.Memory);
        await using var service = new PlayerVitalsMemoryService(_ => memory);
        service.SelectProcess(memory.ProcessId);
        service.Start();
        await WaitUntil(() => service.Current.IsValid);
        Assert.Equal(1278, service.Current.Health!.Current);
        service.SelectProcess(null);
        Assert.False(service.Current.IsValid);
        Assert.Equal(0, service.Current.ProcessId);
        await WaitUntil(() => memory.Disposed);
        await service.DisposeAsync();
        Assert.Contains("停止", service.Current.Error!);
        Assert.Throws<ObjectDisposedException>(() => service.SelectProcess(1));
    }

    [Fact]
    public async Task MemoryService_OpeningFailureIsVisible()
    {
        await using var service = new PlayerVitalsMemoryService(_ => throw new InvalidOperationException("测试打开失败"));
        service.SelectProcess(123);
        service.Start();
        await WaitUntil(() => service.Current.Error?.Contains("测试打开失败") == true);
        Assert.Null(service.Current.Health);
    }

    [Fact]
    public async Task MemoryService_OldReadCannotOverwriteNewSelection()
    {
        using var fixture = new VitalsFixture();
        using var memory = new ObservedMemory(fixture.Memory);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var service = new PlayerVitalsMemoryService(_ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("测试未释放读取");
            return memory;
        });
        service.SelectProcess(memory.ProcessId);
        service.Start();
        try
        {
            await WaitUntil(() => entered.IsSet);
            service.SelectProcess(null);
        }
        finally { release.Set(); }
        await WaitUntil(() => memory.Disposed);
        Assert.Equal(0, service.Current.ProcessId);
        Assert.False(service.Current.IsValid);
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private sealed class VitalsFixture : IDisposable
    {
        public static readonly nint Life = 0x542000;
        public RootMemoryBuilder Root { get; } = RootMemoryBuilder.Create();
        public SyntheticProcessMemory Memory => Root.Memory;
        public VitalsFixture()
        {
            var profile = Poe2MemoryProfile.Current;
            Memory.WritePointer(RootMemoryBuilder.PlayerAddress + profile.Entity.ComponentsOffset + IntPtr.Size, 0x530000 + 3 * IntPtr.Size);
            Memory.WritePointer(0x530000 + 2 * IntPtr.Size, Life);
            Memory.WritePointer(0x520000 + profile.ComponentLookup.BucketOffset + IntPtr.Size, 0x550000 + 3 * profile.ComponentLookup.EntryStride);
            Memory.WritePointer(0x550000 + 2 * profile.ComponentLookup.EntryStride, 0x560200);
            Memory.WriteInt32(0x550000 + 2 * profile.ComponentLookup.EntryStride + IntPtr.Size, 2);
            Memory.WriteUtf8(0x560200, "Life", 32);
            Memory.WritePointer(Life + profile.Life.OwnerOffset, RootMemoryBuilder.PlayerAddress);
            WritePool(profile.Life.HealthOffset, new(1278, 1704, 0, 2500));
            WritePool(profile.Life.ManaOffset, new(2896, 2896, 0, 0));
            WritePool(profile.Life.EnergyShieldOffset, new(321, 500, 0, 0));
        }
        public void WritePool(int offset, VitalPool pool)
        {
            var vital = Poe2MemoryProfile.Current.Vital;
            Memory.WriteBytes(Life + offset, new byte[0x34]);
            Memory.WriteInt32(Life + offset + vital.CurrentOffset, pool.Current);
            Memory.WriteInt32(Life + offset + vital.MaximumOffset, pool.Maximum);
            Memory.WriteInt32(Life + offset + vital.ReservedFlatOffset, pool.ReservedFlat);
            Memory.WriteInt32(Life + offset + vital.ReservedFractionOffset, pool.ReservedFraction);
        }
        public void Dispose() => Root.Dispose();
    }

    private sealed class ObservedMemory(SyntheticProcessMemory inner) : IProcessMemory, IProcessMemoryLayout
    {
        public Action<nint>? OnRead { get; set; }
        public volatile bool Disposed;
        public int ProcessId => inner.ProcessId;
        public nint MainModuleBase => inner.MainModuleBase;
        public long MainModuleSize => inner.MainModuleSize;
        public IEnumerable<ProcessMemoryRegion> EnumerateMainModuleRegions() => inner.EnumerateMainModuleRegions();
        public bool TryRead(nint address, Span<byte> destination) { var result = inner.TryRead(address, destination); OnRead?.Invoke(address); return result; }
        public bool TryReadInt32(nint address, out int value) => inner.TryReadInt32(address, out value);
        public bool TryReadInt64(nint address, out long value) => inner.TryReadInt64(address, out value);
        public bool TryReadFloat(nint address, out float value) => inner.TryReadFloat(address, out value);
        public bool TryReadPointer(nint address, out nint value) => inner.TryReadPointer(address, out value);
        public bool TryReadUtf16(nint address, int maxChars, out string? value) => inner.TryReadUtf16(address, maxChars, out value);
        public void Dispose() { Disposed = true; inner.Dispose(); }
    }
}
