using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FreiAtlas.Core.Area;
using FreiAtlas.Game.Content;

namespace FreiAtlas.Game.Replay;

public sealed partial class AreaSnapshotRecorder : IDisposable
{
    private static readonly HashSet<string> AllowedHexEvidenceKeys = new(StringComparer.Ordinal)
    {
        "StateMachine.State",
        "StateMachine.ListenerVec.Count",
        "RuneStation.HoleCount",
        "RuneStation.AnchorPos"
    };

    private static readonly string[] OutputFileNames =
    [
        "manifest.json",
        "frames.ndjson",
        "terrain.json",
        "unknown.json"
    ];

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly StreamWriter _frames;
    private readonly Dictionary<string, string> _anonymousPlayers = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, AreaReplayArea> _areas = [];
    private readonly Dictionary<uint, AreaTerrainSnapshot> _terrains = [];
    private readonly SortedSet<string> _unknownMetadata = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _unknownTilePaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AreaReplayEvidence> _unknownEvidence = new(StringComparer.Ordinal);
    private string _profileId = string.Empty;
    private string _areaCode = string.Empty;
    private DateTimeOffset _capturedAt;
    private bool _disposed;

    public AreaSnapshotRecorder(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        foreach (var fileName in OutputFileNames)
        {
            var path = Path.Combine(_directory, fileName);
            if (File.Exists(path))
            {
                throw new IOException($"Replay output already exists: {path}");
            }
        }

        _frames = new StreamWriter(
            new FileStream(
                Path.Combine(_directory, "frames.ndjson"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public string OutputDirectory => _directory;

    public int FrameCount { get; private set; }

    public void Record(AreaMapSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (FrameCount == 0)
            {
                _profileId = snapshot.ProfileId;
                _capturedAt = snapshot.CapturedAt;
            }
            else if (!string.Equals(_profileId, snapshot.ProfileId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Replay profile changed from '{_profileId}' to '{snapshot.ProfileId}'.");
            }

            if (string.IsNullOrWhiteSpace(_areaCode)
                && snapshot.Area.AreaHash != 0
                && !string.IsNullOrWhiteSpace(snapshot.Area.AreaCode))
            {
                _areaCode = snapshot.Area.AreaCode;
            }

            CapturePlayerIdentities(snapshot);
            CaptureSupportingData(snapshot);
            var sanitized = Sanitize(snapshot);
            var frame = new AreaReplayFrame(
                FrameCount,
                snapshot.CapturedAt,
                snapshot.Terrain is null ? null : snapshot.Area.AreaHash,
                sanitized);
            _frames.WriteLine(JsonSerializer.Serialize(frame, AreaReplayJson.CompactOptions));
            FrameCount++;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _frames.Dispose();
            if (FrameCount == 0)
            {
                throw new InvalidDataException("Cannot finalize an area replay with no frames.");
            }

            if (_areas.Count == 0)
            {
                throw new InvalidDataException(
                    "Cannot finalize an area replay without a valid area identity.");
            }

            WriteSupportingDocuments();
        }
    }

    private void CaptureSupportingData(AreaMapSnapshot snapshot)
    {
        if (snapshot.Area.AreaHash != 0)
        {
            _areas.TryAdd(
                snapshot.Area.AreaHash,
                new AreaReplayArea(
                    snapshot.Area.AreaHash,
                    SanitizeSensitiveText(snapshot.Area.AreaCode),
                    snapshot.Area.AreaLevel));
        }

        if (snapshot.Terrain is not null)
        {
            _terrains.TryAdd(
                snapshot.Area.AreaHash,
                snapshot.Terrain with
                {
                    TilePaths = snapshot.Terrain.TilePaths
                        .Select(SanitizeSensitiveText)
                        .ToArray()
                });
            var knownTiles = snapshot.Landmarks
                .Select(landmark => SanitizeSensitiveText(landmark.TilePath))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var tilePath in snapshot.Terrain.TilePaths)
            {
                var sanitizedTilePath = SanitizeSensitiveText(tilePath);
                if (!knownTiles.Contains(sanitizedTilePath))
                {
                    _unknownTilePaths.Add(sanitizedTilePath);
                }
            }
        }

        foreach (var entity in snapshot.Entities.Where(entity =>
                     entity.Category == AreaEntityCategory.Other))
        {
            _unknownMetadata.Add(SanitizeSensitiveText(entity.MetadataPath));
        }

        foreach (var evidence in snapshot.Contents.SelectMany(content => content.Evidence))
        {
            var sanitizedSource = SanitizeSensitiveText(evidence.Source);
            var sanitizedKey = SanitizeSensitiveText(evidence.Key);
            var sanitizedValue = SanitizeEvidenceValue(evidence);
            var key = string.Join('\u001f',
                sanitizedSource,
                sanitizedKey,
                sanitizedValue,
                evidence.Confidence.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            _unknownEvidence.TryAdd(
                key,
                new AreaReplayEvidence(
                    sanitizedSource,
                    sanitizedKey,
                    sanitizedValue,
                    evidence.Confidence));
        }
    }

    private void CapturePlayerIdentities(AreaMapSnapshot snapshot)
    {
        if (snapshot.Player is not null)
        {
            GetAnonymousPlayer(snapshot.Player.CharacterName);
        }

        foreach (var entity in snapshot.Entities.Where(entity =>
                     entity.Category == AreaEntityCategory.Player))
        {
            GetAnonymousPlayer(entity.DisplayName);
        }
    }

    private AreaMapSnapshot Sanitize(AreaMapSnapshot snapshot)
    {
        var player = snapshot.Player;
        if (player is not null)
        {
            player = player with
            {
                CharacterName = GetAnonymousPlayer(player.CharacterName)
            };
        }

        var entities = snapshot.Entities
            .Select(entity => entity with
            {
                MetadataPath = SanitizeSensitiveText(entity.MetadataPath),
                DisplayName = entity.Category == AreaEntityCategory.Player
                    ? GetAnonymousPlayer(entity.DisplayName)
                    : SanitizeSensitiveText(entity.DisplayName),
                ModIds = entity.ModIds
                    .Select(SanitizeSensitiveText)
                    .ToArray()
            })
            .ToArray();
        var diagnostics = snapshot.Diagnostics
            .Select(diagnostic => diagnostic with
            {
                Code = SanitizeSensitiveText(diagnostic.Code),
                Message = SanitizeSensitiveText(diagnostic.Message)
            })
            .ToArray();
        var contents = snapshot.Contents
            .Select(content => content with
            {
                InstanceId = SanitizeSensitiveText(content.InstanceId),
                ContentId = SanitizeSensitiveText(content.ContentId),
                DisplayName = SanitizeSensitiveText(content.DisplayName),
                Evidence = content.Evidence
                    .Select(evidence => evidence with
                    {
                        Source = SanitizeSensitiveText(evidence.Source),
                        Key = SanitizeSensitiveText(evidence.Key),
                        Value = SanitizeEvidenceValue(evidence)
                    })
                    .ToArray()
            })
            .ToArray();
        var landmarks = snapshot.Landmarks
            .Select(landmark => landmark with
            {
                LandmarkId = SanitizeSensitiveText(landmark.LandmarkId),
                DisplayName = SanitizeSensitiveText(landmark.DisplayName),
                TilePath = SanitizeSensitiveText(landmark.TilePath)
            })
            .ToArray();
        var panel = snapshot.ExpeditionRecipePanel;
        var expeditionRecipePanel = new AreaExpeditionRecipePanelSnapshot(
            panel.Availability,
            panel.IsOpen,
            panel.PanelBounds,
            panel.ListClipBounds,
            panel.InstanceId is null ? null : SanitizeSensitiveText(panel.InstanceId),
            panel.Rows
                .Select(row => row with
                {
                    RecipeId = SanitizeSensitiveText(row.RecipeId)
                })
                .ToImmutableArray());
        return snapshot with
        {
            ProcessId = 0,
            ProfileId = SanitizeSensitiveText(snapshot.ProfileId),
            Area = snapshot.Area with
            {
                AreaCode = SanitizeSensitiveText(snapshot.Area.AreaCode)
            },
            Player = player,
            Entities = entities,
            Contents = contents,
            Landmarks = landmarks,
            ExpeditionRecipePanel = expeditionRecipePanel,
            Terrain = null,
            Diagnostics = diagnostics
        };
    }

    private string GetAnonymousPlayer(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "player-unknown";
        }

        if (_anonymousPlayers.TryGetValue(name, out var anonymous))
        {
            return anonymous;
        }

        anonymous = $"player-{_anonymousPlayers.Count + 1}";
        _anonymousPlayers.Add(name, anonymous);
        return anonymous;
    }

    private string SanitizeSensitiveText(string value)
    {
        var sanitized = HexAddressPattern().Replace(value, "<redacted-address>");
        sanitized = DecimalAddressPattern().Replace(sanitized, "<redacted-address>");
        sanitized = WindowsUserPathPattern().Replace(
            sanitized,
            "$1<redacted-user>");
        foreach (var (name, anonymous) in _anonymousPlayers
                     .OrderByDescending(pair => pair.Key.Length))
        {
            sanitized = sanitized.Replace(name, anonymous, StringComparison.Ordinal);
        }

        return sanitized;
    }

    private string SanitizeEvidenceValue(AreaContentEvidence evidence)
    {
        if (string.Equals(
                evidence.Source,
                MechanicStateEvidenceReader.StateMachineSource,
                StringComparison.Ordinal)
            && string.Equals(
                evidence.Key,
                MechanicStateEvidenceReader.StateKey,
                StringComparison.Ordinal)
            && MechanicStateValuePattern().IsMatch(evidence.Value))
        {
            return evidence.Value;
        }

        var sanitized = SanitizeSensitiveText(evidence.Value);
        if (AllowedHexEvidenceKeys.Contains(evidence.Key))
        {
            sanitized = LongHexAddressPattern().Replace(
                evidence.Value,
                "<redacted-address>");
            sanitized = DecimalAddressPattern().Replace(
                sanitized,
                "<redacted-address>");
            sanitized = WindowsUserPathPattern().Replace(
                sanitized,
                "$1<redacted-user>");
            foreach (var (name, anonymous) in _anonymousPlayers
                         .OrderByDescending(pair => pair.Key.Length))
            {
                sanitized = sanitized.Replace(name, anonymous, StringComparison.Ordinal);
            }
        }

        return sanitized;
    }

    private void WriteSupportingDocuments()
    {
        var manifest = new AreaReplayManifest(
            AreaReplayJson.SchemaVersion,
            SanitizeSensitiveText(_profileId),
            SanitizeSensitiveText(_areaCode),
            _capturedAt,
            _anonymousPlayers.Values.FirstOrDefault() ?? "player-unknown",
            FrameCount,
            _areas.Values.OrderBy(area => area.AreaHash).ToArray());
        WriteJson("manifest.json", manifest);
        WriteJson(
            "terrain.json",
            new AreaReplayTerrainDocument(
                AreaReplayJson.SchemaVersion,
                _terrains
                    .OrderBy(pair => pair.Key)
                    .Select(pair => new AreaReplayTerrainEntry(pair.Key, pair.Value))
                    .ToArray()));
        WriteJson(
            "unknown.json",
            new AreaReplayUnknownDocument(
                AreaReplayJson.SchemaVersion,
                _unknownMetadata.ToArray(),
                _unknownTilePaths.ToArray(),
                _unknownEvidence.Values
                    .OrderBy(item => item.Source, StringComparer.Ordinal)
                    .ThenBy(item => item.Key, StringComparer.Ordinal)
                    .ThenBy(item => item.Value, StringComparer.Ordinal)
                    .ThenBy(item => item.Confidence)
                    .ToArray()));
    }

    private void WriteJson<T>(string fileName, T value)
    {
        var path = Path.Combine(_directory, fileName);
        var temporaryPath = Path.Combine(_directory, $".{fileName}.tmp");
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(value, AreaReplayJson.Options),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryPath, path, overwrite: true);
    }

    [GeneratedRegex(@"0x[0-9A-Fa-f]{6,16}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant)]
    private static partial Regex HexAddressPattern();

    [GeneratedRegex(@"0x[0-9A-Fa-f]{12,16}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant)]
    private static partial Regex LongHexAddressPattern();

    [GeneratedRegex(@"^0x[0-9A-Fa-f]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex MechanicStateValuePattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])\d{10,20}(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalAddressPattern();

    [GeneratedRegex(@"(?i)([A-Z]:\\Users\\)[^\\/\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsUserPathPattern();
}
