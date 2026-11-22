#requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateSet("x64", "arm64")]
    [string]$Architecture = "x64",
    [string]$ConfigPath = (Join-Path $PSScriptRoot "msix-config.json"),
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) "outputs\msix"),
    [string]$CertificatePath,
    [string]$CertificatePassword,
    [switch]$NoRestore,
    [switch]$OfflineEngines,
    [switch]$SkipRepositoryValidation
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
$metadataScript = Join-Path $repoRoot "build\RepositoryMetadata.ps1"
$validationScript = Join-Path $repoRoot "build\validate-repository.ps1"
. $metadataScript
. (Join-Path $PSScriptRoot "EnginePackaging.ps1")
. (Join-Path $PSScriptRoot "ApplicationManifest.ps1")
. (Join-Path $repoRoot 'build/Toolchain.ps1')
$releaseVersion = Get-RepositoryReleaseVersion -RepositoryRoot $repoRoot
$inputExtensions = @(Get-RepositoryInputExtensions -RepositoryRoot $repoRoot)
$projectPath = Join-Path $repoRoot "src\FormatFactoryWinUI\FormatFactoryWinUI.csproj"
$templatePath = Join-Path $PSScriptRoot "AppxManifest.xml.template"
$runtimeIdentifier = "win-$Architecture"
$workRoot = Join-Path $repoRoot "work"
$publishDirectory = Join-Path $workRoot "publish-msix-$Architecture"
$stagingDirectory = Join-Path $workRoot "msix-staging-$Architecture"
$shellPublishDirectory = Join-Path $workRoot "publish-shell-msix-$Architecture"
$requiredApplicationLibraries = @(
    "FormatFactory.Core.dll",
    "FormatFactory.Engine.dll",
    "FormatFactory.Documents.dll",
    "FormatFactory.Presentation.dll"
    "FormatFactory.FilterWorker.exe",
    "FormatFactory.FilterWorker.dll",
    "FormatFactory.FilterWorker.deps.json",
    "FormatFactory.FilterWorker.runtimeconfig.json"
)
$requiredShellFiles = @('FormatFactoryShellExtension.Native.dll','native-shell-build.json','miniz.LICENSE.txt')

function Reset-BuildDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$AllowedRoot
    )

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $resolvedRoot = [IO.Path]::GetFullPath($AllowedRoot)
    $trimCharacters = [char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $rootPrefix = $resolvedRoot.TrimEnd($trimCharacters) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝清理工作区外目录：$resolvedPath"
    }

    if (Test-Path -LiteralPath $resolvedPath) {
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $resolvedPath | Out-Null
}

function Assert-RequiredFiles {
    param(
        [Parameter(Mandatory)]
        [string]$Directory,
        [Parameter(Mandatory)]
        [string[]]$Names,
        [Parameter(Mandatory)]
        [string]$Label
    )

    $missing = @($Names | Where-Object { -not (Test-Path -LiteralPath (Join-Path $Directory $_) -PathType Leaf) })
    if ($missing.Count -gt 0) {
        throw "$Label 缺少必需文件：$($missing -join ', ')"
    }
}

function Assert-PackagedShellRegistration {
    param(
        [Parameter(Mandatory)][string]$Manifest
    )

    [xml]$document = $Manifest
    $namespaces = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $namespaces.AddNamespace('com', 'http://schemas.microsoft.com/appx/manifest/com/windows10')
    $namespaces.AddNamespace('desktop2', 'http://schemas.microsoft.com/appx/manifest/desktop/windows10/2')
    $classes = @($document.SelectNodes('//com:SurrogateServer/com:Class', $namespaces))
    $path = 'FormatFactoryShellExtension.Native.dll'
    $expected = @{
        'B23F9D12-9ED4-4A40-B854-7A2B0C7D9A14' = 'Both'
        '7657D52B-308A-4B9F-821F-5D839DB74A11' = 'STA'
        '34E71D22-EBB7-49E1-A7B3-CB9186CC4D20' = 'STA'
    }
    if ($classes.Count -ne $expected.Count) { throw 'MSIX Shell COM registration must contain exactly three classes.' }
    foreach ($class in $classes) {
        $id = ([string]$class.Id).ToUpperInvariant()
        if (-not $expected.ContainsKey($id) -or $class.Path -ne $path -or $class.ThreadingModel -ne $expected[$id]) {
            throw "MSIX Shell COM registration changed unexpectedly: $id"
        }
    }
    foreach ($registration in @(
        @{ XPath='//desktop2:ThumbnailHandler'; Id='7657D52B-308A-4B9F-821F-5D839DB74A11' },
        @{ XPath='//desktop2:DesktopPreviewHandler'; Id='34E71D22-EBB7-49E1-A7B3-CB9186CC4D20' }
    )) {
        $nodes = @($document.SelectNodes($registration.XPath, $namespaces))
        if ($nodes.Count -ne 1 -or $nodes[0].Clsid -ne $registration.Id) {
            throw "MSIX EPUB handler registration changed unexpectedly: $($registration.Id)"
        }
    }
}

function Assert-MsixPayload {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath,
        [Parameter(Mandatory)]
        [string[]]$RequiredEntries
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entryNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            [void]$entryNames.Add($entry.FullName.Replace("\", "/"))
        }
        $missing = @($RequiredEntries | Where-Object { -not $entryNames.Contains($_) })
        if ($missing.Count -gt 0) {
            throw "MSIX 包缺少 Shell Extension 文件：$($missing -join ', ')"
        }
    }
    finally {
        $archive.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $ConfigPath)) {
    throw "MSIX 配置文件不存在：$ConfigPath"
}

if (-not $SkipRepositoryValidation) {
    & $validationScript -Quiet
}
$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
foreach ($property in "PackageName", "Publisher", "PublisherDisplayName", "DisplayName", "Description") {
    if ([string]::IsNullOrWhiteSpace([string]$config.$property)) {
        throw "MSIX 配置缺少：$property"
    }
}
if ($config.PSObject.Properties.Name -contains "Version") {
    throw "msix-config.json 不应保存 Version；请只修改仓库根目录 VERSION。"
}

if ($config.Publisher -eq "CN=Format Factory") {
    Write-Warning "当前 Publisher 是本地测试值。上传 Microsoft Store 前，请替换为 Partner Center 分配的 Publisher。"
}

& (Join-Path $PSScriptRoot 'prepare-engines.ps1') -Architecture $Architecture -Offline:$OfflineEngines
$preparedEngines = Join-Path $workRoot "prepared-engines/$Architecture/Engines"
[void](Assert-EngineReceipt -EngineRoot $preparedEngines -Architecture $Architecture -Fingerprint (Get-EnginePreparationFingerprint -PackagingDirectory $PSScriptRoot))
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
Reset-BuildDirectory -Path $publishDirectory -AllowedRoot $workRoot
Reset-BuildDirectory -Path $stagingDirectory -AllowedRoot $workRoot
Reset-BuildDirectory -Path $shellPublishDirectory -AllowedRoot $workRoot

$mainPublishArguments = @(
    "publish", $projectPath,
    "-c", "Release",
    "-p:Platform=$Architecture",
    "-p:RuntimeIdentifiers=$runtimeIdentifier",
    "-r", $runtimeIdentifier,
    "--self-contained", "true",
    "-p:RestoreLockedMode=true",
    "-o", $publishDirectory
)
if ($NoRestore) {
    $mainPublishArguments += "--no-restore"
}
Invoke-LockedDotnet -RepositoryRoot $repoRoot -Arguments $mainPublishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败。"
}

Assert-RequiredFiles -Directory $publishDirectory -Names $requiredApplicationLibraries -Label "Application modules and isolated worker publish"

& (Join-Path $repoRoot 'src/FormatFactoryShellExtension.Native/build.ps1') -Architecture $Architecture -Configuration Release -OutputDirectory $shellPublishDirectory -RunTests
Copy-Item -LiteralPath (Join-Path $repoRoot 'src/FormatFactoryShellExtension.Native/vendor/miniz/LICENSE') -Destination (Join-Path $shellPublishDirectory 'miniz.LICENSE.txt')
Assert-RequiredFiles -Directory $shellPublishDirectory -Names $requiredShellFiles -Label 'C++ Shell publish'
foreach ($shellFile in $requiredShellFiles) {
    Copy-Item -LiteralPath (Join-Path $shellPublishDirectory $shellFile) -Destination $publishDirectory -Force
}

Copy-Item -Path (Join-Path $publishDirectory "*") -Destination $stagingDirectory -Recurse -Force
Get-ChildItem -LiteralPath $stagingDirectory -Filter "*.pdb" -Recurse -File |
    Remove-Item -Force
Assert-RequiredFiles -Directory $stagingDirectory -Names ($requiredShellFiles + $requiredApplicationLibraries) -Label "MSIX staging"
Copy-Item -LiteralPath $preparedEngines -Destination $stagingDirectory -Recurse -Force
$stagedEngines = Join-Path $stagingDirectory 'Engines'
[void](Assert-EngineReceipt -EngineRoot $stagedEngines -Architecture $Architecture)
$manifest = New-FormatFactoryAppxManifest -TemplatePath $templatePath -InputExtensions $inputExtensions `
    -Config $config -Version $releaseVersion -Architecture $Architecture
Assert-PackagedShellRegistration -Manifest $manifest
[IO.File]::WriteAllText((Join-Path $stagingDirectory "AppxManifest.xml"), $manifest, [Text.UTF8Encoding]::new($false))

$makeAppx = Get-Item -LiteralPath (Get-LockedWindowsSdkTool -RepositoryRoot $repoRoot -Name makeappx.exe)

$packageName = "{0}-{1}-{2}.msix" -f $config.PackageName, $releaseVersion, $Architecture
$packagePath = Join-Path $OutputDirectory $packageName
& $makeAppx.FullName pack /o /d $stagingDirectory /p $packagePath
if ($LASTEXITCODE -ne 0) {
    throw "MakeAppx 打包失败。"
}

if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
    if (-not (Test-Path -LiteralPath $CertificatePath)) {
        throw "签名证书不存在：$CertificatePath"
    }
    $signTool = Get-Item -LiteralPath (Get-LockedWindowsSdkTool -RepositoryRoot $repoRoot -Name signtool.exe)
    $signArguments = @("sign", "/fd", "SHA256", "/a", "/f", $CertificatePath)
    if (-not [string]::IsNullOrWhiteSpace($CertificatePassword)) {
        $signArguments += @("/p", $CertificatePassword)
    }
    $signArguments += $packagePath
    & $signTool.FullName @signArguments
    if ($LASTEXITCODE -ne 0) {
        throw "MSIX 签名失败。"
    }
}

Assert-MsixPayload -PackagePath $packagePath -RequiredEntries ($requiredShellFiles + $requiredApplicationLibraries)
Assert-MsixEnginePayload -PackagePath $packagePath -EngineRoot $stagedEngines -Architecture $Architecture
& (Join-Path $PSScriptRoot 'validate-msix.ps1') -PackagePath $packagePath -Architecture $Architecture
Write-Host "MSIX 已生成：$packagePath"
if ([string]::IsNullOrWhiteSpace($CertificatePath)) {
    Write-Warning "该包未签名，仅适合提交前检查；本机安装测试需要使用与 Publisher 匹配的证书签名。"
}
