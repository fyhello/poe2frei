"""PoE2 schema 读取与字段布局计算。

schema 里同一张表可能同时存在 PoE1(validFor=1) 与 PoE2(validFor=2) 两份定义。
必须优先取包含 ``validFor & 2`` 的那份，否则字段偏移会整体错位——这是历史上
多次写出错误数据、导致客户端闪退的根因之一。
"""

from __future__ import annotations

import json

# 字段字节宽度。interval 表示该字段是「当前值 + 最大值」两个副本。
_STATIC_SIZES = {
    'string': 8,
    'foreignrow': 16,
    'row': 8,
    'bool': 1,
}


def field_size(column: dict) -> int:
    """计算 schema 字段占用的字节数。"""
    if column.get('array'):
        return 16  # {长度: u64, 偏移: u64}
    size = _STATIC_SIZES.get(column.get('type'))
    if size is not None:
        return size
    count = 2 if column.get('interval') else 1
    if column.get('type') in ('u16', 'i16'):
        return 2 * count
    if column.get('type') in ('u32', 'i32', 'enumrow', 'f32'):
        return 4 * count
    raise ValueError(f'未知字段类型: {column.get("type")}')


def load_tables(path: str) -> dict[str, dict]:
    """加载 schema，返回仅含 PoE2 定义的表字典。"""
    with open(path, encoding='utf-8') as handle:
        document = json.load(handle)

    tables: dict[str, dict] = {}
    for table in document['tables']:
        name = table['name']
        existing = tables.get(name)
        if existing is None:
            tables[name] = table
        elif (table['validFor'] & 2) and not (existing['validFor'] & 2):
            tables[name] = table
    return tables


def table_width(table: dict) -> int:
    """表一行的总字节宽度。"""
    return sum(field_size(column) for column in table['columns'])


def string_offsets(table: dict) -> list[tuple[int, str]]:
    """列出所有「非数组 string 字段」的偏移，返回 [(偏移, 字段名)]。

    只有这些偏移可以写入字符串指针。数组、外键、数值字段一旦被覆盖，
    客户端读取时会越界，表现为启动即闪退。
    """
    offsets: list[tuple[int, str]] = []
    offset = 0
    for index, column in enumerate(table['columns']):
        if column.get('type') == 'string' and not column.get('array'):
            offsets.append((offset, column.get('name') or f'__col{index}'))
        offset += field_size(column)
    return offsets


def schema_summary(path: str) -> dict:
    """读取 schema 的版本元信息，用于构建日志。"""
    with open(path, encoding='utf-8') as handle:
        document = json.load(handle)
    return {
        'version': document.get('version'),
        'created_at': document.get('createdAt'),
        'table_count': len(document.get('tables', [])),
    }
