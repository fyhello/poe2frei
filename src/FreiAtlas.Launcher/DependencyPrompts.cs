namespace FreiAtlas.Launcher;

internal sealed record DependencyPrompt(
    string Title,
    string Message,
    string? DownloadUrl);

internal static class DependencyPrompts
{
    internal static readonly DependencyPrompt DesktopRuntime = new(
        "FreiAtlas - 缺少运行环境",
        "未检测到 .NET 10 Desktop Runtime x64。\n\n"
        + "这是 FreiAtlas 图形界面需要的微软运行环境。\n"
        + "是否立即打开微软官方下载地址？",
        "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe");

    internal static readonly DependencyPrompt WebView2Runtime = new(
        "FreiAtlas - 缺少运行环境",
        "未检测到 Microsoft Edge WebView2 Runtime。\n\n"
        + "这是 FreiAtlas 设置界面需要的微软组件。\n"
        + "是否立即打开微软官方下载地址？",
        "https://go.microsoft.com/fwlink/p/?LinkId=2124703");

    internal static readonly DependencyPrompt PackageIncomplete = new(
        "FreiAtlas - 启动失败",
        "安装包不完整，缺少启动所需文件。\n\n"
        + "请重新解压完整发行包后再运行 FreiAtlas.exe。",
        null);

    internal static readonly DependencyPrompt ProbeFailure = new(
        "FreiAtlas - 启动失败",
        "运行环境检查失败，无法确认所需组件是否可用。\n\n"
        + "请重新启动软件；如果问题持续，请将错误信息提供给开发者。",
        null);
}
