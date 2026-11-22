# 代码审查与修复记录（2026-09-11）

本轮检查覆盖媒体探测与转换、任务输出路径、进程生命周期、文件导入与激活、目录监控、DOCX 读取和 CSO 校验。保留审查开始时已有的工作区改动。下列已确认问题均已修复。

| 类别 | 已确认问题及影响 | 修复 |
|---|---|---|
| 多输入任务恢复 | 清空主任务输入后再遍历，导致首张图片丢失；恢复时没有保留任务 ID。 | 清空前保存完整有序输入，恢复原任务 ID。见 [MediaImportCoordinator.cs](../../src/FormatFactory.Engine/Application/MediaImportCoordinator.cs)。 |
| 导入取消与失败 | 批量导入中途失败或取消后，早先输入仍占用去重预留，后续重新导入被跳过。 | 对未交付的导入结果统一释放预留，组合任务与监控导入共用清理规则。 |
| 目录监控生命周期 | 停止或切换目录后，等待文件稳定的旧操作仍可能触发导入；丢弃旧结果时也会泄漏预留。 | 取消旧操作，校验当前配置后发布结果，失效结果释放预留；统一目录末尾分隔符。见 [WatchFolderService.cs](../../src/FormatFactory.Engine/Services/Automation/WatchFolderService.cs) 和 [WatchFolderImportCoordinator.cs](../../src/FormatFactory.Engine/Application/WatchFolderImportCoordinator.cs)。 |
| 监控导入依赖 | 无条件要求 FFprobe，导致不需要 FFprobe 的文档、光盘镜像无法自动导入。 | 由统一导入流程按格式判断依赖，同时移除重复停止监控调用。 |
| 命令行与批次激活 | 协议批次解析遗漏 batch resolver；普通路径中的字面 %20 被错误解码成空格。 | 转发批次解析器，普通文件参数保留原始文本。见 [ActivationArgumentParser.cs](../../src/FormatFactory.Engine/Services/Activation/ActivationArgumentParser.cs)。 |
| 输出目录与重名预留 | 跨盘相对路径会变成绝对路径，覆盖自定义输出根；..draft 等合法目录被误判越界；等价路径可以绕开重名预留。 | 检查盘符及完整父目录边界，预留前规范化绝对路径。见 [OutputPathService.cs](../../src/FormatFactory.Engine/Services/Storage/OutputPathService.cs)。 |
| 损坏 CSO 文件 | 在确认索引表实际存在之前，按不可信头部申请巨大数组，短小损坏文件也可能耗尽内存。 | 分配前检查索引表长度与文件边界。见 [CsoImageCodec.cs](../../src/FormatFactory.Engine/Disc/CsoImageCodec.cs)。 |
| DOCX 正文与图片 | 内容控件中的正文被漏读；绝对图片关系错误拼接；只接受固定图片目录，遗漏合法包内图片。 | 按文档顺序展开内容控件，按关系类型及包内路径加载图片。见 [DocxDocumentReader.cs](../../src/FormatFactory.Documents/Formats/DocxDocumentReader.cs)。 |
| 进程取消 | 未成功绑定进程的空 Job 返回终止成功，阻止真正终止进程的回退；已取消的调用仍会启动外部程序。 | 记录 Job 绑定状态，未绑定时执行进程终止回退；启动前检查取消。见 [ProcessJob.cs](../../src/FormatFactory.Engine/Services/Processes/ProcessJob.cs) 和 [ProcessRunner.cs](../../src/FormatFactory.Engine/Services/Processes/ProcessRunner.cs)。 |
| 暂停、继续与释放 | 暂停、继续、终止和释放之间缺少互斥，可能重复挂起或在释放后操作；退出检查也可能抛出异常。 | 用统一生命周期对象串行保护相关操作。见 [ActiveConversionProcess.cs](../../src/FormatFactory.Engine/Services/Processes/ActiveConversionProcess.cs) 和 [ConversionService.cs](../../src/FormatFactory.Engine/Services/Conversion/ConversionService.cs)。 |
| 媒体识别 | 带封面的音频被识别为视频；多视频流的编码、尺寸和色彩信息被混合；图片别名识别使用重复的格式列表。 | 排除封面，统一取首条真实视频流，复用格式目录；防止异常时长导致导入失败。见 [MediaProbeParser.cs](../../src/FormatFactory.Engine/Services/Media/MediaProbeParser.cs)。 |
| 转码参数 | 10 位 SDR 被误判 HDR；HDR 转 FLV 后又写回原 HDR 标签；显式 H264/HEVC 目标遗漏硬件路线。 | 用传递函数判断 HDR，色调映射后写入 BT.709 信息，统一目标编码与容器判断。见 [PresetService.cs](../../src/FormatFactory.Engine/Services/Persistence/PresetService.cs)。 |
| 未保存设置被使用 | 光盘转换从设置页文本框读取尚未保存的外部工具路径。 | 从已提交设置创建转换快照。见 [MainWindow.xaml.cs](../../src/FormatFactoryWinUI/MainWindow.xaml.cs)。 |
| 成功任务误报失败 | 输出文件已成功生成后，写历史或保存队列失败，会把转换结果改成失败。 | 保留实际转换结果，保存异常单独记录并提示。 |
| 重复与冗余代码 | “全部暂停/继续”重复实现；三处进度 UI 更新逻辑重复；质量分支返回相同值。 | 合并共享方法、简化相同分支；丢弃已结束或已移除任务的迟到进度回调。 |

视频流选择使用 FFmpeg 的 V 流说明符排除封面，与 [FFmpeg 官方文档](https://www.ffmpeg.org/ffmpeg.html#Stream-specifiers-1) 的定义一致。

## 验证结果

- 修复前基线：328/328 通过。
- 本轮增加 52 个测试用例，合计 380 项。
- 最后一次全量运行：379 项通过，1 项新增真实媒体测试因合成素材问题失败。随后仅修正该测试素材，保留全部断言；该项定向复测通过。生产代码在此期间未改变，380 项均已有通过结果。
- 真实 FFmpeg 回归覆盖：带封面音频转 FLAC、10 位 SDR 转 FLV、带封面视频转 MP4/JPG、PQ HDR 转 FLV 及 BT.709 色彩信息回读。
- x64 Release 主程序构建通过，0 警告、0 错误。
- 仓库元数据检查通过。
- 按要求未打包，未扩展实体设备与安装包运行验证。

本地测试记录：修复前基线（本地路径 `work/review-results/review-baseline.trx`）、最终全量（本地路径 `work/review-results/review-final.trx`）、最后失败项复测（本地路径 `work/review-results/review-final-rerun.trx`）。
