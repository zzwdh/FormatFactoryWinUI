# WinUI lifecycle and motion smoke tests

Task-experience checks load the real archive and media queue templates. They cover
archive restart and exit recovery, password-free snapshots, common history replay,
publication receipts, result summaries, and failure buttons with task-specific output
folders. Fixtures and reports stay under the isolated run directory. Capture mode
also renders restored encrypted archives, completed summaries, and localized failure actions.

Feedback checks also exercise settings action forwarding, error status across languages,
busy state, and the actual diagnostic report using the isolated smoke data directory.
The loaded feedback form submits to a simulated Sentry receiver and checks that it
closes without a report preview or upload result notification. Telemetry preference
checks exercise independent immediate saving and localization. Tests never send
real telemetry or open a user's feedback page.

Run on an interactive Windows desktop with the repository's .NET SDK installed:

```powershell
./tests/WinUiSmoke/Run.ps1
```

For launch latency, use the separate visible production-startup measurement:

```powershell
./tests/WinUiSmoke/Measure-Startup.ps1 -NuGetConfigPath work/feature-validation/NuGet.Config
```

This compiles the real `App.xaml.cs`, including activation and application services,
and records `window-visible`, `add-files-ready`, and `recovery-complete` from process
creation. It measures 0/100/1,000 retained tasks in three independent processes per
queue size. Fixture preparation runs in a separate process; each sample uses private
data and the prepared x64 engines. The first rendering callback is a visible-frame
proxy, not a DWM presentation measurement. Readiness also waits for the UI dispatcher
and is verified by importing an actual new file. Recovery includes the archive queue.
Reports retain individual samples, stage start times/durations, source fingerprints,
and prelaunch CPU observations. No reboot or file-cache eviction is performed, so
these results must not be called disk-cold startup or installed MSIX acceptance.
The older `Run.ps1 -Startup` remains a non-visible restoration regression check;
its `first-interactive` value means settings initialization, not import readiness.

The default run compiles the actual host XAML, eagerly constructed Queue/History/Settings
UserControls, shared page resources and window code into an isolated
application under `work/ui-regression`. It never activates a window or takes focus.
The generated host reads its Windows App SDK component references from the production
project, so the smoke run exercises the same reduced SDK dependency set rather than
silently restoring the full metapackage.
Compatible-audio checks show one isolated window off screen so the actual task template
is loaded; the window is closed after the checks.
It checks deferred settings initialization, startup localization, repeated settings
visits, and language changes, plus the following motion-related invariants:

Language checks switch through every interface language and verify page titles,
the language picker, the root culture and the reading direction. Capture mode also
renders narrow settings pages and wide automation pages in Hungarian, Greek,
Ukrainian, Vietnamese, Thai, Hindi, Bangla, Tamil, Arabic, Hebrew, Persian, Urdu,
Gurmukhi Punjabi, Amharic and Burmese to inspect longer text, font fallback and
right-to-left layouts. Desktop dialog checks cover Arabic, Hebrew, Persian, Urdu,
Shahmukhi Punjabi and Pashto reading direction.

- Custom engine controls stay in the settings page's default-collapsed advanced group,
  including disc engines. Saved overrides survive first visits, localization and
  startup fallback discovery. Disc and archive conversion have direct main-navigation
  entries; the obsolete advanced tools page and Windows tools card are absent.
- The real restore-defaults action rejects busy queues, resets preferences and the
  settings draft, disables retained automation rules, stops the file watcher, and
  preserves pending queue data and source/output files. Its startup dependency is
  replaced by a recording callback so Windows startup registration is unchanged.
  An injected settings-write failure also verifies that enabled rules and startup
  state roll back across the separate settings and automation stores.
- Both new setting buttons are connected to their window handlers. The unpackaged
  rating action displays its Microsoft Store installation hint without requesting
  or submitting a rating.

- Compatible stereo audio remains an explicit opt-in. The real loaded queue template
  checks English, Chinese and Portuguese labels, omission warnings, queue autosave,
  lossless and missing-AAC admission guards, running-task editing protection and
  unchanged source bytes. Synthetic metadata marks an APAC/AAC pair; this UI fixture
  does not run an encoder or claim APAC decoding.

- Closing a window makes late background UI dispatch and progress callbacks inert.

- Restoring an unavailable disc with an empty target keeps the visible and durable
  queue record blocked without reporting a UI exception. Spanish, English and Chinese
  reminders survive, selecting a valid target refreshes engine availability, and a
  missing source format remains blocked. Built-in ISO/CSO routes still work without
  external engines; the fixtures never access physical media or start a conversion.

- Unloaded category, disc, archive, settings, and history navigation reaches its final state synchronously.
- The first and last task switch correctly between the list and the empty state.
- Adding 100 sample tasks preserves existing view models and causes no collection reset.
- Repeated filtering preserves task identity and actual conversion progress.
- A filtered empty state does not remove backing tasks.
- Every task state has distinct text and icon feedback; unloaded progress stays exact.
- Queue undo restores the original task and its in-memory password; priority updates
  the displayed grouped queue without changing running work. New action handlers are
  subscribed once, and undo/source-recovery labels expose localized accessible names.
  Capture mode includes the undo bar in the narrow dark layout. Automatic retries
  ignore source-location metadata while still rejecting changed conversion settings.

The native workflow checks create temporary image/PDF fixtures under the run folder.
They exercise real Windows file drag data (including directories), image conversion
without FFmpeg, alpha flattening, PDF page navigation, rapid preview changes and
file-handle release. Window placement is restored and saved through a real hidden
AppWindow. These checks do not modify the system clipboard or submit print jobs.

## Rendered captures and loaded motion checks

```powershell
./tests/WinUiSmoke/Run.ps1 -Capture -RunName motion-review
```

For focused motion validation without the conversion-engine checks:

```powershell
./tests/WinUiSmoke/Run.ps1 -Capture -MotionOnly -TimeoutSeconds 300 -RunName fluent-motion-review
```

`-MotionOnly` requires `-Capture` and cannot be combined with `-Performance`,
`-Startup`, or `-ArchiveOnly`. It retains page initialization, task-card invariants,
loaded Composition animations, interrupted navigation, disabled-motion behavior,
layout captures, and window-close checks. It skips the separate conversion-engine
and workflow suites. `results.json` records `motionOnlyRequested` so this focused
run is distinguishable from the complete suite.

`-Capture` explicitly activates a temporary test window, then closes it after the
checks. The harness detaches only the production `MainWindow_Loaded` startup
handler, so real XAML layout and motion still run without starting conversion
engines, downloads, the system tray, or production startup services. It uses
synthetic task paths, isolated settings, and isolated logs. It never redirects
activation to the installed application or opens real user media.

Capture mode temporarily enables the private animation policy snapshot inside the
isolated test process, including disabling its simulated high-contrast flag. A scoped
cleanup restores all previous values. It never changes Windows animation or contrast
settings. The run explicitly asserts that a loaded, visible Composition fade returns
an incomplete task and then reaches its final opacity. Results record the original
preferences, enabled flag, asynchronous fade observation, and successful restoration.

In addition to the default checks, capture mode checks rapid navigation while
loaded, interrupted fade completion, turning off the isolated process's animation
preference during navigation, exact progress through pause and retry, and category
scroll restoration with 100 tasks, and interleaved drag feedback without importing files. It does not change Windows animation settings.

Navigation checks exercise direct disc and archive selection in compact and expanded
panes, selected-route preservation through toggles, short-window reachability, and
label/glyph bounds in Chinese and right-to-left layouts. All interface languages keep
complete accessible names for both conversion entries.

Capture mode also opens the production review reminder in Arabic and dark theme,
checks its three choices and right-to-left layout, and verifies that its `Closed`
event occurs before a business dialog's `Opened` event. Both dialogs close through
the real WinUI lifecycle. This check never registers a launch, persists reminder
state, or submits a Microsoft Store rating; the default hidden run shows no dialogs.

PNG files in `work/ui-regression/<RunName>/captures` cover the queue in light and
dark themes, a narrow queue, wide light and narrow dark settings, disc and archive
conversion pages, compact and expanded navigation, history, and
wide light and narrow dark empty states. Wide and narrow client areas are 1280 and 720
logical pixels; the native window dimensions account for display scaling. Each capture
resets its page and list scroll positions to the top. These are `RenderTargetBitmap` captures
of actual XAML content. They support review of layout, clipping, and theme colors;
a test-only theme resource supplies the solid background that Mica falls back to.
They do not capture the native title bar, desktop-dependent Mica backdrop, or prove
the subjective smoothness of an animation. Review those in the running application
when the final visual experience is evaluated.

## Baseline regression runs

```powershell
./tests/WinUiSmoke/Run.ps1 -BaselinePath ./work/crash-fix-20260925/baseline -RunName baseline
```

The baseline folder must contain `MainWindow.xaml` and `MainWindow.xaml.cs`.
Current layouts place settings in
`Windowing/MainWindow/Settings/MainWindow.Settings.cs` and motion in
`Windowing/MainWindow/Navigation/MainWindow.Motion.cs`; the runner also accepts
the historical top-level `MainWindow.Settings.cs` and `MainWindow.Motion.cs`.
Include the motion file when testing a baseline
from after the motion upgrade. Older baselines skip the motion navigation checks;
use their original window files for the settings lifecycle regression. Capture
mode requires a baseline with the new motion members.

A failing run exits with an error and writes exception stacks and missing settings
control names to `results.json`. Each run also records whether capture mode was
requested. All generated projects, builds, captures, and run data stay under that
run folder. The temporary project is outside `src` and `tests`, so production/test
project architecture boundaries remain intact.

## Repeatable UI performance checks

```powershell
./tests/WinUiSmoke/Run.ps1 -Performance -RunName candidate-performance -TimeoutSeconds 600
./tests/WinUiSmoke/Run.ps1 -HostSourcePath ./work/ui-refactor/baseline/host -Performance -RunName baseline-performance -TimeoutSeconds 600
./tests/WinUiSmoke/Compare-Performance.ps1 -Baseline ./work/ui-regression/baseline-performance/performance.json -Candidate ./work/ui-regression/candidate-performance/performance.json
```

`-Performance` fixes the isolated application to Light before creating XAML so desktop
theme changes cannot invalidate baseline comparison. Reports identify the repository
commit, content fingerprint and capture time. Host/window source overrides are marked
dirty and are for diagnostic comparisons; formal baselines use a complete clean checkout.

`-Performance` runs separately from functional smoke checks. It activates one isolated
test window per process and aggregates nine fresh-process samples: UI construction, first settings
initialization, loaded 1,000/10,000-task category-filter roundtrips, and a deterministic
2,500-paragraph DOCX-to-EPUB conversion using the built-in document engine. Reports include
P95 wall time, private-memory peaks for document conversion, and realized container counts. Queue runs assert
retained task identity and virtualization. Each fresh process also records handle
counts before construction, after construction, and after closing the window; these
are diagnostics, not a long-running leak test. Construction measures the window/services,
not production cold startup: real engine discovery, activation, tray and user data are
excluded. The large-document fixture is created before timing, while engine discovery,
conversion and output validation are included. The reports record its input size and
verify that every process and both revisions use the same input and engine. Performance
mode disables animations only inside its test process, then
restores the prior preference.

The comparison rejects different recorded environments and fails when a measured P95
or realized-container count grows by more than 10%. Use the same machine, OS/runtime,
display scale, power configuration and background workload. A single noisy result
requires investigation and a paired rerun; it is not evidence of a speed improvement.
`-HostSourcePath` accepts a complete saved host source tree, so pre-extraction XAML can
still be built independently of newly added views. The usual `-BaselinePath` option is
for the older, narrower settings-crash baselines. No baseline is downloaded or assumed.
Use `-BuildOnly` to prepare both programs first, then `-SkipBuild` with the same run
names after other compilation and background work has finished; sampling then performs
no build work between the baseline and candidate runs.


## 自动更新验证

`StoreUpdateSmoke.cs` 使用独立设置文件、真实队列存储和假 Store 客户端验证开关即时保存、关闭下载、本地化重绘及退出安装门禁，不连接实际商店。通过 `Run.ps1` 随常规检查执行。

## 摘要、命名预览与大批量接入

`ImportExperienceSmoke.cs` 通过真实窗口导入入口验证自然顺序和中途取消：第一批保留、未发布的第二批可再次导入、不重复创建已入队任务。性能模式另测 1,000／10,000 个等待和失败任务经 `ApplyImportResult` 到可见队列的接入耗时，并检查模型身份与列表虚拟化；输入模型预先准备，此指标不包含文件扫描、媒体探测或队列落盘耗时。

性能报告使用 schema 2，包含原五项指标、四项接入指标，以及一个真实多输出组混排于 10,000 项队列的筛选往返指标，共十项。比较器继续验证旧 schema 1 报告，但拒绝不同版本之间的比较，避免缺少新增指标的报告被当作完整基线。

`ResultSummarySmoke.cs` 检查队列和历史中的测量值、本地化，以及复用历史卡片时旧统计清空。`NamingPreviewSmoke.cs` 检查选中来源、模板插入、尺寸、目录结构和保存前预览，不创建输出目录。`-Capture` 会保存这两项的截图。

常规测试另有 `ResourceLifecycleRegressionTests`，可设置 `FORMAT_FACTORY_STRESS_CYCLES=100` 执行一百轮目录监听与转换子进程取消，并用 `FORMAT_FACTORY_PERFORMANCE_REPORT_DIR` 保存句柄和内存采样。这是重复资源释放回归，不代表数小时运行认证。重复常规界面回归时使用新的 `-RunName`，避免旧用例留下的设置与预设影响结果。

## 压缩包互转验证

`ArchiveConversionSmoke.cs` 检查主导航的压缩包页面直接承载转换控件，并使用公开虚构压缩包与真实控件验证整批／单任务设置、加密格式限制、ZIP 密码限制、分卷归并、坏包失败后继续、源密码重试，以及取消后清空当前及等待任务的密码。

先运行 `packaging/archive/Prepare-ArchiveEngine.ps1 -Architecture x64` 准备官方引擎，再用 `Run.ps1 -ArchiveOnly -Capture -RunName archive-<唯一名称>` 单独验证压缩工具。常规完整回归也包含这些检查。所有输入、输出、设置及截图均位于独立测试目录，不读取生产用户的文件，不向实际反馈接收端发送数据。

