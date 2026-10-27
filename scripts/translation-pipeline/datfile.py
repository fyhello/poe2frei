""".datc64 二进制读取与文本判定。

文件结构::

    uint32   rowCount
    byte[]   rowCount * rowSize    固定区，每个字段按类型定长排列
    0xBB * 8                       固定区与字符串区的分隔哨兵
    byte[]                         字符串区，UTF-16LE 文本以 4 字节对齐的 NUL 结束

行内 string 字段存放 8 字节小端偏移，偏移量相对哨兵起点（即字符串区开头）。
"""

from __future__ import annotations

import struct

SENTINEL = b'\xbb' * 8
MAX_TEXT_CHARS = 4000


class DatError(Exception):
    """`.datc64` 结构无法解析。"""


class Dat:
    """已解析的表，支持按 (行, 列) 读取字符串字段。"""

    __slots__ = ('row_count', 'row_length', 'fixed', 'variable')

    def __init__(self, data: bytes) -> None:
        if len(data) < 8:
            raise DatError('文件过短')

        row_count = struct.unpack_from('<I', data, 0)[0]
        if row_count == 0:
            raise DatError('行数为 0')

        body = data[4:]
        boundary = -1
        pos = 0
        while True:
            pos = body.find(SENTINEL, pos)
            if pos < 0:
                break
            # 哨兵必须恰好落在固定区末尾，否则可能是数据中的巧合匹配
            if pos % row_count == 0:
                boundary = pos
                break
            pos += 1
        if boundary < 0:
            raise DatError('未找到字符串区起始哨兵')

        self.row_count = row_count
        self.row_length = boundary // row_count
        self.fixed = body[:boundary]
        self.variable = body[boundary:]

    def pointer(self, row: int, col: int) -> int:
        """读取 (row, col) 处的 8 字节字符串偏移。"""
        return struct.unpack_from('<Q', self.fixed, row * self.row_length + col)[0]

    def text_at(self, offset: int) -> str:
        """按字段偏移读取字符串；越界或不可读时返回空串。"""
        variable = self.variable
        if offset <= 0 or offset >= len(variable) - 1:
            return ''
        end = variable.find(b'\x00\x00\x00\x00', offset)
        if end < 0:
            return ''
        # 结束位置必须落在 UTF-16 码元边界上
        while (end - offset) % 2:
            end = variable.find(b'\x00\x00\x00\x00', end + 1)
            if end < 0:
                return ''
        if end - offset > MAX_TEXT_CHARS * 2:
            return ''
        return variable[offset:end].decode('utf-16-le', 'replace')

    def text(self, row: int, col: int) -> str:
        """读取 (row, col) 处的字符串字段值。"""
        return self.text_at(self.pointer(row, col))


def has_cjk(text: str) -> bool:
    """是否包含中日韩统一表意文字。"""
    return any(0x4E00 <= ord(ch) <= 0x9FFF for ch in text)


def is_clean(text: str) -> bool:
    """是否为可安全写入的文本。

    允许制表符、换行、回车——暗金风味故事等多行文本依赖它们。
    拒绝控制字符、私用区、代理项以及哨兵填充值，这些是字段错位或二进制数据的特征。
    """
    if not text or len(text) > MAX_TEXT_CHARS:
        return False
    for ch in text:
        code = ord(ch)
        if code < 32 and code not in (9, 10, 13):
            return False
        if code == 0xBBBB or code == 0xFEFE:
            return False
        if 0xD800 <= code <= 0xDFFF:
            return False
        if 0xE000 <= code <= 0xF8FF:
            return False
    return True


_ASSET_PREFIXES = ('data/', 'metadata/', 'art/', 'audio/', 'shaders/', 'effects/', 'ui/')
_ASSET_SUFFIXES = (
    '.csd', '.dds', '.ais', '.ogg', '.ao', '.mat', '.pet', '.fxgraph',
    '.bk2', '.txt', '.json', '.xml',
)


def is_asset_path(text: str) -> bool:
    """是否为资源路径而非展示文本。"""
    lowered = text.lower()
    if lowered.startswith(_ASSET_PREFIXES):
        return True
    return lowered.endswith(_ASSET_SUFFIXES)
