using FreiAtlas.Patcher.EffectPatch;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 编码处理。实测结论：除 .hlsl 外全部 UTF-16LE 带 BOM。
/// 编码用错会让客户端解析器读字符越界直接闪退，这一层不允许猜。
/// </summary>
public sealed class EffectTextCodecTests
{
    [Theory]
    [InlineData("metadata/monsters/monster.ot", EffectTextEncoding.Utf16LeNoBom)]
    [InlineData("shaders/bloomcutoff.hlsl", EffectTextEncoding.Utf8NoBom)]
    [InlineData("metadata/environmentsettings/x.env", EffectTextEncoding.Utf16LeNoBom)]
    public void 无BOM时按扩展名判定编码族(string path, EffectTextEncoding expected)
    {
        // Detect 只负责识别「文件当前是什么编码」，不负责补 BOM。
        // 无 BOM 的 .ot 就应该返回 Utf16LeNoBom——如实反映现状。
        // 是否补 BOM 由 DefaultFor 决定，见下一个测试。
        var bytes = System.Text.Encoding.UTF8.GetBytes("version 3");

        Assert.Equal(expected, EffectTextCodec.Detect(bytes, path));
    }

    [Theory]
    [InlineData("metadata/monsters/monster.ot", EffectTextEncoding.Utf16LeWithBom)]
    [InlineData("metadata/environmentsettings/x.env", EffectTextEncoding.Utf16LeWithBom)]
    [InlineData("shaders/bloomcutoff.hlsl", EffectTextEncoding.Utf8NoBom)]
    public void DefaultFor按扩展名给出应使用的编码(string path, EffectTextEncoding expected)
    {
        // 实测证据：861 个 .env 全部带 BOM，8281 个 .ot 也带 BOM。
        // 所以 DefaultFor 对脚本类必须返回 WithBom，写回时才会补上 BOM。
        Assert.Equal(expected, EffectTextCodec.DefaultFor(path));
    }

    [Fact]
    public void 自带BOM时以BOM为准()
    {
        // 即使扩展名是 .hlsl，有 UTF-16LE BOM 也按 UTF-16LE 处理
        byte[] bytes = [0xFF, 0xFE, (byte)'a', 0];

        Assert.Equal(EffectTextEncoding.Utf16LeWithBom, EffectTextCodec.Detect(bytes, "test.hlsl"));
    }

    [Fact]
    public void UTF16解码与编码往返一致()
    {
        const string original = "version 3\n\nActor\n{\n\tslow_animations_go_to_idle = false\n}\n";
        var encoded = EffectTextCodec.Encode(original, EffectTextEncoding.Utf16LeWithBom);

        // 必须带 BOM
        Assert.Equal(0xFF, encoded[0]);
        Assert.Equal(0xFE, encoded[1]);

        var decoded = EffectTextCodec.Decode(encoded, EffectTextEncoding.Utf16LeWithBom);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void UTF8编码不带BOM()
    {
        const string text = "float4 BloomCutoff( PInput input )";
        var encoded = EffectTextCodec.Encode(text, EffectTextEncoding.Utf8NoBom);

        Assert.NotEqual(0xEF, encoded[0]);
        var decoded = System.Text.Encoding.UTF8.GetString(encoded);
        Assert.StartsWith("float4 BloomCutoff", decoded);
    }

    [Fact]
    public void GetBytes不写BOM必须手动拼Preamble()
    {
        // 这是本轮验证发现的陷阱：
        // new UnicodeEncoding(false, true).GetBytes() 依然不写 BOM，
        // byteOrderMark 参数只影响 GetPreamble()。
        var enc = new System.Text.UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        var withoutPreamble = enc.GetBytes("a");

        Assert.NotEqual(0xFF, withoutPreamble[0]);          // 没有 BOM
        Assert.Equal(0xFF, enc.GetPreamble()[0]);           // Preamble 才有

        // 我们的实现必须带 BOM
        var ours = EffectTextCodec.Encode("a", EffectTextEncoding.Utf16LeWithBom);
        Assert.Equal(0xFF, ours[0]);
        Assert.Equal(0xFE, ours[1]);
    }

    [Fact]
    public void 解码时剥除BOM不进入文本()
    {
        byte[] withBom = [0xFF, 0xFE, (byte)'x', 0];
        var text = EffectTextCodec.Decode(withBom, EffectTextEncoding.Utf16LeWithBom);

        Assert.Equal("x", text);
        Assert.DoesNotContain('\uFEFF', text);
    }
}