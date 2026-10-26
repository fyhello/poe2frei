# 汉化词典生成流水线

从纯净客户端快照生成 `dat_translations.json.gz`，供 `FreiAtlas.Patcher` 在打补丁时写入游戏数据表。

## 一键执行

```powershell
pwsh -File scripts/build-translation-dict.ps1
```

默认参数已指向本机的国服与国际服客户端。首次运行会导出两份快照（约 63 MB），后续复用。

常用开关：

| 开关 | 用途 |
|---|---|
| `-RefreshIntl` | 强制重导国际服快照。仅在确认客户端已还原为官方版本时使用 |
| `-SkipVerify` | 跳过注入验证（不建议） |
| `-NoCopy` | 只生成词典，不复制到资源目录 |

## 为什么需要快照

国际服客户端在**打补丁后就不再是纯净数据源**。若直接读 GGPK 提取，读到的「繁体原文」其实是上一次注入的结果，本轮产出必然错误，且错误会累积。

因此流程固定为：

```
官方客户端 ──导出一次──> .cache/poe2-intl-snapshot/  （只读基准）
                                                      ↓
国服客户端 ──每次导出──> .cache/poe2-cn-snapshot/  ──> 提取 ──> 词典
```

快照会被抽样检查：命中简体专有字即判定为污染并终止构建。

客户端还原方式：用 FreiAtlas 的「还原官方底包」，或使用补丁前自动快照。

## 提取规则

只有一条准入条件：

> 写入位置必须精确等于 schema 声明为 `string` 且非数组的字段起点。

对 `foreignrow`、`row`、`i32`、`array` 等位置写 8 字节字符串指针，会破坏外键、数值或数组结构，客户端读取时越界，表现为启动即闪退。历史上十余次闪退全部源于此。

其他约束：

- **schema 宽度必须等于实测行宽**，否则字段偏移不可信，整表跳过
- **国服与国际服行数不同时用最长公共子序列对齐**，绝不按物理行号顺序配对
- **schema 优先取 `validFor & 2` 的定义**，取到 PoE1 定义会导致字段布局整体错位

## schema 版本

`data/poe2/schema.min.json` 是固定版本，其 `createdAt` 必须与游戏客户端版本匹配。

游戏更新后若出现大量表 `width-mismatch`，说明 schema 落后，需要替换该文件。构建日志会打印当前 schema 日期与表数。

## 目录结构

```
scripts/
  build-translation-dict.ps1          一键入口
  translation-pipeline/
    build_dictionary.py               主逻辑：提取 + 生成
    datfile.py                        .datc64 读取与文本判定
    schema.py                         schema 加载与字段布局
    align.py                          行对齐
    SnapshotExporter/                 从客户端导出快照（C#）
    InjectionVerifier/                在快照上模拟注入并断言（C#）
```

两个 C# 工具通过反射在 PowerShell 宿主进程内调用，因为沙箱禁止 dotnet 子进程直接访问游戏目录。

## 验证

`InjectionVerifier` 在快照上模拟完整注入，逐表断言四条不变量：

1. 行数不变
2. 字符串区哨兵位置不变
3. 除 8 字节指针槽外，其余字节一个都不能变
4. 每个写入的指针都能解析回预期译文

任一不满足即视为不可发布，脚本以非零退出码终止。

## 已知覆盖缺口

`CurrencyItems` 的预兆描述等部分展示列，schema 未在该偏移声明 `string` 字段。这类列不会被写入，表现为游戏中残留繁体。

补充方式：确认该列确为展示文本后，更新 schema 定义，或在 `build_dictionary.py` 中增加带验证的例外规则。不要放宽准入条件本身。
