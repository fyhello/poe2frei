using System.Drawing;
using System.Numerics;
using System.Text;
using System.Text.Json;
using FreiAtlas.Core.Area;

namespace FreiAtlas.Host.Tests;

public sealed class AreaProjectionProbeTraceWriterTests
{
    [Fact]
    public void Write_PersistsReproducibleStringEnumFrameOnce()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"frei-projection-trace-{Guid.NewGuid():N}");
        try
        {
            var frame = TraceFrame();
            string outputPath;
            using (var writer = new AreaProjectionProbeTraceWriter(directory))
            {
                writer.Write(frame);
                writer.Write(frame);
                outputPath = writer.OutputPath;
            }

            var lines = File.ReadAllLines(outputPath);
            var json = Assert.Single(lines);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            Assert.Equal("1-200", root.GetProperty("probeFrameId").GetString());
            Assert.Equal(0xFE2A4CD3u, root.GetProperty("areaHash").GetUInt32());
            Assert.Equal(1, root.GetProperty("sessionSequence").GetInt64());
            Assert.Equal(100, root.GetProperty("clientBounds").GetProperty("x").GetInt32());
            Assert.Equal("MiniMap", root.GetProperty("view").GetProperty("kind").GetString());
            Assert.Equal(1.5f, root.GetProperty("view").GetProperty("zoom").GetSingle());
            Assert.Equal(963.2456f, root.GetProperty("playerGrid").GetProperty("x").GetSingle());
            Assert.Equal(
                AreaMapViewKind.MiniMap.ToString(),
                root.GetProperty("parameters").GetProperty("kind").GetString());
            Assert.Equal(
                1f,
                root.GetProperty("parameters").GetProperty("shift").GetProperty("m11").GetSingle());
            var target = Assert.Single(root.GetProperty("targets").EnumerateArray());
            Assert.Equal("expedition-1", target.GetProperty("targetId").GetString());
            Assert.Equal("Expedition", target.GetProperty("contentKind").GetString());
            Assert.Equal("Selected", target.GetProperty("phase").GetString());
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
    public void Write_DoesNotPersistSensitiveReaderOrProcessData()
    {
        using var output = new StringWriter();
        using var writer = new AreaProjectionProbeTraceWriter(output, "memory.ndjson");

        writer.Write(TraceFrame());
        var json = output.ToString();

        Assert.DoesNotContain("29368", json, StringComparison.Ordinal);
        Assert.DoesNotContain("CharacterName", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Metadata", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0x", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\Users", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Write_PropagatesIOExceptionFromTraceSink()
    {
        using var writer = new AreaProjectionProbeTraceWriter(
            new ThrowingTextWriter(),
            "broken.ndjson");

        var exception = Assert.Throws<IOException>(() => writer.Write(TraceFrame()));

        Assert.Equal("trace-write-failed", exception.Message);
    }

    private static AreaProjectionProbeTraceFrame TraceFrame()
    {
        var view = new AreaMapViewSnapshot(
            AreaMapViewKind.MiniMap,
            AreaMapViewAvailability.Verified,
            true,
            Vector2.Zero,
            1.5f,
            0f,
            false,
            new AreaUiRect(283.875f, 187.5f, 340.5f, 225f),
            0.85f);
        var parameters = new AreaMapProjectionParameters(
            AreaMapViewKind.MiniMap,
            new AreaMapLinearTransform(
                0.0011527776f,
                -0.0011527776f,
                -0.000923549f,
                -0.000923549f),
            AreaMapLinearTransform.Identity,
            Vector2.Zero);
        return new AreaProjectionProbeTraceFrame(
            "1-200",
            DateTimeOffset.Parse("2026-08-01T10:30:38.4039012+00:00"),
            0xFE2A4CD3,
            1,
            new Rectangle(100, 100, 908, 600),
            view,
            new Vector2(963.2456f, 1214.7452f),
            parameters,
            [new AreaProjectionProbeTargetTrace(
                "expedition-1",
                AreaContentKind.Expedition,
                AreaContentPhase.Selected,
                new Vector2(900, 1200),
                new Vector2(450, 300))]);
    }

    private sealed class ThrowingTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
            => throw new IOException("trace-write-failed");
    }
}
