using System.Text;
using System.Text.RegularExpressions;

namespace FreiAtlas.Patcher;

public sealed class UiColorEntry
{
    public string ColourKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public byte[] OriginalColor { get; set; } = [255, 255, 255];
    public byte[] TargetColor { get; set; } = [255, 255, 255];
    public int? ArgbAlpha { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 获取十进制格式的 PoE2 颜色定义字符串，如 "200,92,112" 或 "255,161,23,21"
    /// </summary>
    public string TargetValueString
    {
        get
        {
            if (ArgbAlpha.HasValue)
            {
                return $"{ArgbAlpha.Value},{TargetColor[0]},{TargetColor[1]},{TargetColor[2]}";
            }
            return $"{TargetColor[0]},{TargetColor[1]},{TargetColor[2]}";
        }
    }

    /// <summary>
    /// 获取十六进制颜色字符串（方便前端拾色器呈现，如 "#c85c70"）
    /// </summary>
    public string HexColor
    {
        get => $"#{TargetColor[0]:x2}{TargetColor[1]:x2}{TargetColor[2]:x2}";
        set => SetFromColorString(value);
    }

    public void SetFromColorString(string str)
    {
        var (rgb, alpha) = ParseColorString(str);
        TargetColor = rgb;
        if (alpha.HasValue) ArgbAlpha = alpha;
    }

    public static (byte[] Rgb, int? Alpha) ParseColorString(string str)
    {
        if (string.IsNullOrWhiteSpace(str)) return ([255, 255, 255], null);

        // 处理逗号分隔形式：r,g,b 或 a,r,g,b
        if (str.Contains(','))
        {
            var parts = str.Split(',').Select(p => byte.TryParse(p.Trim(), out var b) ? b : (byte)255).ToArray();
            if (parts.Length == 3) return ([parts[0], parts[1], parts[2]], null);
            if (parts.Length >= 4) return ([parts[1], parts[2], parts[3]], parts[0]);
        }

        // 处理十六进制形式：#RRGGBB 或 #RRGGBBAA 或 RRGGBB
        var hex = str.TrimStart('#').Trim();
        if (hex.Length >= 6)
        {
            try
            {
                byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                int? a = null;
                if (hex.Length >= 8)
                {
                    byte alphaByte = Convert.ToByte(hex.Substring(6, 2), 16);
                    if (alphaByte != 255) a = alphaByte;
                }
                return ([r, g, b], a);
            }
            catch
            {
                return ([255, 255, 255], null);
            }
        }

        return ([255, 255, 255], null);
    }
}

public sealed class UiColorService
{
    private const string TargetPath = "metadata/ui/uisettings.xml";
    private readonly PatchEngine _engine;

    /// <summary>
    /// 1:1 对齐参考软件 Poe2PriceGui.Services.Smoother.UiColorPresets
    /// </summary>
    public static readonly UiColorEntry[] BuiltinEntries =
    [
        new()
        {
            ColourKey = "fractured",
            DisplayName = "分裂词缀（破碎词缀）",
            OriginalColor = [162, 145, 98],
            TargetColor = [200, 92, 112],
            ArgbAlpha = null,
            Enabled = true
        },
        new()
        {
            ColourKey = "craftaffectremove",
            DisplayName = "被移除词缀",
            OriginalColor = [254, 153, 0],
            TargetColor = [161, 23, 21],
            ArgbAlpha = 255,
            Enabled = true
        },
        new()
        {
            ColourKey = "craftaffectreroll",
            DisplayName = "重ROLL词缀",
            OriginalColor = [254, 153, 0],
            TargetColor = [161, 23, 21],
            ArgbAlpha = 255,
            Enabled = true
        },
        new()
        {
            ColourKey = "craftaffectwarning",
            DisplayName = "可能被移除词缀",
            OriginalColor = [235, 200, 80],
            TargetColor = [161, 23, 21],
            ArgbAlpha = 200,
            Enabled = true
        },
        new()
        {
            ColourKey = "harvestmonstertier3",
            DisplayName = "庄园怪物 T3",
            OriginalColor = [184, 218, 242],
            TargetColor = [231, 179, 37],
            ArgbAlpha = 255,
            Enabled = true
        },
        new()
        {
            ColourKey = "item_normal",
            DisplayName = "普通物品",
            OriginalColor = [200, 200, 200],
            TargetColor = [200, 200, 200],
            ArgbAlpha = null,
            Enabled = false
        },
        new()
        {
            ColourKey = "item_magic",
            DisplayName = "魔法物品",
            OriginalColor = [136, 136, 255],
            TargetColor = [84, 138, 255],
            ArgbAlpha = null,
            Enabled = false
        },
        new()
        {
            ColourKey = "item_rare",
            DisplayName = "稀有物品",
            OriginalColor = [255, 255, 119],
            TargetColor = [255, 215, 0],
            ArgbAlpha = null,
            Enabled = false
        },
        new()
        {
            ColourKey = "item_unique",
            DisplayName = "传奇物品",
            OriginalColor = [175, 96, 37],
            TargetColor = [255, 102, 0],
            ArgbAlpha = null,
            Enabled = false
        },
        new()
        {
            ColourKey = "currency",
            DisplayName = "通货物品",
            OriginalColor = [170, 158, 130],
            TargetColor = [224, 179, 60],
            ArgbAlpha = null,
            Enabled = false
        },
        new()
        {
            ColourKey = "gem",
            DisplayName = "技能宝石",
            OriginalColor = [27, 162, 155],
            TargetColor = [26, 209, 181],
            ArgbAlpha = null,
            Enabled = false
        }
    ];

    public UiColorService(PatchEngine engine)
    {
        _engine = engine;
    }

    public bool ApplyColors(
        GameModeInfo info,
        IEnumerable<UiColorEntry> entries,
        Action<string>? progress = null)
    {
        progress?.Invoke("正在读取游戏 uisettings.xml...");
        var bytes = _engine.ReadGameFile(info, TargetPath, out var actualPath);
        if (bytes == null || bytes.Length == 0)
        {
            progress?.Invoke($"未能在游戏文件中找到 {TargetPath}");
            return false;
        }

        var targets = new Dictionary<string, (byte[] Rgb, int? Alpha)>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.Enabled && !string.IsNullOrEmpty(entry.ColourKey))
            {
                targets[entry.ColourKey] = (entry.TargetColor, entry.ArgbAlpha);
            }
        }

        if (targets.Count == 0)
        {
            progress?.Invoke("未启用任何 UI 颜色条目。");
            return false;
        }

        progress?.Invoke("正在修改颜色定义...");
        var modified = ProcessUiColors(bytes, targets);
        if (modified == null)
        {
            progress?.Invoke("未找到可修改的 Colour 定义（文件可能已被修改或路径不正确）。");
            return false;
        }

        progress?.Invoke("正在写入游戏文件（替换原始文件记录）...");
        var success = _engine.WriteGameFiles(info, [(actualPath, modified)]);
        progress?.Invoke(success ? "UI 颜色补丁应用成功！" : "写入失败：未替换任何文件。");
        return success;
    }

    public bool RestoreColors(
        GameModeInfo info,
        Action<string>? progress = null)
    {
        progress?.Invoke("正在读取游戏 uisettings.xml...");
        var bytes = _engine.ReadGameFile(info, TargetPath, out var actualPath);
        if (bytes == null || bytes.Length == 0)
        {
            progress?.Invoke($"未能在游戏文件中找到 {TargetPath}");
            return false;
        }

        progress?.Invoke("正在把颜色改回原版默认值...");
        var targets = new Dictionary<string, (byte[] Rgb, int? Alpha)>(StringComparer.Ordinal);
        foreach (var entry in BuiltinEntries)
        {
            targets[entry.ColourKey] = (entry.OriginalColor, null);
        }

        var modified = ProcessUiColors(bytes, targets);
        if (modified == null)
        {
            progress?.Invoke("未找到需要还原的 Colour 定义（颜色可能已是默认值）。");
            return false;
        }

        progress?.Invoke("正在写回游戏文件...");
        var success = _engine.WriteGameFiles(info, [(actualPath, modified)]);
        progress?.Invoke(success ? "UI 颜色已成功还原为默认值！" : "写入失败：未替换任何文件。");
        return success;
    }

    /// <summary>
    /// 1:1 对齐参考软件 Poe2PriceGui.Services.Smoother.UiColorService.ProcessUiColors
    /// </summary>
    public static byte[]? ProcessUiColors(byte[] bytes, Dictionary<string, (byte[] Rgb, int? Alpha)> targets)
    {
        string[] array = Encoding.Unicode.GetString(bytes).Split(["\r\n", "\n"], StringSplitOptions.None);
        bool inMarkupColours = false;
        bool modified = false;

        for (int i = 0; i < array.Length; i++)
        {
            string text = array[i];
            string trimmed = text.TrimStart('\t', ' ');

            if (trimmed.StartsWith("<Props id=\"MarkupColours\""))
            {
                inMarkupColours = true;
            }
            else if (inMarkupColours && trimmed.StartsWith("</Props>"))
            {
                inMarkupColours = false;
            }
            else
            {
                if (!inMarkupColours)
                {
                    continue;
                }

                Match match = Regex.Match(text, "<Colour\\s+id=\"([^\"]+)\"\\s+value=\"[^\"]*\"");
                if (!match.Success)
                {
                    continue;
                }

                string key = match.Groups[1].Value;
                if (targets.TryGetValue(key, out var val))
                {
                    string colorString = val.Alpha.HasValue
                        ? $"{val.Alpha.Value},{val.Rgb[0]},{val.Rgb[1]},{val.Rgb[2]}"
                        : $"{val.Rgb[0]},{val.Rgb[1]},{val.Rgb[2]}";

                    string replaced = Regex.Replace(text, "value=\"[^\"]*\"", $"value=\"{colorString}\"");
                    if (replaced != text)
                    {
                        array[i] = replaced;
                        modified = true;
                    }
                }
            }
        }

        if (!modified)
        {
            return null;
        }

        return EncodeUtf16Bom(string.Join("\r\n", array));
    }

    public static byte[] EncodeUtf16Bom(string text)
    {
        byte[] bom = [0xFF, 0xFE];
        byte[] bytes = Encoding.Unicode.GetBytes(text);
        byte[] result = new byte[bom.Length + bytes.Length];
        Buffer.BlockCopy(bom, 0, result, 0, bom.Length);
        Buffer.BlockCopy(bytes, 0, result, bom.Length, bytes.Length);
        return result;
    }
}
