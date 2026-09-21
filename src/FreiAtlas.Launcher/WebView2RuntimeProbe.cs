using System.Runtime.InteropServices;

namespace FreiAtlas.Launcher;

internal sealed record WebView2ProbeResult(
    WebView2ProbeStatus Status,
    string? Version,
    string Details);

internal static class WebView2RuntimeProbe
{
    internal static WebView2ProbeResult Probe()
    {
        nint versionPointer = 0;
        try
        {
            var result = NativeMethods.GetAvailableCoreWebView2BrowserVersionString(
                browserExecutableFolder: null,
                out versionPointer);
            if (result < 0 || versionPointer == 0)
            {
                return new WebView2ProbeResult(
                    WebView2ProbeStatus.Missing,
                    null,
                    $"WebView2 Loader 返回 0x{result:X8}。");
            }

            var version = Marshal.PtrToStringUni(versionPointer);
            return string.IsNullOrWhiteSpace(version)
                ? new WebView2ProbeResult(
                    WebView2ProbeStatus.Missing,
                    null,
                    "WebView2 Loader 未返回运行时版本。")
                : new WebView2ProbeResult(
                    WebView2ProbeStatus.Available,
                    version,
                    string.Empty);
        }
        catch (Exception exception) when (
            exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException)
        {
            return new WebView2ProbeResult(
                WebView2ProbeStatus.Failed,
                null,
                exception.Message);
        }
        finally
        {
            if (versionPointer != 0)
            {
                Marshal.FreeCoTaskMem(versionPointer);
            }
        }
    }
}
