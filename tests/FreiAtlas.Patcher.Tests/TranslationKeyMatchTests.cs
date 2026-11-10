using FreiAtlas.Patcher.Core;
using FreiAtlas.Patcher.Translation;
using System.Text;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 翻译按稳定键匹配，而不是按行号。用真实 WorldAreas 表验证：
/// 0.5.5f 在旧行号前插入了新区域，按 Id 匹配后 430/434 仍能得到正确地图名。
/// </summary>
public sealed class TranslationKeyMatchTests
{
    private const string KeyA = "ca978112ca1bbdcafac231b39a23dc4d";

    [Fact]
    public void 稳定键在行重排后定位原记录()
    {
        byte[] original = BuildTable("b", "a");
        var definition = Definition();

        var (patched, count) = DatTextPatcher.PatchTable(original, definition);

        Assert.Equal(1, count);
        Assert.Equal("原名b", NameAt(patched, 0));
        Assert.Equal("译名a", NameAt(patched, 1));
        Assert.Equal("原名a", NameAt(original, 1));
    }

    [Theory]
    [InlineData("a", "a")]
    [InlineData("b", "c")]
    public void 重复键或缺失键不得猜测行号(string first, string second)
    {
        byte[] original = BuildTable(first, second);
        var (patched, count) = DatTextPatcher.PatchTable(original, Definition());
        Assert.Equal(0, count);
        Assert.Same(original, patched);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(9)]
    [InlineData(int.MaxValue)]
    public void 文本列越过当前行时不得写入(int column)
    {
        byte[] original = BuildTable("a", "b");
        var definition = Definition();
        definition.RowPatches[0].Col = column;
        Assert.Equal(0, DatTextPatcher.PatchTable(original, definition).PatchedCount);
    }

    [Theory]
    [InlineData(-1, "string")]
    [InlineData(9, "string")]
    [InlineData(15, "u32")]
    [InlineData(int.MaxValue, "u32")]
    public void 键列越过当前行时不得匹配(int offset, string type)
    {
        var definition = Definition();
        definition.KeyColumns = [new() { Offset = offset, Type = type }];
        Assert.Equal(0, DatTextPatcher.PatchTable(BuildTable("a", "b"), definition).PatchedCount);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 0)]
    [InlineData(2, 1)]
    public void 无稳定键时必须提供相同行数(int rowCount, int expected)
    {
        var definition = Definition();
        definition.KeyColumns.Clear();
        definition.RowCount = rowCount;
        Assert.Equal(expected, DatTextPatcher.PatchTable(BuildTable("a", "b"), definition).PatchedCount);
    }

    private static DatTextPatcher.TablePatchDefinition Definition() => new()
    {
        RowSize = 16,
        RowCount = 2,
        KeyColumns = [new() { Offset = 0, Type = "string" }],
        RowPatches = [new() { Row = 0, Col = 8, Key = KeyA, Text = "译名a" }]
    };

    private static byte[] BuildTable(params string[] ids)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true);
        writer.Write(ids.Length);
        writer.Write(new byte[ids.Length * 16]);
        int stringBase = (int)stream.Position;
        writer.Write(DatBinaryUtils.SentinelValue);
        for (int row = 0; row < ids.Length; row++)
        {
            foreach (var (column, value) in new[] { (0, ids[row]), (8, "原名" + ids[row]) })
            {
                long textPosition = stream.Length;
                stream.Position = textPosition;
                writer.Write(Encoding.Unicode.GetBytes(value + "\0"));
                stream.Position = 4 + row * 16 + column;
                writer.Write((ulong)(textPosition - stringBase));
            }
        }
        return stream.ToArray();
    }

    private static string NameAt(byte[] data, int row) =>
        DatBinaryUtils.ReadPointerTarget(data, 4 + row * 16 + 8, 36);

    private const string RealGameDir =
        @"E:\PSAutoRecover\ui\rood\Grinding Gear Games\Path of Exile 2";

    [Fact]
    [Trait("Category", "RequiresGameClient")]
    public void 世界区域按稳定键匹配后地图名正确()
    {
        if (!Directory.Exists(RealGameDir)) return;

        var info = new GameModeInfo
        {
            IsValid = true,
            GameDirectory = RealGameDir,
            Mode = GameMode.GGPK
        };

        var engine = new PatchEngine();
        var files = engine.ReadGameFiles(
            info,
            ["data/balance/traditional chinese/worldareas.datc64"]);

        Assert.True(files.TryGetValue("data/balance/traditional chinese/worldareas.datc64", out var original));
        var definition = DatTextPatcher.GetEmbeddedCatalog().Tables["worldareas.datc64"];
        var (patched, patchedCount) = DatTextPatcher.PatchTable(original!, definition);

        Assert.True(patchedCount > 0);
        var layout = DatBinaryUtils.DetectLayout(patched, "worldareas");

        string At(int row) => DatBinaryUtils.ReadPointerTarget(
            patched, 4 + row * layout.RowSize + 8, layout.StringBase);

        Assert.Equal("阿兹里神庙", At(430));
        Assert.Equal("长老殿堂", At(434));
        Assert.Equal("瓦尔遗迹", At(431));
    }
}
