using System.Xml.Linq;

namespace FreiAtlas.Host.Tests;

public sealed class ReadingLayerBoundaryTests
{
    private static readonly string WorkspaceRoot = FindWorkspaceRoot();

    [Theory]
    [InlineData("FreiAtlas.Atlas")]
    [InlineData("FreiAtlas.Game")]
    public void ReadingProjects_ReferenceOnlyCore(string projectName)
    {
        var projectPath = Path.Combine(
            WorkspaceRoot,
            "src",
            projectName,
            $"{projectName}.csproj");
        var references = XDocument.Load(projectPath)
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => value is not null)
            .Select(value => Path.GetFileNameWithoutExtension(value!))
            .ToArray();

        Assert.Equal(["FreiAtlas.Core"], references);
    }

    [Theory]
    [InlineData("FreiAtlas.Platform.Windows", "Overlay")]
    [InlineData("FreiAtlas.App", "")]
    [InlineData("FreiAtlas.Settings", "")]
    public void DrawingAndGuiSources_DoNotUseMemoryReaders(
        string projectName,
        string relativeDirectory)
    {
        var directory = Path.Combine(
            WorkspaceRoot,
            "src",
            projectName,
            relativeDirectory);

        AssertNoForbiddenTokens(
            directory,
            "IProcessMemory",
            "ReadProcessMemory",
            "GameMemorySession",
            "AreaEntityReader",
            "AtlasProcessReader",
            "LiveAtlasMemoryProbe",
            "AreaMapProviderService",
            "AtlasProviderService");
    }

    [Fact]
    public void ReplaySources_DoNotDependOnLiveMemoryOrWindowsPlatform()
    {
        var directory = Path.Combine(
            WorkspaceRoot,
            "src",
            "FreiAtlas.Game",
            "Replay");

        AssertNoForbiddenTokens(
            directory,
            "IProcessMemory",
            "ReadProcessMemory",
            "FreiAtlas.Platform.Windows",
            "System.Diagnostics.Process",
            "ProcessAttachment");
    }

    [Fact]
    public void AreaProjectionProbeSources_ConsumeSnapshotsWithoutReadingMemory()
    {
        var files = new[]
        {
            Path.Combine(
                WorkspaceRoot,
                "src",
                "FreiAtlas.Core",
                "Area",
                "AreaMapProjection.cs")
        }
            .Concat(Directory.EnumerateFiles(
                Path.Combine(WorkspaceRoot, "src", "FreiAtlas.Host"),
                "AreaProjectionProbe*.cs",
                SearchOption.TopDirectoryOnly))
            .Concat(Directory.EnumerateFiles(
                Path.Combine(
                    WorkspaceRoot,
                    "src",
                    "FreiAtlas.Platform.Windows",
                    "Overlay"),
                "AreaProjectionProbe*.cs",
                SearchOption.TopDirectoryOnly));

        AssertFilesDoNotContain(
            files,
            "IProcessMemory",
            "ReadProcessMemory",
            "ProcessAttachment",
            "AreaMapProviderService",
            "GameMemorySession",
            "MapUiCandidateProbe",
            "AreaEntityReader",
            "FreiAtlas.Game");
    }

    [Fact]
    public void FormalAreaOverlayPluginAndSurface_ConsumeSnapshotsWithoutReadingMemory()
    {
        var files = Directory.EnumerateFiles(
                Path.Combine(WorkspaceRoot, "src", "FreiAtlas.Plugin.AreaMap"),
                "*.cs",
                SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(
                Path.Combine(
                    WorkspaceRoot,
                    "src",
                    "FreiAtlas.Platform.Windows",
                    "Overlay"),
                "AreaMapOverlay*.cs",
                SearchOption.TopDirectoryOnly));

        AssertFilesDoNotContain(
            files,
            "IProcessMemory",
            "ReadProcessMemory",
            "ProcessAttachment",
            "AreaMapProviderService",
            "GameMemorySession",
            "MapUiCandidateProbe",
            "AreaEntityReader",
            "FreiAtlas.Game");
    }

    [Fact]
    public void NativeExpeditionValuePluginAndSurface_ConsumeSnapshotsWithoutReadingMemory()
    {
        var files = Directory.EnumerateFiles(
                Path.Combine(
                    WorkspaceRoot,
                    "src",
                    "FreiAtlas.Plugin.ExpeditionPanel"),
                "ExpeditionNativeValue*.cs",
                SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(
                Path.Combine(
                    WorkspaceRoot,
                    "src",
                    "FreiAtlas.Platform.Windows",
                    "Overlay"),
                "ExpeditionNativeValue*.cs",
                SearchOption.TopDirectoryOnly));

        AssertFilesDoNotContain(
            files,
            "IProcessMemory",
            "ReadProcessMemory",
            "ProcessAttachment",
            "AreaMapProviderService",
            "GameMemorySession",
            "ExpeditionValueCalculator",
            "ExpeditionValueTextFormatter",
            "FreiAtlas.Game");
    }

    private static void AssertNoForbiddenTokens(
        string directory,
        params string[] forbiddenTokens)
    {
        AssertFilesDoNotContain(
            Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                               && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)),
            forbiddenTokens);
    }

    private static void AssertFilesDoNotContain(
        IEnumerable<string> files,
        params string[] forbiddenTokens)
    {
        var violations = files
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path =>
            {
                var source = File.ReadAllText(path);
                return forbiddenTokens
                    .Where(token => source.Contains(token, StringComparison.Ordinal))
                    .Select(token => $"{Path.GetRelativePath(WorkspaceRoot, path)} contains {token}");
            })
            .ToArray();

        Assert.Empty(violations);
    }

    private static string FindWorkspaceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "FreiAtlas.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate FreiAtlas.slnx.");
    }
}
