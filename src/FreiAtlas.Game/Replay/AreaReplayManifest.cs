using System.Text.Json;
using System.Text.Json.Serialization;
using FreiAtlas.Core.Area;

namespace FreiAtlas.Game.Replay;

public sealed record AreaReplayArea(
    uint AreaHash,
    string AreaCode,
    int AreaLevel);

public sealed record AreaReplayManifest(
    string SchemaVersion,
    string ProfileId,
    string AreaCode,
    DateTimeOffset CapturedAt,
    string AnonymousPlayerId,
    int FrameCount,
    IReadOnlyList<AreaReplayArea> Areas);

internal sealed record AreaReplayFrame(
    int Sequence,
    DateTimeOffset CapturedAt,
    uint? TerrainAreaHash,
    AreaMapSnapshot Snapshot);

internal sealed record AreaReplayTerrainEntry(
    uint AreaHash,
    AreaTerrainSnapshot Terrain);

internal sealed record AreaReplayTerrainDocument(
    string SchemaVersion,
    IReadOnlyList<AreaReplayTerrainEntry> Terrains);

internal sealed record AreaReplayEvidence(
    string Source,
    string Key,
    string Value,
    float Confidence);

internal sealed record AreaReplayUnknownDocument(
    string SchemaVersion,
    IReadOnlyList<string> Metadata,
    IReadOnlyList<string> TilePaths,
    IReadOnlyList<AreaReplayEvidence> Evidence);

internal static class AreaReplayJson
{
    public const string SchemaVersion = "frei-area-replay/1";

    public static JsonSerializerOptions Options { get; } = CreateOptions(writeIndented: true);

    public static JsonSerializerOptions CompactOptions { get; } = CreateOptions(writeIndented: false);

    private static JsonSerializerOptions CreateOptions(bool writeIndented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            IncludeFields = true,
            WriteIndented = writeIndented
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }
}
