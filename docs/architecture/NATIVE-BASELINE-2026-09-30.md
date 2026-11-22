# 内置引擎与 C++ Shell 主基线

2026-09-30 完成源码与构建基线迁移。版本继续由根目录 `VERSION` 管理，当前为 `26.9.32.0`；本轮没有生成、安装或发布新的正式 MSIX。

## 清理与优化

- 根构建入口、单架构 MSIX 和 Store Bundle 入口统一构建 `FormatFactoryShellExtension.Native`，清单直接注册原生 DLL。移除 Managed/NativeCandidate 双轨参数及候选输出子目录。
- 移除旧托管 Shell 源码、项目及其测试程序，以及只为托管 Shell 服务的 `SupportedFormatCatalog` 和内部程序集授权。原生格式表仍由 Core 的唯一 `formats.json` 生成。
- 三个 CLSID、线程模型、九个转换目标、批次 JSON 与 `formatfactory://open/?batch=...` 协议保持兼容。EPUB 缩略图和预览仍只注册到 EPUB。
- 移除运行时 FFmpeg 下载、解压安装、回滚和下载源清单；同步移除设置页下载按钮、旧日志/多语言文本、下载专用测试和来源校验脚本。构建阶段使用 `packaging/engine-sources.json` 准备内置引擎。
- 保留用户指定路径、内置引擎、旧本地引擎缓存和系统工具发现。路径规范化后去重，同一实际路径只探测一次；PE 架构读取结果复用。不会删除用户已有的引擎或设置。
- Explorer 菜单状态按选中项逐个检查，遇到不兼容文件即停止；同一个缩略图处理器缓存解码封面，不同尺寸请求复用。预览窗口创建失败立即返回准确错误并清理窗口，允许重试。
- 根构建不再重复编译已由主程序引用的 FilterWorker；其有 RID 的构建明确使用自包含部署，修复 `NETSDK1150`。修正复制目标的目录取值，补齐本地应用输出遗漏的工作进程 DLL，并在缺失时明确失败。明确 19 处 WinUI 集合表达式的数组类型，消除 `CsWinRT1032`，没有禁用分析器。
- 原生构建及原生包检查不再重复计算源码、DLL 或整个包的哈希。保留来源/许可记录，以及编译、必需文件、PE 架构、三个 COM 类、线程模型、EPUB 注册范围与构建元数据检查。本轮未扫描整套内置引擎或旧发布包的哈希。

## 验证结果

| 检查 | 结果 |
| --- | --- |
| 应用 Release 回归 | 593/593 通过，0 跳过 |
| C++ Shell x64 原生测试 | 22/22 通过 |
| 主程序 x64 / ARM64 Release | 均为 0 警告、0 错误 |
| C++ Shell x64 / ARM64 Release | 均编译、链接成功，无编译警告或错误 |
| 隐藏 WinUI 冒烟 | 26/26 通过；不激活可见窗口 |
| PowerShell 语法 | 18 个构建/打包/测试脚本通过 |
| 仓库门禁 | 通过；版本 26.9.32.0，56 个输入扩展 |
| Windows SDK 清单模式 | x64、ARM64 均通过；保留 56 个关联，预览仅 EPUB |
| 新包验证器的合成载荷 | 6/6 通过：两架构正常载荷可接受，旧 comhost、错误 COM 路径、错误 PE 架构、错误 DLL 长度均被拒绝 |
| 工作进程复制与启动 | x64、ARM64 应用输出四个必需文件齐全；x64 从应用目录启动并通过协议响应检查 |

日志和报告位于 `work/baseline-tests.log`、`work/baseline-validation/native-baseline.trx`、`work/baseline-winui-{x64,arm64}.log`、`work/baseline-manifest.log`、`work/baseline-validation/minimal-msix-validation.log`、`work/baseline-validation/worker-runtime.json` 与 `work/ui-regression/native-baseline/results.json`。原生 DLL 与构建记录位于 `work/native-shell/Release/{x64,arm64}/`。

ARM64 在本机只交叉编译，没有执行 ARM64 真机测试。安装后的 Explorer 激活、升级及卸载仍属于正式安装包验收；轻量清单验证产物仅用于测试，不能安装或分发。

## 当前入口

```powershell
# 主程序与原生 Shell
.\build.ps1 -Architecture all -Configuration Release

# 应用回归
dotnet test .\tests\FormatFactoryWinUI.Tests\FormatFactoryWinUI.Tests.csproj -c Release -p:RestoreLockedMode=true

# 原生测试与轻量清单模式检查
pwsh -NoProfile -File .\tests\FormatFactoryShellExtension.Tests\Run.ps1 -Architecture all -ValidateManifest

# 隐藏 WinUI 冒烟
pwsh -NoProfile -File .\tests\WinUiSmoke\Run.ps1 -RunName native-baseline
```

正式打包继续使用 `packaging/build-msix.ps1` 或 `packaging/build-store-msix.ps1`，均默认原生 Shell。包结构检查入口为 `packaging/validate-msix.ps1`。历史候选发布文档仅保留作记录，不能继续使用其中已移除的双轨参数。
