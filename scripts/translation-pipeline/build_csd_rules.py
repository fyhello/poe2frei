"""从国服与国际服 CSD 快照生成 CSD 补漏规则。

CSD（.csd）是纯文本描述文件，结构如下::

    description
        1 active_skill_area_of_effect_radius     ← stat_key（带序号前缀）
        1                                        ← 该 key 的变体数量
            # table_only "Explosion radius@{0}m" ...   ← 默认（英文）文本
        lang "Korean"                            ← 以下属于【上一个 key】的韩语版本
        1
            # table_only "폭발 반경@{0}미터" ...
        lang "Simplified Chinese"
        1
            # table_only "爆炸范围@{0} 米" ...

要点：`lang "X"` 之后的内容是【当前条目】的该语言译文，不是新条目的开始。
解析时必须把 `lang` 行当作「切换后续文本的语言标记」。

运行时汉化会把国际服 CSD 的语言段整体改写为简体，但国际服 CSD 可能缺少
国服新增的词缀条目。缺失条目无法在运行时凭空生成，必须在构建期提取。

输出结构与运行时读取的 `csd_missing_patches.json` 一致::

    { "<虚拟路径>": { "<stat_key>": "<简体描述>" } }

描述值保留 CSD 原始格式（含 `# table_only "..."` 前缀与后续指令），
因为运行时是整块替换，不做二次解析。
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys

for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, 'reconfigure'):
        _stream.reconfigure(encoding='utf-8', errors='replace')

_LANG_PATTERN = re.compile(r'^\s*lang\s+"([^"]+)"\s*$', re.IGNORECASE)


def read_text(path: str) -> str:
    """读取 CSD 文本，自动识别 UTF-16 / UTF-8。"""
    with open(path, 'rb') as handle:
        raw = handle.read()
    if raw[:2] in (b'\xff\xfe', b'\xfe\xff'):
        return raw.decode('utf-16', 'replace')
    sample = raw[:4096]
    if sample.count(b'\x00') > len(sample) // 4:
        return raw.decode('utf-16-le', 'replace')
    return raw.decode('utf-8-sig', 'replace')


def parse_entries(lines: list[str]) -> dict[str, dict[str, str]]:
    """解析为 {stat_key: {语言: 文本}}，语言键 '' 表示默认（英文）文本。

    状态机：
      - 缩进恰好一级、非注释非引号、非纯数字的行 → 新 stat_key
      - 其余行 → 当前语言的文本（含「变体数量」行，运行时按行原样输出）
      - `lang "X"` → 切换当前语言，文本仍归属当前 stat_key

    文本保留原始缩进与换行：运行时是逐行拼接输出，缩进决定 CSD 的层级语义。
    """
    entries: dict[str, dict[str, list[str]]] = {}
    current_key: str | None = None
    current_lang = ''

    for raw_line in lines:
        line = raw_line.rstrip('\n')

        language = _LANG_PATTERN.match(line)
        if language:
            current_lang = language.group(1).strip()
            if current_key is not None:
                entries[current_key].setdefault(current_lang, [])
            continue

        stripped = line.strip()
        if not stripped:
            continue

        indent = len(line) - len(line.lstrip('\t'))
        # stat_key 行与「变体数量」行缩进相同，靠「是否为纯数字」区分
        if indent == 1 and not stripped.startswith(('#', '"')) and not stripped.isdigit():
            current_key = stripped
            current_lang = ''
            entries.setdefault(current_key, {})
            continue

        if current_key is not None:
            entries[current_key].setdefault(current_lang, []).append(line)

    return {
        key: {lang: '\r\n'.join(body) for lang, body in langs.items() if body}
        for key, langs in entries.items()
    }


def collect(directory: str, language: str) -> dict[str, dict[str, str]]:
    """收集某个语言在全部 CSD 文件中的条目。"""
    result: dict[str, dict[str, str]] = {}
    for root, _dirs, files in os.walk(directory):
        for name in sorted(files):
            if not name.lower().endswith('.csd'):
                continue
            path = os.path.join(root, name)
            relative = os.path.relpath(path, directory).replace('\\', '/')
            try:
                text = read_text(path)
            except OSError as error:
                print(f'  跳过 {relative}: {error}', file=sys.stderr)
                continue

            lines = text.replace('\r\n', '\n').replace('\r', '\n').split('\n')
            entries = parse_entries(lines)
            matched = {
                key: langs[language]
                for key, langs in entries.items()
                if langs.get(language)
            }
            if matched:
                result[relative] = matched
    return result


def collect_keys(directory: str) -> dict[str, set[str]]:
    """递归收集每个 CSD 文件出现过的全部 stat_key（不限语言）。"""
    result: dict[str, set[str]] = {}
    for root, _dirs, files in os.walk(directory):
        for name in sorted(files):
            if not name.lower().endswith('.csd'):
                continue
            path = os.path.join(root, name)
            relative = os.path.relpath(path, directory).replace('\\', '/')
            try:
                text = read_text(path)
            except OSError:
                continue
            lines = text.replace('\r\n', '\n').replace('\r', '\n').split('\n')
            keys = set(parse_entries(lines))
            if keys:
                result[relative] = keys
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description='从 CSD 快照生成补漏规则')
    parser.add_argument('--cn-snapshot', required=True, help='国服 CSD 快照目录')
    parser.add_argument('--intl-snapshot', required=True, help='国际服 CSD 快照目录')
    parser.add_argument('--output', required=True, help='输出 csd_missing_patches.json')
    parser.add_argument('--cn-language', default='Simplified Chinese')
    parser.add_argument('--intl-language', default='Traditional Chinese')
    args = parser.parse_args()

    for directory, label in ((args.cn_snapshot, '国服'), (args.intl_snapshot, '国际服')):
        if not os.path.isdir(directory):
            print(f'错误：{label} CSD 快照目录不存在：{directory}', file=sys.stderr)
            return 1

    print('解析国服 CSD...')
    cn = collect(args.cn_snapshot, args.cn_language)
    print('解析国际服 CSD...')
    # 关键：国际服也按「简体」语言段收集。补录的目标不是「国际服没有这个 key」，
    # 而是「国际服这个 key 没有简体译文」。绝大多数条目两边都有，只是国际服
    # 不提供简体段，运行时改写需要一份简体文本才能填进去。
    intl = collect(args.intl_snapshot, args.cn_language)

    rules: dict[str, dict[str, str]] = {}
    for file_name, cn_entries in sorted(cn.items()):
        intl_entries = intl.get(file_name, {})
        missing = {key: value for key, value in cn_entries.items() if key not in intl_entries}
        if missing:
            rules[f'data/statdescriptions/{file_name}'] = missing

    total = sum(len(v) for v in rules.values())
    print()
    print(f'国服 CSD 有简体条目: {len(cn)} 个文件')
    print(f'国际服 CSD 有简体条目: {len(intl)} 个文件')
    print(f'国际服缺简体段: {total}（{len(rules)} 个文件）')

    os.makedirs(os.path.dirname(os.path.abspath(args.output)), exist_ok=True)
    with open(args.output, 'w', encoding='utf-8', newline='\n') as handle:
        json.dump(rules, handle, ensure_ascii=False, indent=2)

    print(f'输出: {args.output}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
