# FluentFormatConverter

FluentFormatConverter 是使用 WinUI 3 构建的 Windows 本地批量格式转换工具。当前源码版本为 [26.11.20.0](VERSION)。

- 转换视频、音频、图片、文档、电子书、压缩包和光盘镜像。
- 支持拖放、剪贴板和资源管理器导入，并提供格式选择、批量队列、自定义参数及输出命名。
- 支持文件夹监控、定时转换、预设、历史记录及任务恢复。
- MSIX 可集成资源管理器右键菜单、文件关联、分享目标和 EPUB 缩略图与预览。

## 系统要求

安装包支持 Windows 10 22H2（内部版本 19045）及 Windows 11，包含 x64 和 ARM64。Windows 10 ARM64 使用原生转换引擎。仅提供 x64 版本的附带工具在 ARM64 上需要 Windows 11 的 x64 仿真支持。

从源码构建需要 Windows 11、.NET SDK 11.0.100、PowerShell 7、Windows SDK，以及包含 x64 和 ARM64 C++ 工具的 Visual Studio Build Tools。

## 构建与测试

在仓库根目录运行以下命令。将 `x64` 替换为 `arm64` 可构建 ARM64，或使用 `all` 构建两个架构。

```powershell
pwsh -File .\build\validate-repository.ps1
pwsh -File .\build.ps1 -Architecture x64 -Configuration Release -Test
```

## 项目文档

- [使用与开发文档](docs/README.md)
- [架构与依赖边界](docs/ARCHITECTURE.md)
- [源码结构](src/README.md)
