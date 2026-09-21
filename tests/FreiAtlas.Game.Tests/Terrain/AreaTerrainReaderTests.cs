using FreiAtlas.Core.Memory;
using FreiAtlas.Game.Terrain;
using FreiAtlas.Game.Tests.Memory;

namespace FreiAtlas.Game.Tests.Terrain;

public sealed class AreaTerrainReaderTests
{
    private const long AreaInstance = 0x400000;
    private const long Terrain = AreaInstance + 0x8D0;
    private const long Tiles = 0x500000;
    private const long TgtFile = 0x600000;
    private const long Walkable = 0x800000;

    [Fact]
    public void Read_UnpacksLowNibbleForEvenXAndHighNibbleForOddX()
    {
        var memory = CreateValidMemory([0x10, 0x2F]);
        var reader = new AreaTerrainReader(memory);

        var result = reader.Read((nint)AreaInstance, sessionSequence: 7);

        Assert.NotNull(result.Terrain);
        Assert.Equal(4, result.Terrain.Width);
        Assert.Equal(1, result.Terrain.Height);
        Assert.Equal([0, 1, 1, 1], result.Terrain.Walkable.ToArray());
        Assert.Equal(["Metadata/Terrain/Test/BossRoom.tdt"], result.Terrain.TilePaths);
        Assert.Equal(2, result.Tiles.Count);
        Assert.Equal((0, 0), (result.Tiles[0].TileX, result.Tiles[0].TileY));
        Assert.Equal((1, 0), (result.Tiles[1].TileX, result.Tiles[1].TileY));
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData(0, 2L)]
    [InlineData(65_537, 2L)]
    [InlineData(1, (64L * 1024 * 1024) + 1)]
    [InlineData(1, 65_537L)]
    [InlineData(2, 3L)]
    public void Read_RejectsUnsafeOrIndivisibleWalkableRanges(
        int bytesPerRow,
        long totalBytes)
    {
        var memory = CreateValidMemory([0x10, 0x2F]);
        memory.WritePointer((nint)(Terrain + 0xD0 + 8), (nint)(Walkable + totalBytes));
        memory.WriteInt32((nint)(Terrain + 0x130), bytesPerRow);
        var reader = new AreaTerrainReader(memory);

        var result = reader.Read((nint)AreaInstance, sessionSequence: 8);

        Assert.Null(result.Terrain);
        Assert.Empty(result.Tiles);
        Assert.Equal("terrain-grid-invalid", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Read_RejectsMoreThanOneMillionTilesWithoutReturningPartialData()
    {
        var memory = CreateValidMemory([0x10, 0x2F]);
        memory.WritePointer(
            (nint)(Terrain + 0x28 + 8),
            (nint)(Tiles + (1_000_001L * 0x38)));
        var reader = new AreaTerrainReader(memory);

        var result = reader.Read((nint)AreaInstance, sessionSequence: 9);

        Assert.Null(result.Terrain);
        Assert.Empty(result.Tiles);
        Assert.Equal("terrain-tiles-invalid", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Read_CachesOneCopiedResultPerSessionSequence()
    {
        var memory = CreateValidMemory([0x10, 0x2F]);
        var reader = new AreaTerrainReader(memory);
        var first = reader.Read((nint)AreaInstance, sessionSequence: 10);
        var readsAfterFirst = memory.ReadOperationCount;

        memory.WriteBytes((nint)Walkable, [0x00, 0x00]);
        var cached = reader.Read((nint)AreaInstance, sessionSequence: 10);

        Assert.Same(first, cached);
        Assert.Equal(readsAfterFirst, memory.ReadOperationCount);
        Assert.Equal([0, 1, 1, 1], cached.Terrain!.Walkable.ToArray());

        var refreshed = reader.Read((nint)AreaInstance, sessionSequence: 11);
        Assert.NotSame(first, refreshed);
        Assert.Equal([0, 0, 0, 0], refreshed.Terrain!.Walkable.ToArray());
    }

    [Fact]
    public void Read_DecodesSharedTgtFilePathOnlyOnce()
    {
        var inner = CreateValidMemory([0x10, 0x2F]);
        using var memory = new CountingProcessMemory(inner);
        var reader = new AreaTerrainReader(memory);

        var result = reader.Read((nint)AreaInstance, sessionSequence: 12);

        Assert.NotNull(result.Terrain);
        Assert.Equal(1, memory.CallsAt((nint)(TgtFile + 0x08 + 0x10)));
    }

    [Fact]
    public void Read_CachesFailureUntilSessionSequenceChanges()
    {
        var memory = CreateValidMemory([0x10, 0x2F]);
        memory.WriteInt32((nint)(Terrain + 0x130), 0);
        var reader = new AreaTerrainReader(memory);
        var failed = reader.Read((nint)AreaInstance, sessionSequence: 13);
        var readsAfterFailure = memory.ReadOperationCount;

        memory.WriteInt32((nint)(Terrain + 0x130), 2);
        var cached = reader.Read((nint)AreaInstance, sessionSequence: 13);

        Assert.Same(failed, cached);
        Assert.Null(cached.Terrain);
        Assert.Equal(readsAfterFailure, memory.ReadOperationCount);
        Assert.NotNull(reader.Read((nint)AreaInstance, sessionSequence: 14).Terrain);
    }

    private static SyntheticProcessMemory CreateValidMemory(byte[] walkable)
    {
        var memory = new SyntheticProcessMemory();
        memory.WriteBytes((nint)AreaInstance, new byte[0xA00]);
        memory.WriteInt64((nint)(Terrain + 0x18), 2);
        memory.WriteInt64((nint)(Terrain + 0x20), 1);
        memory.WritePointer((nint)(Terrain + 0x28), (nint)Tiles);
        memory.WritePointer((nint)(Terrain + 0x30), (nint)(Tiles + (2 * 0x38)));
        memory.WriteBytes((nint)Tiles, new byte[2 * 0x38]);
        memory.WritePointer((nint)(Tiles + 0x08), (nint)TgtFile);
        memory.WritePointer((nint)(Tiles + 0x38 + 0x08), (nint)TgtFile);
        memory.WriteBytes((nint)TgtFile, new byte[0x100]);
        memory.WriteStdWString(
            (nint)(TgtFile + 0x08),
            "Metadata/Terrain/Test/BossRoom.tdt");
        memory.WritePointer((nint)(Terrain + 0xD0), (nint)Walkable);
        memory.WritePointer(
            (nint)(Terrain + 0xD0 + 8),
            (nint)(Walkable + walkable.Length));
        memory.WriteInt32((nint)(Terrain + 0x130), 2);
        memory.WriteBytes((nint)Walkable, walkable);
        return memory;
    }

    private sealed class CountingProcessMemory : IProcessMemory
    {
        private readonly IProcessMemory _inner;
        private readonly Dictionary<nint, int> _calls = [];

        public CountingProcessMemory(IProcessMemory inner)
        {
            _inner = inner;
        }

        public int ProcessId => _inner.ProcessId;

        public int CallsAt(nint address)
            => _calls.GetValueOrDefault(address);

        public bool TryRead(nint address, Span<byte> destination)
        {
            Count(address);
            return _inner.TryRead(address, destination);
        }

        public bool TryReadInt32(nint address, out int value)
        {
            Count(address);
            return _inner.TryReadInt32(address, out value);
        }

        public bool TryReadInt64(nint address, out long value)
        {
            Count(address);
            return _inner.TryReadInt64(address, out value);
        }

        public bool TryReadFloat(nint address, out float value)
        {
            Count(address);
            return _inner.TryReadFloat(address, out value);
        }

        public bool TryReadPointer(nint address, out nint value)
        {
            Count(address);
            return _inner.TryReadPointer(address, out value);
        }

        public bool TryReadUtf16(nint address, int maxChars, out string? value)
        {
            Count(address);
            return _inner.TryReadUtf16(address, maxChars, out value);
        }

        public void Dispose()
        {
        }

        private void Count(nint address)
            => _calls[address] = CallsAt(address) + 1;
    }
}
