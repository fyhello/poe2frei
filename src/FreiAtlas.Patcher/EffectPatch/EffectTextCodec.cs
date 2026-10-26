using System.Text;

namespace FreiAtlas.Patcher.EffectPatch;

/// <summary>
/// 特效文件的文本编解码。
///
/// 实测编码规则：
///   · .hlsl               → UTF-8 无 BOM
///   · .fxgraph            → UTF-16LE **双重 BOM**（FF FE FF FE，4 字节）
///   · 其余 .ao/.epk/.pet/.trl/.ot/.env → UTF-16LE 单 BOM（FF FE，2 字节）
///
/// 双重 BOM 不是笔误：实测 15 个 fxgraph 全部以 FF FE FF FE 开头。
/// 若按单 BOM 处理，解码后文本会残留一个 U+FEFF，JSON 解析直接失败。
/// </summary>
public static class EffectTextCodec
{
    /// <summary>UTF-16LE 的 BOM。</summary>
    private static readonly byte[] Utf16LePreamble = [0xFF, 0xFE];

    /// <summary>UTF-8 的 BOM。</summary>
    private static readonly byte[] Utf8Preamble = [0xEF, 0xBB, 0xBF];

    /// <summary>按扩展名判定编码。</summary>
    public static EffectTextEncoding Detect(byte[] bytes, string virtualPath)
    {
        if (bytes.Length >= 3
            && bytes[0] == Utf8Preamble[0]
            && bytes[1] == Utf8Preamble[1]
            && bytes[2] == Utf8Preamble[2])
        {
            return EffectTextEncoding.Utf8WithBom;
        }

        // 双重 BOM 必须先判：FF FE FF FE 的前两字节也是单 BOM
        if (bytes.Length >= 4
            && bytes[0] == 0xFF && bytes[1] == 0xFE
            && bytes[2] == 0xFF && bytes[3] == 0xFE)
        {
            return EffectTextEncoding.Utf16LeWithDoubleBom;
        }

        if (bytes.Length >= 2 && bytes[0] == Utf16LePreamble[0] && bytes[1] == Utf16LePreamble[1])
        {
            return EffectTextEncoding.Utf16LeWithBom;
        }

        if (virtualPath.EndsWith(".hlsl", StringComparison.OrdinalIgnoreCase))
        {
            return EffectTextEncoding.Utf8NoBom;
        }

        return EffectTextEncoding.Utf16LeNoBom;
    }

    /// <summary>解码成文本。BOM 会被剥除，不进入文本内容。</summary>
    public static string Decode(byte[] bytes, EffectTextEncoding encoding) => encoding switch
    {
        EffectTextEncoding.Utf8WithBom => Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3),
        EffectTextEncoding.Utf8NoBom => Encoding.UTF8.GetString(bytes),
        EffectTextEncoding.Utf16LeWithBom => Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2),
        EffectTextEncoding.Utf16LeWithDoubleBom => Encoding.Unicode.GetString(bytes, 4, bytes.Length - 4),
        EffectTextEncoding.Utf16LeNoBom => Encoding.Unicode.GetString(bytes),
        _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "未知编码")
    };

    /// <summary>
    /// 编码回字节，并按原编码写回 BOM。
    ///
    /// 注意：Encoding.Unicode.GetBytes() 不写 BOM，即使构造时传了 byteOrderMark: true。
    /// 那个参数只影响 GetPreamble()。必须手动拼接，否则所有脚本文件都会丢失 BOM。
    /// </summary>
    public static byte[] Encode(string text, EffectTextEncoding encoding) => encoding switch
    {
        EffectTextEncoding.Utf8WithBom => [.. Utf8Preamble, .. Encoding.UTF8.GetBytes(text)],
        EffectTextEncoding.Utf8NoBom => Encoding.UTF8.GetBytes(text),
        EffectTextEncoding.Utf16LeWithBom => [.. Utf16LePreamble, .. Encoding.Unicode.GetBytes(text)],
        EffectTextEncoding.Utf16LeWithDoubleBom =>
            [.. Utf16LePreamble, .. Utf16LePreamble, .. Encoding.Unicode.GetBytes(text)],
        EffectTextEncoding.Utf16LeNoBom => Encoding.Unicode.GetBytes(text),
        _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "未知编码")
    };

    /// <summary>
    /// 按扩展名给出「未修改时应使用的编码」。
    /// 用于新建内容或原文件无 BOM 但需要补 BOM 的场景。
    /// </summary>
    public static EffectTextEncoding DefaultFor(string virtualPath)
    {
        if (virtualPath.EndsWith(".hlsl", StringComparison.OrdinalIgnoreCase))
            return EffectTextEncoding.Utf8NoBom;

        if (virtualPath.EndsWith(".fxgraph", StringComparison.OrdinalIgnoreCase))
            return EffectTextEncoding.Utf16LeWithDoubleBom;

        return EffectTextEncoding.Utf16LeWithBom;
    }
}

public enum EffectTextEncoding
{
    Utf8NoBom,
    Utf8WithBom,
    Utf16LeNoBom,
    Utf16LeWithBom,

    /// <summary>UTF-16LE 且文件开头有两个 BOM（FF FE FF FE）。实测 .fxgraph 是这种。</summary>
    Utf16LeWithDoubleBom
}
