"""S3 验证：应用 dark_bloom_cutoff 规则并检查结果（不写入客户端）。

这个脚本用 C# 代码直接跑，避免 PowerShell 反射的类型转换问题。
"""

from __future__ import annotations

import sys
from pathlib import Path

# 生成一个最小的 C# 程序，编译后运行
PROGRAM = r'''
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using FreiAtlas.Patcher;
using FreiAtlas.Patcher.EffectPatch;

internal static class S3Verify
{
    private static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        var info = new GameModeInfo
        {
            GameDirectory = @"E:\PSAutoRecover\ui\rood\Grinding Gear Games\Path of Exile 2",
            Mode = GameMode.GGPK,
            IsValid = true
        };

        var engine = new PatchEngine();
        var paths = new List<string> { "shaders/bloomcutoff.hlsl" };
        var files = engine.ReadGameFiles(info, paths);
        if (!files.TryGetValue("shaders/bloomcutoff.hlsl", out var original))
        {
            Console.WriteLine("读取失败");
            return;
        }

        Console.WriteLine($"原始字节: {original.Length}");

        var encoding = EffectTextCodec.Detect(original, "shaders/bloomcutoff.hlsl");
        Console.WriteLine($"识别编码: {encoding}");

        var text = EffectTextCodec.Decode(original, encoding);
        Console.WriteLine($"原文长度: {text.Length}");

        var rule = EffectPatchRules.All.First(r => r.Id == "dark_bloom_cutoff");
        Console.WriteLine($"规则: {rule.Id}, 幂等特征: {rule.AppliedSentinel}");

        var outcome = EffectRuleApplier.Apply(text, rule);
        Console.WriteLine($"Changed: {outcome.Changed}");
        Console.WriteLine($"SkippedByIdempotency: {outcome.SkippedByIdempotency}");
        Console.WriteLine($"Error: {outcome.Error ?? "<无>"}");
        Console.WriteLine($"新文长度: {outcome.Content.Length}");

        var validation = EffectSyntaxValidator.ValidateFor(outcome.Content, "shaders/bloomcutoff.hlsl");
        Console.WriteLine($"语法校验: IsValid={validation.IsValid}, Error={validation.Error ?? "<无>"}");

        // 再应用一次，验证幂等
        var second = EffectRuleApplier.Apply(outcome.Content, rule);
        Console.WriteLine($"二次应用 Changed: {second.Changed}");
        Console.WriteLine($"二次应用 Skipped: {second.SkippedByIdempotency}");
        Console.WriteLine($"幂等成立: {outcome.Content == second.Content}");

        // 输出新内容供人工核对
        var outPath = Path.Combine(Path.GetTempPath(), "frei-s3-backup", "bloomcutoff.hlsl.patched");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, outcome.Content, Encoding.UTF8);
        Console.WriteLine($"新内容已写到: {outPath}");

        // 显示替换部分
        var idx = outcome.Content.IndexOf("float4 BloomCutoff(", StringComparison.Ordinal);
        if (idx >= 0)
        {
            Console.WriteLine();
            Console.WriteLine("=== 替换后的函数区域 ===");
            Console.WriteLine(outcome.Content.Substring(idx, Math.Min(400, outcome.Content.Length - idx)));
        }
    }
}
'''

CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <AssemblyName>S3Verify</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\\..\\..\\src\\FreiAtlas.Patcher\\FreiAtlas.Patcher.csproj" />
  </ItemGroup>
</Project>
"""


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    root = Path(__file__).resolve().parents[2]
    work = root / ".cache" / "s3-verify"
    work.mkdir(parents=True, exist_ok=True)

    (work / "Program.cs").write_text(PROGRAM, encoding="utf-8")
    (work / "S3Verify.csproj").write_text(CSPROJ, encoding="utf-8")

    print(f"工作目录: {work}")
    print("已生成 C# 验证程序")
    return 0


if __name__ == "__main__":
    sys.exit(main())
