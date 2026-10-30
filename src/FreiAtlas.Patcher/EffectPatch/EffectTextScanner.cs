namespace FreiAtlas.Patcher.EffectPatch;

/// <summary>
/// 特效文本的括号配对扫描。
///
/// 为什么必须只有一份：<c>.ao</c> / <c>.ot</c> 里 <c>animations = '[...]'</c>
/// 用单引号包裹 JSON 数组，内部全是花括号；只认双引号的实现会把它们当块边界，
/// 配平算错后清空/还原会留下多余的 <c>}</c>，客户端拒绝加载。
///
/// 此前 <see cref="EffectBlockScanner"/>、<see cref="EffectRuleApplier"/>、
/// <see cref="SharedFileRestore"/> 各有一份拷贝，对 <c>\r</c> 换行与单引号的处理
/// 已经分叉：同一份文件在不同代码路径上会得到不同的块边界。
/// 统一到这里，任何修正只需改一处。
/// </summary>
public static class EffectTextScanner
{
    /// <summary>
    /// 从开括号位置找配平的闭括号位置。找不到返回 -1。
    ///
    /// 字符串（单/双引号）、行注释、块注释内的括号不参与计数；
    /// 行注释同时识别 <c>\n</c> 与 <c>\r</c>（CR-only 换行的文件也必须正确结束）。
    /// </summary>
    public static int FindMatchingBrace(string text, int openBracePosition)
    {
        if (string.IsNullOrEmpty(text)) return -1;
        if (openBracePosition < 0 || openBracePosition >= text.Length) return -1;

        int depth = 0;
        bool inString = false;
        char stringQuote = '\0';
        bool inLineComment = false;
        bool inBlockComment = false;

        for (int i = openBracePosition; i < text.Length; i++)
        {
            char c = text[i];

            if (inLineComment)
            {
                if (c == '\n' || c == '\r') inLineComment = false;
                continue;
            }

            if (inBlockComment)
            {
                if (c == '*' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    inBlockComment = false;
                    i++;
                }
                continue;
            }

            if (inString)
            {
                // 反斜杠转义只对下一个字符生效，不能跳过后继续判定引号。
                if (c == '\\')
                {
                    i++;
                    continue;
                }
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
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0) return i;
                    break;
            }
        }

        return -1;
    }
}
