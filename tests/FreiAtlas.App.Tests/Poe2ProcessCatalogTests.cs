using FreiAtlas.App.Processes;
using FreiAtlas.App.Runtime;
using FreiAtlas.Host.Processes;
using System.Xml.Linq;

namespace FreiAtlas.App.Tests;

public sealed class Poe2ProcessCatalogTests
{
    [Fact]
    public async Task ReadAsync_MapsStandardizedHostCandidatesWithoutReadingMemory()
    {
        var reader = new FakeCandidateReader(
        [
            new Poe2ProcessCandidate(11, "Ranger", Poe2ProcessCandidateState.InGame),
            new Poe2ProcessCandidate(22, null, Poe2ProcessCandidateState.CharacterNotLoaded),
            new Poe2ProcessCandidate(33, null, Poe2ProcessCandidateState.ReadFailed)
        ]);
        var catalog = new Poe2ProcessCatalog(reader);

        var snapshots = await catalog.ReadAsync();

        Assert.Equal(3, snapshots.Count);
        Assert.Equal(new GameProcessSnapshot(11, "Ranger", GameProcessState.InGame, null), snapshots[0]);
        Assert.Equal(
            new GameProcessSnapshot(22, null, GameProcessState.CharacterNotLoaded, "角色未载入"),
            snapshots[1]);
        Assert.Equal(
            new GameProcessSnapshot(33, null, GameProcessState.ReadFailed, "读取失败"),
            snapshots[2]);
        Assert.Equal(1, reader.ReadCount);
    }

    [Fact]
    public void AppSourcesAndProject_DoNotOwnProcessMemoryReading()
    {
        var workspace = FindWorkspaceRoot();
        var appRoot = Path.Combine(workspace, "src", "FreiAtlas.App");
        var source = string.Join('\n', Directory.EnumerateFiles(
                appRoot,
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText));

        Assert.DoesNotContain("ProcessAttachment", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GameProcessIdentityReader", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IProcessMemory", source, StringComparison.Ordinal);

        var project = File.ReadAllText(Path.Combine(appRoot, "FreiAtlas.App.csproj"));
        Assert.DoesNotContain("FreiAtlas.Atlas.csproj", project, StringComparison.Ordinal);
        Assert.DoesNotContain("FreiAtlas.Platform.Windows.csproj", project, StringComparison.Ordinal);
    }

    [Fact]
    public void InProcessHostDependency_UsesRuntimeLibraryInsteadOfCliApplication()
    {
        var workspace = FindWorkspaceRoot();
        var appProject = XDocument.Load(Path.Combine(
            workspace,
            "src",
            "FreiAtlas.App",
            "FreiAtlas.App.csproj"));
        var appReferences = appProject.Descendants("ProjectReference")
            .Select(element => Path.GetFileNameWithoutExtension(
                element.Attribute("Include")?.Value))
            .ToArray();

        Assert.Contains("FreiAtlas.Host.Runtime", appReferences);
        Assert.DoesNotContain("FreiAtlas.Host", appReferences);

        var cliProject = XDocument.Load(Path.Combine(
            workspace,
            "src",
            "FreiAtlas.Host",
            "FreiAtlas.Host.csproj"));
        var cliReferences = cliProject.Descendants("ProjectReference")
            .Select(element => Path.GetFileNameWithoutExtension(
                element.Attribute("Include")?.Value));

        Assert.Contains("FreiAtlas.Host.Runtime", cliReferences);
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

    private sealed class FakeCandidateReader(IReadOnlyList<Poe2ProcessCandidate> candidates)
        : IPoe2ProcessCandidateReader
    {
        public int ReadCount { get; private set; }

        public Task<IReadOnlyList<Poe2ProcessCandidate>> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult(candidates);
        }
    }
}
