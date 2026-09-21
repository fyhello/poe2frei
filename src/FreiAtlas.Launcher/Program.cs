using System.Diagnostics;

namespace FreiAtlas.Launcher;

internal static class Program
{
    private const uint MessageBoxOk = 0x00000000;
    private const uint MessageBoxYesNo = 0x00000004;
    private const uint MessageBoxIconError = 0x00000010;
    private const uint MessageBoxIconWarning = 0x00000030;
    private const uint MessageBoxSetForeground = 0x00010000;
    private const uint MessageBoxTopMost = 0x00040000;
    private const int DialogResultYes = 6;

    [STAThread]
    private static int Main()
    {
        var applicationDirectory = Path.Combine(AppContext.BaseDirectory, "app");
        var managedApplicationPath = Path.Combine(
            applicationDirectory,
            "FreiAtlas.Settings.exe");
        var webView2LoaderPath = Path.Combine(
            AppContext.BaseDirectory,
            "WebView2Loader.dll");

        var managedApplicationPresent = File.Exists(managedApplicationPath);
        var webView2LoaderPresent = File.Exists(webView2LoaderPath);
        var packageAction = LauncherDecision.Decide(
            managedApplicationPresent,
            webView2LoaderPresent,
            RuntimeProbeStatus.Available,
            WebView2ProbeStatus.Available);
        if (packageAction == LauncherAction.ShowPackageIncomplete)
        {
            ShowError(DependencyPrompts.PackageIncomplete.Message, details: null);
            return 2;
        }

        var runtimeResult = ManagedRuntimeProbe.Probe(managedApplicationPath);
        var runtimeAction = LauncherDecision.Decide(
            managedApplicationPresent,
            webView2LoaderPresent,
            runtimeResult.Status,
            WebView2ProbeStatus.Available);
        if (runtimeAction == LauncherAction.ShowDesktopRuntimePrompt)
        {
            ShowDownloadPrompt(DependencyPrompts.DesktopRuntime);
            return 3;
        }

        if (runtimeAction == LauncherAction.ShowProbeFailure)
        {
            ShowError(DependencyPrompts.ProbeFailure.Message, runtimeResult.Details);
            return 4;
        }

        var webView2Result = WebView2RuntimeProbe.Probe();
        var finalAction = LauncherDecision.Decide(
            managedApplicationPresent,
            webView2LoaderPresent,
            runtimeResult.Status,
            webView2Result.Status);
        if (finalAction == LauncherAction.ShowWebView2Prompt)
        {
            ShowDownloadPrompt(DependencyPrompts.WebView2Runtime);
            return 5;
        }

        if (finalAction == LauncherAction.ShowProbeFailure)
        {
            ShowError(DependencyPrompts.ProbeFailure.Message, webView2Result.Details);
            return 6;
        }

        try
        {
            Process.Start(new ProcessStartInfo(managedApplicationPath)
            {
                UseShellExecute = true,
                WorkingDirectory = applicationDirectory,
            });
            return 0;
        }
        catch (Exception exception)
        {
            ShowError("FreiAtlas 图形界面启动失败。", exception.Message);
            return 7;
        }
    }

    private static void ShowDownloadPrompt(DependencyPrompt prompt)
    {
        var url = prompt.DownloadUrl
            ?? throw new InvalidOperationException("Dependency prompt download URL is missing.");
        var message = $"{prompt.Message}\n\n下载地址：\n{url}";
        var result = NativeMethods.MessageBox(
            0,
            message,
            prompt.Title,
            MessageBoxYesNo
            | MessageBoxIconWarning
            | MessageBoxSetForeground
            | MessageBoxTopMost);
        if (result != DialogResultYes)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            ShowError(
                $"无法打开系统浏览器，请手动访问：\n{url}",
                exception.Message);
        }
    }

    private static void ShowError(string message, string? details)
    {
        var fullMessage = string.IsNullOrWhiteSpace(details)
            ? message
            : $"{message}\n\n详细信息：\n{details}";
        _ = NativeMethods.MessageBox(
            0,
            fullMessage,
            "FreiAtlas - 启动失败",
            MessageBoxOk
            | MessageBoxIconError
            | MessageBoxSetForeground
            | MessageBoxTopMost);
    }
}
