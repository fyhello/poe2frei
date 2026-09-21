using System.Xml.Linq;

namespace FreiAtlas.Settings.Tests;

public sealed class QuickAssistBoundaryTests
{
    [Fact]
    public void RecoveryModule_ReferencesOnlyPublicCoreContracts()
    {
        var project = XDocument.Load(Path.Combine(TestPaths.WorkspaceRoot, "src", "FreiAtlas.QuickAssist", "FreiAtlas.QuickAssist.csproj"));
        var references = project.Descendants("ProjectReference").Select(item => item.Attribute("Include")!.Value).ToArray();
        Assert.EndsWith("FreiAtlas.Core.csproj", Assert.Single(references));
        foreach (var path in Directory.EnumerateFiles(Path.Combine(TestPaths.WorkspaceRoot, "src", "FreiAtlas.QuickAssist"), "*.cs"))
        {
            var source = File.ReadAllText(path);
            Assert.DoesNotContain("FreiAtlas.Game", source);
            Assert.DoesNotContain("IProcessMemory", source);
            Assert.DoesNotContain("Overlay", source);
        }
    }

    [Theory]
    [InlineData("FreiAtlas.Host", "QuickAssistRuntime.cs")]
    [InlineData("FreiAtlas.App", "Runtime/QuickAssistSession.cs")]
    public void AssistantLifecycle_DoesNotDependOnOverlay(string project, string relativePath)
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.WorkspaceRoot, "src", project, relativePath));
        Assert.DoesNotContain("Overlay", source);
        Assert.Contains("IAsyncDisposable", source);
    }
}
