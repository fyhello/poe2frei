using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace FreiAtlas.Patcher.Modules.Font;

/// <summary>
/// 内置字体的安装器。
///
/// 游戏只按 family 名向系统要字体（<see cref="FontPatchService"/> 只改 uisettings 的
/// typeface 属性），因此字体必须在系统里可见，补丁才会真正生效。
///
/// 安装到「当前用户」字体目录并写 HKCU 注册表，不需要管理员权限。
/// 字体文件与清单都放在程序目录的 fonts/ 下，换字体只需替换文件与 font.json。
/// </summary>
public static class FontInstaller
{
    /// <summary>内置字体清单。</summary>
    public sealed record BundledFont(
        string Family,
        string DisplayName,
        string FilePath,
        IReadOnlyList<string> Aliases);

    private const string FontsRegistryKey = @"Software\Microsoft\Windows NT\CurrentVersion\Fonts";
    private const int HwndBroadcast = 0xFFFF;
    private const int WmFontChange = 0x001D;
    private const uint SmtoAbortIfHung = 0x0002;

    private static readonly object Gate = new();
    private static BundledFont? _bundled;
    private static bool _loaded;

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int AddFontResourceW(string fileName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    /// <summary>程序目录下的 fonts 文件夹。</summary>
    public static string FontsDirectory => Path.Combine(AppContext.BaseDirectory, "fonts");

    /// <summary>当前用户字体目录。</summary>
    public static string UserFontDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "Windows", "Fonts");

    /// <summary>读取内置字体清单；缺失或格式错误时返回 null，不影响其它功能。</summary>
    public static BundledFont? Bundled
    {
        get
        {
            lock (Gate)
            {
                if (_loaded) return _bundled;
                _loaded = true;

                try
                {
                    string manifestPath = Path.Combine(FontsDirectory, "font.json");
                    if (!File.Exists(manifestPath)) return null;

                    var doc = JsonSerializer.Deserialize<ManifestDocument>(
                        File.ReadAllText(manifestPath),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (doc is null || string.IsNullOrWhiteSpace(doc.Family) || string.IsNullOrWhiteSpace(doc.File))
                    {
                        return null;
                    }

                    string fontPath = Path.Combine(FontsDirectory, doc.File);
                    if (!File.Exists(fontPath)) return null;

                    var aliases = doc.Aliases is { Count: > 0 } ? doc.Aliases : [doc.Family, doc.DisplayName];
                    _bundled = new BundledFont(
                        doc.Family,
                        string.IsNullOrWhiteSpace(doc.DisplayName) ? doc.Family : doc.DisplayName,
                        fontPath,
                        aliases.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
                    return _bundled;
                }
                catch
                {
                    // 清单损坏不应让字体补丁以外的功能不可用；调用方会拿到 null 并给出明确提示。
                    return null;
                }
            }
        }
    }

    /// <summary>判断某个 typeface 名是否指向内置字体。</summary>
    public static bool IsBundledFamily(string? family)
    {
        var bundled = Bundled;
        if (bundled is null || string.IsNullOrWhiteSpace(family)) return false;
        return bundled.Aliases.Contains(family, StringComparer.OrdinalIgnoreCase)
               || string.Equals(bundled.Family, family, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>内置字体是否已经可以从系统里取到。</summary>
    public static bool IsInstalled()
    {
        var bundled = Bundled;
        if (bundled is null) return false;

        return FontPatchService.GetInstalledFonts()
            .Any(f => bundled.Aliases.Contains(f.TypefaceName, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 确保内置字体可用。已在系统里则直接返回成功；否则复制到当前用户字体目录并注册。
    /// 任何一步失败都返回具体原因，由调用方中止字体补丁，不做静默回退。
    /// </summary>
    public static bool EnsureInstalled(out string message)
    {
        var bundled = Bundled;
        if (bundled is null)
        {
            message = $"未找到内置字体（{FontsDirectory} 下的 font.json 或字体文件缺失）。";
            return false;
        }

        if (IsInstalled())
        {
            message = $"已检测到字体：{bundled.DisplayName}";
            return true;
        }

        try
        {
            Directory.CreateDirectory(UserFontDirectory);
            string targetPath = Path.Combine(UserFontDirectory, Path.GetFileName(bundled.FilePath));

            // 同名文件可能已存在但未注册（例如上次安装被中断），覆盖后重新注册。
            File.Copy(bundled.FilePath, targetPath, overwrite: true);

            using (var key = Registry.CurrentUser.CreateSubKey(FontsRegistryKey, writable: true))
            {
                if (key is null)
                {
                    message = "无法写入当前用户的字体注册表项。";
                    return false;
                }

                key.SetValue($"{bundled.DisplayName} (TrueType)", targetPath, RegistryValueKind.String);
            }

            int added = AddFontResourceW(targetPath);
            SendMessageTimeoutW(
                new IntPtr(HwndBroadcast), WmFontChange, IntPtr.Zero, IntPtr.Zero,
                SmtoAbortIfHung, 1000, out _);

            // 让后续的字体列表重新枚举，否则缓存里仍然没有刚装的字体。
            FontPatchService.InvalidateInstalledFontsCache();

            if (added == 0 && !IsInstalled())
            {
                message = $"字体文件已复制到 {targetPath}，但系统未能加载它。请重启后再试。";
                return false;
            }

            message = $"已安装字体：{bundled.DisplayName}（{targetPath}）";
            return true;
        }
        catch (Exception ex)
        {
            message = $"安装内置字体失败：{ex.Message}";
            return false;
        }
    }

    private sealed class ManifestDocument
    {
        public string Family { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string File { get; set; } = string.Empty;
        public List<string>? Aliases { get; set; }
    }
}
