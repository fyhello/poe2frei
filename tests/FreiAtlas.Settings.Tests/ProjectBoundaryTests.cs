using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

namespace FreiAtlas.Settings.Tests;

public sealed class ProjectBoundaryTests
{
    [Fact]
    public void SettingsProject_DirectlyReferencesOnlyAppProject()
    {
        var projectPath = Path.Combine(
            TestPaths.WorkspaceRoot,
            "src", "FreiAtlas.Settings", "FreiAtlas.Settings.csproj");
        var document = XDocument.Load(projectPath);
        var references = document
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => value is not null)
            .ToArray();

        var reference = Assert.Single(references);
        Assert.EndsWith(
            "FreiAtlas.App\\FreiAtlas.App.csproj",
            reference,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SettingsExecutable_DeclaresPerMonitorV2BeforeCreatingWindows()
    {
        var projectPath = Path.Combine(
            TestPaths.WorkspaceRoot,
            "src", "FreiAtlas.Settings", "FreiAtlas.Settings.csproj");
        var document = XDocument.Load(projectPath);

        Assert.Equal(
            "app.manifest",
            document.Descendants("ApplicationManifest").Single().Value);

        var manifest = File.ReadAllText(Path.Combine(
            TestPaths.WorkspaceRoot,
            "src", "FreiAtlas.Settings", "app.manifest"));
        Assert.Contains("PerMonitorV2", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void LiteRuntimeConfigPreparation_PrioritizesWindowsDesktopFramework()
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            $"freiatlas-runtimeconfig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var runtimeConfigPath = Path.Combine(tempDirectory, "FreiAtlas.Settings.runtimeconfig.json");
            File.WriteAllText(
                runtimeConfigPath,
                """
                {
                  "runtimeOptions": {
                    "tfm": "net10.0",
                    "frameworks": [
                      { "name": "Microsoft.NETCore.App", "version": "10.0.0" },
                      { "name": "Microsoft.WindowsDesktop.App", "version": "10.0.0" }
                    ],
                    "configProperties": {
                      "example": true
                    }
                  }
                }
                """);

            var scriptPath = Path.Combine(
                TestPaths.WorkspaceRoot,
                "scripts", "prepare-lite-runtimeconfig.ps1");
            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-Path");
            startInfo.ArgumentList.Add(runtimeConfigPath);

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(
                process.ExitCode == 0,
                $"Runtime config preparation failed.{Environment.NewLine}{standardOutput}{standardError}");

            using var document = JsonDocument.Parse(File.ReadAllText(runtimeConfigPath));
            var frameworks = document.RootElement
                .GetProperty("runtimeOptions")
                .GetProperty("frameworks")
                .EnumerateArray()
                .Select(element => element.GetProperty("name").GetString())
                .ToArray();
            Assert.Equal("Microsoft.WindowsDesktop.App", frameworks[0]);
            Assert.Equal("Microsoft.NETCore.App", frameworks[1]);
            Assert.True(document.RootElement
                .GetProperty("runtimeOptions")
                .GetProperty("configProperties")
                .GetProperty("example")
                .GetBoolean());
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("..\\..\\THIRD-PARTY-NOTICES.md", "THIRD-PARTY-NOTICES.md")]
    [InlineData("..\\..\\licenses\\POE2Radar-MIT.txt", "licenses\\POE2Radar-MIT.txt")]
    public void SettingsProject_PublishesRequiredThirdPartyNotices(
        string include,
        string link)
    {
        var projectPath = Path.Combine(
            TestPaths.WorkspaceRoot,
            "src", "FreiAtlas.Settings", "FreiAtlas.Settings.csproj");
        var document = XDocument.Load(projectPath);
        var content = Assert.Single(
            document.Descendants("Content"),
            element => string.Equals(
                element.Attribute("Include")?.Value,
                include,
                StringComparison.OrdinalIgnoreCase));

        Assert.Equal(link, content.Element("Link")?.Value);
        Assert.Equal("PreserveNewest", content.Element("CopyToOutputDirectory")?.Value);
        Assert.Equal("PreserveNewest", content.Element("CopyToPublishDirectory")?.Value);
    }
}
