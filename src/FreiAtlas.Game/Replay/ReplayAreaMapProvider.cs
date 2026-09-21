using System.Numerics;
using System.Text;
using System.Text.Json;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Contracts;

namespace FreiAtlas.Game.Replay;

public sealed class ReplayAreaMapProvider : IAreaMapApi, IDisposable
{
    private readonly string _framesPath;
    private readonly IReadOnlyDictionary<uint, AreaTerrainSnapshot> _terrains;
    private readonly StreamReader _frames;
    private int _nextFrame;
    private bool _disposed;

    private ReplayAreaMapProvider(
        AreaReplayManifest manifest,
        string framesPath,
        IReadOnlyDictionary<uint, AreaTerrainSnapshot> terrains)
    {
        Manifest = manifest;
        _framesPath = framesPath;
        _terrains = terrains;
        try
        {
            _frames = OpenFrames(framesPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"{framesPath}: could not open replay frames.",
                exception);
        }

        Current = AreaMapSnapshot.Loading(0, manifest.ProfileId, 0);
    }

    public AreaReplayManifest Manifest { get; }

    public int FrameCount => Manifest.FrameCount;

    public AreaMapSnapshot Current { get; private set; }

    public event Action<AreaMapSnapshot>? SnapshotChanged;

    public static ReplayAreaMapProvider Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var resolvedDirectory = Path.GetFullPath(directory);
        var manifestPath = Path.Combine(resolvedDirectory, "manifest.json");
        var manifest = ReadDocument<AreaReplayManifest>(manifestPath);
        ValidateManifest(manifest, manifestPath);

        var terrainPath = Path.Combine(resolvedDirectory, "terrain.json");
        var terrainDocument = ReadDocument<AreaReplayTerrainDocument>(terrainPath);
        ValidateSchema(terrainDocument.SchemaVersion, terrainPath);
        var terrains = BuildTerrainIndex(terrainDocument, terrainPath);

        var unknownPath = Path.Combine(resolvedDirectory, "unknown.json");
        var unknown = ReadDocument<AreaReplayUnknownDocument>(unknownPath);
        ValidateUnknown(unknown, unknownPath);

        var framesPath = Path.Combine(resolvedDirectory, "frames.ndjson");
        var frameCount = ValidateFrames(framesPath, manifest, terrains);
        if (frameCount != manifest.FrameCount)
        {
            throw new InvalidDataException(
                $"{manifestPath}: frameCount {manifest.FrameCount} does not match "
                + $"{frameCount} frame(s) in {framesPath}.");
        }

        return new ReplayAreaMapProvider(manifest, framesPath, terrains);
    }

    public bool ReadNext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_nextFrame >= FrameCount)
        {
            return false;
        }

        var lineNumber = _nextFrame + 1;
        var line = _frames.ReadLine()
                   ?? throw new InvalidDataException(
                       $"{_framesPath}: line {lineNumber} is missing.");
        Current = ParseFrame(
            line,
            _nextFrame,
            Manifest,
            _terrains,
            _framesPath,
            lineNumber);
        _nextFrame++;
        SnapshotChanged?.Invoke(Current);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _frames.Dispose();
    }

    private static void ValidateManifest(AreaReplayManifest manifest, string path)
    {
        ValidateSchema(manifest.SchemaVersion, path);
        if (string.IsNullOrWhiteSpace(manifest.ProfileId)
            || string.IsNullOrWhiteSpace(manifest.AreaCode)
            || string.IsNullOrWhiteSpace(manifest.AnonymousPlayerId))
        {
            throw new InvalidDataException(
                $"{path}: profileId, areaCode, and anonymousPlayerId are required.");
        }

        if (manifest.FrameCount <= 0)
        {
            throw new InvalidDataException($"{path}: frameCount must be positive.");
        }

        if (manifest.Areas is null || manifest.Areas.Count == 0)
        {
            throw new InvalidDataException($"{path}: at least one area is required.");
        }

        if (manifest.Areas.Any(area =>
                area is null
                || area.AreaHash == 0
                || string.IsNullOrWhiteSpace(area.AreaCode)))
        {
            throw new InvalidDataException($"{path}: area entries are invalid.");
        }
    }

    private static Dictionary<uint, AreaTerrainSnapshot> BuildTerrainIndex(
        AreaReplayTerrainDocument document,
        string path)
    {
        if (document.Terrains is null)
        {
            throw new InvalidDataException($"{path}: terrains is required.");
        }

        var result = new Dictionary<uint, AreaTerrainSnapshot>();
        foreach (var entry in document.Terrains)
        {
            if (entry is null || entry.Terrain is null || entry.AreaHash == 0)
            {
                throw new InvalidDataException($"{path}: terrain entry is invalid.");
            }

            var terrain = entry.Terrain;
            var expectedLength = (long)terrain.Width * terrain.Height;
            if (terrain.Width <= 0
                || terrain.Height <= 0
                || expectedLength > int.MaxValue
                || terrain.Walkable.Length != expectedLength
                || terrain.TilePaths is null)
            {
                throw new InvalidDataException(
                    $"{path}: terrain 0x{entry.AreaHash:X8} dimensions do not match its data.");
            }

            if (!result.TryAdd(entry.AreaHash, terrain))
            {
                throw new InvalidDataException(
                    $"{path}: duplicate terrain for areaHash 0x{entry.AreaHash:X8}.");
            }
        }

        return result;
    }

    private static void ValidateUnknown(AreaReplayUnknownDocument document, string path)
    {
        ValidateSchema(document.SchemaVersion, path);
        if (document.Metadata is null
            || document.TilePaths is null
            || document.Evidence is null
            || document.Metadata.Any(value => value is null)
            || document.TilePaths.Any(value => value is null)
            || document.Evidence.Any(value =>
                value is null
                || value.Source is null
                || value.Key is null
                || value.Value is null
                || !float.IsFinite(value.Confidence)))
        {
            throw new InvalidDataException($"{path}: unknown evidence document is invalid.");
        }
    }

    private static int ValidateFrames(
        string path,
        AreaReplayManifest manifest,
        IReadOnlyDictionary<uint, AreaTerrainSnapshot> terrains)
    {
        StreamReader reader;
        try
        {
            reader = OpenFrames(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"{path}: could not read replay frames.", exception);
        }

        using (reader)
        {
            var count = 0;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                ParseFrame(line, count, manifest, terrains, path, count + 1);
                count++;
            }

            return count;
        }
    }

    private static AreaMapSnapshot ParseFrame(
        string line,
        int expectedSequence,
        AreaReplayManifest manifest,
        IReadOnlyDictionary<uint, AreaTerrainSnapshot> terrains,
        string path,
        int lineNumber)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            throw InvalidFrame(path, lineNumber, "frame is empty");
        }

        AreaReplayFrame frame;
        try
        {
            frame = JsonSerializer.Deserialize<AreaReplayFrame>(
                        line,
                        AreaReplayJson.CompactOptions)
                    ?? throw new JsonException("Replay frame is empty.");
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new InvalidDataException(
                $"{path}: line {lineNumber} contains an invalid replay frame.",
                exception);
        }

        if (frame.Snapshot is null)
        {
            throw InvalidFrame(path, lineNumber, "snapshot is required");
        }

        ValidateFrame(frame, expectedSequence, manifest, path, lineNumber);
        AreaTerrainSnapshot? terrain = null;
        if (frame.TerrainAreaHash is { } areaHash)
        {
            if (!terrains.TryGetValue(areaHash, out terrain))
            {
                throw InvalidFrame(
                    path,
                    lineNumber,
                    $"missing terrain.json areaHash 0x{areaHash:X8}");
            }

            if (frame.Snapshot.Area.AreaHash != areaHash)
            {
                throw InvalidFrame(
                    path,
                    lineNumber,
                    "terrain reference does not match its area");
            }
        }

        return frame.Snapshot with
        {
            CapturedAt = frame.CapturedAt,
            ProcessId = 0,
            Terrain = terrain
        };
    }

    private static void ValidateFrame(
        AreaReplayFrame frame,
        int expectedSequence,
        AreaReplayManifest manifest,
        string path,
        int lineNumber)
    {
        var snapshot = frame.Snapshot;
        if (frame.Sequence != expectedSequence)
        {
            throw InvalidFrame(
                path,
                lineNumber,
                $"sequence {frame.Sequence} does not match expected {expectedSequence}");
        }

        if (frame.CapturedAt != snapshot.CapturedAt)
        {
            throw InvalidFrame(path, lineNumber, "capturedAt does not match snapshot");
        }

        if (!string.Equals(snapshot.ProfileId, manifest.ProfileId, StringComparison.Ordinal))
        {
            throw InvalidFrame(path, lineNumber, "profileId does not match manifest.json");
        }

        if (snapshot.ProcessId != 0)
        {
            throw InvalidFrame(path, lineNumber, "snapshot contains a live processId");
        }

        if (!Enum.IsDefined(snapshot.Status)
            || snapshot.Area is null
            || snapshot.Entities is null
            || snapshot.Contents is null
            || snapshot.Landmarks is null
            || snapshot.MapViews is null
            || snapshot.Diagnostics is null)
        {
            throw InvalidFrame(path, lineNumber, "snapshot contract is incomplete");
        }

        if (snapshot.Status is AreaMapSnapshotStatus.Stable or AreaMapSnapshotStatus.Degraded
            && snapshot.Area.AreaHash == 0)
        {
            throw InvalidFrame(path, lineNumber, "stable snapshot has no area identity");
        }

        if (snapshot.Status == AreaMapSnapshotStatus.Loading
            && (snapshot.Entities.Count > 0
                || snapshot.Contents.Count > 0
                || snapshot.Landmarks.Count > 0
                || frame.TerrainAreaHash is not null))
        {
            throw InvalidFrame(path, lineNumber, "loading snapshot retains area data");
        }

        if (snapshot.Terrain is not null)
        {
            throw InvalidFrame(path, lineNumber, "terrain must be stored in terrain.json");
        }

        if (snapshot.Player is { } player
            && (string.IsNullOrWhiteSpace(player.CharacterName)
                || !IsFinite(player.WorldPosition)
                || !IsFinite(player.GridPosition)))
        {
            throw InvalidFrame(path, lineNumber, "player snapshot is invalid");
        }

        foreach (var entity in snapshot.Entities)
        {
            if (entity is null
                || string.IsNullOrWhiteSpace(entity.MetadataPath)
                || entity.ModIds is null
                || !Enum.IsDefined(entity.Category)
                || !Enum.IsDefined(entity.Disposition)
                || !Enum.IsDefined(entity.Rarity)
                || !Enum.IsDefined(entity.ChestState)
                || !IsFinite(entity.WorldPosition)
                || !IsFinite(entity.GridPosition))
            {
                throw InvalidFrame(path, lineNumber, "entity snapshot is invalid");
            }
        }

        foreach (var content in snapshot.Contents)
        {
            if (content is null
                || string.IsNullOrWhiteSpace(content.InstanceId)
                || string.IsNullOrWhiteSpace(content.ContentId)
                || content.Evidence is null
                || !Enum.IsDefined(content.Kind)
                || !Enum.IsDefined(content.Phase)
                || !IsConfidence(content.Confidence)
                || !IsFinite(content.WorldPosition)
                || !IsFinite(content.GridPosition)
                || content.Evidence.Any(evidence =>
                    evidence is null
                    || evidence.Source is null
                    || evidence.Key is null
                    || evidence.Value is null
                    || !IsConfidence(evidence.Confidence)))
            {
                throw InvalidFrame(path, lineNumber, "content snapshot is invalid");
            }

            if (content.ExpeditionDetails is { HoleCount: < 1 or > 16 })
            {
                throw InvalidFrame(path, lineNumber, "expedition hole count is invalid");
            }
        }

        foreach (var landmark in snapshot.Landmarks)
        {
            if (landmark is null
                || string.IsNullOrWhiteSpace(landmark.LandmarkId)
                || string.IsNullOrWhiteSpace(landmark.TilePath)
                || !Enum.IsDefined(landmark.Kind)
                || !IsFinite(landmark.GridPosition)
                || landmark.TileCount <= 0)
            {
                throw InvalidFrame(path, lineNumber, "landmark snapshot is invalid");
            }
        }

        ValidateView(snapshot.MapViews.LargeMap, AreaMapViewKind.LargeMap, path, lineNumber);
        ValidateView(snapshot.MapViews.MiniMap, AreaMapViewKind.MiniMap, path, lineNumber);
        ValidateRecipePanel(snapshot, path, lineNumber);
        if (snapshot.Diagnostics.Any(diagnostic =>
                diagnostic is null
                || diagnostic.Code is null
                || diagnostic.Message is null
                || !Enum.IsDefined(diagnostic.Severity)))
        {
            throw InvalidFrame(path, lineNumber, "diagnostic snapshot is invalid");
        }
    }

    private static void ValidateRecipePanel(
        AreaMapSnapshot snapshot,
        string path,
        int lineNumber)
    {
        var panel = snapshot.ExpeditionRecipePanel;
        if (panel is null || !Enum.IsDefined(panel.Availability))
        {
            throw InvalidFrame(path, lineNumber, "native recipe panel availability is invalid");
        }

        if (!panel.IsOpen)
        {
            return;
        }

        if (panel.Availability != AreaExpeditionRecipePanelAvailability.Verified
            && panel.Availability != AreaExpeditionRecipePanelAvailability.Unverified)
        {
            throw InvalidFrame(path, lineNumber, "open native recipe panel availability is invalid");
        }

        if (panel.Availability != AreaExpeditionRecipePanelAvailability.Verified)
        {
            return;
        }

        if (panel.PanelBounds is not { } panelBounds
            || panel.ListClipBounds is not { } listBounds
            || string.IsNullOrWhiteSpace(panel.InstanceId)
            || panel.Rows.IsDefaultOrEmpty
            || !IsValidRect(panelBounds)
            || !IsValidRect(listBounds))
        {
            throw InvalidFrame(path, lineNumber, "verified native recipe panel is incomplete");
        }

        foreach (var row in panel.Rows)
        {
            if (row is null
                || string.IsNullOrWhiteSpace(row.RecipeId)
                || row.CatalogRow < 0
                || !IsValidRect(row.Bounds))
            {
                throw InvalidFrame(path, lineNumber, "native recipe panel row is invalid");
            }
        }
    }

    private static bool IsValidRect(AreaUiRect rect)
        => float.IsFinite(rect.X)
           && float.IsFinite(rect.Y)
           && float.IsFinite(rect.Width)
           && float.IsFinite(rect.Height)
           && rect.Width > 0f
           && rect.Height > 0f;

    private static void ValidateView(
        AreaMapViewSnapshot view,
        AreaMapViewKind expectedKind,
        string path,
        int lineNumber)
    {
        if (view is null
            || view.Kind != expectedKind
            || !Enum.IsDefined(view.Availability)
            || !IsFinite(view.Shift)
            || !float.IsFinite(view.Zoom)
            || !float.IsFinite(view.RotationRadians)
            || !IsConfidence(view.Confidence)
            || view.Viewport is { } viewport
            && (!float.IsFinite(viewport.X)
                || !float.IsFinite(viewport.Y)
                || !float.IsFinite(viewport.Width)
                || !float.IsFinite(viewport.Height)
                || viewport.Width < 0f
                || viewport.Height < 0f))
        {
            throw InvalidFrame(path, lineNumber, $"{expectedKind} view is invalid");
        }
    }

    private static T ReadDocument<T>(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, AreaReplayJson.Options)
                   ?? throw new JsonException("Replay document is empty.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException($"{path}: invalid replay document.", exception);
        }
    }

    private static void ValidateSchema(string schemaVersion, string path)
    {
        if (!string.Equals(
                schemaVersion,
                AreaReplayJson.SchemaVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{path}: unsupported schema '{schemaVersion}'.");
        }
    }

    private static StreamReader OpenFrames(string path)
        => new(
            new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 64 * 1024);

    private static InvalidDataException InvalidFrame(
        string path,
        int lineNumber,
        string reason)
        => new($"{path}: line {lineNumber} {reason}.");

    private static bool IsFinite(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X)
           && float.IsFinite(value.Y)
           && float.IsFinite(value.Z);

    private static bool IsConfidence(float value)
        => float.IsFinite(value) && value is >= 0f and <= 1f;
}
