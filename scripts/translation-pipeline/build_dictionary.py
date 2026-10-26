"""从纯净快照生成汉化词典。

数据来源：两个已导出的 .datc64 快照目录（国际服繁中、国服简中）。
不直接读客户端——客户端在打补丁后就不再是纯净数据源，用它做提取会把上一次
注入的结果当成原文，产出无法收敛的错误词典。

提取规则只有一条需要守住：

    只写 schema 明确声明为 string 且非数组的字段。

历史上所有启动即闪退，都是因为往 foreignrow、row、i32、array 等位置写了 8 字节
字符串指针，破坏了外键、数值或数组结构，客户端读取时越界。
"""

from __future__ import annotations

import argparse
import csv
import gzip
import json
import os
import sys

import schema as schema_module
from align import align
from datfile import Dat, DatError, has_cjk, is_asset_path, is_clean

# Windows 控制台默认 GBK，输出日志里的示例文本可能含无法编码的字符。
for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, 'reconfigure'):
        _stream.reconfigure(encoding='utf-8', errors='replace')

# 简体专有字，繁体文本中不会出现。用于判断快照是否已被补丁污染。
_SIMPLIFIED_ONLY = set(
    '击护传伤点术质词缀属连终价药无灵装备开关数体宝结战军减冻净变处复够头夺奖'
    '妇婴宁实宠审对导寻层岁岛币师带帮并广庄庆库应废异弃张弹强归当录彻径总恶悦'
    '惊惧爱态恋'
)

_POLLUTION_THRESHOLD = 0.01


def check_snapshot_purity(
    snapshot_dir: str,
    sample_tables: list[str],
    tables: dict[str, dict],
) -> tuple[bool, str]:
    """抽样检查国际服快照是否为繁体原文。

    只采样 schema 声明的 string 字段。按固定字节步长扫描会读到字段中间位置，
    解出字节错位的乱码，把正常快照误判为污染。

    快照一旦被补丁污染，提取出来的「原文」就是上次的译文，本轮产出必然错误。
    返回 (是否纯净, 说明)。
    """
    sampled = 0
    polluted = 0
    offenders: list[str] = []

    for table_name in sample_tables:
        definition = tables.get(table_name)
        if definition is None:
            continue
        path = os.path.join(snapshot_dir, f'{table_name}.datc64')
        if not os.path.exists(path):
            continue
        with open(path, 'rb') as handle:
            try:
                dat = Dat(handle.read())
            except DatError:
                continue

        columns = [offset for offset, _ in schema_module.string_offsets(definition)]
        step = max(1, dat.row_count // 60)
        for row in range(0, dat.row_count, step):
            for column in columns:
                if column + 8 > dat.row_length:
                    continue
                text = dat.text(row, column)
                if not text or len(text) < 2 or not has_cjk(text):
                    continue
                if not is_clean(text) or is_asset_path(text):
                    continue
                sampled += 1
                if any(ch in _SIMPLIFIED_ONLY for ch in text):
                    polluted += 1
                    if len(offenders) < 3:
                        offenders.append(f'{table_name}: {text[:40]}')

    if sampled == 0:
        return True, '样本为空，跳过纯净度检查'

    ratio = polluted / sampled
    if ratio > _POLLUTION_THRESHOLD:
        detail = '；'.join(offenders)
        return False, (
            f'快照疑似已被补丁污染：{polluted}/{sampled} 条样本含简体专有字（{ratio:.1%}）。'
            f'请先还原官方客户端再重新导出快照。样例：{detail}'
        )
    return True, f'样本 {sampled} 条，简体特征 {polluted} 条（{ratio:.2%}）'


def load_dat(directory: str, table_name: str) -> Dat | None:
    path = os.path.join(directory, f'{table_name}.datc64')
    if not os.path.exists(path):
        return None
    with open(path, 'rb') as handle:
        return Dat(handle.read())


def extract_table(
    cn: Dat,
    intl: Dat,
    row_map: list[int],
    string_offsets: list[tuple[int, str]],
) -> list[tuple[int, int, int, str, str]]:
    """按 schema 声明的 string 字段逐行提取。

    返回 [(国服行, 国际服行, 列, 繁体原文, 简体译文)]。
    """
    results: list[tuple[int, int, int, str, str]] = []
    seen: set[tuple[int, int]] = set()

    for column, _field_name in string_offsets:
        if column + 8 > cn.row_length or column + 8 > intl.row_length:
            continue
        for cn_row in range(cn.row_count):
            intl_row = row_map[cn_row]
            if intl_row < 0 or intl_row >= intl.row_count:
                continue

            simplified = cn.text(cn_row, column)
            if not simplified or not is_clean(simplified):
                continue
            if not has_cjk(simplified) or is_asset_path(simplified):
                continue

            traditional = intl.text(intl_row, column)
            if not traditional or not is_clean(traditional) or is_asset_path(traditional):
                continue
            if traditional == simplified:
                continue

            key = (intl_row, column)
            if key in seen:
                continue
            seen.add(key)
            results.append((cn_row, intl_row, column, traditional, simplified))

    return results


def build_base_items(
    snapshot_dir: str,
    rows: list[tuple[int, str]],
) -> dict[str, str]:
    """构建底子道具映射：Metadata 路径 -> 简体名称。

    底子名在国际服表里是 C=32（Name 字段），路径在 C=0（Id 字段）。
    """
    intl = load_dat(snapshot_dir, 'BaseItemTypes')
    if intl is None:
        return {}

    names = {intl_row: text for intl_row, text in rows}
    result: dict[str, str] = {}
    for row in range(intl.row_count):
        metadata = intl.text(row, 0)
        if not metadata.startswith('Metadata/Items/'):
            continue
        simplified = names.get(row)
        if simplified:
            result[metadata] = simplified
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description='从纯净快照生成汉化词典')
    parser.add_argument('--schema', required=True, help='schema.min.json 路径')
    parser.add_argument('--intl-snapshot', required=True, help='国际服繁中快照目录')
    parser.add_argument('--cn-snapshot', required=True, help='国服简中快照目录')
    parser.add_argument('--output', required=True, help='输出词典 .json.gz')
    parser.add_argument('--report', help='构建报告 CSV 输出路径')
    parser.add_argument('--skip-purity-check', action='store_true', help='跳过快照纯净度检查')
    args = parser.parse_args()

    summary = schema_module.schema_summary(args.schema)
    print(f'schema: version={summary["version"]} 表数={summary["table_count"]}')

    tables = schema_module.load_tables(args.schema)
    print(f'PoE2 表定义: {len(tables)}')

    if not args.skip_purity_check:
        pure, message = check_snapshot_purity(
            args.intl_snapshot,
            ['ClientStrings', 'ActiveSkills', 'BaseItemTypes', 'PassiveSkills'],
            tables,
        )
        print(f'纯净度: {message}')
        if not pure:
            print('错误：快照不可用，终止构建。', file=sys.stderr)
            return 1

    catalogue: dict[str, dict] = {}
    report: list[tuple] = []
    total_patches = 0
    base_item_rows: list[tuple[int, str]] = []

    for table_name in sorted(tables):
        definition = tables[table_name]
        if not (definition['validFor'] & 2):
            continue

        try:
            cn = load_dat(args.cn_snapshot, table_name)
            intl = load_dat(args.intl_snapshot, table_name)
        except DatError as error:
            report.append((table_name, 'skip', 0, 0, 0, f'parse:{error}'))
            continue

        if intl is None or cn is None:
            report.append((table_name, 'skip', 0, 0, 0, 'snapshot-missing'))
            continue

        # schema 宽度必须与实测一致，否则字段偏移不可信，整表跳过
        width = schema_module.table_width(definition)
        if width != cn.row_length or width != intl.row_length:
            report.append((
                table_name, 'skip', cn.row_count, intl.row_count, 0,
                f'width-mismatch(schema={width},cn={cn.row_length},intl={intl.row_length})',
            ))
            continue

        row_map, alignment = align(cn, intl)
        if row_map is None:
            report.append((table_name, 'skip', cn.row_count, intl.row_count, 0, alignment))
            continue

        offsets = schema_module.string_offsets(definition)
        pairs = extract_table(cn, intl, row_map, offsets)

        if table_name == 'BaseItemTypes':
            base_item_rows = [(intl_row, simplified) for _, intl_row, column, _, simplified in pairs if column == 32]

        if not pairs:
            report.append((table_name, 'ok', cn.row_count, intl.row_count, 0, f'{alignment}|no-pair'))
            continue

        patches: list[dict] = []
        seen: set[tuple[int, int]] = set()
        for _, intl_row, column, _, simplified in pairs:
            if intl_row >= intl.row_count or column + 8 > intl.row_length:
                continue
            key = (intl_row, column)
            if key in seen:
                continue
            seen.add(key)
            patches.append({'Row': intl_row, 'Col': column, 'Text': simplified})

        if not patches:
            report.append((table_name, 'ok', cn.row_count, intl.row_count, 0, f'{alignment}|filtered-out'))
            continue

        key = f'{table_name.lower()}.datc64'
        catalogue[key] = {
            'FileName': key,
            'RowSize': intl.row_length,
            'RowPatches': patches,
        }
        total_patches += len(patches)
        report.append((table_name, 'ok', cn.row_count, intl.row_count, len(patches), alignment))

    base_items = build_base_items(args.intl_snapshot, base_item_rows)
    document = {'Tables': catalogue, 'BaseItems': base_items}

    payload = json.dumps(document, ensure_ascii=False, separators=(',', ':')).encode('utf-8')
    os.makedirs(os.path.dirname(os.path.abspath(args.output)), exist_ok=True)
    with gzip.open(args.output, 'wb', compresslevel=9) as handle:
        handle.write(payload)

    if args.report:
        os.makedirs(os.path.dirname(os.path.abspath(args.report)), exist_ok=True)
        with open(args.report, 'w', encoding='utf-8', newline='\n') as handle:
            writer = csv.writer(handle)
            writer.writerow(['Table', 'Status', 'CnRows', 'IntlRows', 'Patches', 'Note'])
            writer.writerows(report)

    succeeded = sum(1 for entry in report if entry[1] == 'ok' and entry[4] > 0)
    skipped = sum(1 for entry in report if entry[1] == 'skip')
    size_mb = os.path.getsize(args.output) / 1024 / 1024

    print()
    print(f'表数: {len(catalogue)}（有产出 {succeeded}，跳过 {skipped}）')
    print(f'补丁: {total_patches}')
    print(f'底子: {len(base_items)}')
    print(f'词典: {size_mb:.2f} MB -> {args.output}')

    if total_patches == 0:
        print('错误：没有提取到任何补丁。', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
