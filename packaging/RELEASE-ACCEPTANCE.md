# 候选构建与正式发布验收

打包及正式验收入口在 PowerShell 7.2 或更新版本（`pwsh`）中运行。

## 1. 构建可验收的候选物

先审阅并提交源码，使工作树干净。运行 `build.ps1 -Architecture all -Msix -CandidateOnly` 或 `packaging/build-store-msix.ps1 -CandidateOnly`。已有完整引擎缓存时可加 `-OfflineEngines`。普通单架构 `build-msix.ps1` 只生成候选 MSIX。

本地安装验收需要在候选构建阶段传入与 Publisher 匹配的证书。证书在测试设备上的信任配置属于测试设备准备；不要在日常开发机静默安装/注册应用。候选阶段输出两个 MSIX、一个 Bundle 和 `.candidate.json`，不会生成 `.msixupload`。候选清单携带源码提交、当前内容指纹、工具链锁哈希和全部包 SHA256；工作树不干净的候选只能评审，不能正式提升。

## 2. 在四个干净 Windows 环境上验收

每个环境记录系统构建号、架构、预装应用和测试身份。复制候选清单与完整候选物，核对 SHA256。四个环境都使用同一个签名 Bundle 的原始字节安装，并记录 Bundle 的 SHA256 及系统选中的包身份、版本和架构。

| 验收环境 | 必需报告 |
| --- | --- |
| Windows 10 22H2 x64（19045） | `device-windows10-x64.json` |
| Windows 10 22H2 ARM64（19045） | `device-windows10-arm64.json` |
| Windows 11 x64（22000 或更新构建） | `device-windows11-x64.json` |
| Windows 11 ARM64（22000 或更新构建） | `device-windows11-arm64.json` |

Windows 10 22H2 是内置 FFmpeg 声明支持的最低版本。Windows 10 ARM64 按可用的原生引擎验收。内置的兼容解码、Calibre、CAJ 和光盘回退工具在 ARM64 上依赖 Windows 11 提供的 x64 仿真。[FFmpeg 构建支持范围](https://github.com/BtbN/FFmpeg-Builds) · [Windows on Arm 仿真范围](https://learn.microsoft.com/windows/arm/apps-on-arm-x86-emulation)

各环境执行下列适用检查，保存日志、截图、任务结果或检测输出。完成后将检查记录为 `passed`。

| 检查名称 | 最低验收内容 |
| --- | --- |
| `clean-install` | 从干净基线安装候选包，检查首次启动、设置和队列页、拖入文件后直接选择输出格式，以及应用内右键。记录包身份、版本、架构和基线 |
| `upgrade`（Windows 11） | 安装上一正式版本，创建设置、历史和待处理任务，再升级到本候选，记录升级前后的版本及数据恢复结果 |
| `restart-persistence`（Windows 10） | 在首版 Windows 10 基线创建设置、历史和待处理任务，关闭并重启候选应用，检查设置和历史保留及任务恢复 |
| `accessibility`（Windows 10） | 运行时切换高对比度、颜色筛选器和动画效果，检查关键控件、队列状态和焦点提示。验证标题栏拖动和双击最大化 |
| `core-conversion` | 从已安装应用执行 Unicode 路径、批量媒体转换、中文/图片/表格 DOCX→PDF、扫描 PDF/OCR、EPUB/Markdown、取消及失败清理；验证内容，不只检查文件存在 |
| `explorer-activation` | 在 Explorer 多选大批支持文件，右键不长期卡住；验证动态目标、批量顺序及重复/混合格式；打开 EPUB 缩略图/预览 |
| `uninstall` | 卸载候选，确认应用及 Explorer 注册清理，源文件和用户输出保留 |
| `native-tests` | 在本机运行同一提交构建的该架构原生测试 EXE，以已安装包的原生 DLL 作为其唯一参数，保存实际输出；ARM64 不能用 x64 主机的交叉编译替代 |
| `engine-corpus` | x64 和 Windows 11 ARM64 以候选包中的引擎运行 `smoke-engines.ps1` 及 CAJ smoke，并核对主程序的实际输出。Windows 10 ARM64 通过已安装应用验证原生 FFmpeg/FFprobe、7-Zip 和 LibreOffice 的实际产物，覆盖媒体、压缩包和文档转换 |
| `arm64-engine-fallback`（Windows 10 ARM64） | 验证原生转换路径可执行。检查仅提供 x64 内置工具的路线显示正确的可用状态或适当的回退，并记录实际选用的引擎和界面提示 |
| `office-macro-disabled`（x64 必需） | 在隔离的 Office 测试基线运行含无害 AutoOpen / Document_Open 宏的 DOC→PDF；宏只尝试写入测试目录标记。记录 Word 版本、宏策略、输入/输出哈希、页数和标记始终不存在的证据，确认真实打开时没有执行宏。只检查脚本文本不足以通过 |

为当前环境准备 `checks.json`。每项包含 `name`、实际 `status` 和本机证据文件的绝对路径 `evidencePaths`。以下为 Windows 10 x64 的模板。Windows 11 的模板用 `upgrade` 替换 `restart-persistence` 和 `accessibility`。ARM64 省略 `office-macro-disabled`，Windows 10 ARM64 另加 `arm64-engine-fallback`。执行完成并取得证据后，再填写 `passed` 和证据路径。

```json
[
  { "name": "clean-install", "status": "pending", "evidencePaths": [] },
  { "name": "restart-persistence", "status": "pending", "evidencePaths": [] },
  { "name": "accessibility", "status": "pending", "evidencePaths": [] },
  { "name": "core-conversion", "status": "pending", "evidencePaths": [] },
  { "name": "explorer-activation", "status": "pending", "evidencePaths": [] },
  { "name": "uninstall", "status": "pending", "evidencePaths": [] },
  { "name": "native-tests", "status": "pending", "evidencePaths": [] },
  { "name": "engine-corpus", "status": "pending", "evidencePaths": [] },
  { "name": "office-macro-disabled", "status": "pending", "evidencePaths": [] }
]
```

完成实际步骤后，在相应测试设备运行记录器。两套 x64 基线都要预装 Word 并完成宏验证。于隔离设备允许测试宏运行，再通过无害标记验证应用实际强制禁用了宏。

```powershell
.\packaging\Record-DeviceAcceptance.ps1 `
  -CandidateManifestPath .\outputs\msix\zzwdh.WinUI-26.9.33.0.candidate.json `
  -ChecksPath .\checks.json -OperatorName '实际验收人员' `
  -CleanMachineBaseline -OutputDirectory .\device-evidence
```

记录器按本机的系统构建号和架构生成上表中的报告名。每份报告包含候选 Bundle 和对应架构包的 SHA256，以及各证据文件的 SHA256。将四份报告和各 `evidence/` 目录中的文件汇入同一目录，保留证据的相对路径，用作 `-DeviceAcceptanceDirectory`。

## 3. 提升同一候选物

```powershell
.\packaging\build-store-msix.ps1 `
  -CandidateManifestPath .\outputs\msix\zzwdh.WinUI-26.9.33.0.candidate.json `
  -DeviceAcceptanceDirectory .\work\release-acceptance `
  -PerformanceBaselinePath .\work\release-baseline\performance.json
```

将例子版本改为当前 `VERSION`。正式入口验证工作树、源码提交、版本、工具链、候选字节和四个验收环境的证据。随后强制运行全部托管回归（非空、零失败、零跳过）、真实 WinUI smoke、0/100/1000 任务生产启动链、九次性能样本、与本机架构包内容逐文件一致的引擎语料和 CAJ 语料。性能通过既有 `Compare-Performance.ps1` 与同机器/运行时/语料的旧基线比较，P95 时延、私有内存、容器数量不得退化超过 10%；基线不可由当前候选自比生成。四阶段启动指标是新口径，检查完整性和恢复语义，并保留 first-interactive、activation-ready、first-restored-task、all-restored；没有旧同口径数据时不宣称启动时间相对改善。报告保存在新的 `work/release-checks/` 目录并绑定两架构包 SHA256；任何失败都会停止。

所有门禁通过才生成 `.msixupload` 及 `.release.json`。正式提升使用验收时的候选原始字节。保留候选清单、自动报告、四份设备报告及其证据，用于发布追溯。

正式性能基线必须包含完整环境、五项指标与九个有效样本，携带较早干净提交的 `source.commit`、`source.fingerprint`、`source.dirty=false` 和早于候选构建的 `capturedAt`。旧报告只能用于独立诊断比较，不能用空报告或当前候选自比作为正式基线。提升结束前再次核对最初接受的候选、设备报告、自动报告及全部证据哈希；期间发生变化时停止并重新验收。

## 支持期与工具链

`global.json` 和 `build/toolchain.lock.json` 必须一致；锁定的 MSVC、SDK、MakeAppx、SignTool 不存在或版本不符时停止。miniz 源码在编译前实际核验锁文件 SHA256。正式提升按 Asia/Shanghai 日期检查锁定运行时的支持期限。26.11.18.0 使用 .NET 11.0.0 正式版和 SDK 11.0.100；发布前核对当前服务补丁与支持政策，升级时同步更新 SDK 锁及工具链锁并重新构建和验收。[官方支持政策](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)。候选构建可继续用于诊断，不代表过期运行时已获准发布。

轻量门禁自测：`tests/FormatFactoryShellExtension.Tests/ReleaseGate.Tests.ps1`。它只生成清楚标记的合成文件，验证校验和、源码/架构不匹配、证据变更、缺失报告和支持期边界；不安装包、不生成正式上传物。
