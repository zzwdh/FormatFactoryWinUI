#requires -Version 7.2
[CmdletBinding()]
param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot "msix-config.json"),
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) "outputs\msix"),
    [string]$CertificatePath,
    [string]$CertificatePassword,
    [switch]$NoRestore,
    [switch]$OfflineEngines,
    [switch]$CandidateOnly,
    [string]$CandidateManifestPath,
    [string]$DeviceAcceptanceDirectory,
    [string]$PerformanceBaselinePath,
    [switch]$SkipRepositoryValidation
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
$metadataScript = Join-Path $repoRoot "build\RepositoryMetadata.ps1"
$validationScript = Join-Path $repoRoot "build\validate-repository.ps1"
. $metadataScript
. (Join-Path $repoRoot 'build/PackageScratch.ps1')
. (Join-Path $PSScriptRoot 'ReleaseGate.ps1')
. (Join-Path $PSScriptRoot 'ApplicationManifest.ps1')
$buildScript = Join-Path $PSScriptRoot "build-msix.ps1"
$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$version = Get-RepositoryReleaseVersion -RepositoryRoot $repoRoot
$packageName = [string]$config.PackageName

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Assert-ArchiveContainsFile {
    param(
        [Parameter(Mandatory)]
        [string]$ArchivePath,
        [Parameter(Mandatory)]
        [string]$SourceFile
    )

    $source = Get-Item -LiteralPath $SourceFile
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entry = $archive.GetEntry($source.Name)
        if ($null -eq $entry) {
            throw "$([IO.Path]::GetFileName($ArchivePath)) 缺少 $($source.Name)"
        }
        if ($entry.Length -ne $source.Length) { throw 'Bundle contains a different package length than the accepted candidate.' }
        $stream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $entryHash = [Convert]::ToHexString($sha.ComputeHash($stream)) } finally { $sha.Dispose(); $stream.Dispose() }
        if ($entryHash -ine (Get-FileHash -LiteralPath $source.FullName).Hash) { throw 'Bundle contains different package bytes than the accepted candidate.' }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-BundleMetadata {
    param(
        [Parameter(Mandatory)]
        [string]$BundlePath,
        [Parameter(Mandatory)]
        [string]$ExpectedName,
        [Parameter(Mandatory)]
        [string]$ExpectedPublisher,
        [Parameter(Mandatory)]
        [string]$ExpectedVersion,
        [Parameter(Mandatory)]
        [hashtable]$ExpectedPackages
    )

    $archive = [IO.Compression.ZipFile]::OpenRead($BundlePath)
    try {
        $entry = $archive.GetEntry("AppxMetadata/AppxBundleManifest.xml")
        if ($null -eq $entry) {
            throw "$([IO.Path]::GetFileName($BundlePath)) 缺少 Bundle 清单。"
        }

        $reader = [IO.StreamReader]::new($entry.Open())
        try {
            [xml]$manifest = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        $namespaceManager = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
        $namespaceManager.AddNamespace("bundle", "http://schemas.microsoft.com/appx/2013/bundle")
        $namespaceManager.AddNamespace("b4", "http://schemas.microsoft.com/appx/2018/bundle")
        $identity = $manifest.SelectSingleNode("/bundle:Bundle/bundle:Identity", $namespaceManager)
        if ($null -eq $identity -or
            $identity.GetAttribute("Name") -cne $ExpectedName -or
            $identity.GetAttribute("Publisher") -cne $ExpectedPublisher -or
            $identity.GetAttribute("Version") -cne $ExpectedVersion) {
            throw "Bundle 身份必须与 msix-config.json 和根目录 VERSION 完全一致。"
        }

        $packages = @($manifest.SelectNodes(
            "/bundle:Bundle/bundle:Packages/bundle:Package[@Type='application']",
            $namespaceManager))
        if ($packages.Count -ne $ExpectedPackages.Count) {
            throw "Bundle 必须且只能包含 $($ExpectedPackages.Count) 个应用架构包。"
        }

        $minimumWindowsVersion = Get-FormatFactoryMinimumWindowsVersion
        foreach ($architecture in $ExpectedPackages.Keys) {
            $expectedFileName = [IO.Path]::GetFileName([string]$ExpectedPackages[$architecture])
            $package = @($packages | Where-Object {
                $_.GetAttribute("Architecture") -ceq $architecture
            })
            if ($package.Count -ne 1 -or
                $package[0].GetAttribute("FileName") -cne $expectedFileName -or
                $package[0].GetAttribute("Version") -cne $ExpectedVersion) {
                throw "Bundle 中的 $architecture 包名称、版本或架构不符合预期。"
            }
            $families = @($package[0].SelectNodes('b4:Dependencies/b4:TargetDeviceFamily', $namespaceManager))
            if ($families.Count -ne 1 -or $families[0].GetAttribute('Name') -cne 'Windows.Desktop' -or
                $families[0].GetAttribute('MinVersion') -cne $minimumWindowsVersion) {
                throw "Bundle 中的 $architecture 包必须声明 Windows.Desktop 最低版本 $minimumWindowsVersion。"
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-MsixUpload {
    param(
        [Parameter(Mandatory)]
        [string]$UploadPath,
        [Parameter(Mandatory)]
        [string]$BundlePath,
        $ExpectedBundleArtifact
    )

    $bundle = Get-Item -LiteralPath $BundlePath
    $archive = [IO.Compression.ZipFile]::OpenRead($UploadPath)
    try {
        $files = @($archive.Entries | Where-Object {
            -not $_.FullName.EndsWith("/", [StringComparison]::Ordinal)
        })
        if ($files.Count -ne 1 -or
            $files[0].FullName -cne $bundle.Name -or
            $files[0].Length -ne $bundle.Length) {
            throw ".msixupload 必须且只能在根目录包含本轮生成的 MSIX Bundle。"
        }
        $stream = $files[0].Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $hash = [Convert]::ToHexString($sha.ComputeHash($stream)) } finally { $sha.Dispose(); $stream.Dispose() }
        $expectedHash = if ($null -ne $ExpectedBundleArtifact) { $ExpectedBundleArtifact.sha256 } else { (Get-FileHash -LiteralPath $bundle.FullName).Hash }
        if ($hash -ine $expectedHash) { throw 'Upload Bundle bytes differ from the accepted candidate.' }
        if ($null -ne $ExpectedBundleArtifact -and ($files[0].FullName -cne $ExpectedBundleArtifact.fileName -or
            $files[0].Length -ne $ExpectedBundleArtifact.bytes)) { throw 'Upload Bundle identity differs from the accepted candidate.' }
    }
    finally {
        $archive.Dispose()
    }
}

if ([string]::IsNullOrWhiteSpace($version) -or [string]::IsNullOrWhiteSpace($packageName)) {
    throw "MSIX 配置缺少 PackageName，或仓库根目录 VERSION 无效。"
}
if ($config.PSObject.Properties.Name -contains "Version") {
    throw "msix-config.json 不应保存 Version；请只修改仓库根目录 VERSION。"
}
if (-not $SkipRepositoryValidation) {
    & $validationScript -Quiet
}

$OutputDirectory = Resolve-PackageOutputDirectory $OutputDirectory
$uploadPath = Join-Path $OutputDirectory ("{0}-{1}.msixupload" -f $packageName, $version)
# Never leave an older upload looking like a release approved by this invocation.
if (Test-Path -LiteralPath $uploadPath) { Remove-Item -LiteralPath $uploadPath -Force }
if (-not $CandidateOnly) {
    if ([string]::IsNullOrWhiteSpace($CandidateManifestPath) -or [string]::IsNullOrWhiteSpace($DeviceAcceptanceDirectory) -or [string]::IsNullOrWhiteSpace($PerformanceBaselinePath)) {
        throw 'Formal upload requires -CandidateManifestPath, -DeviceAcceptanceDirectory with Windows 10 and 11 evidence for both x64 and ARM64, and -PerformanceBaselinePath. Use -CandidateOnly to build review/install candidates; no .msixupload will be generated.'
    }
    if ($CertificatePath) { throw 'Sign during -CandidateOnly build, then accept those exact bytes. Promotion never re-signs or rebuilds accepted packages.' }
    [void](Assert-ReleaseRuntimeSupport -RepositoryRoot $repoRoot)
    $candidateManifestSha256 = (Get-FileHash -LiteralPath $CandidateManifestPath -Algorithm SHA256).Hash
    $candidate = Read-ReleaseCandidate -ManifestPath $CandidateManifestPath -RepositoryRoot $repoRoot -RequireCleanSource
    Assert-ReleaseCandidateUnchanged -ManifestPath $CandidateManifestPath -ManifestSha256 $candidateManifestSha256 -Candidate $candidate -RepositoryRoot $repoRoot -RequireCleanSource
    $deviceTargets = @(Get-ReleaseDeviceTargets)
    $deviceReportArtifacts = @{}
    foreach ($target in $deviceTargets) {
        $deviceReportPath = Join-Path $DeviceAcceptanceDirectory "device-$($target.id).json"
        $deviceReportArtifacts[$target.id] = Get-ReleaseArtifact -Path $deviceReportPath
        [void](Assert-DeviceAcceptanceReport -ReportPath $deviceReportPath -Candidate $candidate -Target $target.id)
        Assert-ReleaseReportUnchanged -ReportPath $deviceReportPath -Artifact $deviceReportArtifacts[$target.id]
    }
    foreach ($architecture in "x64", "arm64") {
        $package = @($candidate.packages | Where-Object architecture -CEQ $architecture)[0]
        & (Join-Path $PSScriptRoot 'validate-msix.ps1') -PackagePath $package.fullPath -Architecture $architecture
        Assert-ArchiveContainsFile -ArchivePath $candidate.bundle.fullPath -SourceFile $package.fullPath
    }
    $expectedPackages = @{}
    foreach ($package in $candidate.packages) { $expectedPackages[$package.architecture] = $package.fullPath }
    Assert-BundleMetadata -BundlePath $candidate.bundle.fullPath -ExpectedName $packageName -ExpectedPublisher ([string]$config.Publisher) -ExpectedVersion $version -ExpectedPackages $expectedPackages
    $reportPath = @(& (Join-Path $PSScriptRoot 'Run-ReleaseChecks.ps1') -CandidateManifestPath $CandidateManifestPath -PerformanceBaselinePath $PerformanceBaselinePath)[-1]
    $automatedReportArtifact = Get-ReleaseArtifact -Path $reportPath
    [void](Assert-AutomatedReleaseReport -ReportPath $reportPath -Candidate $candidate)
    Assert-ReleaseReportUnchanged -ReportPath $reportPath -Artifact $automatedReportArtifact
    # Recheck after lengthy tests so expiration, source or package changes cannot
    # bypass the final upload boundary.
    [void](Assert-ReleaseRuntimeSupport -RepositoryRoot $repoRoot)
    Assert-ReleaseCandidateUnchanged -ManifestPath $CandidateManifestPath -ManifestSha256 $candidateManifestSha256 -Candidate $candidate -RepositoryRoot $repoRoot -RequireCleanSource
    foreach ($target in $deviceTargets) {
        $deviceReportPath = Join-Path $DeviceAcceptanceDirectory "device-$($target.id).json"
        $deviceReportArtifact = $deviceReportArtifacts[$target.id]
        Assert-ReleaseReportUnchanged -ReportPath $deviceReportPath -Artifact $deviceReportArtifact
        [void](Assert-DeviceAcceptanceReport -ReportPath $deviceReportPath -Candidate $candidate -Target $target.id)
        Assert-ReleaseReportUnchanged -ReportPath $deviceReportPath -Artifact $deviceReportArtifact
    }
    Assert-ReleaseReportUnchanged -ReportPath $reportPath -Artifact $automatedReportArtifact
    [void](Assert-AutomatedReleaseReport -ReportPath $reportPath -Candidate $candidate)
    Assert-ReleaseReportUnchanged -ReportPath $reportPath -Artifact $automatedReportArtifact
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $storePackageScratch = New-PackageScratchDirectory
    $uploadDirectory = Join-Path $storePackageScratch.FullName 'release-upload'
    New-Item -ItemType Directory -Path $uploadDirectory | Out-Null
    Copy-Item -LiteralPath $candidate.bundle.fullPath -Destination $uploadDirectory
    [void](Assert-ReleaseArtifact -Artifact $candidate.bundle -Directory $uploadDirectory)
    $partialUpload = "$uploadPath.partial"
    try {
        if (Test-Path -LiteralPath $partialUpload) { Remove-Item -LiteralPath $partialUpload -Force }
        [IO.Compression.ZipFile]::CreateFromDirectory($uploadDirectory,$partialUpload,[IO.Compression.CompressionLevel]::Optimal,$false)
        Assert-MsixUpload -UploadPath $partialUpload -BundlePath $candidate.bundle.fullPath -ExpectedBundleArtifact $candidate.bundle
        Move-Item -LiteralPath $partialUpload -Destination $uploadPath
    } finally {
        if (Test-Path -LiteralPath $partialUpload) { Remove-Item -LiteralPath $partialUpload -Force }
    }
    $release = [ordered]@{ schemaVersion=2; kind='approvedRelease'; sourceCommit=$candidate.sourceCommit; sourceFingerprint=$candidate.sourceFingerprint; version=$version; approvedAt=[DateTimeOffset]::UtcNow.ToString('o'); runtimeSupportUntil=(Get-ToolchainLock -RepositoryRoot $repoRoot).dotnetSupportedUntil; packages=@($candidate.packages | Select-Object architecture,fileName,bytes,sha256); upload=(Get-ReleaseArtifact -Path $uploadPath); automatedReportSha256=$automatedReportArtifact.sha256; deviceReports=@(foreach ($target in $deviceTargets) { [ordered]@{ target=$target.id; sha256=$deviceReportArtifacts[$target.id].sha256 } }) }
    $release | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $OutputDirectory ("{0}-{1}.release.json" -f $packageName,$version)) -Encoding utf8
    Write-Host "正式 MSIX 上传物通过自动化及 Windows 10 / Windows 11 设备验收：$uploadPath"
    & (Join-Path $repoRoot 'build/Write-PackageCompletion.ps1') -RunId $storePackageScratch.Name -Version $version -OutputDirectory $OutputDirectory -Kind 'release'
    return
}
if ($CandidateManifestPath -or $DeviceAcceptanceDirectory -or $PerformanceBaselinePath) { throw '-CandidateOnly builds new packages; acceptance/report arguments belong to promotion.' }
$sourceBefore = Get-ReleaseSourceSnapshot -RepositoryRoot $repoRoot
$storePackageScratch = New-PackageScratchDirectory
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$builtPackages = @{}
foreach ($architecture in "x64", "arm64") {
    $expectedPackage = Join-Path $OutputDirectory ("{0}-{1}-{2}.msix" -f $packageName, $version, $architecture)
    if (Test-Path -LiteralPath $expectedPackage) {
        Remove-Item -LiteralPath $expectedPackage -Force
    }

    $arguments = @{
        Architecture = $architecture
        ConfigPath = $ConfigPath
        OutputDirectory = $OutputDirectory
        SkipRepositoryValidation = $true
        OfflineEngines = [bool]$OfflineEngines
    }
    if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
        $arguments.CertificatePath = $CertificatePath
    }
    if (-not [string]::IsNullOrWhiteSpace($CertificatePassword)) {
        $arguments.CertificatePassword = $CertificatePassword
    }
    if ($NoRestore) {
        $arguments.NoRestore = $true
    }

    & $buildScript @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$architecture MSIX 构建失败。"
    }
    if (-not (Test-Path -LiteralPath $expectedPackage -PathType Leaf)) {
        throw "$architecture MSIX 未生成预期文件：$expectedPackage"
    }
    $builtPackages[$architecture] = $expectedPackage
}

$makeAppx = Get-Item -LiteralPath (Get-LockedWindowsSdkTool -RepositoryRoot $repoRoot -Name makeappx.exe)

$bundleDirectory = Join-Path $storePackageScratch.FullName 'msix-bundle'
$bundlePath = Join-Path $OutputDirectory ("{0}-{1}.msixbundle" -f $packageName, $version)
$uploadPath = Join-Path $OutputDirectory ("{0}-{1}.msixupload" -f $packageName, $version)
New-Item -ItemType Directory -Path $bundleDirectory | Out-Null
foreach ($staleOutput in $bundlePath, $uploadPath) {
    if (Test-Path -LiteralPath $staleOutput) {
        Remove-Item -LiteralPath $staleOutput -Force
    }
}
Copy-Item -LiteralPath $builtPackages["x64"] -Destination $bundleDirectory
Copy-Item -LiteralPath $builtPackages["arm64"] -Destination $bundleDirectory

& $makeAppx.FullName bundle /o /bv $version /d $bundleDirectory /p $bundlePath
if ($LASTEXITCODE -ne 0) {
    throw "MSIX bundle 构建失败。"
}
Assert-ArchiveContainsFile -ArchivePath $bundlePath -SourceFile $builtPackages["x64"]
Assert-ArchiveContainsFile -ArchivePath $bundlePath -SourceFile $builtPackages["arm64"]
Assert-BundleMetadata -BundlePath $bundlePath -ExpectedName $packageName -ExpectedPublisher ([string]$config.Publisher) -ExpectedVersion $version -ExpectedPackages $builtPackages

if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
    $signTool = Get-Item -LiteralPath (Get-LockedWindowsSdkTool -RepositoryRoot $repoRoot -Name signtool.exe)

    $signArguments = @("sign", "/fd", "SHA256", "/a", "/f", $CertificatePath)
    if (-not [string]::IsNullOrWhiteSpace($CertificatePassword)) {
        $signArguments += @("/p", $CertificatePassword)
    }
    $signArguments += $bundlePath
    & $signTool.FullName @signArguments
    if ($LASTEXITCODE -ne 0) {
        throw "MSIX bundle 签名失败。"
    }
}

$sourceAfter = Get-ReleaseSourceSnapshot -RepositoryRoot $repoRoot
if ($sourceBefore.commit -cne $sourceAfter.commit -or $sourceBefore.fingerprint -cne $sourceAfter.fingerprint) { throw 'Source changed during candidate build; discard these packages and rebuild.' }
$candidate = [ordered]@{ schemaVersion=1; kind='candidate'; sourceCommit=$sourceAfter.commit; sourceFingerprint=$sourceAfter.fingerprint; dirtyWorktree=$sourceAfter.dirty; version=$version; builtAt=[DateTimeOffset]::UtcNow.ToString('o'); toolchainLockSha256=(Get-FileHash -LiteralPath (Join-Path $repoRoot 'build/toolchain.lock.json')).Hash; packages=@(foreach ($architecture in 'x64','arm64') { $artifact = Get-ReleaseArtifact -Path $builtPackages[$architecture]; $artifact | Add-Member -NotePropertyName architecture -NotePropertyValue $architecture; $artifact }); bundle=(Get-ReleaseArtifact -Path $bundlePath) }
$candidatePath = Join-Path $OutputDirectory ("{0}-{1}.candidate.json" -f $packageName,$version)
$candidate | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $candidatePath -Encoding utf8
Write-Host "MSIX bundle 已生成：$bundlePath"
Write-Host "仅生成候选 MSIX，未生成正式 .msixupload。提交及包 SHA256：$candidatePath"
if ([string]::IsNullOrWhiteSpace($CertificatePath)) {
    Write-Warning "当前 bundle 未做本地签名；使用真实 Partner Center 包身份时可提交 Microsoft Store，由商店在发布时重新签名。商店外安装仍需使用与 Publisher 匹配且目标设备信任的证书签名。"
}
& (Join-Path $repoRoot 'build/Write-PackageCompletion.ps1') -RunId $storePackageScratch.Name -Version $version -OutputDirectory $OutputDirectory -Kind 'candidate'
