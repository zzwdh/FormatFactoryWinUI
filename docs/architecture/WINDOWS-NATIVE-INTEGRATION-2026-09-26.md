# Windows 原生集成：实现范围与验证

> 本文保留当时的功能记录；Shell 已迁至 C++ 主基线，当前测试入口见 [基线迁移记录](NATIVE-BASELINE-2026-09-30.md)。下文旧托管 Shell 测试项目已移除。

对应本轮价值清单的第 1、2、3、4、6、7、8、9 项。第 5 项键盘快捷操作未在本轮范围内。

| 项目 | 用户入口与行为 | 实现边界 |
|---|---|---|
| 1. 结果文件拖出与复制 | 拖动已完成的任务卡片；或在“更多操作”中选择“复制结果文件”。文件及目录均可交给资源管理器和接收文件的其他应用。 | 使用 Windows 文件数据包，操作为复制；未完成或已丢失的输出不可拖出。应用忽略自身输出拖回队列的操作。 |
| 2. 原生预览与打开 | “更多操作”→“预览输入／结果”，可切换输入和完成的输出；“打开文件”交给默认应用。 | 系统媒体播放控件不自动播放；PDF 翻页；图片按帧浏览、缩放。预览格式受 Windows 解码能力限制，不影响转换格式支持。 |
| 3. 原生图片转换与颜色 | 常规图片输出优先使用 Windows 编解码器；PNG/JPEG/TIFF 任务可设置颜色处理。 | 默认转为 sRGB；保留模式写入源 RGB ICC 配置。缺少 ICC、非 RGB 配置及不支持的目标会明确报错。HEIF/RAW 需要本机相应解码器。多页 TIFF 不静默缩成单页，动画保留 FFmpeg 路线。 |
| 4. OCR 坐标与字体 | 图片／扫描页转为可搜索 PDF，或对无文本 PDF 页面添加 OCR 文本层。 | 使用 Windows OCR 单词矩形定位；按 Unicode 字形选择系统字体并嵌入 PDF。实际识别语言与字形仍受已安装语言包及字体影响。 |
| 6. 窗口状态恢复 | 无需设置；移动、调整窗口、最大化后自动保存，下次启动恢复。 | 保存正常窗口边界与最大化状态。支持负坐标显示器；显示器断开或分辨率缩小时将窗口移入当前工作区。最小化不会覆盖正常边界。 |
| 7. WIA 批量扫描 | 设置中的扫描操作：选择设备、平板或进纸器、DPI、颜色、双面及页数上限，再选择保存目录。 | 选项由设备能力约束；按顺序保存和导入页面。取消、缺纸或卡纸时保留已取得的页面；驱动正在传输的一页可能需要完成后才能取消。 |
| 8. Windows 打印 | 设置中的打印操作：PDF／图片打开系统打印预览，可选页面、份数及打印机支持的双面、色彩等选项。 | 预先准备页面，打印回调同步供页；最终输出为页面图像。其他文档经说明后交给关联应用的打印命令。系统显示“已提交”不代表打印机已经出纸。 |
| 9. EPUB Explorer 处理器 | 安装包含本轮修改的 MSIX 后，资源管理器显示 EPUB 封面缩略图和预览窗格。 | 仅注册 EPUB；读取封面、书籍信息与有限正文，限制 ZIP/XML 资源大小，不执行书内脚本或加载远程资源。不替换 Windows PDF／图片处理器。 |

## 状态与资源管理

- 新入口使用应用的现有本地化目录和 WinUI 控件；颜色选项写入任务普通选项，与队列一起保存。
- PDF 密码仅保存在当前进程内存中，关闭预览后释放引用。
- 图片转换沿用输出路径预留、临时文件验证及成功后提交；取消不生成完成结果。
- 当前原生图片像素管线为 8 位 BGRA。保留 ICC 表示保留配置及其 RGB 色彩空间，不表示保留 16 位或 HDR 精度；质量／快速模式可能将高位深量化为 8 位。无损模式在量化前检查每通道位数，不支持时明确拒绝；HEIF／RAW 无法可靠确认原始精度时也拒绝无损路线。
- 页面预览限制渲染尺寸；切换文件或关闭窗口会取消未完成加载并释放媒体资源。
- 打印准备支持进度和取消，预览缓存 4 页。普通 PDF 以 192 DPI 栅格化、单页最长边 3000 像素；全部页面累计原始像素预算为 512 MiB，超限时在显示打印界面前提示拆分。该预算不等于整个进程内存上限；正式打印页保留到系统提交完成。
- 窗口状态单独保存为应用数据目录的 `window-placement.json`，损坏时按默认窗口启动。
- 扩展只在 MSIX 中声明注册。运行单元测试、构建主程序不会安装或替换当前系统处理器。

## 可重复验证

```powershell
dotnet test tests/FormatFactoryWinUI.Tests/FormatFactoryWinUI.Tests.csproj -c Release
./tests/WinUiSmoke/Run.ps1 -RunName native-integration -TimeoutSeconds 120
dotnet build tests/FormatFactoryShellExtension.Tests/FormatFactoryShellExtension.Tests.csproj -c Release -p:Platform=x64 -r win-x64 --artifacts-path work/shell-tests
& ./work/shell-tests/bin/FormatFactoryShellExtension.Tests/release_win-x64/FormatFactoryShellExtension.Tests.exe
dotnet build src/FormatFactoryWinUI/FormatFactoryWinUI.csproj -c Release -p:Platform=x64 -r win-x64
dotnet build src/FormatFactoryWinUI/FormatFactoryWinUI.csproj -c Release -p:Platform=arm64 -r win-arm64
pwsh -NoProfile -File tests/FormatFactoryShellExtension.Tests/Validate-Package.ps1
```

WinUI 冒烟使用 `work/ui-regression` 下的独立程序和样本，不读取生产队列，也不修改系统剪贴板或提交打印作业。它调用真实 Windows 文件数据包、图像编解码器、PDF 渲染和窗口 API。扫描仪、实体打印机与安装后的 Explorer 注册还需要对应硬件及安装环境验证。

## 本轮验证结果

| 验证 | 结果 |
|---|---|
| 全部核心回归 | 561 / 561 通过，含扫描中断、ICC 策略、16 位无损保护、8 种 EXIF 方向逐像素检查、旋转及裁剪 PDF 坐标、真实 Unicode 字体覆盖 |
| 隔离 WinUI 原生回归 | 24 / 24 通过，含真实 ICC 三种输出读回、媒体加载／关闭、PDF 翻页、输出拖出、窗口恢复及打印页面准备 |
| EPUB Shell 集成回归 | 20 / 20 通过，含真实位图和隐藏窗口生命周期、解析限额、重复销毁资源检查 |
| 主程序 x64 / ARM64 | 编译通过；保留 19 条原有 CsWinRT 集合表达式兼容性警告 |
| Shell x64 / ARM64 | 编译通过，0 警告、0 错误 |
| 包清单和架构 | Windows SDK 双架构清单验证、56 个输入扩展一致性、仅 EPUB 预览注册及项目分层检查通过 |

本轮未安装 MSIX、未改动系统处理器注册、未向实体打印机提交作业。包清单验证使用不可安装或发布的占位验证包。详细结果保存在 `work/native-final-*.log`、`work/ui-regression/native-final/results.json` 与 `work/shell-final-*.log`。
