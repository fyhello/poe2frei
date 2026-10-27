using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FreiAtlas.Patcher.EffectPatch;

/// <summary>
/// 把规则应用到单个文件的内容上。
///
/// 职责边界：只做「文本 → 文本」的变换，不读写客户端、不碰索引。
/// 这样这一层可以完全用单元测试覆盖，不需要真实客户端。
/// </summary>
public static class EffectRuleApplier
{
    /// <summary>
    /// 单条规则的应用结果。
    /// </summary>
    public readonly record struct ApplyOutcome(
        bool Changed,
        string Content,
        string? Error,
        bool SkippedByIdempotency)
    {
        public static ApplyOutcome Unchanged(string content) => new(false, content, null, false);
        public static ApplyOutcome Fail(string content, string error) => new(false, content, error, false);
        public static ApplyOutcome Skip(string content) => new(false, content, null, true);
    }

    /// <summary>
    /// 对一份内容应用一组规则。
    ///
    /// 执行顺序按 <see cref="EffectActionExtensions.Priority"/> 升序：
    /// ReplaceCode(20) → ClearBlock(20) → RemoveObject(30)。
    /// 顺序不能靠调用方保证——先删键会让后续替换目标消失。
    /// </summary>
    public static (string Content, List<(EffectPatchRule Rule, ApplyOutcome Outcome)> Outcomes) ApplyAll(
        string originalContent,
        IEnumerable<EffectPatchRule> rules)
    {
        string content = originalContent;
        var outcomes = new List<(EffectPatchRule, ApplyOutcome)>();

        foreach (var rule in rules.OrderBy(r => r.Action.Priority()))
        {
            var outcome = Apply(content, rule);
            outcomes.Add((rule, outcome));

            if (outcome.Error is not null) continue;   // 出错保留上一版内容，继续下一条
            if (outcome.Changed) content = outcome.Content;
        }

        return (content, outcomes);
    }

    /// <summary>应用单条规则。</summary>
    public static ApplyOutcome Apply(string content, EffectPatchRule rule)
    {
        try
        {
            return rule.Action switch
            {
                EffectAction.ReplaceCode => ApplyReplaceCode(content, rule),
                EffectAction.ClearBlock => ApplyClearBlock(content, rule),
                EffectAction.AppendToBlock => ApplyAppendToBlock(content, rule),
                EffectAction.FogDisable => ApplyFogDisable(content, rule),
                EffectAction.RemoveObject => ApplyRemoveObject(content, rule),
                _ => ApplyOutcome.Fail(content, $"未知动作：{rule.Action}")
            };
        }
        catch (Exception ex)
        {
            return ApplyOutcome.Fail(content, $"{rule.Id}: {ex.Message}");
        }
    }


    /// <summary>
    /// 该动作是否只需要路径、不需要文件内容。
    ///
    /// 泥人贴图是纯替换：名单内给预置块，名单外给固定灰色块，
    /// 读原文件毫无用处。规划阶段据此跳过读取，几万张图能省下几十 GB 的读取。
    /// </summary>
    public static bool IsPathOnlyAction(EffectAction action)
        => action == EffectAction.MudManTexture;

    /// <summary>
    /// 二进制动作的应用入口。
    ///
    /// DDS 是二进制，不能按 UTF-16 解码再编码 —— 那样整个文件会变成乱码。
    /// 这里直接对字节做变换。
    /// </summary>
    public static (bool Changed, byte[] Content, string? Error) ApplyBytes(byte[] content, EffectPatchRule rule)
    {
        try
        {
            switch (rule.Action)
            {
                default:
                    return (false, content, $"{rule.Id}: {rule.Action} 不是二进制动作");
            }
        }
        catch (Exception ex)
        {
            return (false, content, $"{rule.Id}: {ex.Message}");
        }
    }

    /// <summary>
    /// 只看路径、不看内容的动作入口。
    ///
    /// 用于 <see cref="EffectAction.MudManTexture"/>：替换字节完全由路径决定，
    /// 调用方因此可以跳过读取原文件。返回 null 表示该路径不处理。
    /// </summary>
    public static byte[]? ResolveByPath(string virtualPath, EffectPatchRule rule)
    {
        if (rule.Action != EffectAction.MudManTexture) return null;
        return MudMan.GrayDdsMaker.GetReplacement(virtualPath);
    }

    private static ApplyOutcome ApplyReplaceCode(string content, EffectPatchRule rule)
    {
        // 幂等检测：内容里已有注入特征，说明补丁已应用。
        //
        // 已知局限：少数规则的哨兵是裸颜色值（如 minimap_exploring_color 的
        // "float4(r, g, b, a)"），若文件里恰好存在同色字面量，会被误判为已应用
        // 而跳过。该场景概率极低（需颜色值完全撞车），且改用「哨兵 + 正则」
        // 双重判定会破坏幂等语义 —— 因为 Replacement 产物本身也满足 TargetPattern，
        // 无法用正则区分「已应用」与「未应用」。详见第三轮评审报告 P1-3。
        if (rule.AppliedSentinel is not null
            && content.Contains(rule.AppliedSentinel, StringComparison.Ordinal))
        {
            return ApplyOutcome.Skip(content);
        }

        var regex = new Regex(rule.TargetPattern, RegexOptions.None, TimeSpan.FromSeconds(5));
        if (!regex.IsMatch(content))
        {
            return ApplyOutcome.Unchanged(content);
        }

        // 全局替换，不是只换第一处。
        //
        // 实测证据：minimap_blending_pixel.hlsl 里 res_color.rgb *= res_color.a;
        // 出现 2 次（位置 3393 与 4378，分属两个条件分支），只换第一处会让
        // 小地图补丁半失效。防误伤靠规则本身的上下文精度与幂等特征，而不是
        // 武断截断在第一次匹配。
        //
        // 替换方式按 ApplyMode 分三种：
        //
        //   Literal（默认）：Replacement 原样写入，用 MatchEvaluator 返回字面量，
        //     Replacement 里的 $ 不会被当成替换语法。既有规则全部走这条路。
        //
        //   FunctionBody：Replacement 按 Regex 替换语法展开，$1 引用第一个捕获组。
        //
        //   FunctionBodyBraces：同 FunctionBody，但函数右边界用括号配平确定。
        //     着色器函数体可达数万字符、内含 #if 块与行首无缩进的 }，
        //     正则无法可靠判断层级，必须逐字符数括号。
        var replaced = rule.ApplyMode switch
        {
            EffectApplyMode.FunctionBody => regex.Replace(content, rule.Replacement),
            EffectApplyMode.FunctionBodyBraces => ReplaceFunctionBody(content, regex, rule),
            _ => regex.Replace(content, _ => rule.Replacement)
        };
        return replaced == content
            ? ApplyOutcome.Unchanged(content)
            : new ApplyOutcome(true, replaced, null, false);
    }

    /// <summary>
    /// 替换函数体，函数右边界由括号配平确定。
    ///
    /// 正则只负责找到「函数签名 + 开括号」，右边界逐字符数括号得出。
    /// 这样不会被 <c>[\s\S]*?</c> 的非贪婪行为坑到 —— 实测某些着色器函数体内部
    /// 有行首无缩进的 <c>}</c> 和 #if 预处理块，正则无法可靠判断层级，
    /// 一定会吃过头（实测某个文件多删了 37,195 字符）或吃不够。
    /// </summary>
    private static string ReplaceFunctionBody(string content, Regex signatureRegex, EffectPatchRule rule)
    {
        var sb = new StringBuilder(content.Length);
        int cursor = 0;
        int replacedCount = 0;

        while (replacedCount <= 1000)
        {
            var m = signatureRegex.Match(content, cursor);
            if (!m.Success) break;

            // 签名匹配必须以开括号结尾
            var openIndex = m.Index + m.Length - 1;
            if (openIndex < 0 || openIndex >= content.Length || content[openIndex] != '{')
            {
                // 规则写法有误（签名里没含 {），不做任何改动
                return content;
            }

            var closeIndex = FindMatchingBrace(content, openIndex);
            if (closeIndex < 0)
            {
                // 括号不配平，宁可不动这个文件
                return content;
            }

            var signature = content.Substring(m.Index, m.Length);
            var body = ExpandReplacement(rule.Replacement, signature);

            sb.Append(content, cursor, m.Index - cursor);
            sb.Append(body);
            cursor = closeIndex + 1;
            replacedCount++;
        }

        if (replacedCount == 0) return content;
        sb.Append(content, cursor, content.Length - cursor);
        return sb.ToString();
    }

    /// <summary>
    /// 从开括号位置向后找配平的右括号，跳过字符串字面量与注释。
    ///
    /// 必须同时识别单引号：客户端 .ao 的 <c>animations = '[...]'</c> 用单引号
    /// 包裹 JSON 数组，内部全是花括号。只认双引号会让字符串里的括号参与配平，
    /// 导致这一块定位错乱。定位失败时调用方会放弃改动（不写坏文件），
    /// 但规则会静默失效，因此这里必须正确处理。
    /// </summary>
    private static int FindMatchingBrace(string s, int openIndex)
    {
        int depth = 0;
        bool inString = false, inLineComment = false, inBlockComment = false;
        char stringQuote = '\0';

        for (int i = openIndex; i < s.Length; i++)
        {
            char c = s[i];
            char next = i + 1 < s.Length ? s[i + 1] : '\0';

            if (inLineComment)
            {
                if (c == '\n') inLineComment = false;
                continue;
            }
            if (inBlockComment)
            {
                if (c == '*' && next == '/') { inBlockComment = false; i++; }
                continue;
            }
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == stringQuote) inString = false;
                continue;
            }

            if (c == '/' && next == '/') { inLineComment = true; i++; continue; }
            if (c == '/' && next == '*') { inBlockComment = true; i++; continue; }
            if (c == '"' || c == '\'') { inString = true; stringQuote = c; continue; }

            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// 展开 Replacement 里的 $1（函数签名），其余内容原样保留。
    /// 不做完整替换语法处理 —— 着色器代码里可能出现 $ 字面量。
    /// </summary>
    private static string ExpandReplacement(string replacement, string signature)
        => replacement.Replace("$1", signature, StringComparison.Ordinal);

    private static ApplyOutcome ApplyClearBlock(string content, EffectPatchRule rule)
    {
        // TargetPattern 是块名列表，用 | 分隔
        var names = new HashSet<string>(
            rule.TargetPattern.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.Ordinal);

        if (names.Count == 0)
        {
            return ApplyOutcome.Fail(content, $"{rule.Id}: 块名为空");
        }

        var cleared = EffectBlockScanner.ClearBlockContents(content, names);
        return cleared == content
            ? ApplyOutcome.Unchanged(content)
            : new ApplyOutcome(true, cleared, null, false);
    }

    /// <summary>
    /// 在指定块的闭合括号前追加内容，保留块内已有内容。
    ///
    /// 为什么需要这个动作：.ot 里的 Life 块有两种形态。
    /// 空块（<c>Life {}</c>）可以直接替换成带事件的版本；但 674 个怪物文件
    /// 在 Life 里写了业务逻辑（武器挂载、状态机），整体替换会破坏这些逻辑。
    /// 对它们只能追加 on_death / on_revive。
    ///
    /// 块边界用括号配平确定，不用正则 —— 理由同 ReplaceFunctionBody。
    /// </summary>
    private static ApplyOutcome ApplyAppendToBlock(string content, EffectPatchRule rule)
    {
        // 幂等：已注入过就跳过
        if (rule.AppliedSentinel is not null
            && content.Contains(rule.AppliedSentinel, StringComparison.Ordinal))
        {
            return ApplyOutcome.Skip(content);
        }

        var names = rule.TargetPattern
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0)
        {
            return ApplyOutcome.Fail(content, $"{rule.Id}: 块名为空");
        }

        var sb = new StringBuilder(content.Length + rule.Replacement.Length);
        int cursor = 0;
        int appended = 0;

        // 块头：块名与开括号可能分行、可能缩进。
        // 实测 686 个非空 Life 里只有 5 个是行首 `Life {`，其余 681 个是
        // `\tLife\n\t{` 这种分行写法 —— 正则必须容忍中间的空白与换行。
        //
        // 追加内容按行拆分，逐行判断「该键是否已存在于本块内」：
        // 实测 673 个非空 Life 里有 494 个已经写了 on_death、195 个有 on_spawned_dead。
        // 盲目追加会产生重复键，GEAL 对重复键的行为未定义，可能直接报错。
        var injectLines = rule.Replacement
            .Replace("\r\n", "\n").Split('\n')
            .Where(l => l.Trim().Length > 0)
            .ToList();

        var lineKeys = injectLines
            .Select(l =>
            {
                var mm = Regex.Match(l, @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=");
                return mm.Success ? mm.Groups[1].Value : null;
            })
            .ToList();

        foreach (var name in names)
        {
            var head = new Regex(
                @"(?m)^[ \t]*" + Regex.Escape(name) + @"[ \t]*\r?\n?[ \t]*\{",
                RegexOptions.None, TimeSpan.FromSeconds(5));

            while (cursor < content.Length)
            {
                var m = head.Match(content, cursor);
                if (!m.Success) break;

                var openIndex = m.Index + m.Length - 1;
                if (content[openIndex] != '{') break;

                var closeIndex = FindMatchingBrace(content, openIndex);
                if (closeIndex < 0) break;   // 括号不配平，不动这个块

                var blockBody = content.Substring(openIndex + 1, closeIndex - openIndex - 1);

                // 只追加块内尚不存在的键
                var toInject = new StringBuilder();
                for (int i = 0; i < injectLines.Count; i++)
                {
                    var key = lineKeys[i];
                    if (key != null)
                    {
                        var exists = Regex.IsMatch(blockBody,
                            @"(?m)^[ \t]*" + Regex.Escape(key) + @"[ \t]*=");
                        if (exists) continue;
                    }
                    toInject.Append(injectLines[i]).Append('\n');
                }

                sb.Append(content, cursor, closeIndex - cursor);
                if (toInject.Length > 0)
                {
                    sb.Append(toInject);
                    appended++;
                }
                cursor = closeIndex;
            }
        }

        if (appended == 0) return ApplyOutcome.Unchanged(content);
        sb.Append(content, cursor, content.Length - cursor);
        return new ApplyOutcome(true, sb.ToString(), null, false);
    }

    /// <summary>
    /// 关闭 .fxgraph 的屏幕空间雾。
    ///
    /// .fxgraph 是 JSON 节点图：nodes 数组里每个节点有 type / index / stage，
    /// links 数组按 type 名引用上下游。要禁用雾的输出，不能删节点 ——
    /// 下游连线会指向不存在的源，渲染图断裂。正确做法是把
    /// stage=PixelOutput 的 AlbedoColor 节点内容换掉，保持 index 与位置不变。
    ///
    /// TargetPattern 形如 "AlbedoColor|PixelOutput"：
    /// 前段是节点 type，后段是必须匹配的 stage。
    /// </summary>
    private static ApplyOutcome ApplyFogDisable(string content, EffectPatchRule rule)
    {
        if (rule.AppliedSentinel is not null
            && content.Contains(rule.AppliedSentinel, StringComparison.Ordinal))
        {
            return ApplyOutcome.Skip(content);
        }

        var parts = rule.TargetPattern.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return ApplyOutcome.Fail(content, $"{rule.Id}: TargetPattern 须为「节点类型|stage」");
        }
        var nodeType = parts[0];
        var stage = parts[1];

        JsonObject root;
        try
        {
            root = JsonNode.Parse(content, null, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            }) as JsonObject ?? throw new InvalidOperationException("顶层不是 JSON 对象");
        }
        catch (JsonException ex)
        {
            return ApplyOutcome.Fail(content, $"{rule.Id}: JSON 解析失败 - {ex.Message}");
        }

        if (root["nodes"] is not JsonArray nodes)
        {
            return ApplyOutcome.Fail(content, $"{rule.Id}: 没有 nodes 数组");
        }

        int targetIdx = -1;
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            if (n?["type"]?.GetValue<string>() != nodeType) continue;
            if (n["stage"]?.GetValue<string>() != stage) continue;
            targetIdx = i;
            break;
        }

        if (targetIdx < 0) return ApplyOutcome.Unchanged(content);

        var original = nodes[targetIdx]!.DeepClone();

        // 额外写一个顶层标记字段，作为幂等判别依据。
        // 不能用格式化后的文本做特征：JsonSerializer 会把 0.0 写成 0，
        // 缩进也会随 options 变化，字符串比较不可靠。
        root["frei_atlas_fog_disabled"] = true;

        // 换成输出全透明的常量节点。保持 index 与 ui_position ——
        // 下游连线按 index 引用，改了会断链。
        nodes[targetIdx] = new JsonObject
        {
            ["type"] = "ConstantFloat4",
            ["index"] = original["index"]?.DeepClone() ?? 0,
            ["parameters"] = new JsonArray
            {
                new JsonObject
                {
                    ["value"] = new JsonArray { 0.0, 0.0, 0.0, 0.0 },
                    ["srgb"] = false
                }
            },
            ["ui_position"] = original["ui_position"]?.DeepClone() ?? new JsonArray { 0.0, 0.0 }
        };

        return new ApplyOutcome(true, SerializeJson(root), null, false);
    }

    private static ApplyOutcome ApplyRemoveObject(string content, EffectPatchRule rule)
    {
        // .env 是 JSON。用 DOM 删除而不是正则删行：
        // 正则删行遇到尾随逗号、嵌套对象会破坏语法。
        //
        // 实测 861 个 .env 里有 2 个带尾随逗号，必须 AllowTrailingCommas。
        var options = new JsonNodeOptions { PropertyNameCaseInsensitive = false };
        var docOptions = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        };

        JsonObject root;
        try
        {
            var node = JsonNode.Parse(content, options, docOptions);
            if (node is not JsonObject obj)
            {
                return ApplyOutcome.Fail(content, $"{rule.Id}: 顶层不是 JSON 对象");
            }
            root = obj;
        }
        catch (JsonException ex)
        {
            return ApplyOutcome.Fail(content, $"{rule.Id}: JSON 解析失败 - {ex.Message}");
        }

        var keys = rule.TargetPattern
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        bool removed = false;
        foreach (var key in keys)
        {
            if (root.Remove(key)) removed = true;
        }

        if (!removed) return ApplyOutcome.Unchanged(content);

        // 回写：必须保持原格式
        //   实测 .env 全部 LF 换行 + 2 空格缩进，JsonWriter 默认输出 CRLF，必须显式配置
        var serialized = SerializeJson(root);
        return new ApplyOutcome(true, serialized, null, false);
    }

    /// <summary>
    /// 按游戏原始格式序列化 JSON：LF 换行、2 空格缩进。
    ///
    /// 实测 200 个 .env 全部是 LF + 2 空格，而 JsonSerializer 默认输出 CRLF。
    /// 不配置的话每个文件都会格式漂移。
    /// </summary>
    public static string SerializeJson(JsonNode node)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            IndentCharacter = ' ',
            IndentSize = 2,
            NewLine = "\n",
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        return node.ToJsonString(options);
    }
}
