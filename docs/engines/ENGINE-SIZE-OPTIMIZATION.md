# 引擎与运行时包体优化

优化分为主 FFmpeg 的共享库交付、Windows App SDK 组件依赖精简，以及 LibreOffice/Calibre 按应用实际转换路线裁剪三个部分。文档、电子书、CAJ、兼容解码与光盘功能继续随包提供；没有改成按需下载。

## 主 FFmpeg

`ffmpeg.exe` 和 `ffprobe.exe` 共用七个私有 DLL：`avcodec-63.dll`、`avdevice-63.dll`、`avfilter-12.dll`、`avformat-63.dll`、`avutil-61.dll`、`swresample-7.dll`、`swscale-10.dll`。程序及 DLL 位于同一目录，保留原来的发现路径。x64 与 ARM64 均使用原生 GPL shared 构建，不以 LGPL 构建替代，不删除 libx264/libx265 等能力。ffplay、开发头文件和导入库不进入运行包。

旧的 2026-09-19 每日构建已无法从上游取得，改用 2026-09-30 月末保留构建 `n9.0.2-17-g2a571b6068`。来源、归档 SHA256、必需文件与 PE 文件均在 `packaging/engine-sources.json` 中锁定。许可和来源说明继续随包交付。

共享库校验覆盖每个 EXE/DLL 的架构以及普通、延迟和间接 FFmpeg/运行库依赖。缺失依赖不能由开发机的 PATH 补齐；prepared receipt 和 MSIX 字节校验沿用相同门禁。

## Windows App SDK

主程序显式引用 WinUI、Foundation 和 InteractiveExperiences，保留其 Base 依赖。Foundation 提供应用生命周期、通知和后台任务，InteractiveExperiences 提供窗口与组合能力。组件版本保持原元包锁定图中已解析的版本。

移除未使用的 AI、ML、Search、Widgets 等组件依赖，而非手动删除发布目录中的 DLL。主程序与隔离 FilterWorker 继续使用自包含 .NET，Windows App SDK 也保持自包含；`PublishTrimmed` 仍为 false。WinUI 冒烟宿主动态读取生产项目的 SDK 组件引用，以验证实际精简后的依赖集合。

## LibreOffice 与 Calibre 转换配置

在前两项优化的基础上，26.10.25.0 工作区进一步裁剪文档和电子书引擎。LibreOffice 移除独立 Python、Java UNO 组件、预装脚本和交互编辑资源，同时保留原生文档过滤器、Math/图表/OLE/Basic/VBA、字体、布局资源和所有语言的断字字典。Calibre 保留完整冻结 Python 归档及转换插件，移除当前应用不调用的 WebEngine、阅读器/编辑器/服务端、新闻、语音和多媒体依赖。

Calibre 的应用内入口始终为 EPUB，输出 MOBI/AZW3；其他受支持的文档/图片输入先经应用内流程生成 EPUB。因此独立 Calibre 的 PDF 输出等完整命令行功能不属于随包配置，应用的 PDF 转换能力仍由自身流程与 LibreOffice 提供。配置范围、保护项和测试方法见 [文档与电子书引擎裁剪](OFFICE-EBOOK-PRUNING.md)。

## 验证与体积口径

- 对照旧、新主引擎的编码器、解码器、滤镜、封装格式、协议及硬件加速清单；实际执行编码、解码和 ffprobe 探测。
- 对文档裁剪前后执行相同语料转换，比较正文、页数和逐页渲染；电子书比较内容、元数据、图片、SVG 和解混淆后的嵌入字体，避免把随机标识或封面当作内容变化。
- 运行共享 DLL 缺失、间接依赖、错误架构和 PATH 隔离回归，以及托管回归、真实 WinUI 冒烟和启动恢复检查。
- 构建 x64 与 ARM64 候选包并校验包内内容；x64 主机对原生 ARM64 内容只能进行静态与交叉构建检查，真机运行验收另行完成。
- 对比 MSIX 文件实际字节数与包内各组件压缩字节数，分别报告下载包与未压缩文件体积。不能把删除的未压缩字节直接当成包体节省。

FFmpeg/SDK 第一阶段以原有 26.10.23.0 成品为基线，测量记录保存在 `work/engine-size-optimization/baseline-packages.json`。基线版本使用的运行时与当前工作区可能不同，因此报告同时单列 FFmpeg 和 SDK 文件变化，避免把全部差值归因于单一改动。

LibreOffice/Calibre 第二阶段单独记录 26.10.25.0 裁剪前的成品及组件压缩体积，基线为 `work/office-ebook-pruning/baseline-packages.json`。试验目录的未压缩净减少量与重新打包后的 MSIX 实际减少量分开统计。当前版本始终以根 `VERSION` 为准；所有版本的主交付物统一保存到 `outputs/msix/`，不维护平行的优化版交付目录。

主交付目录与正式商店验收状态分别记录：版本统一不代表已完成干净源码、两架构真机和性能基线验收。正式 `.msixupload` 继续由既有发布入口检查通过后生成，不修改门禁或伪造验收结果。
