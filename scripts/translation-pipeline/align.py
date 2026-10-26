"""国服与国际服的行对齐。

两边行数相同时直接按行号对应。行数不同时必须用序列比对，不能按「前缀物理行号」
配对——国服会在组内插入特供条目，一旦插入，后续所有行都会整体后移，把 A 的译文
写到 B 上。
"""

from __future__ import annotations

import difflib

from datfile import Dat, has_cjk


def pick_key_columns(cn: Dat, max_columns: int = 3) -> list[int]:
    """挑选用于序列比对的列。

    候选列必须：非空比例足够、内容不含中文（保证两边可比）、区分度足够。
    返回区分度最高的若干列的字节偏移。
    """
    total_rows = cn.row_count
    if total_rows == 0:
        return []

    scored: list[tuple[int, int]] = []
    for column in range(0, cn.row_length - 7, 8):
        non_empty = 0
        with_cjk = 0
        distinct: set[str] = set()
        for row in range(total_rows):
            text = cn.text(row, column)
            if not text:
                continue
            non_empty += 1
            distinct.add(text)
            if has_cjk(text):
                with_cjk += 1
        if non_empty < total_rows * 0.1 or with_cjk > 0 or len(distinct) < 5:
            continue
        scored.append((len(distinct), column))

    scored.sort(reverse=True)
    return [column for _, column in scored[:max_columns]]


def row_signatures(dat: Dat, columns: list[int]) -> list[tuple[str, ...]]:
    """按指定列生成每行的签名。"""
    return [
        tuple(dat.text(row, column) for column in columns)
        for row in range(dat.row_count)
    ]


def build_row_map(
    cn_signatures: list[tuple[str, ...]],
    intl_signatures: list[tuple[str, ...]],
) -> list[int]:
    """用最长公共子序列对齐，返回「国服行 -> 国际服行」，无对应时为 -1。"""
    matcher = difflib.SequenceMatcher(None, cn_signatures, intl_signatures, autojunk=False)
    row_map = [-1] * len(cn_signatures)
    for tag, cn_start, cn_end, intl_start, intl_end in matcher.get_opcodes():
        equal_length = (cn_end - cn_start) == (intl_end - intl_start)
        if tag == 'equal':
            for shift in range(cn_end - cn_start):
                row_map[cn_start + shift] = intl_start + shift
        elif tag == 'replace' and equal_length:
            for shift in range(cn_end - cn_start):
                row_map[cn_start + shift] = intl_start + shift
    return row_map


def align(cn: Dat, intl: Dat, min_match_ratio: float = 0.5) -> tuple[list[int] | None, str]:
    """对齐两张表，返回 (行映射, 说明)。映射为 None 表示放弃该表。"""
    if cn.row_count == intl.row_count:
        return list(range(cn.row_count)), 'row-count-equal'

    columns = pick_key_columns(cn)
    if not columns:
        return None, 'no-key-column'

    row_map = build_row_map(row_signatures(cn, columns), row_signatures(intl, columns))
    matched = sum(1 for target in row_map if target >= 0)
    if matched < cn.row_count * min_match_ratio:
        return None, f'lcs-weak({matched}/{cn.row_count})'
    return row_map, f'lcs(cols={columns},matched={matched}/{cn.row_count})'
