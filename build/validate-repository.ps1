[CmdletBinding()]
param(
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot "RepositoryMetadata.ps1")
. (Join-Path $PSScriptRoot 'Toolchain.ps1')
[void](Get-ToolchainLock -RepositoryRoot $repoRoot)
[void](Assert-VendoredMiniz -VendorDirectory (Join-Path $repoRoot 'src/FormatFactoryShellExtension.Native/vendor/miniz'))

$version = Get-RepositoryReleaseVersion -RepositoryRoot $repoRoot
$extensions = @(Get-RepositoryInputExtensions -RepositoryRoot $repoRoot)

$configPath = Join-Path $repoRoot "packaging\msix-config.json"
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
if ($config.PSObject.Properties.Name -contains "Version") {
    throw "msix-config.json 不应再保存 Version；请只修改根目录 VERSION。"
}

$projectPaths = @(
    (Join-Path $repoRoot "src\FormatFactoryWinUI\FormatFactoryWinUI.csproj")
)
foreach ($projectPath in $projectPaths) {
    $projectText = Get-Content -LiteralPath $projectPath -Raw
    foreach ($propertyName in "Version", "AssemblyVersion", "FileVersion", "InformationalVersion") {
        if ($projectText -match "<$propertyName>") {
            throw "$([IO.Path]::GetFileName($projectPath)) 不应覆盖 $propertyName；版本由 Directory.Build.props 统一提供。"
        }
    }
}

if (Test-Path -LiteralPath (Join-Path $repoRoot 'src\FormatFactoryShellExtension\FormatFactoryShellExtension.csproj')) {
    throw '已退休的托管 Shell 项目不应重新进入 C++ Shell 基线。'
}

$appManifestPath = Join-Path $repoRoot "src\FormatFactoryWinUI\app.manifest"
$appManifestText = Get-Content -LiteralPath $appManifestPath -Raw
if ($appManifestText -match "<assemblyIdentity\b") {
    throw "app.manifest 不应复制程序集版本；程序集身份由构建版本属性生成。"
}
[xml]$appManifest = $appManifestText
$requestedExecutionLevel = $appManifest.SelectSingleNode("//*[local-name()='requestedExecutionLevel']")
if ($null -eq $requestedExecutionLevel -or
    $requestedExecutionLevel.GetAttribute("level") -cne "asInvoker" -or
    $requestedExecutionLevel.GetAttribute("uiAccess") -cne "false") {
    throw "app.manifest 必须以 asInvoker 且 uiAccess=false 运行，不能请求提升权限。"
}

foreach ($projectPath in $projectPaths) {
    [xml]$project = Get-Content -LiteralPath $projectPath -Raw
    $platformText = @($project.SelectNodes("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='Platforms']") |
        ForEach-Object { $_.InnerText }) -join ";"
    $runtimeIdentifierText = @($project.SelectNodes("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='RuntimeIdentifiers']") |
        ForEach-Object { $_.InnerText }) -join ";"
    $platforms = @($platformText.Split([char]";", [StringSplitOptions]::RemoveEmptyEntries))
    $runtimeIdentifiers = @($runtimeIdentifierText.Split([char]";", [StringSplitOptions]::RemoveEmptyEntries))
    foreach ($architecture in "x64", "arm64") {
        if ($platforms -cnotcontains $architecture -or
            $runtimeIdentifiers -cnotcontains "win-$architecture") {
            throw "$([IO.Path]::GetFileName($projectPath)) 必须同时声明 $architecture 与 win-$architecture。"
        }
    }
}

$templatePath = Join-Path $repoRoot "packaging\AppxManifest.xml.template"
$template = Get-Content -LiteralPath $templatePath -Raw
$placeholderCount = ([regex]::Matches($template, [regex]::Escape("__SUPPORTED_FILE_TYPES__"))).Count
if ($placeholderCount -ne 1 -or ([regex]::Matches($template, "__ASSOCIATED_FILE_TYPES__")).Count -ne 1 -or ([regex]::Matches($template, "__PREVIEW_FILE_TYPES__")).Count -ne 1) {
    throw "AppxManifest 模板必须分别生成普通文件关联、EPUB预览关联和完整分享目标扩展。"
}
if (([regex]::Matches($template, [regex]::Escape("__EXPLORER_ITEM_TYPES__"))).Count -ne 1 -or
    $template -match '<desktop5:ItemType\s+Type="\*"') {
    throw "AppxManifest 模板必须从格式目录生成 Explorer ItemType，且不能注册所有文件。"
}
[xml]$templateManifest = $template
$expectedManifestLanguages = @(
    "en-US", "zh-CN", "zh-TW", "ja-JP", "ko-KR", "es-ES", "fr-FR", "de-DE",
    "pt-BR", "ru-RU", "it-IT", "nl-NL", "pl-PL", "tr-TR", "ar-SA"
)
$languageDirectory = Join-Path $repoRoot "src/FormatFactory.Core/Localization/AdditionalLanguages"
$additionalLanguageCodes = @(Get-ChildItem -LiteralPath $languageDirectory -Filter "*.json" -File |
    Select-Object -ExpandProperty BaseName)
$appLanguageCodes = @($expectedManifestLanguages | Where-Object { $_ -notin @("zh-CN", "zh-TW") }) +
    @("zh-Hans", "zh-Hant") + $additionalLanguageCodes
$menuResources = Get-Content -Raw -LiteralPath (Join-Path $repoRoot "src/FormatFactoryShellExtension.Native/ExplorerLanguageData.h")
$menuLanguages = @([regex]::Matches($menuResources, '(?m)^\s*\{\s*L"(?<code>[\w-]+)"\s*,') |
    ForEach-Object { $_.Groups["code"].Value })
if (@(Compare-Object -ReferenceObject $appLanguageCodes -DifferenceObject $menuLanguages).Count -ne 0) {
    throw "资源管理器转换菜单与应用语言选项不一致。"
}
# Embedded UI packs can also serve languages that Microsoft Store does not list as package languages.
$inAppOnlyLanguages = @($config.InAppOnlyLanguages)
$expectedManifestLanguages += @($additionalLanguageCodes | Where-Object { $_ -notin $inAppOnlyLanguages })
$manifestLanguages = @($templateManifest.SelectNodes(
    "/*[local-name()='Package']/*[local-name()='Resources']/*[local-name()='Resource']") |
    ForEach-Object { $_.GetAttribute("Language") })
if ($manifestLanguages.Count -ne $expectedManifestLanguages.Count -or
    @(Compare-Object -ReferenceObject $expectedManifestLanguages -DifferenceObject $manifestLanguages).Count -ne 0) {
    throw "AppxManifest 模板必须声明应用支持的全部 $($expectedManifestLanguages.Count) 种语言。"
}
$capabilityElements = @($templateManifest.SelectNodes(
    "/*[local-name()='Package']/*[local-name()='Capabilities']/*"))
if ($capabilityElements.Count -ne 1 -or
    $capabilityElements[0].LocalName -cne "Capability" -or
    $capabilityElements[0].NamespaceURI -cne "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities" -or
    $capabilityElements[0].GetAttribute("Name") -cne "runFullTrust") {
    throw "AppxManifest 模板必须只请求桌面应用所需的 runFullTrust 能力。"
}

foreach ($disabledRegistration in @(
    "A4D7E2F1-6B90-4C3A-8D21-7F5B6E9C0A12",
    "E1B0D2E3-4F5A-4D6C-8B7C-1A2E3F4B5C6D",
    "F2C3D4E5-6A7B-4C8D-9E0F-1A2B3C4D5E6F",
    "com.microsoft.windows.widgets"
)) {
    if ($template.IndexOf($disabledRegistration, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "AppxManifest 模板仍注册未完成的 Shell 功能：$disabledRegistration"
    }
}

$epubAssociation = $templateManifest.SelectSingleNode("//*[local-name()='FileTypeAssociation' and @Name='formatfactory-epub-preview']")
if ($null -eq $epubAssociation -or $extensions -notcontains '.epub' -or
    $epubAssociation.InnerText -notmatch '__PREVIEW_FILE_TYPES__') {
    throw "EPUB Shell 预览只能注册格式目录允许导入的 EPUB。"
}
$handlerIds = @{
    'ThumbnailHandler' = '7657D52B-308A-4B9F-821F-5D839DB74A11'
    'DesktopPreviewHandler' = '34E71D22-EBB7-49E1-A7B3-CB9186CC4D20'
}
foreach ($handler in $handlerIds.GetEnumerator()) {
    $nodes = @($templateManifest.SelectNodes("//*[local-name()='$($handler.Key)']"))
    if ($nodes.Count -ne 1 -or $nodes[0].ParentNode -ne $epubAssociation -or
        $nodes[0].GetAttribute('Clsid') -cne $handler.Value) {
        throw "Shell 预览处理器必须精准注册且仅属于 EPUB：$($handler.Key)"
    }
    $class = $templateManifest.SelectSingleNode("//*[local-name()='SurrogateServer']/*[local-name()='Class' and @Id='$($handler.Value)']")
    if ($null -eq $class -or $class.GetAttribute('ThreadingModel') -cne 'STA' -or
        $class.GetAttribute('Path') -cne 'FormatFactoryShellExtension.Native.dll') {
        throw "EPUB Shell 处理器必须由 STA COM surrogate 隔离承载。"
    }
}
$catalogText = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot "src\FormatFactory.Core\Models") -Filter "FormatCatalog*.cs" -File |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join [Environment]::NewLine
if ($catalogText.IndexOf("formats.json", [StringComparison]::Ordinal) -lt 0) {
    throw "FormatCatalog 必须从嵌入式 formats.json 加载格式定义。"
}

$shellBuildPath = Join-Path $repoRoot 'src\FormatFactoryShellExtension.Native\build.ps1'
$shellBuildText = Get-Content -LiteralPath $shellBuildPath -Raw
if ($shellBuildText.IndexOf('src/FormatFactory.Core/Models/formats.json', [StringComparison]::Ordinal) -lt 0 -or
    $shellBuildText.IndexOf("-contains 'Import'", [StringComparison]::Ordinal) -lt 0 -or
    $shellBuildText.IndexOf('Formats.generated.h', [StringComparison]::Ordinal) -lt 0) {
    throw "C++ Shell 必须由 Core 的共享格式目录生成扩展表，并仅接受具有 Import 能力的扩展。"
}

. (Join-Path $repoRoot 'packaging\ApplicationManifest.ps1')
[void](New-FormatFactoryAppxManifest -TemplatePath $templatePath -InputExtensions $extensions `
    -Config $config -Version $version -Architecture x64)
$shellClasses = @($templateManifest.SelectNodes("//*[local-name()='SurrogateServer']/*[local-name()='Class']"))
$explorerClass = @($shellClasses | Where-Object { $_.GetAttribute('Id') -ceq 'B23F9D12-9ED4-4A40-B854-7A2B0C7D9A14' })
if ($shellClasses.Count -ne 3 -or $explorerClass.Count -ne 1 -or
    $explorerClass[0].GetAttribute('ThreadingModel') -cne 'Both' -or
    $explorerClass[0].GetAttribute('Path') -cne 'FormatFactoryShellExtension.Native.dll') {
    throw "正式 Shell 基线必须仅注册 C++ Explorer 命令及 EPUB 缩略图、预览三个 COM 类。"
}

$storeBuildScriptPath = Join-Path $repoRoot "packaging\build-store-msix.ps1"
$storeBuildScriptText = Get-Content -LiteralPath $storeBuildScriptPath -Raw
if ($storeBuildScriptText -notmatch '(?s)&\s+\$makeAppx\.FullName\s+bundle\s+/o\s+/bv\s+\$version\b' -or
    $storeBuildScriptText.IndexOf('foreach ($architecture in "x64", "arm64")', [StringComparison]::Ordinal) -lt 0 -or
    $storeBuildScriptText.IndexOf("Assert-MsixUpload", [StringComparison]::Ordinal) -lt 0) {
    throw "Store 打包必须固定根 VERSION、构建 x64/ARM64，并校验 .msixupload 结构。"
}

& (Join-Path $PSScriptRoot "validate-architecture.ps1") -Quiet
& (Join-Path $PSScriptRoot "validate-dependencies.ps1") -Quiet
& (Join-Path $repoRoot 'tests/EnginePackaging/Run.ps1') | Out-Null

if (-not $Quiet) {
    Write-Host "仓库元数据校验通过：版本 $version，输入扩展 $($extensions.Count) 个。"
}
