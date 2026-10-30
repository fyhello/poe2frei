using System.IO;
using System.Text.Json;

namespace FreiAtlas.Patcher.Modules.ColorMod;

/// <summary>
/// 官方 Overrides 规则库加载器 (与参考项目 15,902 条规则完全对齐)
/// </summary>
public static class ColorModOverrides
{
    private static ColorModOverrideDoc? _doc;
    private static bool _tried;
    private static HashSet<string>? _allStatIds;

    public static bool IsLoaded
    {
        get
        {
            EnsureLoaded();
            return _doc != null;
        }
    }

    public static int BlockCount
    {
        get
        {
            EnsureLoaded();
            return _doc?.Blocks.Count ?? 0;
        }
    }

    public static string? LoadError { get; private set; }

    public static IReadOnlyCollection<string> AllStatIds
    {
        get
        {
            EnsureLoaded();
            if (_doc == null) return [];
            if (_allStatIds == null)
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var b in _doc.Blocks.Values)
                {
                    foreach (var s in b.Stats)
                    {
                        set.Add(s);
                    }
                }
                _allStatIds = set;
            }
            return _allStatIds;
        }
    }

    public static void EnsureLoaded()
    {
        if (_tried) return;
        _tried = true;

        var list = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "data", "poe2", "colormod_overrides.json"),
            Path.Combine(AppContext.BaseDirectory, "colormod_overrides.json")
        };

        string? foundPath = list.FirstOrDefault(File.Exists);
        if (foundPath == null)
        {
            LoadError = "配置不存在: colormod_overrides.json";
            return;
        }

        try
        {
            var json = File.ReadAllText(foundPath);
            var doc = JsonSerializer.Deserialize<ColorModOverrideDoc>(json);
            if (doc != null && doc.Version >= 1)
            {
                _doc = doc;
            }
            else
            {
                LoadError = "配置版本不受支持";
            }
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
            _doc = null;
        }
    }

    public static byte[]? Rgb(string? tag)
    {
        EnsureLoaded();
        if (tag == null || _doc == null) return null;
        if (!_doc.NamedColors.TryGetValue(tag, out var val) || val.Rgb.Count < 3) return null;
        return [(byte)val.Rgb[0], (byte)val.Rgb[1], (byte)val.Rgb[2]];
    }


    public static ColorModBlockPlan? Find(string csdFileName, IReadOnlyList<string> statIds, int occurrence)
    {
        EnsureLoaded();
        if (_doc == null || statIds.Count == 0) return null;
        string key = csdFileName + "|" + string.Join('|', statIds) + (occurrence > 1 ? ("#" + occurrence) : "");
        if (!_doc.Blocks.TryGetValue(key, out var val)) return null;
        return val;
    }
}
