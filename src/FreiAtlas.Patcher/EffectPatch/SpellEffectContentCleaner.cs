using System.Text;
using System.Text.RegularExpressions;

namespace FreiAtlas.Patcher.EffectPatch;

/// <summary>
/// 按文件类型与规划规则清空或改写特效内容。
///
/// 各种格式的处理方式经真实客户端验证（与易刷最新 110 项技能屏蔽完全对齐）：
///   .ao / .aoc 类 C 语法。按文件精确清空指定块；支持 hide_segments 局部网格分片隐藏。
///   .epk       引用表。写成单个空格 " "（0x20 0x00）。
///   .pet       粒子定义。写成字符 "0"（0x30 0x00）。
///   .trl       轨迹定义。写成字符 "0"（0x30 0x00）。
///   .ot        游戏脚本。用于实体隐藏，注入 client -> Render -> disable_rendering = true。
/// </summary>
public static class SpellEffectContentCleaner
{
    /// <summary>视觉特效块（.ao 内）。</summary>
    public static readonly string[] VisualBlocks =
        ["ParticleEffects", "TrailsEffects", "ScreenShake"];

    /// <summary>音效块（.ao 内）。</summary>
    public static readonly string[] AudioBlocks =
        ["SoundEvents", "SoundParams"];

    /// <summary>可处理的扩展名。</summary>
    public static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".ao", ".aoc", ".epk", ".pet", ".trl", ".ot"
        };

    /// <summary>
    /// 根据规划好的文件动作执行清洗。
    /// </summary>
    public static string? CleanWithPlan(string virtualPath, string content, PlannedFileAction action)
    {
        return action.Kind switch
        {
            PlannedActionKind.ClearBlockAndHideSegments => CleanAoWithSegments(content, action.Blocks, action.HideSegments),
            PlannedActionKind.Zero => "0",
            PlannedActionKind.Empty => " ",
            PlannedActionKind.HideEntity => CleanOtDisableRendering(content),
            _ => null
        };
    }

    /// <summary>
    /// 兼容旧版入口：清空一个文件里的常规特效内容。
    /// </summary>
    public static string? Clean(string virtualPath, string content, bool includeAudio)
    {
        var ext = GetExtension(virtualPath);
        return ext switch
        {
            ".ao" or ".aoc" => CleanAo(content, includeAudio),
            ".epk" => " ",
            ".pet" => "0",
            ".trl" => "0",
            _ => null
        };
    }

    /// <summary>
    /// 清空 .ao 里的特效块（可指定块列表，或使用默认视觉+音效块）。
    /// </summary>
    public static string CleanAo(string content, bool includeAudio)
    {
        var names = new HashSet<string>(VisualBlocks, StringComparer.Ordinal);
        if (includeAudio)
        {
            foreach (var n in AudioBlocks) names.Add(n);
        }
        return EffectBlockScanner.ClearBlockContents(content, names);
    }

    /// <summary>
    /// 清空指定块名列表。
    /// </summary>
    public static string CleanAo(string content, IEnumerable<string> blockNames)
    {
        var names = new HashSet<string>(blockNames, StringComparer.Ordinal);
        return EffectBlockScanner.ClearBlockContents(content, names);
    }

    /// <summary>
    /// 清空 .ao 块并在 SkinMesh 中注入 hide_segments。
    /// 严格遵循易刷优先级：先清块（优先级 20），再注入 hideSegments（优先级 35）。
    /// </summary>
    public static string CleanAoWithSegments(string content, IReadOnlyList<string>? blocks, IReadOnlyList<string>? hideSegments)
    {
        var result = content;
        if (blocks is not null && blocks.Count > 0)
        {
            result = CleanAo(result, blocks);
        }

        if (hideSegments is not null && hideSegments.Count > 0)
        {
            result = CleanSkinMeshHideSegments(result, hideSegments);
        }

        return result;
    }

    /// <summary>
    /// 在 .ao / .aoc 文件的 client -> SkinMesh 中注入 hide_segments 属性。
    /// 对齐易刷 Vt("hide-segments") 的行为。
    /// </summary>
    public static string CleanSkinMeshHideSegments(string content, IReadOnlyList<string> segments)
    {
        var segmentsValue = "\"" + string.Join(",", segments) + "\"";
        return SetNestedProperty(content, "client", "SkinMesh", "hide_segments", segmentsValue);
    }

    /// <summary>
    /// 在 .ot 文件的 client -> Render 中注入 disable_rendering = true。
    /// 对齐易刷 Vt("entity") 的行为。
    /// </summary>
    public static string CleanOtDisableRendering(string content)
    {
        return SetNestedProperty(content, "client", "Render", "disable_rendering", "true");
    }

    /// <summary>
    /// 在嵌套块 outerBlock -> innerBlock 中设置或更新指定的属性键值对。
    /// 若 outerBlock 或 innerBlock 不存在，则按标准类 C 语法嵌套创建。
    /// </summary>
    private static string SetNestedProperty(
        string content,
        string outerBlock,
        string innerBlock,
        string propName,
        string propValue)
    {
        var newline = content.Contains("\r\n") ? "\r\n" : "\n";
        var outerBlocks = EffectBlockScanner.FindBlocks(content, new HashSet<string>([outerBlock]));

        if (outerBlocks.Count == 0)
        {
            // 外部块不存在，整体在文件末尾追加
            var sb = new StringBuilder(content.TrimEnd());
            sb.Append(newline).Append(newline);
            sb.Append(outerBlock).Append(newline).Append('{').Append(newline);
            sb.Append("\t").Append(innerBlock).Append(newline).Append("\t{").Append(newline);
            sb.Append("\t\t").Append(propName).Append(" = ").Append(propValue).Append(newline);
            sb.Append("\t}").Append(newline);
            sb.Append('}').Append(newline);
            return sb.ToString();
        }

        // 外层块已存在（取第一个匹配）
        var outer = outerBlocks[0];
        var (outerInnerStart, outerInnerLen) = outer.InnerSpan;
        var outerInnerText = content.Substring(outerInnerStart, outerInnerLen);

        var innerBlocks = EffectBlockScanner.FindBlocks(outerInnerText, new HashSet<string>([innerBlock]));
        if (innerBlocks.Count == 0)
        {
            // 内层块不存在，在外层块内容开头插入内层块
            var insertCode = $"{newline}\t{innerBlock}{newline}\t{{{newline}\t\t{propName} = {propValue}{newline}\t}}{newline}";
            return content.Insert(outerInnerStart, insertCode);
        }

        // 内层块已存在，在内层块里寻找是否已有该属性
        var inner = innerBlocks[0];
        int globalInnerStart = outerInnerStart + inner.InnerSpan.Start;
        int globalInnerLen = inner.InnerSpan.Length;
        var innerInnerText = content.Substring(globalInnerStart, globalInnerLen);

        // pattern 由属性名拼成，属性名来自内置规则表；仍加超时兜底，
        // 避免将来出现含特殊字符的属性名导致回溯失控。
        var propRegex = new Regex(
            $@"([ \t]*{Regex.Escape(propName)}[ \t]*=[ \t]*)[^\r\n;]+([ \t;]*)",
            RegexOptions.None,
            TimeSpan.FromSeconds(2));
        if (propRegex.IsMatch(innerInnerText))
        {
            var replacedInner = propRegex.Replace(innerInnerText, $"$1{propValue}$2");
            return content.Remove(globalInnerStart, globalInnerLen).Insert(globalInnerStart, replacedInner);
        }

        // 内层块没有该属性，在内层块开头插入
        var insertProp = $"{newline}\t\t{propName} = {propValue};";
        return content.Insert(globalInnerStart, insertProp);
    }

    public static string GetExtension(string virtualPath)
    {
        int dot = virtualPath.LastIndexOf('.');
        return dot < 0 ? string.Empty : virtualPath[dot..].ToLowerInvariant();
    }
}
