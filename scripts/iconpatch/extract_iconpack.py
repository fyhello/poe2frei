"""从 POE2小助手的图标包中提取图标素材，生成 FreiAtlas 内置资源。

图标包格式（.iconpack，magic "P2PI"，version 1）：
    偏移 0   [4]   magic "P2PI"
    偏移 4   [4]   version = 1
    偏移 8   [4]   条目数
    偏移 12  [4]   索引区起始偏移
    偏移 16  ...   数据区：各条目原始字节连续排列，整体按绝对偏移 XOR 混淆
    index_off ...  索引区：条目描述，同样 XOR 混淆

索引条目：uint16 路径长度 + UTF8 路径 + uint32 相对偏移 + uint32 大小

XOR 位置使用「文件内绝对偏移」而不是相对偏移，这是容易搞错的地方。

输出三类内容：
    icons.dat       784 个 DDS 原始字节（写入游戏时必须用原始 DDS）
    icons.index.json   路径、分类、偏移、大小、雪碧图坐标
    icons.sheet.webp   48px 缩略图雪碧图，供前端 CSS 定位渲染

用法：
    python scripts/iconpatch/extract_iconpack.py [--source <iconpack 路径>] [--out <输出目录>]
"""

from __future__ import annotations

import argparse
import gzip
import io
import json
import math
import struct
import sys
from pathlib import Path

# 图标包的 XOR 密钥，32 字节
OBFUSCATION_KEY = bytes(
    [
        77, 121, 73, 99, 111, 110, 80, 97, 99, 107,
        70, 111, 114, 80, 111, 69, 50, 80, 114, 105,
        99, 101, 71, 117, 105, 50, 48, 50, 54, 78,
        88, 84,
    ]
)

# 分类映射，与 POE2小助手 IconPatchCategories 保持一致。
# 顺序有意义：先匹配到的分类生效。
CATEGORY_MAPPINGS: tuple[tuple[str, int, str], ...] = (
    ("art/textures/interface/2d/2dart/uiimages/ingame/expedition/remnant/", 4, "维金"),
    ("art/textures/interface/2d/2dart/uiimages/ingame/expedition/tome/", 4, "维金"),
    ("art/2ditems/currency/", 1, "通货"),
    ("art/2ditems/gems/", 2, "血脉宝石"),
    ("art/2ditems/maps/", 3, "终局地图"),
)

FALLBACK_GROUP_INDEX = 5
FALLBACK_CATEGORY = "其他"

# 雪碧图参数。48px 是列表缩略图的显示尺寸上限，再大只会增加体积。
SPRITE_CELL = 48
SPRITE_COLUMNS = 32
SPRITE_QUALITY = 82

DEFAULT_SOURCE = Path(
    r"C:\Users\25147\AppData\Local\Poe2PriceGui\current\data\iconpatch.iconpack"
)
DEFAULT_OUTPUT = Path(__file__).resolve().parents[2] / "src" / "FreiAtlas.Patcher" / "Resources" / "iconpatch"


def xor_bytes(data: bytes, position: int) -> bytes:
    """按绝对偏移对数据做 XOR。加密与解密是同一运算。"""
    key = OBFUSCATION_KEY
    key_len = len(key)
    return bytes(b ^ key[(position + i) % key_len] for i, b in enumerate(data))


def read_iconpack(path: Path) -> list[tuple[str, bytes]]:
    """解析图标包，返回 (虚拟路径, DDS 原始字节) 列表。"""
    raw = path.read_bytes()

    if len(raw) < 16:
        raise ValueError(f"文件过短，不是有效的图标包：{path}")

    magic = raw[0:4].decode("ascii", errors="replace")
    if magic != "P2PI":
        raise ValueError(f"magic 不匹配，期望 P2PI，实际 {magic!r}")

    version, count, index_offset = struct.unpack("<III", raw[4:16])
    if version != 1:
        raise ValueError(f"不支持的图标包版本：{version}")
    if index_offset >= len(raw):
        raise ValueError(f"索引区偏移越界：{index_offset} >= {len(raw)}")

    # 索引区：先解密再逐条读取
    index_blob = xor_bytes(raw[index_offset:], index_offset)
    cursor = 0
    entries: list[tuple[str, int, int]] = []

    for _ in range(count):
        if cursor + 2 > len(index_blob):
            raise ValueError("索引区被截断（读取路径长度失败）")
        name_len = struct.unpack("<H", index_blob[cursor : cursor + 2])[0]
        cursor += 2

        if cursor + name_len + 8 > len(index_blob):
            raise ValueError("索引区被截断（读取条目内容失败）")
        virtual_path = index_blob[cursor : cursor + name_len].decode("utf-8")
        cursor += name_len

        relative_offset, size = struct.unpack("<II", index_blob[cursor : cursor + 8])
        cursor += 8
        entries.append((virtual_path, relative_offset, size))

    # 数据区：绝对偏移 = 16 + 相对偏移
    result: list[tuple[str, bytes]] = []
    for virtual_path, relative_offset, size in entries:
        start = 16 + relative_offset
        end = start + size
        if end > index_offset:
            raise ValueError(f"条目数据越界：{virtual_path}（{end} > {index_offset}）")
        result.append((virtual_path, xor_bytes(raw[start:end], start)))

    return result


def classify(virtual_path: str) -> tuple[int, str]:
    """按路径前缀判定分类，规则与 POE2小助手一致。"""
    lowered = virtual_path.lower()
    for prefix, group_index, name in CATEGORY_MAPPINGS:
        if lowered.startswith(prefix.lower()):
            return group_index, name
    return FALLBACK_GROUP_INDEX, FALLBACK_CATEGORY


def build_sprite_sheet(images: list) -> tuple[bytes, list[tuple[int, int]]]:
    """把图标按统一格子拼成雪碧图，返回 WebP 字节与每格的像素坐标。"""
    from PIL import Image

    rows = math.ceil(len(images) / SPRITE_COLUMNS)
    sheet = Image.new("RGBA", (SPRITE_COLUMNS * SPRITE_CELL, rows * SPRITE_CELL), (0, 0, 0, 0))

    coords: list[tuple[int, int]] = []
    for index, image in enumerate(images):
        thumb = image if image.size == (SPRITE_CELL, SPRITE_CELL) else image.resize(
            (SPRITE_CELL, SPRITE_CELL), Image.LANCZOS
        )
        x = (index % SPRITE_COLUMNS) * SPRITE_CELL
        y = (index // SPRITE_COLUMNS) * SPRITE_CELL
        sheet.paste(thumb, (x, y))
        coords.append((x, y))

    buffer = io.BytesIO()
    sheet.save(buffer, format="WEBP", quality=SPRITE_QUALITY, method=6)
    return buffer.getvalue(), coords


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    parser = argparse.ArgumentParser(description="提取 POE2小助手图标包")
    parser.add_argument("--source", type=Path, default=DEFAULT_SOURCE, help="iconpack 文件路径")
    parser.add_argument("--out", type=Path, default=DEFAULT_OUTPUT, help="输出目录")
    args = parser.parse_args()

    if not args.source.exists():
        print(f"源文件不存在：{args.source}")
        return 1

    print(f"读取图标包：{args.source}")
    entries = read_iconpack(args.source)
    print(f"解析出 {len(entries)} 个图标")

    # 校验路径唯一性，重复路径会让写入行为不确定
    paths = [p for p, _ in entries]
    if len(set(paths)) != len(paths):
        duplicates = len(paths) - len(set(paths))
        print(f"发现 {duplicates} 个重复路径，请检查图标包")
        return 1

    from PIL import Image

    images = []
    decoded: list[tuple[str, bytes]] = []
    for virtual_path, data in entries:
        if not data.startswith(b"DDS "):
            print(f"DDS 头无效，跳过：{virtual_path}")
            return 1
        image = Image.open(io.BytesIO(data))
        image.load()
        images.append(image.convert("RGBA"))
        decoded.append((virtual_path, data))

    print(f"DDS 解码完成：{len(images)} 个")

    args.out.mkdir(parents=True, exist_ok=True)

    # ① DDS 原始字节：连续排列，索引记录相对偏移
    blob = bytearray()
    index_items = []
    for (virtual_path, data), (x, y) in zip(decoded, build_sprite_sheet(images)[1]):
        group_index, category = classify(virtual_path)
        index_items.append(
            {
                "path": virtual_path,
                "fileName": virtual_path.rsplit("/", 1)[-1],
                "groupIndex": group_index,
                "category": category,
                "offset": len(blob),
                "size": len(data),
                "spriteX": x,
                "spriteY": y,
            }
        )
        blob.extend(data)

    bin_path = args.out / "icons.dat"
    with gzip.open(bin_path, "wb", compresslevel=9) as handle:
        handle.write(bytes(blob))
    print(f"写入 {bin_path.name}：{len(blob) / 1024 / 1024:.2f} MB -> {bin_path.stat().st_size / 1024 / 1024:.2f} MB")

    # ② 雪碧图
    sheet_bytes, _ = build_sprite_sheet(images)
    sheet_path = args.out / "icons.sheet.webp"
    sheet_path.write_bytes(sheet_bytes)
    print(f"写入 {sheet_path.name}：{len(sheet_bytes) / 1024:.0f} KB")

    # ③ 索引
    manifest = {
        "version": 1,
        "cellSize": SPRITE_CELL,
        "columns": SPRITE_COLUMNS,
        "count": len(index_items),
        "entries": index_items,
    }
    index_path = args.out / "icons.index.json"
    index_path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(f"写入 {index_path.name}：{index_path.stat().st_size / 1024:.0f} KB")

    # 分类统计，便于核对
    stats: dict[str, int] = {}
    for item in index_items:
        stats[item["category"]] = stats.get(item["category"], 0) + 1
    print("分类统计：" + "、".join(f"{k} {v}" for k, v in sorted(stats.items())))

    return 0


if __name__ == "__main__":
    sys.exit(main())
