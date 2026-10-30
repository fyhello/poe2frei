using System.Text.Json;
using FreiAtlas.Patcher.Modules.ColorMod;
using FreiAtlas.Patcher.MtxPatch;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// P4 数据迁移回归：内嵌资源必须与迁移前的黄金快照逐条一致。
///
/// 黄金快照由迁移前已构建的程序集反射导出（scripts/export-p4-golden.ps1），
/// 用来锁定颜色表、样式表、精确翻译表与短语表的数量、内容和顺序。
/// </summary>
public sealed class P4ResourceMigrationTests
{
    private static readonly JsonSerializerOptions GoldenJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static T[] LoadGolden<T>(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", fileName);
        Assert.True(File.Exists(path), $"未找到黄金快照：{path}");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T[]>(json, GoldenJsonOptions)
            ?? throw new InvalidOperationException($"黄金快照为空：{fileName}");
    }

    [Fact]
    public void 两套数据资源均已嵌入()
    {
        var names = typeof(MtxTranslationDictionary).Assembly.GetManifestResourceNames();
        Assert.Contains("FreiAtlas.Patcher.Resources.colormod.defaults.json.gz", names);
        Assert.Contains("FreiAtlas.Patcher.Resources.mtx.translations.json.gz", names);
    }

    [Fact]
    public void 颜色表_数量与内容与黄金快照一致()
    {
        var golden = LoadGolden<ColorGolden>("colormod.colors.golden.json");
        Assert.Equal(481, golden.Length);
        Assert.Equal(481, ColorModDefaults.DefaultColorMap.Count);

        foreach (var entry in golden)
        {
            Assert.True(
                ColorModDefaults.DefaultColorMap.TryGetValue(entry.Id, out var actual),
                $"缺少颜色条目：{entry.Id}");
            Assert.Equal(entry.Rgb, actual!.Select(value => (int)value).ToArray());
        }
    }

    [Fact]
    public void 样式表_数量与内容与黄金快照一致()
    {
        var golden = LoadGolden<StyleGolden>("colormod.styles.golden.json");
        Assert.Equal(8, golden.Length);
        Assert.Equal(8, ColorModDefaults.DefaultStyleMap.Count);

        foreach (var entry in golden)
        {
            Assert.True(
                ColorModDefaults.DefaultStyleMap.TryGetValue(entry.Id, out var actual),
                $"缺少样式条目：{entry.Id}");
            Assert.Equal(entry.Style, actual.ToString());
        }
    }

    [Fact]
    public void 精确翻译表_数量与内容与黄金快照一致()
    {
        var golden = LoadGolden<ExactGolden>("mtx.exact.golden.json");
        Assert.Equal(20, golden.Length);
        Assert.Equal(20, MtxTranslationDictionary.ExactCount);

        var goldenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in golden)
        {
            Assert.True(goldenKeys.Add(entry.En), $"黄金快照存在重复键：{entry.En}");
            Assert.True(
                MtxTranslationDictionary.ExactEntries.TryGetValue(entry.En, out var actual),
                $"缺少精确翻译：{entry.En}");
            Assert.Equal(entry.Zh, actual);
        }

        foreach (var key in MtxTranslationDictionary.ExactEntries.Keys)
        {
            Assert.Contains(key, goldenKeys);
        }
    }

    [Fact]
    public void 短语表_数量顺序与内容与黄金快照一致()
    {
        var golden = LoadGolden<PhraseGolden>("mtx.phrases.golden.json");
        Assert.Equal(874, golden.Length);
        Assert.Equal(874, MtxTranslationDictionary.PhraseCount);

        var actual = MtxTranslationDictionary.PhraseEntries;
        for (int i = 0; i < golden.Length; i++)
        {
            Assert.Equal(i, golden[i].Index);
            Assert.Equal(golden[i].Pattern, actual[i].Pattern);
            Assert.Equal(golden[i].Zh, actual[i].Replacement);
        }
    }

    [Fact]
    public void 翻译结果仍走资源数据()
    {
        Assert.Equal(
            "永恒 鼠标指针",
            MtxTranslationDictionary.Translate("EternalCursor", MtxCategory.Misc));
        Assert.Contains(
            "微光林地巫医",
            MtxTranslationDictionary.Translate("Glimmerwood Witchdoctor", MtxCategory.Misc));
    }

    private sealed class ColorGolden
    {
        public string Id { get; set; } = string.Empty;

        public int[] Rgb { get; set; } = [];
    }

    private sealed class StyleGolden
    {
        public string Id { get; set; } = string.Empty;

        public string Style { get; set; } = string.Empty;
    }

    private sealed class ExactGolden
    {
        public string En { get; set; } = string.Empty;

        public string Zh { get; set; } = string.Empty;
    }

    private sealed class PhraseGolden
    {
        public int Index { get; set; }

        public string Pattern { get; set; } = string.Empty;

        public string Zh { get; set; } = string.Empty;
    }
}
