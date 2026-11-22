# Microsoft Store 自动更新

应用使用 Microsoft Store 原生更新服务，为商店分发的 MSIX 检查、下载和安装新版本。设置 → 应用更新中的“自动更新”默认开启，修改立即保存，不需要点击“保存设置”。旧设置文件缺少此字段时使用开启的默认值。

## 使用行为

- 启动完成后延迟 30 秒检查，然后每 6 小时检查一次，不阻塞启动或导航。
- 商店允许静默更新时，后台下载可用的更新。下载期间继续使用转换功能。
- 应用正常退出时，先停止自动化与转换、保存任务队列和窗口位置，再为已下载的更新提交静默安装。收起到托盘不属于退出。
- 不在用户使用应用期间主动执行安装或重启。退出保存失败、扫描/打印等操作仍在进行、设置仍在保存或更新仍在下载时，跳过此次安装。
- 关闭开关会立即停止应用主动的自动检查和下载，不再在退出时安装；偏好会保留到下次启动。磁盘保存失败会显示现有设置保存错误，不能保证下次启动仍关闭。
- 正常退出会等待已经开始的开关保存，避免关闭开关后立即退出丢失该偏好。
- 关闭自动更新后仍可手动“检查更新”。发现更新时引导到 Microsoft Store；此时不会自动下载或安装。
- 无包调试版、开发/侧载 MSIX 和管理员运行时，此更新通道不可用；设置页保留“打开 Microsoft Store 更新”入口。

## 平台边界

Microsoft Store 静默下载要求系统允许商店自动更新且当前网络不是计量网络；服务、网络、电池等条件可能使更新暂时不可用。未能静默下载时显示商店入口，后续自动检查可重试。[官方更新指南](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/package-updates-from-store)

检查 API 由平台限流：每 30 分钟最多重新检测一次，24 小时最多十次；超过限流返回此前已知状态，因此刚发布的更新或连续手动检查不保证立即看到新版本。[官方检查 API](https://learn.microsoft.com/en-us/uwp/api/windows.services.store.storecontext.getappandoptionalstorepackageupdatesasync)

此开关仅控制应用主动发起的更新。Microsoft Store 自身的自动更新仍由 Store/设备设置决定，应用无法通过此开关关闭它。

下载完成并不等于安装完成。退出路径显式调用静默安装 API，并撤销应用恢复重启注册，避免用户退出后再次打开应用。退出最多等待十秒；已经交给 Store 的安装不会因等待结束或窗口释放而取消，实际完成由 Store 管理。安装失败不阻止正常退出。[静默安装 API](https://learn.microsoft.com/en-us/uwp/api/windows.services.store.storecontext.trysilentdownloadandinstallstorepackageupdatesasync)

该功能随包含此代码的商店版本生效。停留在更早版本的用户需要先通过商店更新一次；新逻辑无法远程加入已安装的旧版本。独立发行版若需要更新，应另行部署带签名的 App Installer 发布渠道或独立更新服务，此次未引入这些渠道。

## 实现与验证

`WindowsStoreUpdateClient` 在窗口 UI 线程创建 `StoreContext` 并关联 HWND，使用查询、静默下载及静默安装 API。`StoreUpdateCoordinator` 串行处理检查、下载和安装，处理快速开关、取消、迟到结果与错误；只在退出入口调用安装。

单元测试覆盖开关持久化、旧设置默认、保存转换草稿不覆盖更新偏好、关闭途中取消、单飞检查、下载缓存、退出安装及失败。WinUI 冒烟测试使用隔离设置文件和假商店客户端验证真实设置事件与本地化，不调用真实更新服务。

真实 Store 下载和安装需在已由商店签名安装的版本上，通过另一个版本或 package flight 验证。本地编译和假客户端测试不能代替这项发行验证。

2026-10-12 本地验证：Release x64 构建通过；更新、设置及本地化相关单元测试 122 项通过；真实 WinUI 冒烟检查 83 项通过，报告位于 `work/ui-regression/store-updates-20261012/results.json`；仓库元数据校验通过。本轮未发布商店版本，未触发真实 Store 更新。
