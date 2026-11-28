[CmdletBinding()]
param(
    [ValidateSet("x64", "arm64", "all")]
    [string]$Architecture = "x64",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoRestore,
    [switch]$Test,
    [switch]$Msix,
    [switch]$CandidateOnly,
    [string]$CandidateManifestPath,
    [string]$DeviceAcceptanceDirectory,
    [string]$PerformanceBaselinePath,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "outputs\msix"),
    [string]$CertificatePath,
    [string]$CertificatePassword
)

$ErrorActionPreference = "Stop"
$mainProject = Join-Path $PSScriptRoot "src\FormatFactoryWinUI\FormatFactoryWinUI.csproj"
$shellBuildScript = Join-Path $PSScriptRoot "src\FormatFactoryShellExtension.Native\build.ps1"
$testProject = Join-Path $PSScriptRoot "tests\FormatFactoryWinUI.Tests\FormatFactoryWinUI.Tests.csproj"
$validationScript = Join-Path $PSScriptRoot "build\validate-repository.ps1"
. (Join-Path $PSScriptRoot 'build/Toolchain.ps1')

& $validationScript

if ($Msix -and $Configuration -ne "Release") {
    throw "MSIX 仅支持 Release 配置。"
}

$architectures = if ($Architecture -eq "all") { @("x64", "arm64") } else { @($Architecture) }
if (-not $Msix) {
    foreach ($targetArchitecture in $architectures) {
        $runtimeIdentifier = "win-$targetArchitecture"
        $arguments = @(
            "build", $mainProject,
            "-c", $Configuration,
            "-p:Platform=$targetArchitecture",
            "-p:RuntimeIdentifiers=$runtimeIdentifier",
            "-r", $runtimeIdentifier
        )
        if ($Configuration -eq "Release") {
            $arguments += "-p:RestoreLockedMode=true"
        }
        if ($NoRestore) {
            $arguments += "--no-restore"
        }

        Invoke-LockedDotnet -RepositoryRoot $PSScriptRoot -Arguments $arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$([IO.Path]::GetFileName($mainProject)) 的 $targetArchitecture 构建失败。"
        }
        & $shellBuildScript -Architecture $targetArchitecture -Configuration $Configuration -RunTests:$Test
    }
}

if ($Test) {
    $testRuntime = 'win-' + [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
    & (Join-Path $PSScriptRoot 'build/build-conversion-worker.ps1') -RuntimeIdentifier $testRuntime -Profile $Configuration.ToLowerInvariant() -Test
    $testArguments = @("test", $testProject, "-c", $Configuration)
    if ($Configuration -eq "Release") {
        $testArguments += "-p:RestoreLockedMode=true"
    }
    if ($NoRestore) {
        $testArguments += "--no-restore"
    }

    Invoke-LockedDotnet -RepositoryRoot $PSScriptRoot -Arguments $testArguments
    if ($LASTEXITCODE -ne 0) {
        throw "自动化测试失败。"
    }
}

if ($Msix) {
    if ($Architecture -ne 'all' -and -not $CandidateOnly) { throw 'Single-architecture MSIX is a candidate. Pass -CandidateOnly; formal Store promotion requires -Architecture all and device evidence.' }
    $packageScript = if ($Architecture -eq "all") {
        Join-Path $PSScriptRoot "packaging\build-store-msix.ps1"
    }
    else {
        Join-Path $PSScriptRoot "packaging\build-msix.ps1"
    }
    $packageArguments = @{
        OutputDirectory = $OutputDirectory
        SkipRepositoryValidation = $true
    }
    if ($Architecture -ne "all") {
        $packageArguments.Architecture = $Architecture
    }
    else {
        $packageArguments.CandidateOnly = [bool]$CandidateOnly
        $packageArguments.CandidateManifestPath = $CandidateManifestPath
        $packageArguments.DeviceAcceptanceDirectory = $DeviceAcceptanceDirectory
        $packageArguments.PerformanceBaselinePath = $PerformanceBaselinePath
    }
    if ($NoRestore) {
        $packageArguments.NoRestore = $true
    }
    if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
        $packageArguments.CertificatePath = $CertificatePath
    }
    if (-not [string]::IsNullOrWhiteSpace($CertificatePassword)) {
        $packageArguments.CertificatePassword = $CertificatePassword
    }

    & $packageScript @packageArguments
}

Write-Host "仓库构建完成：$Configuration / $Architecture / C++ Shell"
