[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $repoRoot 'build\RepositoryMetadata.ps1')
. (Join-Path $repoRoot 'packaging\ApplicationManifest.ps1')

. (Join-Path $repoRoot 'build/Toolchain.ps1')
$makeAppx = Get-Item -LiteralPath (Get-LockedWindowsSdkTool -RepositoryRoot $repoRoot -Name makeappx.exe)

$extensions = @(Get-RepositoryInputExtensions -RepositoryRoot $repoRoot)
$config = Get-Content -LiteralPath (Join-Path $repoRoot 'packaging\msix-config.json') -Raw | ConvertFrom-Json
$config.PackageName = 'FormatFactory.ManifestSchemaTest'
$config.DisplayName = 'Manifest schema test - not an application'
$version = Get-RepositoryReleaseVersion -RepositoryRoot $repoRoot
$workRoot = Join-Path $repoRoot ('work\shell-manifest-validation-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workRoot | Out-Null
$packageDirectory = Join-Path $workRoot 'packages'
New-Item -ItemType Directory -Path $packageDirectory | Out-Null

$expectedMinVersion = '10.0.19045.0'
foreach ($architecture in 'x64', 'arm64') {
    $stage = Join-Path $workRoot $architecture
    New-Item -ItemType Directory -Path (Join-Path $stage 'Assets') -Force | Out-Null
    foreach ($asset in 'AppIcon150.png', 'AppIcon44.png') {
        Copy-Item -LiteralPath (Join-Path $repoRoot "src\FormatFactoryWinUI\Assets\$asset") -Destination (Join-Path $stage 'Assets')
    }
    # These sentinel files satisfy manifest path checks only. This is never an installable application payload.
    foreach ($file in 'FormatFactoryWinUI.exe', 'FormatFactoryShellExtension.Native.dll',
        'Microsoft.Windows.ApplicationModel.Background.UniversalBGTask.dll') {
        [IO.File]::WriteAllText((Join-Path $stage $file), 'Manifest schema validation sentinel only.')
    }
    $manifest = New-FormatFactoryAppxManifest -TemplatePath (Join-Path $repoRoot 'packaging\AppxManifest.xml.template') `
        -InputExtensions $extensions -Config $config -Version $version -Architecture $architecture
    [xml]$document = $manifest
    $family = $document.SelectSingleNode("//*[local-name()='TargetDeviceFamily']")
    if ($family.MinVersion -cne $expectedMinVersion) { throw "Unexpected minimum OS for $architecture." }
    $associations = @($document.SelectNodes("//*[local-name()='FileTypeAssociation']"))
    $actualExtensions = @($associations | ForEach-Object {
        $_.SelectNodes("*[local-name()='SupportedFileTypes']/*[local-name()='FileType']") | ForEach-Object { $_.InnerText }
    })
    if ($actualExtensions.Count -ne $extensions.Count -or @(Compare-Object $actualExtensions $extensions).Count -ne 0) {
        throw '清单生成更改了格式目录中的文件关联。'
    }
    $previewAssociation = $document.SelectSingleNode("//*[local-name()='FileTypeAssociation' and @Name='formatfactory-epub-preview']")
    $previewTypes = @($previewAssociation.SelectNodes("*[local-name()='SupportedFileTypes']/*[local-name()='FileType']"))
    if ($previewTypes.Count -ne 1 -or $previewTypes[0].InnerText -cne '.epub') { throw '预览扩展的作用范围必须严格为 EPUB。' }
    [IO.File]::WriteAllText((Join-Path $stage 'AppxManifest.xml'), $manifest, [Text.UTF8Encoding]::new($false))
    $output = Join-Path $packageDirectory "manifest-schema-only-$architecture.msix"
    $log = Join-Path $workRoot "makeappx-$architecture.log"
    & $makeAppx.FullName pack /o /d $stage /p $output *> $log
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath $log
        throw "Windows SDK 清单模式验证失败：$architecture"
    }
    Write-Host "PASS Windows SDK manifest schema: $architecture; $expectedMinVersion; EPUB-only preview; $($extensions.Count) exact associations."
}

$bundlePath = Join-Path $workRoot 'manifest-schema-only.msixbundle'
$bundleLog = Join-Path $workRoot 'makeappx-bundle.log'
& $makeAppx.FullName bundle /bv $version /d $packageDirectory /p $bundlePath *> $bundleLog
if ($LASTEXITCODE -ne 0) {
    Get-Content -LiteralPath $bundleLog
    throw 'Windows SDK rejected the combined x64 and ARM64 test bundle.'
}
$bundle = [IO.Compression.ZipFile]::OpenRead($bundlePath)
try {
    $reader = [IO.StreamReader]::new($bundle.GetEntry('AppxMetadata/AppxBundleManifest.xml').Open())
    try { [xml]$bundleManifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $packages = @($bundleManifest.SelectNodes("//*[local-name()='Package' and @Type='application']"))
    if ($packages.Count -ne 2 -or @(Compare-Object @('x64', 'arm64') @($packages | ForEach-Object Architecture)).Count -ne 0) {
        throw 'The test bundle must contain both x64 and ARM64 application packages.'
    }
    foreach ($package in $packages) {
        $family = $package.SelectSingleNode("*[local-name()='Dependencies']/*[local-name()='TargetDeviceFamily']")
        if ($null -eq $family -or $family.GetAttribute('MinVersion') -cne $expectedMinVersion) {
            throw "The test bundle lost the Windows 10 minimum for $($package.Architecture)."
        }
    }
} finally { $bundle.Dispose() }
Write-Host "PASS combined Windows SDK manifest schema: x64 and ARM64; $expectedMinVersion."
Write-Host "Validation-only artifacts (not for installation): $workRoot"
