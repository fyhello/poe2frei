using FreiAtlas.Patcher.Modules.MapBeam;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 光柱补丁的副本路径生成与写入放行。
///
/// 光柱的工作方式：把原始 .ao 复制到新路径 EF_SceneryChild_*.ao，
/// 再把原路径改写成引用该副本的 wrapper。副本路径在当前客户端中并
/// 不存在，属于本模块新建。
///
/// 早期 PatchEngine 的「只写已存在路径」保护会把副本静默丢弃，而 wrapper
/// 已经写进去并引用它 —— 结果是地图钉模型引用不存在的资源，模型不显示，
/// 但程序仍报「成功应用 N 个光柱」。实测客户端中 EF_SceneryChild_* 数量为 0，
/// 证明生成的路径必然不存在。
/// </summary>
public sealed class MapBeamCopyPathTests
{
    /// <summary>
    /// 副本路径必须稳定且互不相同：同一输入得到同一路径，
    /// 不同输入得到不同路径，否则多个地图会互相覆盖副本。
    /// </summary>
    [Fact]
    public void 副本路径对同一输入稳定且彼此不同()
    {
        const string pathA = "Metadata/Terrain/WorldMaps/Maps/MapA/beacon.ao";
        const string pathB = "Metadata/Terrain/WorldMaps/Maps/MapB/beacon.ao";

        string copyA1 = MapBeamPatchService.GetCopyFilePath(pathA);
        string copyA2 = MapBeamPatchService.GetCopyFilePath(pathA);
        string copyB = MapBeamPatchService.GetCopyFilePath(pathB);

        Assert.Equal(copyA1, copyA2);
        Assert.NotEqual(copyA1, copyB);
    }

    /// <summary>
    /// 副本路径必须落在 Pins 目录下并保留 .ao 扩展名，
    /// 否则客户端的资源解析器找不到它。
    /// </summary>
    [Fact]
    public void 副本路径落在Pins目录且保留扩展名()
    {
        const string original = "Metadata/Terrain/WorldMaps/Maps/MapA/beacon.ao";
        string copy = MapBeamPatchService.GetCopyFilePath(original);

        Assert.Contains("Doodads/Pins/", copy);
        Assert.EndsWith(".ao", copy);
        Assert.Contains("EF_SceneryChild_", copy);
    }

    /// <summary>
    /// 生成的路径必须带 EF_SceneryChild_ 标记 ——
    /// MapBeamPatchService 正是靠这个标记筛出「本模块新建的路径」，
    /// 交给 PatchEngine 放行。
    /// </summary>
    [Fact]
    public void 副本路径带可识别标记()
    {
        string copy = MapBeamPatchService.GetCopyFilePath("x.ao");
        Assert.Contains("EF_SceneryChild_", copy, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 哈希输出必须是 8 位十六进制，避免路径过长或含非法字符。
    /// </summary>
    [Fact]
    public void 哈希输出为8位十六进制()
    {
        string hex = MapBeamPatchService.Fnv1a32Hex8("any input");
        Assert.Equal(8, hex.Length);
        Assert.All(hex, c => Assert.True(Uri.IsHexDigit(c)));
    }

    /// <summary>
    /// 写入时必须把副本路径列入「允许新建」，否则会被过滤掉。
    ///
    /// 这条是 P1-3 的核心：PatchEngine 默认只写客户端已存在的路径，
    /// 光柱模块必须显式传入 allowedNewPaths 才能把副本真正写进去。
    /// 这里用源码断言锁定调用形态 —— 一旦有人去掉该参数，
    /// 光柱会重新变成悬空引用且没有任何报错。
    /// </summary>
    [Fact]
    public void 光柱写入必须显式放行新建副本路径()
    {
        string sourcePath = FindSource("MapBeamPatchService.cs");
        string source = File.ReadAllText(sourcePath);

        Assert.Contains("EF_SceneryChild_", source);
        Assert.Contains("allowedNewPaths", source);
        Assert.Contains("WriteGameFiles(info, filesToWrite, allowedNewPaths)", source);
    }

    /// <summary>
    /// PatchEngine 必须支持 allowedNewPaths 参数，并在两处 FilterExisting
    /// 调用中传递它（GGPK 与 Bundles2 两种模式各一处）。
    /// </summary>
    [Fact]
    public void PatchEngine两种模式都支持放行新建路径()
    {
        string sourcePath = FindSource("PatchEngine.cs");
        string source = File.ReadAllText(sourcePath);

        Assert.Contains("IReadOnlySet<string>? allowedNewPaths", source);
        Assert.Equal(2, CountOccurrences(source, "FilterExisting(ggpk.Index, fileList, allowedNewPaths)")
                      + CountOccurrences(source, "FilterExisting(index, fileList, allowedNewPaths)"));
    }

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    /// <summary>从测试输出目录回溯到源码目录找目标文件。</summary>
    private static string FindSource(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "FreiAtlas.Patcher");
            if (Directory.Exists(candidate))
            {
                var found = Directory.GetFiles(candidate, fileName, SearchOption.AllDirectories);
                if (found.Length > 0) return found[0];
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"未找到源文件：{fileName}");
    }
}
