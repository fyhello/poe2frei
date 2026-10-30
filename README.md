# FreiAtlas

Path of Exile 2 客户端补丁工具：设置界面（WPF + WebView2）与补丁执行库。

## 项目结构

| 目录 | 说明 |
|---|---|
| `src/FreiAtlas.Patcher` | 补丁核心库：索引会话、备份/快照、特效、翻译、图标、商城外观、字体、物价等模块 |
| `src/FreiAtlas.Settings` | 设置窗口（`MainWindow.xaml.cs`）与前端资源（`Web/`） |
| `src/FreiAtlas.Core` | 设置模型与共享类型 |
| `tests/FreiAtlas.Patcher.Tests` | 补丁库单元测试（约 480 例） |
| `tests/FreiAtlas.Settings.Tests` | 设置层测试（约 130 例） |
| `libs/` | 第三方依赖：`LibBundle3` / `LibBundledGGPK3` / `LibGGPK3` / `oo2core.dll` |
| `data/poe2/` | 运行时数据（如 `colormod_overrides.json`），随构建复制到输出目录 |
| `fonts/` | 内置字体清单 `font.json`；字体二进制因授权与体积原因不入库 |

## 构建与测试

需要 .NET SDK 10.0.302（见 `global.json`）。

```powershell
dotnet build FreiAtlas.slnx -c Debug
dotnet test tests/FreiAtlas.Patcher.Tests/FreiAtlas.Patcher.Tests.csproj -c Debug
dotnet test tests/FreiAtlas.Settings.Tests/FreiAtlas.Settings.Tests.csproj -c Debug
```

发布（Windows x64）：

```powershell
dotnet publish src/FreiAtlas.Settings/FreiAtlas.Settings.csproj -c Release -r win-x64 --self-contained true
```

`oo2core.dll`、`LibBundle3.dll` 等由 `libs/` 复制到输出目录；发布包必须包含它们，否则索引读写会失败。

## 测试分类

部分用例需要本机安装真实客户端（默认路径不可用时会自行返回）。它们标记为：

```powershell
dotnet test ... --filter "Category!=RequiresGameClient"
```

需要在具备客户端的机器上做真机回归时，去掉该过滤条件。

## 约定

- 源码编码 UTF-8（无 BOM）；行尾由 Git 按 `core.autocrlf` 处理。
- 客户端虚拟路径一律通过 `FreiAtlas.Patcher.Core.VirtualPath.Normalize` 规范化，禁止在各模块自行 `Replace('\\','/')`。
- 客户端索引统一通过 `ClientIndexSession` 打开；除 `WriteIndexIntoGgpk` 的写入路径外不得自行 `new BundledGGPK` / `new LibBundle3.Index`。
- 备份目录名、Zip 条目名等会被文件系统/归档器解释的入口，必须经 `VirtualPath.IsSafeRelative` 校验。
