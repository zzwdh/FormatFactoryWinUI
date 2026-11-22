# 文档与电子书引擎裁剪

LibreOffice 与 Calibre 按格式工厂实际使用的转换路线准备。裁剪在锁定版本和 SHA256 校验后的 MSI 管理员映像上执行，不修改下载缓存，不安装或注册这些软件。当前版本以根目录 `VERSION` 为准，交付目录统一为 `outputs/msix/`。

## 转换范围

| 引擎 | 应用调用范围 | 保留的关键资源 | 移除的可选部分 |
| --- | --- | --- | --- |
| LibreOffice | DOC/DOCX → PDF，以及 DOC → DOCX 规范化/修复 | 原生 Writer/Calc/Draw/Math 与导入/导出库，图表、OLE、Basic/VBA，私有字体，语言/布局资源和所有语言的断字字典 | 独立 Python、Python/Java UNO 组件、预装脚本/向导、AutoText/AutoCorrect、Notebookbar、Lightproof Python 语法检查器，以及原有帮助/模板/图库等裁剪项 |
| Calibre | EPUB → MOBI/AZW3，以及 ebook-meta 元数据验证 | 完整冻结 Python 归档与索引、转换/元数据插件，QtCore/Gui/Widgets/Network/Svg、图片解码、字体、ICU/语言映射、封面/书衣模板与断字资源 | Qt WebEngine、QML/Quick、阅读器/编辑器/服务端、新闻、语音、多媒体/私有 FFmpeg、命令行消息翻译及未使用的 Qt 绑定/插件 |

PDF、DOC/DOCX、CAJ 与图片等输入在输出 MOBI/AZW3 时，先由应用自己的文档、提取或 OCR 流程生成 EPUB，再交给 Calibre。Calibre 自身的 PDF 输出、浏览器、阅读器、编辑器、新闻抓取和朗读不属于这个配置；应用支持的转换路线保持原有职责划分。

LibreOffice 的 Basic/VBA、Math 与 OLE 库用于文档导入和渲染。保留这些库不代表启用宏执行。Java 桥接 DLL 中，`mergedlo.dll` 仍导入的 `jvmaccesslo.dll`、`jvmfwklo.dll` 继续保留。删除 Python/Java UNO 实现时同步删除对应工厂注册；Lightproof 删除时同步调整扩展注册，保留断字字典及其配置。

Calibre 的冻结 Python 归档不做逐模块改写。其启动器和内置插件枚举会动态导入多个模块，归档索引也参与启动，简单删除 Python 文件无法证明安全。裁剪集中于明确不在产品路线内的独立组件；留下的转换插件、Qt、字体和语言资源仍随包交付，不要求用户联网补装。

## 准备与保护

`packaging/LibreOfficePruning.ps1` 和 `packaging/CalibrePruning.ps1` 使用明确的路径清单裁剪，执行前检查关键运行文件。它们拒绝运行目录、祖先或后代中的符号链接/目录联接，并在删除前验证绝对路径仍处于指定运行目录。移除子树中的许可证、版权和通知文本复制到 `pruning-notices/`；根许可证及各组件来源锁继续保留。

两项裁剪可重复执行，已裁剪目录再次执行不会继续减少体积。准备脚本、裁剪模块、来源锁和许可文档都参与准备缓存指纹。`bundle-manifest.json` 继续记录保留文件的长度与 SHA256，staging 与成品 MSIX 再次按此清单逐文件核对。

原包私有模块清单用于检查裁剪后的普通和延迟导入，保留模块不得引用已经删除的私有依赖。该检查覆盖 EXE、DLL、PYD 与 LibreOffice 的 PE 格式 BIN/COM 入口。静态依赖检查与真实转换语料配合使用，因为运行时动态加载的依赖不能只靠 PE 导入表证明。

## 验证方法

`packaging/smoke-office-ebook-pruning.py` 可将同一套语料分别交给裁剪前和裁剪后的引擎。运行时使用隔离配置/缓存目录，并限制 PATH，避免借用开发机上的其他软件。测试覆盖中文、图片、图表、表格、页眉页脚、分页及超链接；PDF 比较正文、页数、图像/绘图内容和逐页渲染结果。DOC 往返及 DOCX 规范化也进入对比。

电子书语料覆盖 MOBI/AZW3、中英文语言设置、元数据、PNG/SVG 封面与正文、CSS 和嵌入字体。输出经固定的裁剪前引擎回读后比较内容；禁用回读时随机生成的默认封面，解混淆 EPUB 字体后比较完整字体哈希。PDF 时间戳、电子书随机标识等不稳定字节不作为内容变化依据。测试同时检查引擎目录未被转换过程修改。

另外用 LibreOffice 上游固定提交的 VBA、OLE、OMML 矩阵及 MathType 语料，比较直接 PDF 和规范化 DOCX 后 PDF 的正文、页数与渲染结果。源语料 SHA256 与固定上游位置保存在审计目录；测试未执行文档宏。

防误删和依赖检查的合成回归入口为 `tests/EnginePruning/Run.ps1`。其范围包括必需资源、许可、注册项、幂等性、越界/目录联接拒绝、普通/延迟/间接依赖缺失、PATH 隔离和错误架构。完整运行结果与当前包验收结果应一同查看，不能将有限语料等同于所有文档的兼容性保证。x64 主机不能代替原生 ARM64 真机转换验收。

## 体积与证据

试验中 LibreOffice x64 从 621,184,431 字节降至 538,114,138 字节，净减少 83,070,293 字节（79.22 MiB）；Calibre 从 662,075,036 字节降至 276,560,433 字节，净减少 385,514,603 字节（367.66 MiB）。以上是准备目录的未压缩字节，已计入保留的通知文件，不能直接当作安装包下载体积的减少量。ARM64 应用包使用相同的 x64 Calibre 运行时；LibreOffice 使用其原生 ARM64 构建，须单独测量。

第二阶段的基线为裁剪前 26.10.25.0 成品，与第一阶段 26.10.23.0 的 FFmpeg/SDK 对比独立统计。成品对比应报告每架构 MSIX 的实际字节、包内引擎压缩字节和 Bundle 字节，以及各成品 SHA256。

本地审计材料位于 `work/office-ebook-pruning/`：

- `baseline-packages.json`：裁剪前成品、组件压缩体积和哈希。
- `lo-pruning-result.json`、`lo-pruning-idempotency.json`、`lo-protected-dependencies.json`：LibreOffice 删除清单、重复执行与字体/断字资源保护检查。
- `lo-upstream-ab/results.json`、`upstream-fixtures/sources.json`：上游复杂文档的转换对比与来源哈希。
- `calibre-experiment/` 下的 `measurement.json`、`pe-closure-final.json`、`dynamic-loading-summary.json`、`idempotence-final.json`：Calibre 文件对比、静态与动态依赖、重复执行检查。
- 语料输出目录中的 `latest-results.json`：当前转换运行的完整状态、逐项结果与详细输出位置；以实际运行状态为准。

许可与对应源码义务沿用各组件原许可证，完整入口见 [BUNDLED-ENGINES.md](../../packaging/BUNDLED-ENGINES.md)。这轮裁剪不修改正式商店发布门禁。
