namespace FreiAtlas.Patcher.EffectPatch;

/// <summary>
/// 写入前的语法门禁。
///
/// 修改游戏脚本与着色器，一旦结构被破坏客户端就会崩溃。这里做最后一道检查：
/// 括号配平、引号配对。校验不过就拒绝写入，宁可让补丁无效，也不能让客户端起不来。
/// </summary>
public static class EffectSyntaxValidator
{
    /// <summary>
    /// 校验结果。
    /// </summary>
    public readonly record struct Result(bool IsValid, string? Error)
    {
        public static Result Ok => new(true, null);
        public static Result Fail(string error) => new(false, error);
    }

    /// <summary>
    /// 大括号与引号配平校验。
    ///
    /// 跳过字符串与注释内的符号——与 <see cref="EffectBlockScanner"/> 用同一套规则，
    /// 保证「扫描时的理解」与「校验时的理解」一致。
    /// </summary>
    public static Result Validate(string text, string virtualPath)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Result.Fail($"{virtualPath}: 内容为空");
        }

        // .ao 是「多段文档拼接」格式，不是单一括号层级。
        // 实测 metadata/effects/spells/ascendancy_classes/pathfinder/explosion_cold.ao：
        //   {  skin = "..."  }        ← 一段内联配置
        //   BoneGroups { ... }
        //   ParticleEffects { animations = '[...]' }
        //   }                          ← 第一段文档结束
        //   version 3                  ← 第二段文档开始
        //   extends "..."
        // 按整文件做括号配平必然在第 44 行报「多余的 }」，而这个文件
        // 客户端能正常加载。因此对 .ao 只校验引号与注释闭合，
        // 不校验括号层级 —— 层级错了客户端自己会拒绝，这里拦下来只是误报。
        if (virtualPath.EndsWith(".ao", StringComparison.OrdinalIgnoreCase)
            || virtualPath.EndsWith(".aoc", StringComparison.OrdinalIgnoreCase))
        {
            return ValidateQuotesOnly(text, virtualPath);
        }

        int braceDepth = 0;
        int parenDepth = 0;
        bool inDoubleString = false;
        bool inSingleString = false;
        bool inLineComment = false;
        bool inBlockComment = false;
        bool escaped = false;
        int line = 1;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n') line++;

            if (inLineComment)
            {
                if (c == '\n') inLineComment = false;
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

            // 双引号字符串：**允许跨行**。
            // PoE 的 .ot 里有这种合法写法：
            //     define_shared_state =
            //     "
            //         weapon_health = 3;
            //         emerged;
            //     "
            // 按 C 风格「字符串不能换行」判会误报 179 个合法文件为坏文件，
            // 导致整批规则被中止。只在文件末尾仍未闭合才算错。
            if (inDoubleString)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') inDoubleString = false;
                continue;
            }

            // 单引号内容：内部的双引号与大括号都不参与配对。
            // .ao 里常见 animations = '[ { "name": "once" } ]' 这种跨行 JSON。
            if (inSingleString)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '\'') inSingleString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inDoubleString = true;
                    break;
                case '\'':
                    inSingleString = true;
                    break;
                case '/':
                    if (i + 1 < text.Length)
                    {
                        if (text[i + 1] == '/') { inLineComment = true; i++; }
                        else if (text[i + 1] == '*') { inBlockComment = true; i++; }
                    }
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    braceDepth--;
                    if (braceDepth < 0)
                    {
                        return Result.Fail($"{virtualPath}: 第 {line} 行出现多余的 }}");
                    }
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    parenDepth--;
                    if (parenDepth < 0)
                    {
                        return Result.Fail($"{virtualPath}: 第 {line} 行出现多余的 )");
                    }
                    break;
            }
        }

        if (inDoubleString) return Result.Fail($"{virtualPath}: 文件结束时双引号仍未闭合");
        if (inBlockComment) return Result.Fail($"{virtualPath}: 文件结束时块注释仍未闭合");
        if (braceDepth != 0) return Result.Fail($"{virtualPath}: 大括号未配平（剩余 {braceDepth} 个未闭合）");
        if (parenDepth != 0) return Result.Fail($"{virtualPath}: 小括号未配平（剩余 {parenDepth} 个未闭合）");

        return Result.Ok;
    }

    /// <summary>
    /// 只校验引号与注释的闭合，不校验括号层级。
    ///
    /// 用于 .ao 这类多段文档拼接的格式：括号层级不成立是格式本身的性质，
    /// 但引号未闭合一定是真的被改坏了。
    /// </summary>
    private static Result ValidateQuotesOnly(string text, string virtualPath)
    {
        bool inDoubleString = false;
        bool inSingleString = false;
        bool inLineComment = false;
        bool inBlockComment = false;
        bool escaped = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (inLineComment)
            {
                if (c == '\n') inLineComment = false;
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

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                continue;
            }

            if (inDoubleString)
            {
                if (c == '"') inDoubleString = false;
                continue;
            }

            if (inSingleString)
            {
                if (c == '\'') inSingleString = false;
                continue;
            }

            if (c == '"') { inDoubleString = true; continue; }
            if (c == '\'') { inSingleString = true; continue; }

            if (c == '/')
            {
                if (i + 1 < text.Length)
                {
                    if (text[i + 1] == '/') { inLineComment = true; i++; }
                    else if (text[i + 1] == '*') { inBlockComment = true; i++; }
                }
            }
        }

        if (inDoubleString) return Result.Fail($"{virtualPath}: 文件结束时双引号仍未闭合");
        if (inSingleString) return Result.Fail($"{virtualPath}: 文件结束时单引号仍未闭合");
        if (inBlockComment) return Result.Fail($"{virtualPath}: 文件结束时块注释仍未闭合");

        return Result.Ok;
    }

    /// <summary>
    /// 针对 HLSL 的额外校验：检查着色器入口函数是否仍然存在。
    ///
    /// 着色器文件头部的 //PRECOMPILE 指令按函数名编译，函数被删掉会导致编译失败。
    /// </summary>
    public static Result ValidateHlslEntryPoints(string text, string virtualPath)
    {
        // 提取所有 //PRECOMPILE ps_all <FuncName> 指令
        var requireFunctions = new List<string>();
        foreach (var rawLine in text.Split('\n'))
        {
            // 必须两端都 Trim：文件是 CRLF 换行，只 TrimStart 会把行尾的 \r
            // 留在函数名里，正则变成 \bBloomCutoff\r\s*\( 永远匹配不到。
            var line = rawLine.Trim();
            if (!line.StartsWith("//PRECOMPILE", StringComparison.Ordinal)) continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // 形如：//PRECOMPILE ps_all BloomCutoff [DST_FP32 1]
            if (parts.Length >= 3)
            {
                requireFunctions.Add(parts[2]);
            }
        }

        foreach (var func in requireFunctions.Distinct(StringComparer.Ordinal))
        {
            // 函数定义形如：float4 BloomCutoff( ... )
            bool defined = System.Text.RegularExpressions.Regex.IsMatch(
                text,
                $@"\b{System.Text.RegularExpressions.Regex.Escape(func)}\s*\(",
                System.Text.RegularExpressions.RegexOptions.None,
                TimeSpan.FromSeconds(5));

            if (!defined)
            {
                return Result.Fail($"{virtualPath}: PRECOMPILE 引用的入口函数 {func} 在文件中不存在");
            }
        }

        return Result.Ok;
    }

    /// <summary>
    /// 按文件类型选择合适的校验组合。
    /// </summary>
    public static Result ValidateFor(string text, string virtualPath)
    {
        var basic = Validate(text, virtualPath);
        if (!basic.IsValid) return basic;

        if (virtualPath.EndsWith(".hlsl", StringComparison.OrdinalIgnoreCase))
        {
            return ValidateHlslEntryPoints(text, virtualPath);
        }

        return Result.Ok;
    }
}
