using FreiAtlas.Core.Area;
using FreiAtlas.Core.Memory;
using FreiAtlas.Game.Memory;

namespace FreiAtlas.Game.Terrain;

internal sealed record AreaTerrainTile(
    int TileX,
    int TileY,
    string TilePath);

internal sealed record AreaTerrainReadResult(
    AreaTerrainSnapshot? Terrain,
    IReadOnlyList<AreaTerrainTile> Tiles,
    IReadOnlyList<AreaReadDiagnostic> Diagnostics);

internal sealed class AreaTerrainReader
{
    private const int MaximumTileCount = 1_000_000;
    private const int MaximumBytesPerRow = 65_536;
    private const int MaximumRows = 65_536;
    private const int MaximumWalkableBytes = 64 * 1024 * 1024;
    private const int MaximumTilePathLength = 512;

    private readonly IProcessMemory _memory;
    private readonly Poe2MemoryProfile _profile;
    private readonly GameMemoryReader _reader;
    private long _cachedSessionSequence = long.MinValue;
    private AreaTerrainReadResult? _cachedResult;

    public AreaTerrainReader(
        IProcessMemory memory,
        Poe2MemoryProfile? profile = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _profile = profile ?? Poe2MemoryProfile.Current;
        _reader = new GameMemoryReader(memory, _profile);
    }

    public AreaTerrainReadResult Read(
        nint areaInstance,
        long sessionSequence)
    {
        if (_cachedResult is not null
            && _cachedSessionSequence == sessionSequence)
        {
            return _cachedResult;
        }

        _cachedSessionSequence = sessionSequence;
        _cachedResult = ReadCore(areaInstance);
        return _cachedResult;
    }

    private AreaTerrainReadResult ReadCore(nint areaInstance)
    {
        var terrainAddress = areaInstance + _profile.AreaInstance.TerrainOffset;
        if (!_memory.TryReadInt64(
                terrainAddress + _profile.Terrain.TotalTilesOffset,
                out var tilesX)
            || !_memory.TryReadInt64(
                terrainAddress + _profile.Terrain.TotalTilesOffset + sizeof(long),
                out var tilesY)
            || tilesX <= 0
            || tilesY <= 0
            || tilesX > int.MaxValue
            || tilesY > int.MaxValue)
        {
            return Failure(
                "terrain-tiles-invalid",
                "The terrain tile dimensions were unavailable or outside the supported range.");
        }

        if (!_reader.TryReadStdVector(
                terrainAddress + _profile.Terrain.TilesOffset,
                _profile.Tile.StructureSize,
                MaximumTileCount,
                out var tileRange))
        {
            return Failure(
                "terrain-tiles-invalid",
                "The terrain tile vector was invalid or exceeded the supported limit.");
        }

        if (!_memory.TryReadInt32(
                terrainAddress + _profile.Terrain.BytesPerRowOffset,
                out var bytesPerRow)
            || bytesPerRow <= 0
            || bytesPerRow > MaximumBytesPerRow
            || !_reader.TryReadStdVector(
                terrainAddress + _profile.Terrain.WalkableOffset,
                stride: 1,
                maximumCount: MaximumWalkableBytes,
                out var walkableRange)
            || walkableRange.Count <= 0
            || walkableRange.Count % bytesPerRow != 0)
        {
            return Failure(
                "terrain-grid-invalid",
                "The packed terrain grid was invalid or exceeded the supported limit.");
        }

        var rows = walkableRange.Count / bytesPerRow;
        if (rows <= 0 || rows > MaximumRows)
        {
            return Failure(
                "terrain-grid-invalid",
                "The packed terrain grid row count was outside the supported range.");
        }

        var packed = new byte[walkableRange.Count];
        if (!_memory.TryRead(walkableRange.First, packed))
        {
            return Failure(
                "terrain-grid-read-failed",
                "The packed terrain grid could not be read as one complete buffer.");
        }

        int width;
        byte[] walkable;
        try
        {
            width = checked(bytesPerRow * 2);
            walkable = new byte[checked(width * rows)];
        }
        catch (OverflowException)
        {
            return Failure(
                "terrain-grid-invalid",
                "The unpacked terrain dimensions exceeded the supported range.");
        }

        for (var row = 0; row < rows; row++)
        {
            var packedRow = row * bytesPerRow;
            var outputRow = row * width;
            for (var x = 0; x < width; x++)
            {
                var packedValue = packed[packedRow + (x >> 1)];
                var nibble = (x & 1) == 0
                    ? packedValue & 0x0F
                    : packedValue >> 4;
                walkable[outputRow + x] = (byte)(nibble == 0 ? 0 : 1);
            }
        }

        var diagnostics = new List<AreaReadDiagnostic>();
        var tiles = ReadTiles(
            tileRange,
            checked((int)tilesX),
            diagnostics);
        var tilePaths = tiles
            .Select(tile => tile.TilePath)
            .Where(path => !string.IsNullOrEmpty(path))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var snapshot = new AreaTerrainSnapshot(
            width,
            rows,
            new ReadOnlyMemory<byte>(walkable),
            tilePaths);
        return new AreaTerrainReadResult(snapshot, tiles, diagnostics);
    }

    private IReadOnlyList<AreaTerrainTile> ReadTiles(
        MemoryRange range,
        int tilesX,
        List<AreaReadDiagnostic> diagnostics)
    {
        if (range.Count == 0)
        {
            return [];
        }

        var pathsByFile = new Dictionary<nint, string?>();
        var tiles = new List<AreaTerrainTile>(range.Count);
        for (var index = 0; index < range.Count; index++)
        {
            var tileAddress = range.First + (index * _profile.Tile.StructureSize);
            if (!_reader.TryReadPointer(
                    tileAddress + _profile.Tile.TgtFileOffset,
                    out var targetFile))
            {
                continue;
            }

            if (!pathsByFile.TryGetValue(targetFile, out var tilePath))
            {
                tilePath = _reader.TryReadStdWString(
                    targetFile + _profile.TgtFile.PathOffset,
                    MaximumTilePathLength,
                    out var path)
                    ? path
                    : null;
                pathsByFile[targetFile] = tilePath;
                if (tilePath is null)
                {
                    diagnostics.Add(new AreaReadDiagnostic(
                        "terrain-tile-path-read-failed",
                        "A terrain tile path could not be decoded.",
                        AreaDiagnosticSeverity.Warning));
                }
            }

            if (string.IsNullOrEmpty(tilePath))
            {
                continue;
            }

            tiles.Add(new AreaTerrainTile(
                index % tilesX,
                index / tilesX,
                tilePath));
        }

        return tiles;
    }

    private static AreaTerrainReadResult Failure(
        string code,
        string message)
        => new(
            null,
            [],
            [new AreaReadDiagnostic(
                code,
                message,
                AreaDiagnosticSeverity.Warning)]);
}
