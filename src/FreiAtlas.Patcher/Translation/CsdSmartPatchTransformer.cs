using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FreiAtlas.Patcher.Translation;

/// <summary>
/// CSD 词缀与属性描述文件智能改写引擎：
/// 1. 严格块级界定：以 description, no_description, include 严格界定块范围，严禁跨块错位；
/// 2. 原位无感替换：在原版 lang "Traditional Chinese" 出现的物理位置，直接以官方简中文本输出，原版其他语言顺序 100% 原样保留；
/// 3. 原生简中优先：若 block 原生自带 lang "Simplified Chinese"，1:1 提取其内容并在原 Traditional Chinese 位置输出，移除多余的 Simplified Chinese 段；
/// 4. 国服补漏注入：若 block 原生缺失简中（全游戏仅 302 块），从规则库精准注入国服官方简中（带完整标头与条数）；
/// 5. 哨兵繁体兜底：两端均无官方简中的废弃占位符，保留繁体原文兜底防崩溃；
/// 6. 顶级与非描述区域（no_description, include, 注释）100% 保持原样不碰。
/// </summary>
public static class CsdSmartPatchTransformer
{
    private static bool IsCsdBlockStart(string line, string keyword)
    {
        string text = line.TrimStart('\t', ' ');
        if (text.StartsWith(keyword, StringComparison.Ordinal))
        {
            if (text.Length == keyword.Length) return true;
            char c = text[keyword.Length];
            return c == '\t' || c == ' ';
        }
        return false;
    }

    private sealed class LangSection
    {
        public string LangName { get; set; } = string.Empty;
        public List<string> Lines { get; } = [];
    }

    /// <summary>
    /// 对国际服原版的 CSD 字节执行严格块级改写并返回 UTF-16LE 字节数组（带 BOM）
    /// </summary>
    public static byte[] Transform(
        byte[] originalBytes,
        string relativePath,
        IReadOnlyDictionary<string, string>? missingRulesForFile = null)
    {
        if (originalBytes.Length == 0) return originalBytes;

        string fullText = Encoding.Unicode.GetString(originalBytes);
        bool hasBom = fullText.StartsWith('\uFEFF');
        if (hasBom) fullText = fullText.Substring(1);

        string[] lines = fullText.Split(["\r\n", "\n"], StringSplitOptions.None);
        var outputLines = new List<string>(lines.Length);

        int i = 0;
        while (i < lines.Length)
        {
            string line = lines[i];
            if (!IsCsdBlockStart(line, "description"))
            {
                outputLines.Add(line);
                i++;
                continue;
            }

            int blockStart = i;
            int nextBlock = i + 1;
            while (nextBlock < lines.Length &&
                   !IsCsdBlockStart(lines[nextBlock], "description") &&
                   !IsCsdBlockStart(lines[nextBlock], "no_description") &&
                   !IsCsdBlockStart(lines[nextBlock], "include"))
            {
                nextBlock++;
            }

            ProcessDescriptionBlock(lines, blockStart, nextBlock, missingRulesForFile, outputLines);
            i = nextBlock;
        }

        string result = string.Join("\r\n", outputLines);
        if (hasBom) result = "\uFEFF" + result;

        return Encoding.Unicode.GetBytes(result);
    }

    private static void ProcessDescriptionBlock(
        string[] allLines,
        int start,
        int end,
        IReadOnlyDictionary<string, string>? missingRules,
        List<string> output)
    {
        var commonLines = new List<string>();
        var langSections = new List<LangSection>();
        LangSection? currentLang = null;
        string statKey = string.Empty;

        for (int i = start; i < end; i++)
        {
            string line = allLines[i];
            string trimmed = line.TrimStart('\t', ' ');

            if (trimmed.StartsWith("lang \"", StringComparison.OrdinalIgnoreCase))
            {
                int q1 = trimmed.IndexOf('"');
                int q2 = trimmed.LastIndexOf('"');
                string lang = (q1 >= 0 && q2 > q1) ? trimmed.Substring(q1 + 1, q2 - q1 - 1) : string.Empty;

                currentLang = new LangSection { LangName = lang };
                langSections.Add(currentLang);
            }
            else if (currentLang != null)
            {
                currentLang.Lines.Add(line);
            }
            else
            {
                commonLines.Add(line);
                if (string.IsNullOrEmpty(statKey) && i > start && !string.IsNullOrWhiteSpace(trimmed))
                {
                    statKey = trimmed;
                }
            }
        }

        // 1. 输出描述块头和默认英文规则（保持原样）
        output.AddRange(commonLines);

        // 2. 检查简中与繁中情况
        var scSection = langSections.FirstOrDefault(l => l.LangName.Equals("Simplified Chinese", StringComparison.OrdinalIgnoreCase));
        var tcSection = langSections.FirstOrDefault(l => l.LangName.Equals("Traditional Chinese", StringComparison.OrdinalIgnoreCase));

        // 准备待输出的简体中文内容行
        List<string>? targetChineseContent = null;
        if (scSection != null && scSection.Lines.Count > 0)
        {
            // 原生具备简中：直接采用原生简中内容
            targetChineseContent = scSection.Lines;
        }
        else if (missingRules != null && TryGetMissingRule(missingRules, statKey, out string? ruleContent))
        {
            // 原生缺失简中但规则库收录：采用规则库内容
            targetChineseContent = new List<string>();
            var rLines = ruleContent.Split(["\r\n", "\n"], StringSplitOptions.None);
            foreach (var rl in rLines)
            {
                if (!string.IsNullOrWhiteSpace(rl))
                {
                    targetChineseContent.Add(rl.StartsWith('\t') ? rl : "\t" + rl);
                }
            }
        }
        else if (tcSection != null)
        {
            // 两端均无简中（如废弃哨兵）：兜底保留繁体内容防崩溃
            targetChineseContent = tcSection.Lines;
        }

        // 2.5 与「词缀多色彩染色」合并：保留繁中段里已注入的 <rgb(...)> 标签。
        //
        // 冲突背景：染色补丁把 <rgb(r,g,b)>...</rgb> 写进 Traditional Chinese 段，
        // 而本方法会用原生 Simplified Chinese 段整段覆盖它，导致颜色全部丢失。
        // 客户端原始 .csd 本身不含任何 <rgb> 标签（实测 stat_descriptions.csd 为 0 处），
        // 因此这里出现的标签必然来自染色补丁，可安全地重新包裹到替换后的文本上。
        if (targetChineseContent is not null
            && tcSection is not null
            && !ReferenceEquals(targetChineseContent, tcSection.Lines))
        {
            var preservedTags = ExtractColorTags(tcSection.Lines);
            if (preservedTags.Count > 0)
            {
                targetChineseContent = ReapplyColorTags(targetChineseContent, preservedTags);
            }
        }

        // 3. 原位输出语言块（保持原有语言在原版的物理先后顺序！）
        bool hasEmittedChinese = false;

        foreach (var sec in langSections)
        {
            if (sec.LangName.Equals("Traditional Chinese", StringComparison.OrdinalIgnoreCase))
            {
                // 在原版 Traditional Chinese 的位置输出中文
                if (targetChineseContent != null && targetChineseContent.Count > 0)
                {
                    output.Add("\tlang \"Traditional Chinese\"");
                    output.AddRange(targetChineseContent);
                    hasEmittedChinese = true;
                }
            }
            else if (sec.LangName.Equals("Simplified Chinese", StringComparison.OrdinalIgnoreCase))
            {
                // 如果之前没有遇到 Traditional Chinese（例如该块原版只有 SC），则在 SC 位置输出
                if (!hasEmittedChinese && targetChineseContent != null && targetChineseContent.Count > 0)
                {
                    output.Add("\tlang \"Traditional Chinese\"");
                    output.AddRange(targetChineseContent);
                    hasEmittedChinese = true;
                }
                // 否则已在 TC 位置输出过，此处静默跳过，避免重复
            }
            else
            {
                // 其他语言（Russian, German, French, etc.）原位输出
                output.Add($"\tlang \"{sec.LangName}\"");
                output.AddRange(sec.Lines);
            }
        }
    }

    /// <summary>
    /// 提取一组文本行里出现过的 &lt;rgb(r,g,b)&gt; 开标签，按出现顺序去重返回。
    ///
    /// 用于与词缀染色补丁合并：染色把颜色标签注入繁中段，
    /// 翻译用原生简中段覆盖时会丢掉它们，这里先把标签捞出来再重新套上。
    /// </summary>
    private static List<string> ExtractColorTags(IReadOnlyList<string> lines)
    {
        var tags = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string line in lines)
        {
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(line, @"<rgb\([^)]*\)>"))
            {
                if (seen.Add(m.Value)) tags.Add(m.Value);
            }
        }

        return tags;
    }

    /// <summary>
    /// 把保留下来的颜色标签重新套到替换后的文本行上。
    ///
    /// 逐行对齐：第 n 个内容行套第 n 个标签；标签少于行数时按顺序循环复用，
    /// 多于行数时多余标签丢弃。只包裹纯文本部分，行首缩进与引号保持在标签之外，
    /// 与染色补丁自身产出的格式一致（<c>\t\t# "&lt;rgb(..)&gt;文本&lt;/rgb&gt;"</c>）。
    /// </summary>
    private static List<string> ReapplyColorTags(List<string> content, List<string> tags)
    {
        if (tags.Count == 0) return content;

        var result = new List<string>(content.Count);
        for (int i = 0; i < content.Count; i++)
        {
            string line = content[i];
            string tag = tags[i % tags.Count];

            // 已带颜色标签（重复执行）则原样保留，保证幂等
            if (line.Contains("<rgb(", StringComparison.Ordinal))
            {
                result.Add(line);
                continue;
            }

            int quoteStart = line.IndexOf('"');
            int quoteEnd = line.LastIndexOf('"');
            if (quoteStart < 0 || quoteEnd <= quoteStart)
            {
                result.Add(line);
                continue;
            }

            string prefix = line.Substring(0, quoteStart + 1);
            string inner = line.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
            string suffix = line.Substring(quoteEnd);

            if (string.IsNullOrWhiteSpace(inner))
            {
                result.Add(line);
                continue;
            }

            // CSD 的词缀染色语法是 <rgb(r,g,b)>{{文本}}，没有 </rgb> 闭合标签。
            // 官方 .csd 里不存在 </rgb>（全仓仅此一处误写），染色补丁写入的也是
            // {{...}} 形式。原实现用 </rgb> 收尾且丢了 {{}} 包裹，
            // 游戏无法识别这两个改动，词缀颜色与文本都会异常。
            const string openWrap = "{{";
            const string closeWrap = "}}";
            result.Add($"{prefix}{tag}{openWrap}{inner}{closeWrap}{suffix}");
        }

        return result;
    }

    private static bool TryGetMissingRule(
        IReadOnlyDictionary<string, string> dict,
        string statKey,
        out string content)
    {
        if (dict.TryGetValue(statKey, out content!) && !string.IsNullOrWhiteSpace(content))
        {
            return true;
        }

        int space = statKey.IndexOf(' ');
        if (space > 0)
        {
            string nameOnly = statKey.Substring(space + 1).Trim();
            if (dict.TryGetValue(nameOnly, out content!) && !string.IsNullOrWhiteSpace(content))
            {
                return true;
            }
        }

        content = string.Empty;
        return false;
    }
}
