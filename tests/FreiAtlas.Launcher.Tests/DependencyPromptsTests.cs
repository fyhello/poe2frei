namespace FreiAtlas.Launcher.Tests;

public sealed class DependencyPromptsTests
{
    [Fact]
    public void DesktopRuntimePrompt_IsChineseAndUsesExactX64Download()
    {
        var prompt = DependencyPrompts.DesktopRuntime;

        Assert.Contains("缺少运行环境", prompt.Title, StringComparison.Ordinal);
        Assert.Contains(".NET 10 Desktop Runtime x64", prompt.Message, StringComparison.Ordinal);
        Assert.Contains("图形界面", prompt.Message, StringComparison.Ordinal);
        Assert.Equal(
            "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe",
            prompt.DownloadUrl);
    }

    [Fact]
    public void WebView2Prompt_IsChineseAndUsesOfficialEvergreenBootstrapper()
    {
        var prompt = DependencyPrompts.WebView2Runtime;

        Assert.Contains("缺少运行环境", prompt.Title, StringComparison.Ordinal);
        Assert.Contains("Microsoft Edge WebView2 Runtime", prompt.Message, StringComparison.Ordinal);
        Assert.Contains("设置界面", prompt.Message, StringComparison.Ordinal);
        Assert.Equal(
            "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
            prompt.DownloadUrl);
    }

    [Fact]
    public void NonDependencyFailures_AreChineseAndHaveNoDownload()
    {
        Assert.Contains("安装包不完整", DependencyPrompts.PackageIncomplete.Message);
        Assert.Null(DependencyPrompts.PackageIncomplete.DownloadUrl);
        Assert.Contains("运行环境检查失败", DependencyPrompts.ProbeFailure.Message);
        Assert.Null(DependencyPrompts.ProbeFailure.DownloadUrl);
    }
}
