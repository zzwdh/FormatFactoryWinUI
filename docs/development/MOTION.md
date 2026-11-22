# 页面与任务动效

动效参考 [Microsoft Fluent 2 Motion](https://fluent2.microsoft.design/motion)，沿用现有导航、转换引擎、队列和自适应布局，在 WinUI 原生控件之上加入短促的状态过渡。主页面保持空间锚点，轻微位移只用于新内容与局部提示。

## 动效约定

| 场景 | 实现 |
| --- | --- |
| 同页分类筛选 | 任务区域淡出 83ms、原位淡入 167ms；导航请求按版本处理，最新选择优先 |
| 转换、设置、历史、压缩、自动化页面切换 | 原位淡出 83ms、淡入 167ms；保存并恢复每个任务分类的滚动位置 |
| 首末任务与空状态 | 167ms 的内容入场；复用既有差量集合更新 |
| 新任务 | 同批至多 3 张已生成的可见卡片入场，间隔 35ms；恢复队列、筛选和回收容器不重播 |
| 删除与列表位置变化 | WinUI RepositionThemeTransition 移动保留的容器，不逐帧改宽高 |
| 拖放 | 边框反馈淡入 83ms、中心提示轻移 4px；离开淡出；重复进入/离开可中断 |
| 任务状态 | 每种状态有单独图标与本地化文字，节点变化播放一次轻微强调，屏幕阅读器可读取 |
| 真实进度 | 原生 ProgressBar 的 Updating → Determinate 过渡；Value 立即等于真实进度，无额外每帧计时器 |
| 未知进度、暂停与失败 | 读取信息时使用不确定进度；暂停、取消、失败或隐藏后停止循环与未完成的过渡 |
| 卡片交互 | 83ms 减速进入、167ms 加速退出；透明度与边框/填充颜色从当前呈现值续接；卡内编辑器位置稳定 |
| 汇总和消息 | 活动/完成数量变化时单次强调；撤销、压缩、自动化等消息首次打开时 167ms/4px 入场，内容刷新不重播 |
| 压缩与自动化空态 | 仅空/非空改变时 167ms/6px 入场；任务进度和规则重绘不重播 |

83/167/250ms 是本应用采用的 Windows 常见节奏，不表示复刻了 Microsoft Store 的内部参数。

进入采用减速曲线 `(0.1,0.9,0.2,1)`，退出采用加速曲线 `(0.7,0,1,0.5)`，响应布局位移采用标准曲线 `(0.8,0,0.2,1)`。这些是本应用的实现参数。动画反向时由 Composition 的 `this.StartingValue` 续接；被替换的动画立即释放等待者，最终状态仍由新动画、卸载或超时兜底收敛。

## 系统偏好与性能

- 实时响应 Windows 动画开关和高对比设置，自定义动效也直接进入终态。
- 窗口失焦时结束装饰动效；元素卸载和窗口关闭时清理事件、计时兜底和等待任务。
- 透明度与位移由 Composition 驱动；进度使用原生独立转场，不预测进度。
- 保留队列差量同步、合并刷新、200ms 进度批处理和虚拟化列表。
- 浅色、深色、高对比的主题资源保留；新增状态使用系统语义色并同时提供图标/文字。

## 主要文件

- `src/FormatFactoryWinUI/Windowing/MainWindow/Navigation/MainWindow.Motion.cs`：页面、列表、拖放、汇总与消息编排。
- `src/FormatFactoryWinUI/Motion/FluentMotion.cs`：统一节奏、系统偏好、动画中断与清理。
- `src/FormatFactoryWinUI/Controls/TaskProgressBar.cs`：真实进度与原生动画策略。
- `src/FormatFactoryWinUI/Controls/TaskStatusBadge.cs`：状态图标和可访问文字。
- `src/FormatFactoryWinUI/Controls/CardInteraction.cs`：悬停和键盘焦点反馈。

## 验证

`tests/WinUiSmoke/Run.ps1 -Capture` 使用真实主窗口 XAML 和代码创建隔离窗口，验证快速导航、滚动恢复、100任务集合、动画中断、状态绑定与终态，并渲染各主题/尺寸截图。详见该测试目录的 README。截图使用 XAML 渲染，窗口系统材质和标题栏不在截图内。

另运行 PresentationBatchingTests、AdaptiveLayoutContractTests、ArchitectureBoundaryTests 与 MediaTaskViewModelLocalizationTests，检查既有列表保留、布局和展示层边界。

### Fluent 升级验证（2026-11-14）

新增真实加载检查覆盖原位淡入、入场与退场反转、跨设置/压缩/自动化/历史页快速导航；`-MotionOnly -Capture` 可单独验证界面动效，跳过转换引擎流程。

- x64 Release 主程序构建通过：0 警告、0 错误，记录为 `work/fluent-motion-build-20261114.log`。
- 最终聚焦验证 109/109 项通过，无跳过，生成 54 张截图；`motionOnlyRequested`、真实异步 Composition 淡出、启用动画与偏好恢复标记均为真。结果位于 `work/ui-regression/fluent-motion-20261114/results.json`。
- 系统设置未改动；测试仅覆盖隔离进程的动画偏好快照。截图检查了实际 XAML 的宽窄、浅深和从右到左布局，不代表帧率测量或主观流畅度验收。
- 首次受限环境的完整运行有桌面/文件访问失败，保存在同目录 `full-restricted-results.json`，不能作为全套回归通过证据。首次正常桌面的聚焦运行有两项响应布局活动状态检查未通过，保存在 `focused-initial-results.json`；补充启动次数、启用状态和实际宽度诊断后复跑全部通过，未为此放宽断言或修改生产布局逻辑。间歇性原因尚未确认。
- 为恢复现有测试宿主编译，将 PDF/DOCX 来源哈希的异步读取移出 `SequenceEqual` 参数，避免 .NET 11 将 Span 保留跨过 `await`；校验语义不变。

### 本次验证结果（2026-09-26）

- x64 Release 构建：0 错误，21 个既有 CsWinRT 兼容性警告。
- 展示层批处理、自适应布局、架构边界和本地化：24 项测试通过。
- 隔离 WinUI 窗口：29 项检查通过，包含 8 张界面截图。
- 明确验证开启动画后，可见元素的淡出异步执行；快速连续导航最终停在最后选择的页面，动画中断和禁用时能够正确完成。
- 系统动画偏好原为关闭。测试只临时覆盖隔离进程的内部快照，测试后完整恢复，未修改 Windows 设置。
- 本次结果位于 `work/ui-regression/motion-composition-enabled/results.json`；截图位于同目录的 `captures`。

截图用于布局和主题检查；本次未测量动画帧率，也不以截图作为动画流畅度的证明。
