namespace FreiAtlas.Launcher;

internal enum RuntimeProbeStatus
{
    Available,
    MissingDesktopRuntime,
    Failed,
}

internal enum WebView2ProbeStatus
{
    Available,
    Missing,
    Failed,
}

internal enum LauncherAction
{
    LaunchManagedApplication,
    ShowPackageIncomplete,
    ShowDesktopRuntimePrompt,
    ShowWebView2Prompt,
    ShowProbeFailure,
}

internal static class LauncherDecision
{
    internal static LauncherAction Decide(
        bool managedApplicationPresent,
        bool webView2LoaderPresent,
        RuntimeProbeStatus runtimeStatus,
        WebView2ProbeStatus webView2Status)
    {
        if (!managedApplicationPresent || !webView2LoaderPresent)
        {
            return LauncherAction.ShowPackageIncomplete;
        }

        if (runtimeStatus == RuntimeProbeStatus.MissingDesktopRuntime)
        {
            return LauncherAction.ShowDesktopRuntimePrompt;
        }

        if (runtimeStatus == RuntimeProbeStatus.Failed)
        {
            return LauncherAction.ShowProbeFailure;
        }

        return webView2Status switch
        {
            WebView2ProbeStatus.Missing => LauncherAction.ShowWebView2Prompt,
            WebView2ProbeStatus.Failed => LauncherAction.ShowProbeFailure,
            _ => LauncherAction.LaunchManagedApplication,
        };
    }
}
