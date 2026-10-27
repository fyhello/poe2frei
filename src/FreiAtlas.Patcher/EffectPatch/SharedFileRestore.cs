using System.Text.RegularExpressions;

namespace FreiAtlas.Patcher.EffectPatch;

/// <summary>
/// 「共享文件」的定点还原工具。
///
/// 背景：<c>metadata/characters/character.ot</c> 会被两个补丁写入 ——
/// 视距调整往 <c>Positioned</c> 块注入 <c>on_initial_position_set</c>，
/// MTX 翻滚往 <c>BaseEvents</c> 块注入 <c>AddEffectPack</c>。
///
/// 两者使用各自独立的逐文件备份组（<c>effect_patch</c> 与 <c>mtx_patch_v1</c>），
/// 若还原时整文件覆盖写回，会把另一个补丁的改动一起抹掉，形成「幽灵复活」：
/// 还原 A 之后 B 消失，再还原 B 又把 A 带回来。
///
/// 因此对这类共享文件，还原改为「只移除本补丁注入的内容」。
///
/// 实测依据：客户端原始 <c>character.ot</c>（5,898 字符）中
/// <c>on_initial_position_set</c>、<c>on_construction_complete</c>、
/// <c>AddEffectPack</c> 出现次数均为 <b>0</b>，
/// 因此这些行必然是补丁注入的，移除它们不会破坏原始内容。
/// </summary>
public static class SharedFileRestore
{
    /// <summary>
    /// 共享文件判定：该路径是否需要走定点还原而非整文件写回。
    /// </summary>
    public static bool IsSharedFile(string virtualPath)
        => !string.IsNullOrEmpty(virtualPath)
           && virtualPath.Replace('\\', '/')
               .EndsWith("/characters/character.ot", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 对共享文件执行定点还原：从当前内容里移除本补丁注入的行与空块。
    ///
    /// 供 EffectPatchApplier（视距）与 MtxPatchApplier（翻滚）共用，
    /// 保证两侧行为完全对称。
    /// </summary>
    /// <param name="currentBytes">客户端当前文件字节。</param>
    /// <param name="virtualPath">虚拟路径，用于编码检测与校验。</param>
    /// <param name="linePattern">本补丁注入行的模式（<see cref="ViewDistanceLine"/> 或 <see cref="DodgeRollLine"/>）。</param>
    /// <param name="cleanedBytes">还原后的字节；无变化时为 null。</param>
    /// <param name="error">失败原因；成功时为 null。</param>
    /// <returns>是否得到了可写入的结果。</returns>
    public static bool TryRestoreSharedFile(
        byte[] currentBytes,
        string virtualPath,
        string linePattern,
        out byte[]? cleanedBytes,
        out string? error)
    {
        cleanedBytes = null;
        error = null;

        if (currentBytes is null || currentBytes.Length == 0)
        {
            error = "文件内容为空";
            return false;
        }

        string currentText;
        EffectTextEncoding encoding;
        try
        {
            encoding = EffectTextCodec.Detect(currentBytes, virtualPath);
            currentText = EffectTextCodec.Decode(currentBytes, encoding);
        }
        catch (Exception ex)
        {
            error = $"解码失败：{ex.Message}";
            return false;
        }

        if (string.IsNullOrEmpty(currentText))
        {
            error = "解码结果为空";
            return false;
        }

        string cleaned = RemoveMatchingLines(currentText, linePattern);
        cleaned = RemoveEmptyInjectedBlocks(cleaned);

        if (string.Equals(cleaned, currentText, StringComparison.Ordinal))
        {
            // 当前内容已无本补丁痕迹，无需写入
            return true;
        }

        var validation = EffectSyntaxValidator.ValidateFor(cleaned, virtualPath);
        if (!validation.IsValid)
        {
            error = $"定点还原后语法校验失败：{validation.Error}";
            return false;
        }

        cleanedBytes = EffectTextCodec.Encode(cleaned, encoding);
        return true;
    }

    /// <summary>
    /// 从文件内容中移除指定正则匹配的整行（含行尾换行）。
    ///
    /// 只删整行，不做局部替换，避免破坏相邻结构与缩进。
    /// </summary>
    /// <param name="content">当前文件内容。</param>
    /// <param name="linePattern">要移除的行模式（不需要写 ^ $ 与换行，由本方法补全）。</param>
    /// <returns>移除后的内容；无匹配时原样返回。</returns>
    public static string RemoveMatchingLines(string content, string linePattern)
    {
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(linePattern))
        {
            return content;
        }

        Regex regex;
        try
        {
            // 整行锚定，逐行判断。不能用 (?m) + 拼接换行：
            // 当 linePattern 内部含 [ \t]* 这类可跨行的量词时，
            // (?m) 下的匹配只吃掉行首一段，会删成残行
            // （实测留下过 "1000000000.0f, 1000000000.0f, 2.0f); }"）。
            regex = new Regex(
                $@"^[ \t]*(?:{linePattern})[ \t]*$",
                RegexOptions.None,
                TimeSpan.FromSeconds(5));
        }
        catch (RegexParseException)
        {
            return content;
        }

        string newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string[] lines = content.Split(["\r\n", "\n"], StringSplitOptions.None);

        var kept = new List<string>(lines.Length);
        bool removed = false;

        foreach (string line in lines)
        {
            if (regex.IsMatch(line))
            {
                removed = true;
                continue;
            }
            kept.Add(line);
        }

        return removed ? string.Join(newline, kept) : content;
    }

    /// <summary>
    /// 判断内容里是否还残留指定模式的注入行。
    /// </summary>
    public static bool ContainsInjectedLine(string content, string linePattern)
    {
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(linePattern))
        {
            return false;
        }

        try
        {
            return Regex.IsMatch(content, $@"(?m)^[ \t]*(?:{linePattern})", RegexOptions.None, TimeSpan.FromSeconds(5));
        }
        catch (RegexParseException)
        {
            return false;
        }
    }

    /// <summary>
    /// 移除因注入行被删而留下的空块。
    ///
    /// MTX 翻滚注入会在 <c>client</c> 块里新建
    /// <c>BaseEvents { on_construction_complete = { AddEffectPack(...); } }</c>，
    /// 移除 AddEffectPack 后会留下两层空壳。原始 character.ot 完全没有
    /// <c>BaseEvents</c> 与 <c>on_construction_complete</c>（实测均为 0 次），
    /// 因此空壳可以整体删除。
    ///
    /// 用括号配平逐字符扫描，避免正则处理嵌套花括号不可靠。
    /// </summary>
    public static string RemoveEmptyInjectedBlocks(string content)
    {
        if (string.IsNullOrEmpty(content)) return content;

        string result = content;
        result = RemoveEmptyBlockByName(result, "on_construction_complete");
        result = RemoveEmptyBlockByName(result, "BaseEvents");
        return result;
    }

    /// <summary>
    /// 删除「块名为指定名称、且块内除空白外无内容」的块（含块名与花括号）。
    /// </summary>
    private static string RemoveEmptyBlockByName(string content, string blockName)
    {
        Regex nameRegex;
        try
        {
            nameRegex = new Regex(
                $@"(?m)^[ \t]*{Regex.Escape(blockName)}[ \t]*=?[ \t]*\r?\n?[ \t]*\{{",
                RegexOptions.None,
                TimeSpan.FromSeconds(5));
        }
        catch (RegexParseException)
        {
            return content;
        }

        var sb = new System.Text.StringBuilder(content.Length);
        int cursor = 0;

        while (cursor < content.Length)
        {
            var m = nameRegex.Match(content, cursor);
            if (!m.Success) break;

            int openBrace = content.IndexOf('{', m.Index);
            if (openBrace < 0) break;

            int closeBrace = FindMatchingBrace(content, openBrace);
            if (closeBrace < 0) break;

            string inner = content.Substring(openBrace + 1, closeBrace - openBrace - 1);

            if (inner.Trim().Length == 0)
            {
                int start = m.Index;
                int end = closeBrace + 1;
                while (end < content.Length && (content[end] == '\r' || content[end] == '\n')) end++;

                sb.Append(content, cursor, start - cursor);
                cursor = end;
            }
            else
            {
                sb.Append(content, cursor, closeBrace + 1 - cursor);
                cursor = closeBrace + 1;
            }
        }

        sb.Append(content, cursor, content.Length - cursor);
        return sb.ToString();
    }

    /// <summary>
    /// 从开括号位置找配平的闭括号。字符串与注释内的括号不参与计数。
    ///
    /// 单引号必须识别：.ao 里的 <c>animations = '[...]'</c> 用单引号包裹
    /// JSON 数组，内部全是花括号，漏判会让块边界错位、还原时删错范围。
    /// </summary>
    private static int FindMatchingBrace(string text, int openBrace)
    {
        int depth = 0;
        bool inString = false;
        char stringQuote = '\0';
        bool inLineComment = false;
        bool inBlockComment = false;

        for (int i = openBrace; i < text.Length; i++)
        {
            char c = text[i];

            if (inLineComment)
            {
                if (c == '\n') inLineComment = false;
                continue;
            }
            if (inBlockComment)
            {
                if (c == '*' && i + 1 < text.Length && text[i + 1] == '/') { inBlockComment = false; i++; }
                continue;
            }
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == stringQuote) inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                case '\'':
                    inString = true;
                    stringQuote = c;
                    break;
                case '/':
                    if (i + 1 < text.Length)
                    {
                        if (text[i + 1] == '/') { inLineComment = true; i++; }
                        else if (text[i + 1] == '*') { inBlockComment = true; i++; }
                    }
                    break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return i;
                    break;
            }
        }

        return -1;
    }

    /// <summary>
    /// 视距补丁注入的行模式。
    ///
    /// 注入形态（实测）：
    /// <c>\ton_initial_position_set = { CreateCameraZoomNode(1000000000.0f, 1000000000.0f, 2.0f); }</c>
    /// 必须一直匹配到行尾的 <c>}</c>，否则整行锚定会失败。
    /// </summary>
    public const string ViewDistanceLine =
        @"on_initial_position_set\s*=\s*\{[^\r\n]*CreateCameraZoomNode[^\r\n]*\}[^\r\n]*";

    /// <summary>
    /// MTX 翻滚补丁注入的行模式（只匹配本模块注入的 AddEffectPack 行）。
    /// </summary>
    public const string DodgeRollLine =
        @"AddEffectPack\(\s*'[^']*\.epk'\s*\);[^\r\n]*";

    /// <summary>
    /// 视距补丁注入后可能留下的空块（注入前原文件没有 on_construction_complete）。
    /// 这里只处理 AddEffectPack 被移除后留下的空 on_construction_complete 块。
    /// </summary>
    public const string EmptyConstructionBlock =
        @"on_construction_complete\s*=\s*\{\s*\}";
}
