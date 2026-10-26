using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.Modules.ColorMod;

/// <summary>
/// 装备与地图词缀多色彩染色服务 (ColorMod)
/// 严格对齐参考项目的动态 CSD 扫描、多语言自适应检测 (简中/繁中/汉化) 与四层决策上色体系
/// </summary>
public sealed class ColorModService
{
    private readonly PatchEngine _engine;

    public ColorModService(PatchEngine engine)
    {
        _engine = engine;
    }

    /// <summary>
    /// 从当前游戏客户端动态提取全部词缀，并执行多语言自适应与四层上色决策
    /// </summary>
    public List<ColorModEntry> ExtractEntries(GameModeInfo info, Action<string>? progress = null)
    {
        var resultList = new List<ColorModEntry>();
        var statIdIndexMap = new Dictionary<string, int>(StringComparer.Ordinal);

        // 1. 使用客户端实际语言。汉化后 CSD 的「Traditional Chinese」段内容已是简体，
        // 因此必须按 info 判定的简繁取值，不能假定国际服就一定是繁体。
        string targetLang = Poe2LanguageDetector.GetTargetCsdLanguage(info.IsChina);
        progress?.Invoke($"已检测到客户端语言环境: 【{targetLang}】，准备动态读取客户端词缀...");

        bool readSuccess = false;

        if (info.IsValid)
        {
            try
            {
                // 构建 10 组需要读取的 CSD 文件虚拟路径
                var csdFilesToRead = new List<string>();
                foreach (var tuple in ColorModDefaults.GroupCsdFiles)
                {
                    csdFilesToRead.Add("data/statdescriptions/" + tuple.CsdFile);
                    csdFilesToRead.Add("metadata/statdescriptions/" + tuple.CsdFile);
                }

                progress?.Invoke("正在从客户端资源包高速检索 10 组 StatDescriptions 文件...");
                var fileBytesMap = _engine.ReadGameFiles(info, csdFilesToRead);

                // 立即快照异常：静态字段后续可能被别的读取覆盖。
                // 扫描属只读流程，读取失败时明确告知是 IO / 损坏问题，而不是「客户端没有这些文件」。
                var scanReadEx = PatchEngine.LastReadException;
                if (scanReadEx is not null)
                {
                    progress?.Invoke($"动态扫描读取客户端资源异常：{scanReadEx.Message}");
                }

                foreach (var tuple in ColorModDefaults.GroupCsdFiles)
                {
                    string primaryPath = "data/statdescriptions/" + tuple.CsdFile;
                    string secondaryPath = "metadata/statdescriptions/" + tuple.CsdFile;

                    byte[]? bytes = null;
                    if (fileBytesMap.TryGetValue(primaryPath, out var b1) && b1 != null && b1.Length > 0)
                    {
                        bytes = b1;
                    }
                    else if (fileBytesMap.TryGetValue(secondaryPath, out var b2) && b2 != null && b2.Length > 0)
                    {
                        bytes = b2;
                    }

                    if (bytes == null || bytes.Length == 0) continue;

                    string text = DecodeUtf16(bytes);
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    readSuccess = true;
                    // 动态解析 Description Blocks
                    var blocks = ColorModDefaults.ParseDescriptionBlocks(text, targetLang);
                    // 动态构建带有四层决策规则的 ColorModEntry
                    var entries = ColorModDefaults.BuildEntriesFromBlocks(blocks, tuple.Group);

                    foreach (var entry in entries)
                    {
                        if (statIdIndexMap.TryGetValue(entry.StatId, out var existingIdx))
                        {
                            resultList[existingIdx] = entry;
                        }
                        else
                        {
                            statIdIndexMap[entry.StatId] = resultList.Count;
                            resultList.Add(entry);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                progress?.Invoke($"动态扫描过程出现异常：{ex.Message}，将尝试读取本地离线词典缓存...");
            }
        }

        // 不使用任何离线缓存兜底：缓存是历史遗留的繁体词库，与当前客户端语言可能
        // 完全不符，静默回退会让用户以为看到的是客户端真实内容，进而把错误的规则
        // 写进游戏。提取失败时明确报错，由用户决定重试或检查客户端路径。
        if (!readSuccess || resultList.Count == 0)
        {
            progress?.Invoke("未能从客户端提取到任何词缀条目。请确认游戏路径正确且客户端文件完整，然后重新点击提取。");
            return [];
        }

        progress?.Invoke($"从游戏客户端成功动态提取 {resultList.Count} 条词缀，多语言适配完成！");
        return resultList;
    }


    /// <summary>
    /// 将词缀上色配置真实应用到游戏客户端的 CSD 文件中
    /// </summary>
    public bool ApplyColorMods(
        GameModeInfo info,
        ColorModStyle globalStyle,
        IEnumerable<ColorModEntry> entries,
        Action<string>? progress = null)
    {
        if (!info.IsValid)
        {
            progress?.Invoke($"游戏目录无效：{info.ErrorMessage}");
            return false;
        }

        var entryList = entries.ToList();
        var enabledEntries = entryList.Where(e => e.Enabled && !string.IsNullOrWhiteSpace(e.StatId)).ToList();

        if (enabledEntries.Count == 0)
        {
            progress?.Invoke("未启用任何词缀上色规则。");
            return false;
        }

        progress?.Invoke($"准备为 {enabledEntries.Count} 条词缀应用染色规则...");

        var customColorMap = enabledEntries.ToDictionary(e => e.StatId, e => e.Color, StringComparer.Ordinal);
        var customStyleMap = enabledEntries.ToDictionary(e => e.StatId, e => e.Style, StringComparer.Ordinal);
        var disabledStats = entryList.Where(e => !e.Enabled).Select(e => e.StatId).ToHashSet(StringComparer.Ordinal);

        var csdFilesToRead = new List<string>();
        foreach (var tuple in ColorModDefaults.GroupCsdFiles)
        {
            csdFilesToRead.Add("data/statdescriptions/" + tuple.CsdFile);
            csdFilesToRead.Add("metadata/statdescriptions/" + tuple.CsdFile);
        }

        var existingFiles = _engine.ReadGameFiles(info, csdFilesToRead);

        // 写入流程对读取失败零容忍：读不到 CSD 会导致 filesToWrite 为空，
        // 若继续往下走会变成「没改任何文件却报成功」的假成功。
        var applyReadEx = PatchEngine.LastReadException;
        if (applyReadEx is not null)
        {
            progress?.Invoke($"读取客户端 CSD 文件失败，已中止染色写入：{applyReadEx.Message}");
            return false;
        }

        var filesToWrite = new List<(string VirtualPath, byte[] Data)>();

        foreach (var tuple in ColorModDefaults.GroupCsdFiles)
        {
            string primaryPath = "data/statdescriptions/" + tuple.CsdFile;
            string secondaryPath = "metadata/statdescriptions/" + tuple.CsdFile;

            string? chosenPath = null;
            byte[]? rawBytes = null;

            if (existingFiles.TryGetValue(primaryPath, out var b1) && b1 != null && b1.Length > 0)
            {
                chosenPath = primaryPath;
                rawBytes = b1;
            }
            else if (existingFiles.TryGetValue(secondaryPath, out var b2) && b2 != null && b2.Length > 0)
            {
                chosenPath = secondaryPath;
                rawBytes = b2;
            }

            if (chosenPath == null || rawBytes == null) continue;

            byte[] patchedBytes = TransformColorMods(chosenPath, rawBytes, customColorMap, customStyleMap, disabledStats);
            if (patchedBytes != rawBytes && patchedBytes.Length > 0)
            {
                filesToWrite.Add((chosenPath, patchedBytes));
            }
        }

        if (filesToWrite.Count == 0)
        {
            progress?.Invoke("未找到需要更新的客户端词缀文件或文件未发生变更。");
            return true;
        }

        progress?.Invoke($"正在向客户端安全写入 {filesToWrite.Count} 个已染色的 StatDescriptions 资源文件...");
        bool writeSuccess = _engine.WriteGameFiles(info, filesToWrite);
        if (writeSuccess)
        {
            progress?.Invoke($"词缀染色成功！已更新 {filesToWrite.Count} 个客户端核心资源文件。");
        }
        else
        {
            // 透出根因：写入失败可能是备份不可用、路径不匹配或写入后校验不一致，
            // 只提示「检查文件占用或运行权限」会把用户引向错误方向。
            progress?.Invoke($"写入客户端文件失败：{PatchEngine.LastWriteException?.Message ?? "请检查文件占用或运行权限。"}");
        }

        return writeSuccess;
    }

    private static readonly Regex WholeTagWrapRe = new("^<[A-Za-z][A-Za-z0-9]*>\\{\\{(?<c>.*)\\}\\}$", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex LeadingBareTagRe = new("^<[A-Za-z][A-Za-z0-9]*>(?<c>.*)$", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// 依照参考项目 PatchTransforms.ColorMods 完整算法，对 CSD 文本内容执行染色注入
    /// </summary>
    public static byte[] TransformColorMods(
        string path,
        byte[] bytes,
        Dictionary<string, byte[]> customColorMap,
        Dictionary<string, ColorModStyle>? customStyleMap,
        HashSet<string>? disabledStats)
    {
        string text = DecodeUtf16(bytes);
        if (string.IsNullOrWhiteSpace(text)) return bytes;

        string[] array = text.Split(["\r\n", "\n"], StringSplitOptions.None);
        string fileName = Path.GetFileName(path.Replace('\\', '/'));
        var occurrenceMap = new Dictionary<string, int>(StringComparer.Ordinal);
        var outputLines = new List<string>(array.Length);
        bool changed = false;

        int num = 0;
        while (num < array.Length)
        {
            if (!IsCsdBlockStart(array[num], "description"))
            {
                outputLines.Add(array[num]);
                num++;
                continue;
            }

            int num2;
            for (num2 = num + 1; num2 < array.Length && !IsCsdBlockStart(array[num2], "description") && !IsCsdBlockStart(array[num2], "include") && !IsCsdBlockStart(array[num2], "no_description"); num2++)
            {
            }

            int num3;
            for (num3 = num + 1; num3 < num2 && string.IsNullOrWhiteSpace(array[num3]); num3++)
            {
            }

            if (num3 >= num2)
            {
                for (int num4 = num; num4 < num2; num4++)
                {
                    outputLines.Add(array[num4]);
                }
                num = num2;
                continue;
            }

            string[] array2 = array[num3].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string[] array3 = ((array2.Length != 0 && int.TryParse(array2[0], out _)) ? array2[1..] : array2);
            string key = string.Join(' ', array3);
            occurrenceMap.TryGetValue(key, out var occurrence);
            occurrenceMap[key] = occurrence + 1;

            ColorModBlockPlan? colorModBlockPlan = ((array3.Length != 0) ? ColorModOverrides.Find(fileName, array3, occurrence) : null);
            if ((disabledStats != null && array3.Any(disabledStats.Contains)) || (colorModBlockPlan != null && !array3.Any(customColorMap.ContainsKey)))
            {
                for (int num5 = num; num5 < num2; num5++)
                {
                    outputLines.Add(array[num5]);
                }
                num = num2;
            }
            else if (colorModBlockPlan == null)
            {
                if (EmitFallbackBlock(array, num, num2, num3, array3, customColorMap, customStyleMap, outputLines))
                {
                    changed = true;
                }
                num = num2;
            }
            else
            {
                if (EmitPlannedBlock(array, num, num2, num3, array3, colorModBlockPlan, customColorMap, customStyleMap, outputLines))
                {
                    changed = true;
                }
                num = num2;
            }
        }

        if (!changed) return bytes;
        return EncodeUtf16Bom(string.Join("\r\n", outputLines));
    }

    private static bool IsCsdBlockStart(string line, string keyword)
    {
        string text = line.TrimStart('\t', ' ');
        bool flag = text.StartsWith(keyword, StringComparison.Ordinal);
        if (flag)
        {
            bool flag2 = text.Length == keyword.Length;
            if (!flag2)
            {
                char c = text[keyword.Length];
                flag2 = (c == '\t' || c == ' ');
            }
            flag = flag2;
        }
        return flag;
    }

    private static bool EmitPlannedBlock(
        string[] lines,
        int start,
        int end,
        int header,
        string[] statIds,
        ColorModBlockPlan plan,
        Dictionary<string, byte[]> colorMap,
        Dictionary<string, ColorModStyle>? customStyleMap,
        List<string> output)
    {
        bool result = false;
        for (int i = start; i < header; i++)
        {
            output.Add(lines[i]);
        }
        output.Add(lines[header]);

        byte[]? matchedUserColor = null;
        foreach (string key in statIds)
        {
            if (colorMap.TryGetValue(key, out byte[]? val))
            {
                matchedUserColor = val;
                break;
            }
        }

        if (matchedUserColor != null && PlanDistinctTags(plan) > 1)
        {
            matchedUserColor = null;
        }

        ColorModStyle userStyle = ColorModStyle.Whole;
        foreach (string key2 in statIds)
        {
            if (customStyleMap != null && customStyleMap.TryGetValue(key2, out var val2))
            {
                userStyle = val2;
                break;
            }
        }

        int num = header + 1;
        while (num < end)
        {
            string text = lines[num];
            string text2 = text.TrimStart('\t', ' ');
            if (text2.StartsWith("lang ", StringComparison.Ordinal))
            {
                output.Add(text);
                num++;
            }
            else if (int.TryParse(text2.Trim(), out int lineCount) && lineCount > 0)
            {
                var list = new List<string>();
                int k;
                for (k = num + 1; k < end; k++)
                {
                    if (list.Count >= lineCount) break;
                    list.Add(lines[k]);
                }

                int expected = ExpectedLineCount(plan);
                if (expected != plan.Slots.Count && lineCount == expected)
                {
                    output.Add(text);
                    output.AddRange(list);
                    num = k;
                    continue;
                }

                var list2 = new List<string>();
                for (int l = 0; l < list.Count; l++)
                {
                    ColorModSlotPlan? slotPlan = ((l < plan.Slots.Count) ? plan.Slots[l] : null);
                    List<string>? applied = ApplySlotPlan(list[l], slotPlan, matchedUserColor, userStyle);
                    if (applied == null)
                    {
                        list2.Add(list[l]);
                        continue;
                    }
                    list2.AddRange(applied);
                    result = true;
                }

                output.Add(text[..^text2.Length] + list2.Count);
                output.AddRange(list2);
                num = k;
            }
            else
            {
                output.Add(text);
                num++;
            }
        }
        return result;
    }

    private static int ExpectedLineCount(ColorModBlockPlan plan)
    {
        int num = 0;
        foreach (ColorModSlotPlan slot in plan.Slots)
        {
            int num2 = num;
            List<ColorModTier>? list = slot?.Tiers;
            num = num2 + ((list == null || list.Count <= 0) ? 1 : list.Count);
        }
        return num;
    }

    private static int PlanDistinctTags(ColorModBlockPlan plan)
    {
        var hashSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (ColorModSlotPlan slot in plan.Slots)
        {
            if (slot == null) continue;
            if (slot.Tag != null) hashSet.Add(slot.Tag);
            if (slot.Tiers == null) continue;
            foreach (ColorModTier tier in slot.Tiers)
            {
                if (tier.Tag != null) hashSet.Add(tier.Tag);
            }
        }
        return hashSet.Count;
    }

    private static List<string>? ApplySlotPlan(
        string line,
        ColorModSlotPlan? plan,
        byte[]? userColor,
        ColorModStyle userStyle)
    {
        if (plan == null) return null;

        string indent = line[..^line.TrimStart('\t', ' ').Length];
        int num = line.IndexOf('"');
        int num2 = line.LastIndexOf('"');
        if (num < 0 || num2 <= num) return null;

        string prefix = line[indent.Length..num];
        string inner = line[(num + 1)..num2];
        string suffix = line[(num2 + 1)..];

        List<ColorModTier>? tiers = plan.Tiers;
        if (tiers != null && tiers.Count > 0)
        {
            var list = new List<string>(tiers.Count);
            foreach (ColorModTier tier in tiers)
            {
                byte[]? rgb = ((tier.Tag != null) ? (userColor ?? ColorModOverrides.Rgb(tier.Tag)) : null);
                string wrapped = ((rgb != null && !IsAlreadyWrapped(inner, tier.Wrap, rgb, tier.WrapTrad))
                    ? WrapText(inner, tier.Wrap, rgb, tier.WrapTrad)
                    : inner);
                string p = (string.IsNullOrWhiteSpace(tier.Prefix) ? prefix : (tier.Prefix + " "));
                string s = ((tier.Tail != null) ? (" " + tier.Tail) : suffix);
                list.Add($"{indent}{p}\"{wrapped}\"{s}");
            }
            return list;
        }

        if (plan.Tag == null) return null;
        byte[]? tagColor = userColor ?? ColorModOverrides.Rgb(plan.Tag);
        if (tagColor == null) return null;

        if (IsAlreadyWrapped(inner, plan.Wrap, tagColor, plan.WrapTrad))
        {
            return null;
        }

        string? wrap = plan.Wrap;
        bool isWhole = (wrap == "*" || (wrap != null && wrap.Length == 0));
        string resultText = ((isWhole || plan.Wrap == null)
            ? WrapWhole(inner, tagColor, userStyle)
            : WrapText(inner, plan.Wrap, tagColor, plan.WrapTrad));

        if (resultText == inner) return null;

        string finalPrefix = ((plan.Prefix != null) ? (plan.Prefix + " ") : prefix);
        string finalTail = ((plan.Tail != null) ? (" " + plan.Tail) : suffix);
        return [$"{indent}{finalPrefix}\"{resultText}\"{finalTail}"];
    }

    private static string WrapWhole(string inner, byte[] color, ColorModStyle style)
    {
        string text = $"<rgb({color[0]},{color[1]},{color[2]})>";
        string text2 = UnwrapGameTag(StripOurColourWrapper(inner));
        if (style != ColorModStyle.NumberOnly)
        {
            return text + "{{" + text2 + "}}";
        }
        return AnnotateNumberSpans(text2, color);
    }

    private static string WrapText(string inner, string? needle, byte[] color, string? needleTrad = null)
    {
        string text = $"<rgb({color[0]},{color[1]},{color[2]})>";
        string?[] array = [needle, needleTrad];
        foreach (string? needleItem in array)
        {
            if (!string.IsNullOrEmpty(needleItem) && needleItem != "*")
            {
                var span = FindWrapSpan(inner, needleItem);
                if (span.HasValue)
                {
                    var (start, len) = span.Value;
                    return $"{inner[..start]}{text}{{{{{inner.Substring(start, len)}}}}}{inner[(start + len)..]}";
                }
            }
        }
        return text + "{{" + UnwrapGameTag(StripOurColourWrapper(inner)) + "}}";
    }

    private static bool IsAlreadyWrapped(string inner, string? wrap, byte[] rgb, string? wrapTrad)
    {
        string text = $"<rgb({rgb[0]},{rgb[1]},{rgb[2]})>";
        string?[] array = [wrap, wrapTrad];
        foreach (string? wrapItem in array)
        {
            if (string.IsNullOrEmpty(wrapItem) || wrapItem == "*") continue;
            if (inner.Contains(text + "{{" + wrapItem + "}}", StringComparison.Ordinal)) return true;
            var span = FindWrapSpan(inner, wrapItem);
            if (span.HasValue)
            {
                var (start, len) = span.Value;
                int num = text.Length + 2;
                if (start >= num && inner[start - 2] == '{' && inner[start - 1] == '{' && inner.AsSpan(start - num, text.Length).SequenceEqual(text))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static (int Start, int Length)? FindWrapSpan(string text, string needle)
    {
        int num = text.IndexOf(needle, StringComparison.Ordinal);
        if (num >= 0) return (num, needle.Length);

        num = IndexOfIgnoringWhitespace(text, needle);
        if (num >= 0) return (num, MatchLengthIgnoringWhitespace(text, num, needle));

        var (norm, map) = NormalizeForMatch(text);
        string normNeedle = NormalizeForMatch(needle).Norm;
        if (normNeedle.Length == 0) return null;

        int num2 = norm.IndexOf(normNeedle, StringComparison.Ordinal);
        if (num2 < 0) return null;

        int num3 = map[num2];
        int num4 = map[num2 + normNeedle.Length - 1] + 1;
        return (num3, num4 - num3);
    }

    private static (string Norm, List<int> Map) NormalizeForMatch(string s)
    {
        var sb = new StringBuilder(s.Length);
        var map = new List<int>(s.Length);
        int num = 0;
        while (num < s.Length)
        {
            char c = s[num];
            bool flag = char.IsWhiteSpace(c) || (c == '{' || c == '}');
            if (flag)
            {
                num++;
                continue;
            }
            switch (c)
            {
                case '<':
                {
                    int num5 = s.IndexOf('>', num);
                    num = ((num5 > num) ? (num5 + 1) : (num + 1));
                    continue;
                }
                case '[':
                {
                    int num2 = s.IndexOf('|', num);
                    int num3 = s.IndexOf(']', num);
                    if (num3 <= num) break;
                    int num4 = ((num2 > num && num2 < num3) ? num2 : num3);
                    for (int i = num; i < num4; i++)
                    {
                        if (!char.IsWhiteSpace(s[i]))
                        {
                            sb.Append(s[i]);
                            map.Add(i);
                        }
                    }
                    sb.Append(']');
                    map.Add(num3);
                    num = num3 + 1;
                    continue;
                }
            }
            if (IsDecoration(c))
            {
                num++;
                continue;
            }
            sb.Append(c);
            map.Add(num);
            num++;
        }
        return (sb.ToString(), map);
    }

    private static bool IsDecoration(char c)
    {
        return c is '※' or '‼' or '⁉' or '←' or '↑' or '→' or '↓' or '■' or '□'
            or '▲' or '△' or '▼' or '▽' or '◆' or '◇' or '○' or '●'
            or '★' or '☆' or '✦' or '✧' or '《' or '》' or '【' or '】'
            or '〔' or '〕' or '〖' or '〗';
    }

    private static int IndexOfIgnoringWhitespace(string haystack, string needle)
    {
        string text = new(needle.Where(c => !char.IsWhiteSpace(c)).ToArray());
        for (int num = 0; num < haystack.Length; num++)
        {
            int num2 = 0;
            for (int num3 = num; num3 < haystack.Length; num3++)
            {
                if (num2 >= text.Length) break;
                char c = haystack[num3];
                if (!char.IsWhiteSpace(c))
                {
                    if (c != text[num2]) break;
                    num2++;
                }
            }
            if (num2 == text.Length)
            {
                for (; num < haystack.Length && char.IsWhiteSpace(haystack[num]); num++)
                {
                }
                return num;
            }
        }
        return -1;
    }

    private static int MatchLengthIgnoringWhitespace(string haystack, int start, string needle)
    {
        string text = new(needle.Where(c => !char.IsWhiteSpace(c)).ToArray());
        int num = 0;
        int num2;
        for (num2 = start; num2 < haystack.Length; num2++)
        {
            if (num >= text.Length) break;
            if (!char.IsWhiteSpace(haystack[num2])) num++;
        }
        return num2 - start;
    }

    private static bool EmitFallbackBlock(
        string[] lines,
        int start,
        int end,
        int header,
        string[] statIds,
        Dictionary<string, byte[]> colorMap,
        Dictionary<string, ColorModStyle>? customStyleMap,
        List<string> output)
    {
        byte[]? matchedColor = null;
        ColorModStyle style = ColorModStyle.Whole;
        foreach (string key in statIds)
        {
            if (colorMap.TryGetValue(key, out byte[]? val))
            {
                matchedColor = val;
                if (customStyleMap != null && customStyleMap.TryGetValue(key, out var val2))
                {
                    style = val2;
                }
                break;
            }
        }

        if (matchedColor == null)
        {
            for (int j = start; j < end; j++) output.Add(lines[j]);
            return false;
        }

        bool result = false;
        for (int k = start; k <= header; k++) output.Add(lines[k]);

        for (int l = header + 1; l < end; l++)
        {
            string text = lines[l].TrimStart('\t', ' ');
            if (text.StartsWith("lang ", StringComparison.Ordinal) || int.TryParse(text.Trim(), out _))
            {
                output.Add(lines[l]);
                continue;
            }
            string? text2 = AnnotateVariantLine(lines[l], matchedColor, style);
            if (text2 != null)
            {
                output.Add(text2);
                result = true;
            }
            else
            {
                output.Add(lines[l]);
            }
        }
        return result;
    }

    private static string StripOurColourWrapper(string inner)
    {
        string input = Regex.Replace(inner, "<rgb\\(\\d+,\\d+,\\d+\\)>", "");
        input = Regex.Replace(input, "<(?:AT|DF|DA|LUK|SP)\\d>", "");
        if (input.StartsWith("{{", StringComparison.Ordinal) && input.EndsWith("}}", StringComparison.Ordinal) && input.Length >= 4)
        {
            input = input[2..^2];
        }
        return input;
    }

    private static string UnwrapGameTag(string text)
    {
        Match match = WholeTagWrapRe.Match(text);
        if (match.Success) return match.Groups["c"].Value;
        Match match2 = LeadingBareTagRe.Match(text);
        if (match2.Success) return match2.Groups["c"].Value;
        return text;
    }

    private static string? AnnotateVariantLine(string line, byte[] color, ColorModStyle style)
    {
        int num = line.IndexOf('"');
        if (num < 0) return null;
        int num2 = line.IndexOf('"', num + 1);
        if (num2 < 0) return null;

        string inner = line[(num + 1)..num2];
        string prefix = line[..num];
        string suffix = line[(num2 + 1)..];

        string text = UnwrapGameTag(StripOurColourWrapper(inner));
        string value3 = style switch
        {
            ColorModStyle.NumberOnly => AnnotateNumberSpans(text, color),
            ColorModStyle.FollowGame => AnnotateFollowGame(text, color) ?? AnnotateNumberSpans(text, color),
            _ => $"<rgb({color[0]},{color[1]},{color[2]})>" + "{{" + text + "}}"
        };

        string text2 = $"{prefix}\"{value3}\"{suffix}";
        if (text2 == line) return null;
        return text2;
    }

    private static string AnnotateNumberSpans(string text, byte[] color)
    {
        string rgb = $"<rgb({color[0]},{color[1]},{color[2]})>";
        string text2 = "\\{[0-9][^}]*\\}";
        string text3 = "(?:\\s*[-–—～~到至%·,，.。、/:：]?\\s*)";
        string pattern = text2 + "(?:" + text3 + text2 + ")*(?:%+|[:+][+\\-.:a-zA-Z0-9%]*)?";
        return Regex.Replace(text, pattern, (Match m) => rgb + "{{" + m.Value + "}}");
    }

    private static string? AnnotateFollowGame(string text, byte[] color)
    {
        List<(int, int)> list = ColorModDefaults.FollowGameSpans(text);
        if (list.Count == 0) return null;

        string rgb = $"<rgb({color[0]},{color[1]},{color[2]})>";
        var sb = new StringBuilder();
        int num = 0;
        foreach (var item in list)
        {
            if (item.Item1 > num)
            {
                sb.Append(text[num..item.Item1]);
            }
            sb.Append(rgb).Append("{{").Append(text[item.Item1..item.Item2]).Append("}}");
            num = item.Item2;
        }
        if (num < text.Length)
        {
            sb.Append(text[num..]);
        }
        return sb.ToString();
    }

    private static string DecodeUtf16(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return string.Empty;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        return Encoding.Unicode.GetString(bytes);
    }

    private static byte[] EncodeUtf16Bom(string text)
    {
        byte[] body = Encoding.Unicode.GetBytes(text);
        byte[] result = new byte[body.Length + 2];
        result[0] = 0xFF;
        result[1] = 0xFE;
        Buffer.BlockCopy(body, 0, result, 2, body.Length);
        return result;
    }

    /// <summary>
    /// 启动时的初始词缀列表。
    ///
    /// 刻意返回空：词缀内容必须由用户显式点击「提取客户端」后从客户端实时读取。
    /// 不在启动时加载任何历史缓存，避免展示与当前客户端语言不符的过期文本。
    /// </summary>
    public static List<ColorModEntry> GetBuiltinEntries() => [];

}
