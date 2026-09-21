namespace FreiAtlas.Launcher.Tests;

public sealed class LauncherDecisionTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Decide_MissingPackageFile_ShowsPackageIncomplete(
        bool managedApplicationPresent,
        bool webView2LoaderPresent)
    {
        var action = LauncherDecision.Decide(
            managedApplicationPresent,
            webView2LoaderPresent,
            RuntimeProbeStatus.Available,
            WebView2ProbeStatus.Available);

        Assert.Equal(LauncherAction.ShowPackageIncomplete, action);
    }

    [Fact]
    public void Decide_MissingDesktopRuntime_ShowsDesktopRuntimePrompt()
    {
        var action = LauncherDecision.Decide(
            managedApplicationPresent: true,
            webView2LoaderPresent: true,
            RuntimeProbeStatus.MissingDesktopRuntime,
            WebView2ProbeStatus.Available);

        Assert.Equal(LauncherAction.ShowDesktopRuntimePrompt, action);
    }

    [Fact]
    public void Decide_RuntimeProbeFailure_ShowsProbeFailure()
    {
        var action = LauncherDecision.Decide(
            managedApplicationPresent: true,
            webView2LoaderPresent: true,
            RuntimeProbeStatus.Failed,
            WebView2ProbeStatus.Available);

        Assert.Equal(LauncherAction.ShowProbeFailure, action);
    }

    [Fact]
    public void Decide_MissingWebView2_ShowsWebView2Prompt()
    {
        var action = LauncherDecision.Decide(
            managedApplicationPresent: true,
            webView2LoaderPresent: true,
            RuntimeProbeStatus.Available,
            WebView2ProbeStatus.Missing);

        Assert.Equal(LauncherAction.ShowWebView2Prompt, action);
    }

    [Fact]
    public void Decide_WebView2ProbeFailure_ShowsProbeFailure()
    {
        var action = LauncherDecision.Decide(
            managedApplicationPresent: true,
            webView2LoaderPresent: true,
            RuntimeProbeStatus.Available,
            WebView2ProbeStatus.Failed);

        Assert.Equal(LauncherAction.ShowProbeFailure, action);
    }

    [Fact]
    public void Decide_AllDependenciesAvailable_LaunchesManagedApplication()
    {
        var action = LauncherDecision.Decide(
            managedApplicationPresent: true,
            webView2LoaderPresent: true,
            RuntimeProbeStatus.Available,
            WebView2ProbeStatus.Available);

        Assert.Equal(LauncherAction.LaunchManagedApplication, action);
    }
}
