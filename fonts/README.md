# 内置字体目录

本目录用于存放随程序分发的内置字体。

## 文件结构

| 文件 | 是否入库 | 说明 |
|---|---|---|
| `font.json` | ✅ 入库 | 字体清单：family / displayName / file / aliases |
| `*.ttf` / `*.otf` | ❌ 不入库 | 字体二进制文件，需本地放置 |

## 为什么字体文件不入库

字体文件通常有独立授权，随代码仓库分发可能超出授权范围；且单个中文字体 8~20 MB，
会永久占用仓库历史。因此 `.gitignore` 排除了 `fonts/*.ttf` 与 `fonts/*.otf`。

## 本地构建前需要做什么

把字体文件放入本目录，文件名与 `font.json` 里的 `file` 字段一致，例如：

```
fonts/FZZhunYuan.ttf
```

缺少字体文件时：

- 编译不受影响
- `FontInstaller.Bundled` 返回 null
- 字体补丁会明确报「未找到内置字体配置」，不会静默回退到其它字体

## 换字体

替换本目录下的字体文件，并同步修改 `font.json` 的 `family` / `displayName` / `file`
三个字段即可，无需改代码。

`family` 必须是字体的**英文族名**（Windows 下 WPF `Fonts.SystemFontFamilies` 的 `Source`），
不是中文显示名。
