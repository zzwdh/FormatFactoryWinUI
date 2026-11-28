#requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateSet('win-x64','win-arm64')][string]$RuntimeIdentifier = 'win-x64',
    [ValidateSet('debug','release')][string]$Profile = 'release',
    [string]$TargetDirectory = (Join-Path $PSScriptRoot '../work/cargo-target'),
    [switch]$Test,
    [switch]$ShellLibrary
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Toolchain.ps1')
$lock = Get-ToolchainLock -RepositoryRoot $repository
$arch = $RuntimeIdentifier.Substring(4)
$triples = @{x64='x86_64-pc-windows-msvc'; arm64='aarch64-pc-windows-msvc'}
$triple = $triples[$arch]
$hostArch = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
if (!$triples.ContainsKey($hostArch)) { throw "An x64 or ARM64 PowerShell host is required." }
$toolchain = "$($lock.rustVersion)-$($triples[$hostArch])"
$rustup = Get-Command rustup.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if (!$rustup -and $env:CARGO_HOME) {
    $rustup = Get-Command (Join-Path $env:CARGO_HOME 'bin/rustup.exe') -CommandType Application -ErrorAction SilentlyContinue
}
$portableRoot = Join-Path $repository 'work/rust-backend-tools'
$usePortable = !$rustup -and !$env:CARGO_HOME -and !$env:RUSTUP_HOME
if ($usePortable) {
    $rustup = Get-Command (Join-Path $portableRoot 'cargo/bin/rustup.exe') -CommandType Application -ErrorAction SilentlyContinue
}
if (!$rustup) { throw "Install rustup and Rust $toolchain with the $triple target." }

$vcRoot = Resolve-LockedVcTools -RepositoryRoot $repository -Lock $lock
$sdkLib = Join-Path ${env:ProgramFiles(x86)} "Windows Kits/10/Lib/$($lock.windowsSdkVersion)"
$vcHost = "Host$hostArch"
$hostBin = Join-Path $vcRoot "bin/$vcHost/$hostArch"
$targetLinker = Assert-LockedToolVersion -Path (Join-Path $vcRoot "bin/$vcHost/$arch/link.exe") -ExpectedVersion $lock.linkerFileVersion
function Get-LibraryPaths([string]$architecture) {
    Join-Path $vcRoot "lib/$architecture"
    Join-Path $sdkLib "um/$architecture"
    Join-Path $sdkLib "ucrt/$architecture"
}

# Cargo builds host-side procedural macros even when cross-compiling. Keep the
# host libraries on LIB and pass the target paths only to target rustc invocations.
$targetLibs = Get-LibraryPaths $arch
foreach ($lib in $targetLibs) {
    if (!(Test-Path -LiteralPath $lib -PathType Container)) { throw "Locked native library directory missing: $lib" }
}
$linkerVariable = 'CARGO_TARGET_' + $triple.Replace('-','_').ToUpperInvariant() + '_LINKER'
$previousLinker = [Environment]::GetEnvironmentVariable($linkerVariable)
$previousFlags = $env:CARGO_ENCODED_RUSTFLAGS
$previousLib = $env:LIB
$previousPath = $env:PATH
try {
    if ($usePortable) {
        $env:CARGO_HOME = Join-Path $portableRoot 'cargo'
        $env:RUSTUP_HOME = Join-Path $portableRoot 'rustup'
    }
    [Environment]::SetEnvironmentVariable($linkerVariable,$targetLinker)
    $targetFlags = @('-Ctarget-feature=+crt-static') + @($targetLibs | ForEach-Object { "-Lnative=$_" })
    $env:CARGO_ENCODED_RUSTFLAGS = $targetFlags -join [char]31
    $env:LIB = (Get-LibraryPaths $hostArch) -join ';'
    $env:PATH = "$hostBin;$previousPath"
    $action = if ($Test) { 'test' } else { 'build' }
    $manifest = if ($ShellLibrary) { Join-Path $repository 'src/FormatFactoryShellExtension.Native/rust/Cargo.toml' }
        else { Join-Path $repository 'src/FormatFactory.ConversionWorker/Cargo.toml' }
    $notice = if ($ShellLibrary) { 'FormatFactoryShellExtension.Native.licenses.zip' } else { 'FormatFactory.ConversionWorker.licenses.zip' }
    $arguments = @('run',$toolchain,'cargo',$action,'--locked','--target',$triple,'--target-dir',
        [IO.Path]::GetFullPath($TargetDirectory),'--manifest-path',$manifest)
    if ($Profile -eq 'release') { $arguments += '--release' }
    & $rustup.Source @arguments
    if ($LASTEXITCODE -ne 0) { throw "Locked Rust worker $action failed ($RuntimeIdentifier). Verify that rustup has $toolchain and target $triple installed." }
    if (!$Test) {
        $metadata = & $rustup.Source run $toolchain cargo metadata --offline --locked --format-version 1 --filter-platform $triple --manifest-path $manifest
        if ($LASTEXITCODE -ne 0) { throw 'Cannot collect the licenses of the locked worker dependencies.' }
        $sysroot = & $rustup.Source run $toolchain rustc --print sysroot
        if ($LASTEXITCODE -ne 0) { throw 'Cannot locate the Rust standard library copyright notices.' }
        & (Join-Path $PSScriptRoot 'Write-WorkerNotices.ps1') -Sysroot $sysroot -Metadata ($metadata | ConvertFrom-Json) `
            -Destination (Join-Path $TargetDirectory "$triple/$Profile/$notice")
    }
}
finally {
    if ($usePortable) { $env:CARGO_HOME = $null; $env:RUSTUP_HOME = $null }
    if ([string]::IsNullOrEmpty($previousLinker)) { Remove-Item -LiteralPath "Env:$linkerVariable" -ErrorAction SilentlyContinue }
    else { [Environment]::SetEnvironmentVariable($linkerVariable,$previousLinker) }
    $env:CARGO_ENCODED_RUSTFLAGS = $previousFlags
    $env:LIB = $previousLib
    $env:PATH = $previousPath
}
