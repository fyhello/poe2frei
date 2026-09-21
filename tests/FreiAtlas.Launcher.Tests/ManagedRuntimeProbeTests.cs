namespace FreiAtlas.Launcher.Tests;

public sealed class ManagedRuntimeProbeTests
{
    [Fact]
    public void Classify_ZeroExitCode_ReturnsAvailable()
    {
        var result = ManagedRuntimeProbe.Classify(exitCode: 0, standardError: string.Empty);

        Assert.Equal(RuntimeProbeStatus.Available, result.Status);
    }

    [Fact]
    public void Classify_MissingWindowsDesktopFramework_ReturnsMissingDesktopRuntime()
    {
        var result = ManagedRuntimeProbe.Classify(
            exitCode: unchecked((int)0x80008096),
            standardError: "Framework: 'Microsoft.WindowsDesktop.App', version '10.0.0' (x64)");

        Assert.Equal(RuntimeProbeStatus.MissingDesktopRuntime, result.Status);
    }

    [Theory]
    [InlineData("You must install or update .NET to run this application.")]
    [InlineData("The required library hostfxr.dll could not be found.")]
    public void Classify_MissingDotNetHost_ReturnsMissingDesktopRuntime(string standardError)
    {
        var result = ManagedRuntimeProbe.Classify(exitCode: -1, standardError);

        Assert.Equal(RuntimeProbeStatus.MissingDesktopRuntime, result.Status);
    }

    [Fact]
    public void Classify_UnrelatedFailure_ReturnsFailedWithDetails()
    {
        var result = ManagedRuntimeProbe.Classify(
            exitCode: 17,
            standardError: "unexpected startup failure");

        Assert.Equal(RuntimeProbeStatus.Failed, result.Status);
        Assert.Contains("unexpected startup failure", result.Details, StringComparison.Ordinal);
    }
}
