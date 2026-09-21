using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreiAtlas.Host;

internal interface IAreaProjectionProbeTraceSink : IDisposable
{
    string OutputPath { get; }

    void Write(AreaProjectionProbeTraceFrame frame);
}

internal sealed class AreaProjectionProbeTraceWriter : IAreaProjectionProbeTraceSink
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly TextWriter _writer;
    private readonly HashSet<string> _writtenFrameIds = new(StringComparer.Ordinal);
    private bool _disposed;

    public AreaProjectionProbeTraceWriter(string directory)
        : this(CreateOutput(directory))
    {
    }

    internal AreaProjectionProbeTraceWriter(TextWriter writer, string outputPath)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        OutputPath = string.IsNullOrWhiteSpace(outputPath)
            ? throw new ArgumentException("A trace output path is required.", nameof(outputPath))
            : outputPath;
    }

    private AreaProjectionProbeTraceWriter(TraceOutput output)
        : this(output.Writer, output.Path)
    {
    }

    public string OutputPath { get; }

    public void Write(AreaProjectionProbeTraceFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        if (_writtenFrameIds.Contains(frame.ProbeFrameId))
        {
            return;
        }

        var json = JsonSerializer.Serialize(frame, JsonOptions);
        _writer.WriteLine(json);
        _writer.Flush();
        _writtenFrameIds.Add(frame.ProbeFrameId);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writer.Dispose();
    }

    private static TraceOutput CreateOutput(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("A projection trace directory is required.", nameof(directory));
        }

        var fullDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullDirectory);
        var outputPath = Path.Combine(fullDirectory, "projection.ndjson");
        var stream = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read);
        var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new TraceOutput(writer, outputPath);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            IncludeFields = true,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record TraceOutput(TextWriter Writer, string Path);
}
