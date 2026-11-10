using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.EffectPatch.MudMan;

/// <summary>
/// 「泥人」贴图压缩的核心：把经过双重过滤后的名单内 DDS 换成极小尺寸的预置块。
///
/// 严格对齐易刷（worker.js + remote_ni.js）的真实实现：
///   1. 发光/自发光过滤（zd()）：路径匹配 glow|emiss|self[_ -]?illum 的贴图一律保留原图，
///      防止发光遮罩被替换成灰块后铺满全屏（如红光泛滥）。
///   2. 热更保护名单过滤（k() / ye()）：排除 15,087 张怪物主贴图、Boss 与机制贴图及 UI/粒子目录。
///   3. 双表替换（fa.process）：
///      · 在定制表（ua，有效 6,979 张）内：返回各自的预置色调 DDS 块（保留场景明暗与主色）
///      · 在名单表（mt，有效 15,743 张）内：返回 qd() 生成的 172 字节 4×4 BC1 默认中性灰块
///      · 名单外贴图：一律返回 null，原样保留。
/// </summary>
public static class GrayDdsMaker
{
    /// <summary>单张贴图替换后的最大字节数（定制块最大 292 字节）。</summary>
    public const int MaxStubBytes = 292;

    /// <summary>默认纯灰块字节数（148 字节 DDS+DX10 头 + 3 级 mip × 8 字节 = 172 字节）。</summary>
    public const int DefaultGrayStubBytes = 172;

    /// <summary>
    /// 发光/自发光贴图过滤（对应 worker.js 的 zd()）。
    /// </summary>
    private static readonly Regex GlowOrEmissiveRegex = new(
        @"glow|emiss|self[_ -]?illum",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 目录级保护过滤（对应 remote_ni.js 的 k() 目录排除）。
    /// </summary>
    private static readonly Regex ProtectedDirRegex = new(
        @"(?:^|/)(?:interface|ui|minimap|worldmap|fonts|particles|tormented_?spirits|azmeriwisps|wisp_doodads|overseerlargeowl|vaalbossstatue|chieftain02|bloodapeandpaleservant|karuiboar|clearfellbull|intrepiddeer|werecat|shapeshifters)(?:/|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 172 字节默认纯灰 DDS（对应 worker.js 的 qd()）。
    /// 4×4 BC1_UNORM (DXGI_FORMAT = 71)，3 级 Mipmap，RGB565 = 0x8410 (33808)。
    /// </summary>
    private static readonly byte[] DefaultGrayDds = BuildDefaultGrayDds();

    /// <summary>该路径是否命中泥人贴图替换（通过双重过滤且在有效表内）。</summary>
    public static bool IsListed(string virtualPath)
    {
        if (string.IsNullOrEmpty(virtualPath)) return false;
        if (!virtualPath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) return false;
        if (GlowOrEmissiveRegex.IsMatch(virtualPath)) return false;
        if (ProtectedDirRegex.IsMatch(VirtualPath.Normalize(virtualPath))) return false;

        return MudManTables.IsListed(virtualPath);
    }

    /// <summary>
    /// 取得该路径对应的替换字节。
    ///
    /// 定制表（ua）内返回定制色调块；名单表（mt）内返回 172 字节默认纯灰块；
    /// 其余路径或被保护/发光过滤拦截的路径返回 null，表示不处理。
    /// </summary>
    /// <returns>替换字节；该路径不处理时返回 null。</returns>
    public static byte[]? GetReplacement(string virtualPath)
    {
        if (!IsListed(virtualPath)) return null;

        return MudManTables.TryGetCustom(virtualPath) ?? DefaultGrayDds;
    }

    private static byte[] BuildDefaultGrayDds()
    {
        var buf = new byte[DefaultGrayStubBytes];
        Encoding.ASCII.GetBytes("DDS ").CopyTo(buf, 0);

        ReadOnlySpan<(int Offset, uint Value)> headerFields =
        [
            (4, 124u),
            (8, 135175u),       // 0x00021007: CAPS | HEIGHT | WIDTH | PIXELFORMAT | MIPMAPCOUNT
            (12, 4u),           // height = 4
            (16, 4u),           // width = 4
            (24, 1u),           // depth = 1
            (28, 3u),           // mipMapCount = 3 (4x4, 2x2, 1x1)
            (76, 32u),          // ddspf.size = 32
            (80, 4u),           // ddspf.flags = DDPF_FOURCC
            (108, 6557704u),    // 0x00641008: COMPLEX | TEXTURE | MIPMAP
            (128, 71u),         // DXGI_FORMAT_BC1_UNORM
            (132, 3u),          // D3D10_RESOURCE_DIMENSION_TEXTURE2D
            (140, 1u)           // arraySize = 1
        ];

        foreach (var (offset, value) in headerFields)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset, 4), value);
        }

        Encoding.ASCII.GetBytes("DX10").CopyTo(buf, 84);

        // 填充 3 个 BC1 块（148..172），每块前 4 字节为两个 RGB565 中性灰 0x8410 (33808)
        for (int offset = 148; offset < buf.Length; offset += 8)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(offset, 2), 33808);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(offset + 2, 2), 33808);
        }

        return buf;
    }
}
