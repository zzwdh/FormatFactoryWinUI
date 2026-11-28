# MSIX 打包

项目以 MSIX 作为唯一正式交付和 Microsoft Store 提交格式。

每次正式构建生成一个包含 x64 和 ARM64 的 Bundle，两个架构包使用同一应用身份。

| 架构 | 清单最低系统版本 | 适用设备 |
| --- | --- | --- |
| x64 | `10.0.19045.0` | Windows 10 22H2 x64 和 Windows 11 x64 |
| ARM64 | `10.0.19045.0` | Windows 10 22H2 ARM64 使用原生转换引擎。Windows 11 ARM64 可通过 x64 仿真运行其他内置工具 |

打包与正式发布检查使用 PowerShell 7.2 或更新版本（`pwsh`）。入口会拒绝旧版解释器，避免运行到打包中途才因运行时 API 不可用而失败。

## 首次配置

当前已写入 Partner Center 提供的包身份。后续如果产品身份发生变化，再更新 `msix-config.json` 中的以下字段：

- `PackageName`：Package/Identity/Name
- `Publisher`：Package/Identity/Publisher
- `PublisherDisplayName`：面向用户显示的发布者名称

`Publisher` 必须与签名证书的 Subject 完全一致。当前配置为：PackageName=`zzwdh.WinUI`、Publisher=`CN=B07EB5A4-EC7F-428F-8829-9FB109CE5A74`、PublisherDisplayName=`zzwdh`。

版本只在仓库根目录 `VERSION` 中维护，格式必须是 MSIX 接受的四段数字，例如 `1.2.3.4`。程序集、单体 MSIX 与 Bundle 不再各自保存版本副本。

应用支持的输入扩展只在 `src/FormatFactory.Core/Models/formats.json` 中维护。该文件嵌入 Core 程序集供主程序使用，构建时生成原生 Shell 格式表，并在打包时生成文件关联、Share Target 和 Explorer 右键菜单清单；重复、大小写或格式错误会令构建立即失败。

## 统一构建入口

不要依赖解决方案文件推断平台映射。仓库根目录脚本会先校验版本、扩展清单与 Shell 注册，再逐项目构建：

```powershell
.\build.ps1 -Architecture x64
.\build.ps1 -Architecture arm64
.\build.ps1 -Architecture all
```

同时生成 MSIX 时增加 `-Msix -CandidateOnly`。`-Architecture all` 生成双架构候选 Bundle 及带源码和包哈希的候选清单：

```powershell
.\build.ps1 -Architecture all -Msix -CandidateOnly
```

## 直接生成包

```powershell
.\packaging\build-msix.ps1 -Architecture x64
.\packaging\build-msix.ps1 -Architecture arm64
```

生成双架构候选 Bundle，不生成正式上传物：

```powershell
.\packaging\build-store-msix.ps1 -CandidateOnly
```

Release 构建与 MSIX 发布会按 x64、ARM64 专属 NuGet 锁文件执行严格还原；锁文件与依赖不一致时直接失败。`-NoRestore` 仅用于已预先还原**同一目标架构**且 `obj/project.assets.json` 未被另一架构恢复覆盖的单架构构建。双架构脚本依次切换运行时资产，应保留默认的锁定还原：

```powershell
.\packaging\build-store-msix.ps1 -OfflineEngines -CandidateOnly
```

生成结果位于 `outputs/msix/`。每次构建都会先清理对应架构的 `work/publish-*` 与 staging 目录，并执行以下发布门禁：

- Shell Extension publish 和 staging 包含原生 DLL、构建记录与 `FormatFactoryShellExtension.Native.licenses.zip`。许可证归档收录 Rust 标准库、flate2、CRC32 及其锁定依赖的声明。
- 单体 MSIX 打包后再次检查上述文件，缺少任意文件立即失败。
- Store 脚本会删除同名旧单体包，确保 x64 与 ARM64 都来自本轮构建。
- Bundle 必须包含本轮生成的 x64 与 ARM64 单体包。
- 单体 MSIX 的最低系统版本必须与上表的架构要求一致。
- Bundle 的 Name、Publisher 和 Version 必须与 `msix-config.json`、根 `VERSION` 完全一致；构建脚本会显式传递版本，禁止 MakeAppx 改用时间版本。
- `.msixupload` 必须且只能在根目录包含本轮生成的 Bundle。
- 根版本、嵌入式格式目录、文件关联和 Share Target 必须来自唯一元数据源。
- 正式项目注册 Explorer 命令以及仅适用于 EPUB 的缩略图、预览处理器；三个 COM 类与对应清单必须一致，不包含 Widget 占位原型。
- PDB 与应用图标源稿不会复制到发布包。

候选清单记录 Git 提交、源码内容指纹、工作树是否干净、工具链锁哈希、两架构包和 Bundle 的 SHA256。正式入口使用同一干净提交的已验收候选生成 `.msixupload`，执行托管回归、WinUI 冒烟、0/100/1000 任务真实启动恢复、相对可比旧基线的性能检查，以及包内引擎和 CAJ 语料检查。Windows 10 22H2、Windows 11 各有 x64 和 ARM64 两份实际验收报告，四份报告对应同一次候选构建。完整过程见 [发布验收协议](RELEASE-ACCEPTANCE.md)。

如果需要本机安装测试，传入与清单 Publisher 匹配的 PFX：

```powershell
.\packaging\build-msix.ps1 -Architecture x64 -CertificatePath .\packaging\FormatFactoryWinUI-test.pfx
```

Microsoft Store 会在认证和发布流程中重新签名；提交前必须使用 Partner Center 的真实包身份生成包，无需为 MSIX Store 提交购买受信任 CA 代码签名证书。未传证书时生成的是未签名候选物，不能直接本机安装；商店外分发需要自行签名。商店上传时可同时提供 x64 和 arm64 包，让 Store 按设备架构分发。参见 [Microsoft Store MSIX 包要求](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements#code-signing-for-microsoft-store-submissions)。

## C++ Shell 主基线

`src/FormatFactoryShellExtension.Native` 提供 C++20 原生 DLL，使用经典 COM/WRL 实现 Explorer 命令、EPUB 缩略图与 EPUB 预览处理器，静态链接 C/C++ 运行库。主程序一次自包含发布会同时输出 `FormatFactory.Documents.dll`、隔离 IFilter 的 `FormatFactory.FilterWorker` 和目标架构的 `FormatFactory.ConversionWorker.exe`。同目录下的 `FormatFactory.ConversionWorker.licenses.zip` 包含锁定版本 Rust 标准库的版权说明，以及 Cargo 为该架构解析的各依赖包所附许可文件。打包脚本在暂存前检查这些文件。

三个 CLSID、线程模型、Explorer 子命令 GUID、批次 JSON、`formatfactory://open/?batch=...` 协议以及文件关联保持不变。格式扩展表在构建时由唯一的 `formats.json` 生成；Rust 静态库处理 EPUB 的 ZIP 目录、成员读取和路径。miniz 的源码和许可证位于 vendor 目录，用于在原生测试中生成独立 ZIP 夹具。默认产物统一位于 `outputs/msix/`，已移除 `Managed` / `NativeCandidate` 双轨选择和旧托管项目。

```powershell
.\src\FormatFactoryShellExtension.Native\build.ps1 -Architecture all -RunTests
.\packaging\build-msix.ps1 -Architecture x64 -OfflineEngines
.\packaging\build-msix.ps1 -Architecture arm64 -OfflineEngines
.\packaging\build-store-msix.ps1 -OfflineEngines -CandidateOnly
```

`packaging/validate-msix.ps1` 检查主程序、FilterWorker、自包含部署文件、页面 XBF/PRI、原生 DLL 的 PE 架构、三个 COM 类和 EPUB 处理器注册；还核对原生 DLL 与被测试构建记录的 SHA256。原生包验证还核对 Shell 构建记录中的 Rust 版本与 Cargo.lock 哈希，并读取随包许可证。原生构建会校验生成测试夹具所用的 miniz 源码。x64 主机可运行 x64 原生测试并交叉编译 ARM64；未执行另一架构的测试会明确标为候选，正式提升需该架构设备验收。普通编译和原生测试不触发数 GB 内置引擎的打包扫描。Explorer 未缓存的快速状态查询返回 `E_PENDING`，只在允许慢查询时枚举选择项，根菜单和九个子命令共享短期分类缓存。

## 内置转换引擎

SDK 固定为 .NET 11 正式版 `11.0.100`（见根目录 `global.json`）。`build/toolchain.lock.json` 同时固定 MSVC 14.44.35207 / 编译器 19.44.35229.0、Windows SDK 10.0.28000.0、MakeAppx/SignTool 10.0.28000.2526；缺少或版本不符即失败，不自动选择较新工具。自包含发布使用 .NET 运行时 `11.0.0`。正式提升按 Asia/Shanghai 日期检查锁文件记录的支持期限。升级工具链时同步更新 `global.json` 与锁文件，并重新验证两个架构的运行时。[官方支持政策](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)。`build-msix.ps1` 自动调用 `prepare-engines.ps1`，从锁定来源准备相应架构引擎，并将整个 `Engines` 目录加入 MSIX。只构建普通程序集不会把数百 MB 的引擎复制到每个调试输出目录。

```powershell
# 首次联网准备，下载完成后支持离线重建
.\packaging\prepare-engines.ps1 -Architecture all
# 校验并复用本地锁定缓存；任何缺失或哈希错误都会停止
.\packaging\prepare-engines.ps1 -Architecture all -Offline
# 按锁文件还原 .NET 依赖，并使用本地引擎缓存生成双架构 MSIX
.\packaging\build-store-msix.ps1 -OfflineEngines -CandidateOnly
```

下载缓存位于 `work/engine-downloads`，已准备运行文件位于 `work/prepared-engines/<arch>/Engines`。`-NoRestore` 只控制 .NET 依赖还原，`-OfflineEngines` 才控制引擎下载。`prepare-engines.ps1 -Force` 会重新提取/裁剪；默认仅在来源或准备脚本变化时重新准备，复用前仍检查每个文件的长度和 SHA256。被修改、缺失或多出的运行文件不会静默使用。

包括 7-Zip、FFmpeg/ffprobe、LibreOffice、Calibre（ebook-convert、ebook-meta 和 worker）、独立 Python CAJ 转换环境、icedax/readom 光盘后备工具。7-Zip、FFmpeg 和 LibreOffice 提供原生 x64/ARM64。内置的兼容解码器、Calibre、CAJ、光盘后备组件在 Windows 11 ARM64 上使用 x64 仿真。Windows 10 ARM64 根据可用的原生及外部工具提供转换路线。[Windows on Arm 仿真范围](https://learn.microsoft.com/windows/arm/apps-on-arm-x86-emulation)

MakeMKV 因 Windows 再分发许可限制仅附带未修改的官方安装器，应用设置页由用户选择安装，且仍受厂商试用/注册规则约束；不会把安装器误判为可直接使用的蓝光转换引擎。

主 FFmpeg 采用 GPL shared 构建，两个命令行工具共用七个私有共享 DLL；每个 DLL 都必须通过文件、哈希、架构和导入依赖检查。兼容解码引擎继续单独保留。Windows App SDK 按实际功能引用 WinUI、Foundation 和 InteractiveExperiences 组件，Base 由其依赖保留，不再通过完整元包携带未使用的 AI、ML、Search 和 Widgets 组件。.NET 与 Windows App SDK 继续自包含，安装后的启动和转换不要求另行下载运行时；未开启托管裁剪。瘦身候选和基线对比的验证方法见 [包体优化说明](../docs/engines/ENGINE-SIZE-OPTIMIZATION.md)。

兼容 LibreMPEG 按输入解码路线重新编译，两个工具共用六个私有 DLL，保留 FFV1/PCM 中间文件、软件 AV1/JPEG XL、ICC、压缩数据与字符转换。`engine-sources.json` 此项锁定的是对应源码归档；`packaging/ffmpeg` 保存便携构建工具锁、配置与构建脚本。准备过程先提供 CAJ 已有的隔离 Python，首次构建后按输入指纹和逐文件哈希复用运行时。开发源码与工具仅进入构建缓存。具体保留范围、隐式滤镜和转换验收见 [FFmpeg 裁剪说明](../docs/engines/FFMPEG-PRUNING.md)。

裁剪说明与许可/源码入口见 `BUNDLED-ENGINES.md` 及各组件 `sources.lock.json`。LibreOffice 保留原生转换库、过滤器、Math/图表/OLE/Basic/VBA 支持、私有字体、布局资源和所有语言的断字字典；移除独立 Python、Java UNO 组件、预装脚本/向导、交互编辑资源和相应注册项。Calibre 保留完整冻结 Python 归档、转换/元数据插件、实际依赖的 Qt 与图片/字体/语言资源；移除 WebEngine、阅读器/编辑器/服务端、语音、新闻和多媒体等应用未调用的组件。当前软件先把其他文档/图片输入转换成 EPUB，再调用 Calibre 输出 MOBI/AZW3；此配置不提供独立 Calibre 的 PDF 输出等完整命令行能力。两项裁剪均保留许可说明，并拒绝目录联接或越界路径。具体保留项与验证口径见 [文档与电子书引擎裁剪](../docs/engines/OFFICE-EBOOK-PRUNING.md)。MSI 只进行管理员映像提取，不安装或注册 LibreOffice/Calibre；提取串行执行并设置超时。

附加发布门禁：

- 所有下载先校验固定 SHA256；光盘组件同时按照上游锁定的 SHA512 验证。
- 必需程序及其伴随文件必须存在，主要原生程序必须匹配声明的运行架构；LibreOffice 原包的跨架构辅助文件与托管程序集按其明确身份单独验证。
- CAJ 的所有 EXE、DLL、PYD 会检查普通导入和延迟导入，Visual C++ 依赖必须由组件私有目录提供，不能借用构建机已安装的运行库。
- LibreOffice/Calibre 裁剪后检查所有保留原生模块的普通与延迟导入，原包中的私有依赖不能因裁剪而缺失；准备脚本和裁剪模块都参与缓存指纹。
- `bundle-manifest.json` 记录来源、运行架构、用途（运行时/安装器）、每个文件长度与 SHA256。
- 复制到 staging 后再次核对 receipt，MSIX 完成后打开成品 ZIP 并逐文件校验所有引擎内容及 receipt 自身；缺失、额外或损坏文件都会停止交付。
- 通过 `smoke-engines.ps1` 执行实际音视频编码、并发文档转换、中文/图片/图表内容、DOC 往返和电子书转换检查；当前主机不能运行另一原生架构时，只对该架构执行静态校验。

```powershell
.\packaging\smoke-engines.ps1 -EngineDirectory .\work\prepared-engines\x64\Engines
```

ARM64 LibreOffice 的 MSI 中 System64 运行库实际为 x64；准备脚本改从锁定版本的微软签名 ARM64 VC++ 发行档案中提取私有运行库，并验证 PE 架构。脚本与来源位于 `vcredist/`，不执行运行库安装。

CAJ/MuPDF 随包提供微软签名的 x64 `msvcp140.dll`、`vcruntime140.dll` 和 `vcruntime140_1.dll`，两种应用架构使用相同的 x64 CAJ 引擎。无需用户另装 Microsoft Visual C++。启动探测前检查私有文件，缺失时禁用此引擎；打包回归覆盖缺失普通、延迟及间接依赖，避免开发机上的系统运行库掩盖遗漏。
