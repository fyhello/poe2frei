"""在真实客户端索引上统计各规则的路径命中数量。

只读操作：只枚举索引里的文件路径，不读取文件内容，不修改任何东西。

用法：
    python scripts/effectpatch/scan_client_paths.py [--ggpk <Content.ggpk 路径>]
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

DEFAULT_GGPK = r"E:\PSAutoRecover\ui\rood\Grinding Gear Games\Path of Exile 2\Content.ggpk"


def load_rules() -> list[dict]:
    path = Path(__file__).resolve().parent / "rules.json"
    if not path.exists():
        raise SystemExit(f"规则文件不存在，请先运行 probe_rules.py：{path}")
    return json.loads(path.read_text(encoding="utf-8"))


def read_index_paths(ggpk_path: str) -> list[str]:
    """从 GGPK 读取所有文件虚拟路径。

    复用项目已有的 LibGGPK3 系列程序集，避免自己实现 GGPK 解析。
    """
    import clr  # type: ignore

    libs = Path(__file__).resolve().parents[2] / "libs"
    for name in ("SystemExtensions", "LibGGPK3", "LibBundle3", "LibBundledGGPK3"):
        clr.AddReference(str(libs / f"{name}.dll"))

    from LibBundledGGPK3 import BundledGGPK  # type: ignore

    ggpk = BundledGGPK(ggpk_path, False)
    try:
        ggpk.Index.ParsePaths()
        paths = []
        for record in ggpk.Index.Files.Values:
            p = record.Path
            if p:
                paths.append(str(p).replace("\\", "/"))
        return paths
    finally:
        ggpk.Dispose()


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    parser = argparse.ArgumentParser(description="统计特效规则的路径命中")
    parser.add_argument("--ggpk", default=DEFAULT_GGPK, help="Content.ggpk 路径")
    args = parser.parse_args()

    rules = load_rules()
    print(f"规则数: {len(rules)}")

    if not Path(args.ggpk).exists():
        print(f"客户端不存在: {args.ggpk}")
        return 1

    print(f"读取索引: {args.ggpk}")
    paths = read_index_paths(args.ggpk)
    print(f"索引文件总数: {len(paths)}")
    print()

    # 按扩展名快速统计，先看目标文件类型有多少
    print("=== 目标扩展名统计 ===")
    ext_counts: dict[str, int] = {}
    for p in paths:
        dot = p.rfind(".")
        if dot < 0:
            continue
        ext = p[dot:].lower()
        if ext in (".ot", ".aoc", ".ao", ".env", ".hlsl", ".epk", ".pet", ".trl"):
            ext_counts[ext] = ext_counts.get(ext, 0) + 1
    for ext, count in sorted(ext_counts.items(), key=lambda kv: -kv[1]):
        print(f"  {ext:8} {count:6} 个文件")
    print()

    # 逐规则统计路径命中
    print("=== 逐规则路径命中 ===")
    by_feature: dict[str, list[tuple[dict, int, list[str]]]] = {}

    for rule in rules:
        try:
            pattern = re.compile(rule["path"], re.IGNORECASE)
        except re.error as exc:
            print(f"  ✗ {rule['id']}: 正则错误 {exc}")
            continue

        matched = [p for p in paths if pattern.search(p)]
        by_feature.setdefault(rule["feature"], []).append((rule, len(matched), matched[:3]))

    for feature, items in by_feature.items():
        print(f"\n{feature}")
        for rule, count, samples in items:
            mark = "✓" if count > 0 else "✗"
            print(f"  {mark} {rule['id']:24} 命中 {count:6} 个文件")
            for sample in samples:
                print(f"      {sample}")

    print()

    # 汇总
    total_hit = sum(c for items in by_feature.values() for _, c, _ in items if c > 0)
    total_rule = sum(len(items) for items in by_feature.values())
    miss = [r["id"] for items in by_feature.values() for r, c, _ in items if c == 0]

    print("=" * 72)
    print(f"规则命中: {total_rule - len(miss)}/{total_rule}")
    if miss:
        print(f"未命中规则: {', '.join(miss)}")
        print("这些规则可能不适用于当前游戏版本，实施时需要特别处理。")
    else:
        print("全部规则均能在当前客户端匹配到文件。")
    print("=" * 72)

    return 0


if __name__ == "__main__":
    sys.exit(main())
