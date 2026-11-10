using FreiAtlas.Patcher.Core;

namespace FreiAtlas.Patcher.Tests;

/// <summary>
/// 写盘路径的性能约束。
///
/// FilterExisting / AddFileRecordPlaceholder / Index.Replace / CompareAll 都只依赖
/// LibBundle3 的 NameHash 与内部 _Files 字典，不需要路径树。写盘前再调用
/// ParsePaths() 会把 426 万条路径完整解析一遍，实测每次约 8-10 秒。
/// </summary>
public sealed class PatchEngineWritePathTests
{
    [Fact]
    public void 写盘与写后校验不得调用ParsePaths()
    {
        string source = ReadPatchEngineSource();

        foreach (var signature in new[]
                 {
                     "private bool WriteGameFilesCore(",
                     "private bool VerifyWrittenFiles("
                 })
        {
            string body = ExtractMethodBody(source, signature);
            Assert.DoesNotContain("ParsePaths()", body, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 会话无效时 CommitSession 必须把原因写进 LastWriteException。
    ///
    /// 否则调用方只能显示「未知原因」，而真正的原因（路径冲突 / 超出内存上限）
    /// 只存在于局部变量 session.Conflicts 里，随方法返回一起丢失。
    /// </summary>
    [Fact]
    public void CommitSession_会话无效时必须写入LastWriteException()
    {
        string root = Path.Combine(
            Path.GetTempPath(), "FreiAtlasCommitSessionTest_" + Guid.NewGuid().ToString("N"));

        try
        {
            var engine = new PatchEngine(new PatchBackupManager(root));
            engine.LastWriteException = null;

            var session = engine.BeginSession();
            typeof(PatchSession).GetProperty("IsValid")!
                .GetSetMethod(nonPublic: true)!
                .Invoke(session, [false]);

            var info = new GameModeInfo
            {
                IsValid = true,
                Mode = GameMode.GGPK,
                GameDirectory = Path.Combine(root, "game")
            };

            bool ok = engine.CommitSession(info);

            Assert.False(ok);
            Assert.NotNull(engine.LastWriteException);
            Assert.Contains("会话无效", engine.LastWriteException!.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string ReadPatchEngineSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string baseDir = Path.Combine(dir.FullName, "src", "FreiAtlas.Patcher");
            if (Directory.Exists(baseDir))
            {
                string path = Path.Combine(baseDir, "Core", "PatchEngine.cs");
                Assert.True(File.Exists(path), $"未找到 PatchEngine.cs：{path}");
                return File.ReadAllText(path);
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException("未能定位 src/FreiAtlas.Patcher");
    }

    private static string ExtractMethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"未找到方法签名：{signature}");

        int braceStart = source.IndexOf('{', start);
        Assert.True(braceStart >= 0, $"方法缺少方法体：{signature}");

        int depth = 0;
        for (int i = braceStart; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[braceStart..(i + 1)];
                }
            }
        }

        throw new InvalidDataException($"无法定位方法体结束：{signature}");
    }
}
