namespace FreiAtlas.Patcher.EffectPatch;

/// <summary>
/// 大括号块的定位与清空。
///
/// 为什么不用正则：平衡组正则虽然能做，但跳过字符串与注释的逻辑藏在正则语法里，
/// 难以验证、难以调试，回溯行为也不可控。逐字符状态机只需要一次遍历，
/// 逻辑完全可见，边界情况容易写测试。
///
/// 参考实现来自 easyfarm.node（Rust，src/text_patch.rs）的正则语义，
/// 用状态机重新实现了同样的行为。
/// </summary>
public static class EffectBlockScanner
{
    /// <summary>
    /// 扫描结果中的一个块。
    /// </summary>
    public readonly record struct BlockSpan(
        string Name,
        int NameStart,      // 块名首字符位置
        int OpenBracePos,   // 开括号 { 位置
        int CloseBracePos)  // 配平后的闭括号 } 位置
    {
        /// <summary>块内容区间（不含括号本身）。</summary>
        public (int Start, int Length) InnerSpan => (OpenBracePos + 1, CloseBracePos - OpenBracePos - 1);
    }

    /// <summary>
    /// 在文本中找出所有指定名称的块。
    ///
    /// 只匹配「行首（允许前导空白）的标识符后跟 {」这种形态，
    /// 避免把赋值语句右侧的对象字面量误判成块。
    /// </summary>
    public static List<BlockSpan> FindBlocks(string text, IReadOnlySet<string> blockNames)
    {
        var results = new List<BlockSpan>();
        var state = new ScanState(text);
        state.Run(blockNames, results);
        return results;
    }

    /// <summary>
    /// 把指定块的内容清空，保留空括号。
    ///
    /// 不能删除整块：游戏引擎按 Schema 寻址属性节点，节点不存在会抛断言失败。
    /// 正确形态是 ParticleEffects {} 而不是把 ParticleEffects {...} 整段删掉。
    /// </summary>
    /// <returns>清空后的文本；没有匹配到块时返回原文。</returns>
    public static string ClearBlockContents(string text, IReadOnlySet<string> blockNames)
    {
        var blocks = FindBlocks(text, blockNames);
        if (blocks.Count == 0) return text;

        // 从后往前改，避免前面的修改影响后面块的位置
        var builder = new System.Text.StringBuilder(text);
        foreach (var block in blocks.OrderByDescending(b => b.OpenBracePos))
        {
            var (start, length) = block.InnerSpan;
            if (length <= 0) continue;   // 已经是空块，跳过
            builder.Remove(start, length);
        }
        return builder.ToString();
    }

    /// <summary>
    /// 逐字符扫描器。
    ///
    /// 关键状态：
    ///   - inString：是否在 "" 内，字符串内的括号不参与深度计数
    ///   - inLineComment：是否在 // 之后，注释内的括号不参与
    ///   - inBlockComment：是否在 /* */ 内
    ///   - escaped：当前字符是否被 \ 转义
    /// </summary>
    private sealed class ScanState(string text)
    {
        private readonly string _text = text;
        private int _pos;

        public void Run(IReadOnlySet<string> blockNames, List<BlockSpan> results)
        {
            while (_pos < _text.Length)
            {
                if (!TryMatchBlockStart(blockNames, out var name, out var nameStart))
                {
                    SkipOne();
                    continue;
                }

                // 找到了块开头，从开括号开始找配平的闭括号
                int openBrace = _pos;   // 此时 _pos 指向 {
                int closeBrace = EffectTextScanner.FindMatchingBrace(_text, openBrace);
                if (closeBrace < 0)
                {
                    // 未配平：不记录，继续往后扫（后面可能还有正常的块）
                    _pos = openBrace + 1;
                    continue;
                }

                results.Add(new BlockSpan(name, nameStart, openBrace, closeBrace));
                _pos = closeBrace + 1;
            }
        }

        /// <summary>
        /// 尝试在当前位置匹配「行首标识符 + 空白 + {」。
        /// 匹配成功时 _pos 停在 { 上。
        /// </summary>
        private bool TryMatchBlockStart(IReadOnlySet<string> blockNames, out string name, out int nameStart)
        {
            name = string.Empty;
            nameStart = -1;

            // 跳过行首空白
            int p = _pos;
            while (p < _text.Length && (_text[p] == ' ' || _text[p] == '\t')) p++;

            // 必须在行首（或文本开头）。
            // 往回看时要跨过前导空白：缩进的块（如 client { ParticleEffects } 里的内层块）
            // 前面有空格，直接判断 p-1 是否为换行会漏掉它们。
            int back = p;
            while (back > 0 && (_text[back - 1] == ' ' || _text[back - 1] == '\t')) back--;
            if (back > 0 && _text[back - 1] != '\n' && _text[back - 1] != '\r') return false;
            if (p >= _text.Length) return false;

            // 读标识符
            int idStart = p;
            while (p < _text.Length && (char.IsLetterOrDigit(_text[p]) || _text[p] == '_')) p++;
            if (p == idStart) return false;

            string identifier = _text[idStart..p];
            if (!blockNames.Contains(identifier)) return false;

            // 标识符后跳到 {：中间允许空白、换行、行注释、块注释。
            //
            // 游戏文件里块名与开括号几乎总是分行写的：
            //     ParticleEffects
            //     {
            // 只跳空格不跳换行会导致完全匹配不到。
            //
            // 注释也要跳过。虽然实测 200 个 .ao 样本里没有这种写法，但
            // 「ParticleEffects // 注释\n{」是合法语法，漏掉会静默失配。
            p = SkipTrivia(p);
            if (p >= _text.Length || _text[p] != '{') return false;

            name = identifier;
            nameStart = idStart;
            _pos = p;
            return true;
        }

        /// <summary>
        /// 跳过空白、换行、行注释与块注释，返回第一个有效字符的位置。
        /// 用于「块名」与「{」之间的间隙处理。
        /// </summary>
        private int SkipTrivia(int pos)
        {
            int p = pos;
            while (p < _text.Length)
            {
                char c = _text[p];

                if (c is ' ' or '\t' or '\r' or '\n')
                {
                    p++;
                    continue;
                }

                if (c == '/' && p + 1 < _text.Length)
                {
                    if (_text[p + 1] == '/')
                    {
                        p += 2;
                        while (p < _text.Length && _text[p] != '\n' && _text[p] != '\r') p++;
                        continue;
                    }
                    if (_text[p + 1] == '*')
                    {
                        p += 2;
                        while (p + 1 < _text.Length
                               && !(_text[p] == '*' && _text[p + 1] == '/')) p++;
                        p = Math.Min(p + 2, _text.Length);
                        continue;
                    }
                }

                break;
            }
            return p;
        }



        /// <summary>跳过当前位置的一个字符。用于非块头位置的推进。</summary>
        private void SkipOne()
        {
            char c = _text[_pos];

            // 跳过字符串，避免把字符串里的内容当成块头。单引号同上：必须整体跳过。
            if (c == '"' || c == '\'')
            {
                char quote = c;
                _pos++;
                bool escaped = false;
                while (_pos < _text.Length)
                {
                    char s = _text[_pos];
                    if (escaped) { escaped = false; _pos++; continue; }
                    if (s == '\\') { escaped = true; _pos++; continue; }
                    if (s == quote) { _pos++; return; }
                    _pos++;
                }
                return;
            }

            // 跳过行注释
            if (c == '/' && _pos + 1 < _text.Length && _text[_pos + 1] == '/')
            {
                _pos += 2;
                while (_pos < _text.Length && _text[_pos] != '\n' && _text[_pos] != '\r') _pos++;
                return;
            }

            // 跳过块注释
            if (c == '/' && _pos + 1 < _text.Length && _text[_pos + 1] == '*')
            {
                _pos += 2;
                while (_pos + 1 < _text.Length
                       && !(_text[_pos] == '*' && _text[_pos + 1] == '/')) _pos++;
                _pos = Math.Min(_pos + 2, _text.Length);
                return;
            }

            _pos++;
        }
    }
}
