# 架构与依赖约定

## 项目边界

| 项目 | 责任 | 直接依赖 |
| --- | --- | --- |
| FormatFactory.Core | 任务与格式模型、转换契约、平台接口、导入规则、本地化 | 无 |
| FormatFactory.Engine | 导入、目录监控、队列调度、持久化、FFmpeg、图像与光盘转换、进程及原生设备访问 | Core |
| FormatFactory.Documents | 文档转换流程、外部文档工具、PDF / DOCX / EPUB 等格式读写与校验 | Core、Engine |
| FormatFactory.Presentation | 展示模型、布局规则、集合与进度批处理、能力展示 | Core、Engine |
| FormatFactoryWinUI | XAML、窗口、WinRT 适配、OCR、激活、Shell 集成、对象装配 | Core、Engine、Documents、Presentation，构建时还引用 FilterWorker |
| FormatFactoryShellExtension.Native | C++ Explorer COM 菜单、EPUB 缩略图与预览 | Windows SDK、miniz；构建时读取 formats.json |

Core、Engine、Documents、Presentation 面向 net11.0，可独立于 Windows App SDK 和 WinRT 编译。Win32 / COM 及外部工具路线按能力要求在 Windows 上运行。

Documents 负责从路线选择、内容读取、写入和验证到提交的文档流程，并直接引用 Open XML SDK、PDFsharp、PdfPig。宿主负责将 Windows OCR、PDF 页面渲染和图像解码接入该流程。通用引擎和展示层可在没有文档格式库的情况下构建。

现有 FormatFactoryWinUI.* 命名空间予以保留，以稳定 XAML 类型引用、序列化类型和资源名称。判断归属以项目和目录为准。

## 目录导航

完整项目及功能目录见 [源码导航](../src/README.md)，使用说明与开发记录见 [文档导航](README.md)。Engine 的共享服务按功能放入 `Services/` 子目录；Core 的文案实现集中在 `Localization/Resources/`；主窗口功能分部集中在宿主的 `Windowing/MainWindow/`。这些物理归类不改变程序集、命名空间或运行时数据格式。

## 平台能力与对象装配

导入通过 IImportStorageAccess 解析路径和恢复访问令牌；媒体探测通过 IMediaMetadataReader 补充元数据。Engine 的默认实现可处理普通本地路径，并不需要加载 WinRT。Windows 宿主在 Composition 中创建实际适配器，并注入导入服务。

`WindowConversionServices` 装配每个窗口的导入器、队列和引擎。目录监控与主队列共享导入去重状态，自动化、历史重放和源文件重新定位各自持有导入器。引擎按文档、光盘、原生图像、FFmpeg 的顺序匹配。`WindowShellServices` 提供选择器、Explorer 操作及依赖窗口句柄的工厂。设备观察器、队列排空和资源释放由窗口生命周期协调。

应用数据根由宿主在服务创建前配置。打包环境使用 ApplicationData.LocalFolder 下的 FormatFactoryWinUI；非打包环境保留 LocalApplicationData 下的原有路径。引擎负责文件写入，宿主负责选址。测试和测试辅助进程配置独立临时目录，不使用真实用户的队列、设置、日志或 FFmpeg 配置。

## 压缩包任务会话与共享策略

压缩包流程使用与主队列相同的分层方向：Core 的 `ArchiveTask` 保存运行时状态及不含密码的检查点；Engine 的 `ArchiveSession` 管理转换、恢复、取消、串行保存、提交收据和历史补录；Presentation 的 `ArchiveTaskItem` 仅包装模型并提供本地化、按钮状态与结果摘要。其原有 `FormatFactoryWinUI.Views` 类型命名空间保留，实际编译归属以程序集为准。

`ArchiveConversionView` 只保留选择器、设置与密码对话框、输出位置打开，以及会话到只读展示集合的适配。任务集合由会话持有，页面包装对象不复制业务状态。会话操作须从宿主同步上下文调用；转换及来源测量在后台执行，来源密码请求通过注入的调度器回到宿主上下文。新增页面或后台入口不能直接从任意线程修改同一会话。

`IArchiveConverter`、`IArchiveTaskPersistence`、`IArchiveCommitReceiptStore` 是外部执行与持久化边界，生产实现继续使用现有转换服务、任务文件和提交收据。成功顺序为：验证后准备收据、发布输出、保存成功检查点、删除收据、补记历史并保存历史标记。保存成功检查点失败时保留产物和收据，历史失败也不改变转换成功状态。恢复失败时禁止覆盖原任务文件；退出中断与用户取消保持不同语义。

通用文件版本指纹由 `FileFingerprintService` 读取，临时 IO 失败由 `ConversionFailurePolicy` 判定。普通转换、队列及预览不再调用自动化编排类中的静态工具方法；现有指纹持久化类型与失败判定规则保持兼容。

首轮迁移范围及验证记录见 [2026-11-03 架构整理](architecture/ARCHITECTURE-CLEANUP-2026-11-03.md)。

## 主队列状态与操作协调

主队列由三个互补对象负责：Engine 的 `QueueSession` 持有真实任务、调度准入、持久化事务、恢复、排序约束与撤销保留；Engine 的 `QueueOperationsCoordinator` 组合开始、重试、暂停、移除、批量编辑和优先执行等命令；Presentation 的 `QueuePageViewModel` 持有任务包装集合、可见集合、筛选、选择、队列摘要、任务栏展示数据及批次结果。包装项与会话引用同一个 `MediaTask`，不得另建一套业务状态。

`MainWindow` 创建这些对象并连接事件，`QueueView.BindPage` 绑定展示集合。窗口保留就绪检查、文件和参数对话框、原生进程操作、通知、动画及调度器计时器。命令执行前从控件同步当前选择，避免延后的选择事件使批量操作使用旧选中项；可编辑范围及格式交集由操作协调器计算。排序和撤销最终仍交给 `QueueSession`，不直接修改展示顺序来替代真实队列事务。

`QueueOutputGroups` 根据可见任务维护多输出组、普通任务段与展开状态。每组在首个等待任务的位置显示，全部离开等待状态后按首个成员定位。同一个任务重新绑定时，旧行会释放订阅，新行继承组的展开状态。

后台进度通过 `QueuePageViewModel.PublishProgress` 合并，按任务引用与执行代次拒绝旧回调。它只写受锁保护的待处理快照；窗口的 200 ms 计时器回到宿主上下文后调用 `DrainProgress`，再更新绑定对象、原生通知和任务栏。集合修改、选择和进度应用须在宿主上下文执行；这个线程约定不意味着整个 ViewModel 可以从任意线程调用。

## 应用启动、恢复与退出

Engine 的 `ApplicationLifecycleCoordinator` 维护 `ApplicationStartupPhase`、`ApplicationShutdownPhase` 和初始化、关闭、已关闭状态。`MainWindow.Lifecycle` 通过类型明确的 `ApplicationStartupOperations` / `ApplicationShutdownOperations` 提供宿主动作。协调器决定先后与等待关系，窗口实现设置应用、设备观察器、窗口句柄、对话框和关闭等平台动作；生命周期操作须从同一宿主上下文调用。

启动依次准备窗口、加载并应用设置，然后并行发现文档、光盘及硬件能力。等待硬件清单、媒体引擎以及文档和光盘发现完成后，依次恢复主队列和压缩包任务，再发布交互就绪。此后初始化自动化、核对恢复的自动化组，配置目录监控并安排补充服务。交互就绪不必等待所有后台服务完成；`StartupTask` 表示整个启动流程，`ActivationReady` 是恢复完成后的激活导入屏障。

设置加载完成只记录 `settings-ready`；`first-interactive` 保留为旧指标，不代表文件导入可用。真实 `add-files-ready` 还要求出现可见渲染帧、恢复完成、添加按钮与快捷操作可用，并在调度器返回后观察。托盘隐藏启动不轮询可见窗口就绪。

退出依次停止压缩包任务、停止导入、关闭相关对话框、取消历史索引、停止自动化、等待更新偏好保存、停止并保存队列，然后等待正在进行的启动结束。若自动化在退出期间才初始化，等待后再停止一次。保存失败时由宿主提供现有确认对话框：继续使用会恢复队列准入和自动化；选择不保存退出会再次停止队列并跳过更新安装。保存成功后才执行窗口位置保存和退出更新安装，最后允许关闭窗口。重复退出请求共享进行中的操作，`MarkClosed` 阻止后续启动和释放激活等待。

如果退出取消了尚未完成的队列恢复，旧激活等待会被释放以便退出，关闭状态继续阻止导入。用户随后选择继续使用时，已发布的持久任务模型保留在会话中，`QueueSession` 使用新生命周期令牌继续剩余探测；未成功发布的快照则重新加载。协调器重新建立激活屏障，等待主队列及压缩包恢复完成，再重新开放宿主操作并接续自动化和监控。不能仅恢复准入标志而跳过恢复或后台初始化。

## 媒体转换阶段

`ConversionService` 在输入预检前按任务 ID 登记执行。每次执行拥有自己的取消令牌与 `FfmpegProcessRunner`。预检、编码尝试、验证及提交前均可响应取消，暂停与恢复作用于当前编码进程。任务退出并完成取消清理后，同一 ID 才可再次执行。阶段分工如下。

| 类型 | 责任 |
| --- | --- |
| `MediaConversionPreparation` | 选项与来源保真检查、输入预检及兼容解码选择、流复制准备、引擎输出能力检查 |
| `MediaEncodingService` | 编码器和硬件解码尝试、硬件到 CPU 回退、目标体积搜索、ICO 帧生成 |
| `MediaOutputValidator` | 体积、完整时长、尺寸、媒体有效性、动画及复制流验证 |
| `MediaOutputCommitter` | 最后取消检查、通过 `OutputPathLease.CommitValidated` 发布产物、报告成功 |

私有产物检查与清理由 `MediaConversionArtifacts` 共用。预检发生在预留输出路径前；编码尝试只写私有临时路径；验证通过后才提交并报告 100%。提交冲突保留已验证产物和恢复记录，完成通知异常不能使已提交产物变成失败。兼容解码临时替换的来源路径、探测结果、音轨及选项由入口的 `finally` 恢复，进程及取消记录也在该边界清理。转换参数、持久化格式和路径租约协议沿用既有实现。

四项整理的范围、收益及验证记录见 [2026-11-03 完整架构整理](architecture/ARCHITECTURE-COMPLETE-2026-11-03.md)。

## 资源归属

- 唯一格式源为 Core/Models/formats.json。主程序与打包清单使用它，原生 Shell 在构建时生成对应的 C++ 格式表，不加载 Core 程序集。
- 随包引擎来源统一位于 packaging/engine-sources.json；FFmpeg 运行时只发现和验证可执行程序，已移除旧在线下载器及其独立来源清单。
- 本地化资源只在 Core 编译一次。
- WinUI 图标、XBF、PRI、清单和 Windows App SDK 留在宿主中。
- 各项目通过 ProjectReference 使用依赖，不链接其他项目的 C# 源码。
- 主程序的发布目标同时带入文本提取辅助进程的可执行文件、程序集和运行配置。两个进程共用对应架构的 .NET 运行时。MSIX 打包从该发布目录取件，并检查文档程序集和辅助进程文件。

C++ Shell 由根 build.ps1 和 MSIX 打包脚本调用原生构建入口，静态链接 C/C++ 运行库，不再发布托管 comhost、deps 或 runtimeconfig。原有三个 CLSID 和批量激活协议保持兼容。

## 测试与构建门禁

Tests 和 TestHelper 引用生产程序集。测试替身应实现平台接口，禁止在生产命名空间中重新定义同名服务来替换真正实现。只有针对真实内部实现的测试使用 InternalsVisibleTo。

运行：

    dotnet test tests/FormatFactoryWinUI.Tests/FormatFactoryWinUI.Tests.csproj -c Release
    pwsh -File build/validate-repository.ps1
    pwsh -File build.ps1 -Architecture all -Configuration Release -Test

build/validate-architecture.ps1 被仓库门禁调用，检查项目依赖方向、无 WinUI / WinRT 的库边界以及无生产源码链接。增加模块时需明确修改依赖约定；不要靠重复编译或循环引用解决访问问题。

## 添加功能时的归属

- 格式定义、稳定选项和跨引擎数据契约放 Core。
- 导入、通用转换、进程与持久化实现放 Engine。
- 文档转换路线、内容模型、读写及校验实现放 Documents。
- 可脱离窗口验证的显示转换与列表更新逻辑放 Presentation。
- WinRT、文件选择器、剪贴板、通知、窗口操作放宿主 Platform。
- 接入新系统能力时先定义最小接口，再在 Composition 注入实际实现与测试替身。

## 历史验证（2026-09-20）

- 完整 Release 自动化测试：479 / 479 通过，0 失败、0 跳过。
- 主程序：x64 与 ARM64 Release 构建通过，均为 0 警告、0 错误。
- Explorer Shell 扩展：x64 与 ARM64 Release 构建通过，均为 0 警告、0 错误。
- 主程序：x64 与 ARM64 自包含发布通过；已检查 Core、Engine、Presentation、主程序及 App.xbf、MainWindow.xbf、PRI 齐全。
- 仓库元数据、架构边界、FFmpeg 来源清单校验通过；147 个迁移文件完整。
- 本次使用 SDK global.json 指定版本和本机 NuGet 缓存完成构建；网络源还原曾超时，离线配置仅放在忽略的 work 目录，没有修改仓库的常规包源设置。

原始日志位于 work/architecture-tests.log、work/architecture-winui-*.log、work/architecture-shell-*.log、work/architecture-publish-*.log。发布产物位于 work/architecture-publish-x64 和 work/architecture-publish-arm64。

未执行 MSIX 安装、交互式界面回归或 ARM64 真机验收。此前改动没有可比较的完整测试基线：首次基线测试在源码迁移时遇到旧链接路径错误，因此这里报告的是最终代码的验证结果。
