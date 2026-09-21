using System.Text.Json.Nodes;
using FreiAtlas.Core.Area;
using FreiAtlas.Game.Replay;

namespace FreiAtlas.Game.Tests.Replay;

public sealed class ReplayAreaMapProviderTests
{
    [Fact]
    public void ReadNext_ReplaysSnapshotsAndEventsInRecordedOrder()
    {
        var directory = RecordThreeFrames();
        try
        {
            using (var provider = ReplayAreaMapProvider.Load(directory))
            {
                var observed = new List<AreaMapSnapshot>();
                provider.SnapshotChanged += observed.Add;

                Assert.Equal(AreaMapSnapshotStatus.Loading, provider.Current.Status);
                Assert.True(provider.ReadNext());
                Assert.True(provider.ReadNext());
                Assert.True(provider.ReadNext());
                Assert.False(provider.ReadNext());

                Assert.Equal(3, observed.Count);
                Assert.Equal(new uint[] { 0xA1, 0xA1, 0xB2 }, observed.Select(x => x.Area.AreaHash));
                Assert.Equal(AreaContentPhase.Completed, provider.Current.Contents.Single(
                    content => content.Kind == AreaContentKind.Boss).Phase);
                Assert.Equal(
                    6,
                    provider.Current.Contents.Single(content =>
                        content.Kind == AreaContentKind.Expedition)
                        .ExpeditionDetails!.HoleCount);
                Assert.Equal(new System.Numerics.Vector2(19, 23), provider.Current.MapViews.LargeMap.Shift);
                Assert.NotNull(provider.Current.Terrain);
                Assert.Equal(new byte[] { 1, 0, 1, 1 }, provider.Current.Terrain!.Walkable.ToArray());
                Assert.Equal("player-1", provider.Current.Player!.CharacterName);
                Assert.Equal(0, provider.Current.ProcessId);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_ReportsCorruptFrameWithFileAndLineNumber()
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
                    DateTimeOffset.UtcNow));
            }

            File.AppendAllText(Path.Combine(directory, "frames.ndjson"), "{broken json}\n");

            var exception = Assert.Throws<InvalidDataException>(
                () => ReplayAreaMapProvider.Load(directory));
            Assert.Contains("frames.ndjson", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("line 2", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_RejectsUnknownSchema()
    {
        var directory = RecordThreeFrames();
        try
        {
            var path = Path.Combine(directory, "manifest.json");
            File.WriteAllText(
                path,
                File.ReadAllText(path).Replace(
                    "frei-area-replay/1",
                    "frei-area-replay/99",
                    StringComparison.Ordinal));

            var exception = Assert.Throws<InvalidDataException>(
                () => ReplayAreaMapProvider.Load(directory));
            Assert.Contains("manifest.json", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("schema", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_RejectsMissingReferencedTerrainFile()
    {
        var directory = RecordThreeFrames();
        try
        {
            File.Delete(Path.Combine(directory, "terrain.json"));

            var exception = Assert.Throws<InvalidDataException>(
                () => ReplayAreaMapProvider.Load(directory));
            Assert.Contains("terrain.json", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_RejectsNullSnapshotWithFileAndLineNumber()
    {
        var directory = RecordThreeFrames();
        try
        {
            MutateJsonLine(directory, 0, root => root["snapshot"] = null);

            var exception = Assert.Throws<InvalidDataException>(
                () => ReplayAreaMapProvider.Load(directory));
            Assert.Contains("frames.ndjson", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("line 1", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_RejectsNullTerrainCollection()
    {
        var directory = RecordThreeFrames();
        try
        {
            var path = Path.Combine(directory, "terrain.json");
            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            root["terrains"] = null;
            File.WriteAllText(path, root.ToJsonString());

            var exception = Assert.Throws<InvalidDataException>(
                () => ReplayAreaMapProvider.Load(directory));
            Assert.Contains("terrain.json", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_RejectsNumericEnumValues()
    {
        var directory = RecordThreeFrames();
        try
        {
            MutateJsonLine(directory, 0, root =>
                root["snapshot"]!["status"] = 2);

            var exception = Assert.Throws<InvalidDataException>(
                () => ReplayAreaMapProvider.Load(directory));
            Assert.Contains("frames.ndjson", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("line 1", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_RejectsReplayWithNoFrames()
    {
        var directory = RecordThreeFrames();
        try
        {
            var manifestPath = Path.Combine(directory, "manifest.json");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            manifest["frameCount"] = 0;
            File.WriteAllText(manifestPath, manifest.ToJsonString());
            File.WriteAllText(Path.Combine(directory, "frames.ndjson"), string.Empty);

            var exception = Assert.Throws<InvalidDataException>(
                () => ReplayAreaMapProvider.Load(directory));
            Assert.Contains("frame", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_KeepsFrameFileOpenForIncrementalReplay()
    {
        var directory = RecordThreeFrames();
        var provider = ReplayAreaMapProvider.Load(directory);
        try
        {
            var path = Path.Combine(directory, "frames.ndjson");
            Assert.Throws<IOException>(() =>
            {
                using var exclusive = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None);
            });
            Assert.True(provider.ReadNext());
        }
        finally
        {
            ((object)provider as IDisposable)?.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_LegacyFrameWithoutExpeditionDetailsDefaultsToNull()
    {
        var directory = RecordThreeFrames();
        try
        {
            MutateJsonLine(directory, 0, root =>
            {
                var contents = root["snapshot"]!["contents"]!.AsArray();
                var expedition = contents
                    .Select(node => node!.AsObject())
                    .Single(item => item["kind"]!.GetValue<string>() == "Expedition");
                expedition.Remove("expeditionDetails");
            });

            using var provider = ReplayAreaMapProvider.Load(directory);
            Assert.True(provider.ReadNext());
            Assert.Null(provider.Current.Contents.Single(content =>
                content.Kind == AreaContentKind.Expedition).ExpeditionDetails);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_LegacyFrameWithoutNativeRecipePanelDefaultsToUnavailable()
    {
        var directory = RecordThreeFrames();
        try
        {
            MutateJsonLine(directory, 0, root =>
                root["snapshot"]!.AsObject().Remove("expeditionRecipePanel"));

            using var provider = ReplayAreaMapProvider.Load(directory);
            Assert.True(provider.ReadNext());
            Assert.Equal(
                AreaExpeditionRecipePanelSnapshot.Unavailable,
                provider.Current.ExpeditionRecipePanel);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_RejectsIncompleteVerifiedNativeRecipePanel()
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

            MutateJsonLine(directory, 0, root =>
            {
                root["snapshot"]!["expeditionRecipePanel"]!["rows"] = new JsonArray();
            });

            var exception = Assert.Throws<InvalidDataException>(
                () => ReplayAreaMapProvider.Load(directory));
            Assert.Contains("invalid replay frame", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_RejectsInvalidExpeditionHoleCount()
    {
        var directory = RecordThreeFrames();
        try
        {
            MutateJsonLine(directory, 0, root =>
            {
                var expedition = root["snapshot"]!["contents"]!.AsArray()
                    .Select(node => node!.AsObject())
                    .Single(item => item["kind"]!.GetValue<string>() == "Expedition");
                expedition["expeditionDetails"]!["holeCount"] = 17;
            });

            var recorded = Record.Exception(() =>
            {
                using var provider = ReplayAreaMapProvider.Load(directory);
            });
            var exception = Assert.IsType<InvalidDataException>(recorded);
            Assert.Contains("frames.ndjson", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("expedition hole count is invalid", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string RecordThreeFrames()
    {
        var directory = AreaReplayTestData.CreateDirectory();
        using var recorder = new AreaSnapshotRecorder(directory);
        recorder.Record(AreaReplayTestData.Snapshot(
            0xA1,
            "MapOne",
            1,
            DateTimeOffset.Parse("2026-08-01T01:02:03Z")));
        recorder.Record(AreaReplayTestData.Snapshot(
            0xA1,
            "MapOne",
            1,
            DateTimeOffset.Parse("2026-08-01T01:02:04Z")));
        recorder.Record(AreaReplayTestData.Snapshot(
            0xB2,
            "MapTwo",
            2,
            DateTimeOffset.Parse("2026-08-01T01:03:00Z"),
            AreaContentPhase.Completed,
            new System.Numerics.Vector2(19, 23)));
        return directory;
    }

    private static void MutateJsonLine(
        string directory,
        int lineIndex,
        Action<JsonObject> mutation)
    {
        var path = Path.Combine(directory, "frames.ndjson");
        var lines = File.ReadAllLines(path);
        var root = JsonNode.Parse(lines[lineIndex])!.AsObject();
        mutation(root);
        lines[lineIndex] = root.ToJsonString();
        File.WriteAllLines(path, lines);
    }
}
