using System.Runtime.InteropServices;

namespace FreiAtlas.Launcher;

internal static partial class NativeMethods
{
    [LibraryImport(
        "user32.dll",
        EntryPoint = "MessageBoxW",
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int MessageBox(
        nint windowHandle,
        string text,
        string caption,
        uint type);

    [LibraryImport(
        "WebView2Loader.dll",
        EntryPoint = "GetAvailableCoreWebView2BrowserVersionString",
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetAvailableCoreWebView2BrowserVersionString(
        string? browserExecutableFolder,
        out nint versionInfo);
}
