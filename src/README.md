# 源码导航

项目的构建、测试入口见 [项目首页](../README.md)，职责边界见 [架构约定](../docs/ARCHITECTURE.md)。目录归类保留现有程序集和 `FormatFactoryWinUI.*` 命名空间。

## 项目分工

| 项目 | 主要内容 |
| --- | --- |
| [FormatFactory.Core](FormatFactory.Core) | 契约、领域模型、唯一格式目录、导入规则、本地化 |
| [FormatFactory.Engine](FormatFactory.Engine) | 用例、队列、转换、持久化、进程和设备能力 |
| [FormatFactory.Documents](FormatFactory.Documents) | 文档转换、工具发现、PDF / DOCX / EPUB 读写与校验 |
| [FormatFactory.Presentation](FormatFactory.Presentation) | 展示模型、布局和进度批处理 |
| [FormatFactoryWinUI](FormatFactoryWinUI) | WinUI 页面、窗口、Windows 平台适配和依赖装配 |
| [FormatFactory.ConversionWorker](FormatFactory.ConversionWorker) | Rust 转换后台，负责媒体与归档转换策略、结构化文档、镜像、队列写入、文件提交和目录监控 |
| [FormatFactory.FilterWorker](FormatFactory.FilterWorker) | 隔离运行的 IFilter 文本提取进程 |
| [FormatFactoryShellExtension.Native](FormatFactoryShellExtension.Native) | Explorer 的 C++ COM 入口、缩略图和预览，以及静态链接的 Rust EPUB 归档读取器 |

## Core

- `Abstractions/`：转换与平台契约。
- `Models/`：稳定模型和 `formats.json`；格式目录及其加载实现保留在此，供构建和打包读取。
- `Policies/`：导入规则。
- `Localization/`：本地化服务、资源目录与附加语言加载入口。
- `Localization/Resources/`：按功能拆分的 C# 文案资源。
- `Localization/AdditionalLanguages/`：嵌入式语言 JSON，沿用现有资源名称与位置。

## Engine

`Application/` 保存导入、队列、压缩包及自动化用例。`ConversionEngines/` 提供图像与光盘引擎，`Documents/` 提供系统字体、扫描和打印能力，`Disc/` 负责光盘实现。下表列出共享服务。

| Services 子目录 | 内容 |
| --- | --- |
| `Activation/` | 启动参数、批次、激活路由和 App Service 协议 |
| `Archives/` | 压缩包转换、任务存储、提交收据和历史重放 |
| `Automation/` | 自动化存储、预设与目录监控 |
| `Conversion/` | 转换编排、失败处理、输出验证和提交 |
| `Diagnostics/` | 日志、反馈、遥测和安全诊断 |
| `Hardware/` | GPU、硬件加速探测及能力报告 |
| `Imaging/` | 图像、动画、颜色和 WIC 处理 |
| `Media/` | FFmpeg、媒体探测、预检和兼容解码 |
| `Persistence/` | 设置、队列、历史和恢复状态持久化 |
| `Processes/` | 外部进程、资源限制、暂停和终止 |
| `Storage/` | 数据目录、输出路径、磁盘预算和文件恢复 |
| `WindowsNative/` | Windows 原生设备、COM、系统与 Shell 能力 |

## Documents

`Conversion/` 管理转换流程、外部工具选择与文本提取。`Formats/` 实现文档内容模型、读写与验证。此项目拥有 Open XML SDK、PDFsharp 与 PdfPig 依赖，引用通用引擎提供的进程、诊断及输出服务。WinUI 宿主提供 Windows OCR、PDF 渲染与图像解码。

## Windows 宿主

`App.xaml`、`MainWindow.xaml` 及对应 `.xaml.cs` 保留在项目根目录，页面位于 `Views/`。主窗口的功能分部放入 `Windowing/MainWindow/`：

| 子目录 | 内容 |
| --- | --- |
| `Automation/` | 自动化规则与执行入口 |
| `Import/` | 拖放、批量导入和源文件重新定位 |
| `Lifecycle/` | 启动、关闭、性能、窗口位置和诊断 |
| `Navigation/` | 导航、布局、页面与动效 |
| `Output/` | 输出参数、结果操作和产物恢复 |
| `Queue/` | 队列批量操作、排序、撤销和失败处理 |
| `Settings/` | 设置、反馈、评分、更新和遥测 |

原生媒体预览窗口位于 `Windowing/Preview/`。`Composition/` 创建并连接服务；`Platform/` 保存 WinRT 和 Windows 适配；`Controls/`、`Converters/`、`Motion/` 分别保存控件、转换器和通用动效。

## 添加文件

先按职责选项目，再选择功能目录。C# 文件由各项目递归编译，新增功能分部应与同组文件放在一起。移动文件时检查测试和构建脚本中的精确路径及目录遍历，同时更新文档链接。XAML、嵌入资源、清单和打包输入的位置变动需要另行核对资源路径。

本地构建产物位于各项目的 `bin/`、`obj/`；发布成品在根 `outputs/`；过程证据在根 `work/`。这些目录不存放新的生产源码。
