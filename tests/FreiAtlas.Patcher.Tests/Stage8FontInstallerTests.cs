using System.Text.Json;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 内置字体装配的回归测试。
///
/// 字体补丁只改 uisettings 的 typeface 属性，游戏按 family 名向系统要字体，
/// 所以字体必须先装到系统里。这里锁定三件事：清单存在且可解析、默认值走内置字体、
/// 应用前确实调用了安装。
/// </summary>
public sealed class Stage8FontInstallerTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(params string[] parts)
    {
        string path = Path.Combine([RepoRoot(), .. parts]);
        Assert.True(File.Exists(path), $"未找到文件：{path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void 字体清单与字体文件都存在()
    {
        string root = RepoRoot();
        string manifest = Path.Combine(root, "fonts", "font.json");
        Assert.True(File.Exists(manifest), "缺少 fonts/font.json");

        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
        var family = doc.RootElement.GetProperty("family").GetString();
        var file = doc.RootElement.GetProperty("file").GetString();

        Assert.False(string.IsNullOrWhiteSpace(family));
        Assert.False(string.IsNullOrWhiteSpace(file));
        Assert.True(File.Exists(Path.Combine(root, "fonts", file!)), $"缺少字体文件 fonts/{file}");
    }

    [Fact]
    public void 默认字体为空表示使用内置字体()
    {
        string settings = Read("src", "FreiAtlas.Core", "Settings", "ClientPatchSettings.cs");
        Assert.Contains("string FontFamily = \"\",", settings);
        Assert.Contains("FontFamily: \"\",", settings);
    }

    [Fact]
    public void 应用字体前会确保内置字体已安装()
    {
        string mainWindow = Read("src", "FreiAtlas.Settings", "MainWindow.xaml.cs");
        Assert.Contains("FontInstaller.EnsureInstalled", mainWindow);
        Assert.Contains("resolvedFontFamily", mainWindow);
    }
}
