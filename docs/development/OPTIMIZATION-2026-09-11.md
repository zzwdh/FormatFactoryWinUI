# 补充代码检查与优化（2026-09-11）

本次以当前本地工作区为基线，保留已有未提交修改。重点检查转换队列、工具安装、历史记录、图片集合导入和 Windows 光盘读取。此前的检查记录保留在 `CODE-REVIEW-2026-09-11.md`。

## 本次修复

| 问题 | 修复后的行为 | 主要文件 |
|---|---|---|
| FFmpeg 更新时，旧安装目录移动失败仍可能进入删除逻辑；安装成功后的日志或进度异常也会触发回滚。 | 只回滚本次实际移动的新目录；验证失败或取消时恢复旧版本；回滚失败保留备份及两层错误；通知在安装提交后执行。 | `Services/FfmpegLocator.cs` |
| FFmpeg 压缩包在界面线程解压，更新期间可能卡顿。 | 在后台解压，完成后检查取消状态，取消时清理暂存目录。 | `Services/FfmpegLocator.cs` |
| 文档或光盘任务暂停后立即继续，同一任务 ID 在旧完成回调内重启会被队列拒绝。 | 区分正在执行的任务与尚未结束的完成回调；允许重启，并继续等待旧、新操作及回调全部结束。 | `Services/ConversionQueueCoordinator.cs` |
| 历史 JSON 损坏、空记录或文件被占用时，读取异常可能传到导航和搜索界面。 | 读取失败记录日志并返回空结果；保存仍使用严格读取，保留损坏原文件并上报保存失败，避免覆盖历史数据。 | `Services/ConversionHistoryStore.cs` |
| 图片集合恢复没有完整传递取消信号，最后一张图片处理期间取消仍可能返回任务并占用去重预留。 | 路径解析传递取消信号，交付任务前再次检查取消，失败后释放预留，允许重新导入。 | `Application/MediaImportCoordinator.cs` |
| Windows 数据光盘输出 ISO 时，文件句柄尚未关闭就重命名，导致共享冲突。 | 先刷新并关闭暂存文件，再提交输出；失败或取消清理暂存文件，保持已有输出不变。 | `Disc/WindowsOpticalDiscReader.cs` |

## 验证

- 修改前完整基线：380 项通过，0 失败、0 跳过。
- 新增 33 项回归：安装事务 10 项、历史记录 13 项、图片集合取消 2 项、队列重启 3 项、光盘输出 5 项。
- 最终全量：413 项通过，0 失败、0 跳过；已重新编译测试程序集。
- x64 与 ARM64 的主程序、Shell 扩展均通过 Release 构建，0 警告、0 错误。
- 仓库元数据检查通过：版本 26.8.30.0，输入扩展 46 个。
- 队列重启的 3 项测试已在修复前复现失败，修复后通过；光盘完整写入已在修复前复现文件共享冲突，修复后通过。

验证记录：完整基线（本地路径 `work/optimization-results/optimization-baseline.trx`）、最终全量（本地路径 `work/optimization-results/optimization-final.trx`）、双架构构建（本地路径 `work/optimization-results/build-all.log`）、队列修复前（本地路径 `work/review-results/queue-restart-before.trx`）、队列修复后（本地路径 `work/review-results/queue-process-after.trx`）、光盘修复前（本地路径 `work/review-results/data-disc-before-fix.trx`）、光盘修复后（本地路径 `work/review-results/data-disc-after-fix.trx`）。

本轮不执行真实光盘读取、刻录、安装包部署或在线 FFmpeg 下载。安装回归使用隔离的临时目录，光盘输出回归使用可控输入和真实文件操作。
