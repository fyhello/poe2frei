using System.Text.Json;
using FreiAtlas.Core.Area;
using FreiAtlas.Game.Content;
using FreiAtlas.Game.Replay;

namespace FreiAtlas.Game.Tests.Replay;

public sealed class AreaSnapshotRecorderTests
{
    [Theory]
    [InlineData("Other", "State", "C:\\Users\\secret", "C:\\Users\\<redacted-user>")]
    [InlineData("StateMachine", "State", "0x123456789ABC", "<redacted-address>")]
    [InlineData("StateMachine", "Fake", "0x00000001", "<redacted-address>")]
    public void Record_SanitizesUntrustedStateEvidence(
        string source,
        string key,
        string value,
        string expectedValue)
    {
        var directory = AreaReplayTestData.CreateDirectory();
        try
        {
            var snapshot = AreaReplayTestData.Snapshot(
                0xA1,
                "MapOne",
                1,
                DateTimeOffset.Parse("2026-08-01T01:02:03Z"));
            var content = snapshot.Contents.Single(content =>
                content.Kind == AreaContentKind.Expedition) with
            {
                Evidence =
                [
                    new AreaContentEvidence(
                        source,
                        key,
                        value,
                        0.95f)
                ]
            };

            using (var recorder = new AreaSnapshotRecorder(directory))
            {
                recorder.Record(snapshot with { Contents = [content] });
            }

            using var frame = JsonDocument.Parse(
                File.ReadLines(Path.Combine(directory, "frames.ndjson")).Single());
            var evidence = frame.RootElement
                .GetProperty("snapshot")
                .GetProperty("contents")
                .EnumerateArray()
                .SelectMany(item => item.GetProperty("evidence").EnumerateArray())
                .Single();

            Assert.Equal(expectedValue, evidence.GetProperty("value").GetString());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Record_PreservesMechanicStateEvidenceValueInFrames()
    {
        var directory = AreaReplayTestData.CreateDirectory();
        try
        {
            var snapshot = AreaReplayTestData.Snapshot(
                0xA1,
                "MapOne",
                1,
                DateTimeOffset.Parse("2026-08-01T01:02:03Z"));
            var content = snapshot.Contents.Single(content =>
                content.Kind == AreaContentKind.Expedition) with
            {
                Evidence =
                [
                    new AreaContentEvidence(
                        MechanicStateEvidenceReader.StateMachineSource,
                        MechanicStateEvidenceReader.StateKey,
                        "0x00000001",
                        0.95f)
                ]
            };

            using (var recorder = new AreaSnapshotRecorder(directory))
            {
                recorder.Record(snapshot with { Contents = [content] });
            }

            using var frame = JsonDocument.Parse(
                File.ReadLines(Path.Combine(directory, "frames.ndjson")).Single());
            var evidence = frame.RootElement
                .GetProperty("snapshot")
                .GetProperty("contents")
                .EnumerateArray()
                .SelectMany(item => item.GetProperty("evidence").EnumerateArray())
                .Single(item =>
                    item.GetProperty("source").GetString()
                        == MechanicStateEvidenceReader.StateMachineSource
                    && item.GetProperty("key").GetString()
                        == MechanicStateEvidenceReader.StateKey);

            Assert.Equal("0x00000001", evidence.GetProperty("value").GetString());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Record_WritesFourSanitizedFilesAndPreservesStateEvidence()
    {
        var directory = AreaReplayTestData.CreateDirectory();
        try
        {
            using (var recorder = new AreaSnapshotRecorder(directory))
            {
                recorder.Record(AreaReplayTestData.Snapshot(
                    0xA1,
                    "MapOne",
                    1,
                    DateTimeOffset.Parse("2026-08-01T01:02:03Z")));
                recorder.Record(AreaReplayTestData.Snapshot(
                    0xA1,
                    "MapOne",
                    1,
                    DateTimeOffset.Parse("2026-08-01T01:02:04Z"),
                    largeMapShift: new System.Numerics.Vector2(12, 14)));
                recorder.Record(AreaReplayTestData.Snapshot(
                    0xB2,
                    "MapTwo",
                    2,
                    DateTimeOffset.Parse("2026-08-01T01:03:00Z"),
                    AreaContentPhase.Completed));

                Assert.Equal(3, recorder.FrameCount);
            }

            var files = Directory.GetFiles(directory)
                .Select(path => Path.GetFileName(path)!)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(
                ["frames.ndjson", "manifest.json", "terrain.json", "unknown.json"],
                files);

            var allJson = string.Join(
                '\n',
                Directory.GetFiles(directory).Select(File.ReadAllText));
            Assert.DoesNotContain(AreaReplayTestData.CharacterName, allJson, StringComparison.Ordinal);
            Assert.DoesNotContain(AreaReplayTestData.RawAddress, allJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(AreaReplayTestData.DecimalAddress, allJson, StringComparison.Ordinal);
            Assert.DoesNotContain(AreaReplayTestData.WindowsAccountName, allJson, StringComparison.Ordinal);
            Assert.Contains(AreaReplayTestData.StateValue, allJson, StringComparison.Ordinal);
            Assert.Contains("\"holeCount\":6", allJson, StringComparison.Ordinal);
            Assert.Contains("player-1", allJson, StringComparison.Ordinal);

            using var manifest = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(directory, "manifest.json")));
            var root = manifest.RootElement;
            Assert.Equal("frei-area-replay/1", root.GetProperty("schemaVersion").GetString());
            Assert.Equal("poe2-test-profile", root.GetProperty("profileId").GetString());
            Assert.Equal("MapOne", root.GetProperty("areaCode").GetString());
            Assert.Equal("player-1", root.GetProperty("anonymousPlayerId").GetString());
            Assert.Equal(3, root.GetProperty("frameCount").GetInt32());

            Assert.Equal(
                3,
                File.ReadLines(Path.Combine(directory, "frames.ndjson")).Count());
            var unknown = File.ReadAllText(Path.Combine(directory, "unknown.json"));
            Assert.Contains("Metadata/Unknown/player-1/ReplayEntity", unknown, StringComparison.Ordinal);
            Assert.Contains("StateMachine.State", unknown, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Record_PreservesVerifiedRecipePanelWithoutAddresses()
    {
        var directory = AreaReplayTestData.CreateDirectory();
        try
        {
            using (var recorder = new AreaSnapshotRecorder(directory))
            {
                recorder.Record(AreaReplayTestData.Snapshot(
                    0xA1,
                    "MapOne",
                    1,
                    DateTimeOffset.UtcNow) with
                {
                    ExpeditionRecipePanel = AreaReplayTestData.VerifiedRecipePanel()
                });
            }

            var json = File.ReadAllText(Path.Combine(directory, "frames.ndjson"));
            Assert.Contains("\"recipeId\":\"recipe-1\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("panelAddress", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("containerAddress", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Record_UsesFirstValidAreaAfterLoadingForManifest()
    {
        var directory = AreaReplayTestData.CreateDirectory();
        try
        {
            using (var recorder = new AreaSnapshotRecorder(directory))
            {
                recorder.Record(AreaMapSnapshot.Loading(
                    30744,
                    "poe2-test-profile",
                    0));
                recorder.Record(AreaReplayTestData.Snapshot(
                    0xA1,
                    "MapOne",
                    1,
                    DateTimeOffset.Parse("2026-08-01T01:02:03Z")));
            }

            using var manifest = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(directory, "manifest.json")));
            Assert.Equal(
                "MapOne",
                manifest.RootElement.GetProperty("areaCode").GetString());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Record_SortsOtherwiseEqualUnknownEvidenceByConfidence()
    {
        var directory = AreaReplayTestData.CreateDirectory();
        try
        {
            var snapshot = AreaReplayTestData.Snapshot(
                0xA1,
                "MapOne",
                1,
                DateTimeOffset.Parse("2026-08-01T01:02:03Z"));
            var expedition = snapshot.Contents.Single(content =>
                content.Kind == AreaContentKind.Expedition) with
            {
                Evidence =
                [
                    new AreaContentEvidence("StateMachine", "Shared", "same", 0.9f),
                    new AreaContentEvidence("StateMachine", "Shared", "same", 0.1f)
                ]
            };
            snapshot = snapshot with { Contents = [expedition] };

            using (var recorder = new AreaSnapshotRecorder(directory))
            {
                recorder.Record(snapshot);
            }

            using var unknown = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(directory, "unknown.json")));
            var confidence = unknown.RootElement
                .GetProperty("evidence")
                .EnumerateArray()
                .Select(item => item.GetProperty("confidence").GetSingle())
                .ToArray();
            Assert.Equal([0.1f, 0.9f], confidence);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Dispose_WithoutFramesRejectsEmptyReplay()
    {
        var directory = AreaReplayTestData.CreateDirectory();
        try
        {
            var recorder = new AreaSnapshotRecorder(directory);

            Assert.Throws<InvalidDataException>(recorder.Dispose);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
