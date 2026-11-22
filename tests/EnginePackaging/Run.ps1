[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $repo 'packaging/EnginePackaging.ps1')
$taskRoot = Join-Path $repo ('work/engine-packaging-tests-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($taskRoot)
$script:passed = 0
function Test-Case([string]$Name, [scriptblock]$Action) {
    & $Action
    $script:passed++
    Write-Output "PASS $Name"
}
function Reject([scriptblock]$Action, [string]$MessagePattern) {
    $rejected = $false
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notmatch $MessagePattern) { throw "Unexpected rejection: $($_.Exception.Message)" }
        $rejected = $true
    }
    if (-not $rejected) { throw "Dependency validation accepted invalid payload; expected: $MessagePattern" }
}
function New-PeFixture {
    param([string]$Path, [string[]]$Imports = @(), [string[]]$DelayImports = @(), [int]$Machine = 0x8664)
    # Deliberately synthetic PE files, never launched or used as release evidence.
    # PEReader handles the real headers; import descriptors and names occupy one
    # file-backed section, including the terminating all-zero descriptors.
    $bytes = [byte[]]::new(2048)
    function Put16([int]$Offset, [uint16]$Value) { [BitConverter]::GetBytes($Value).CopyTo($bytes, $Offset) }
    function Put32([int]$Offset, [uint32]$Value) { [BitConverter]::GetBytes($Value).CopyTo($bytes, $Offset) }
    function Put64([int]$Offset, [uint64]$Value) { [BitConverter]::GetBytes($Value).CopyTo($bytes, $Offset) }
    Put16 0 0x5a4d
    Put32 0x3c 0x80
    Put32 0x80 0x4550
    Put16 0x84 $Machine
    Put16 0x86 1
    Put16 0x94 240
    Put16 0x96 0x22
    $optional = 0x98
    Put16 $optional 0x20b
    Put64 ($optional + 24) 0x140000000
    Put32 ($optional + 32) 0x1000
    Put32 ($optional + 36) 0x200
    Put32 ($optional + 56) 0x2000
    Put32 ($optional + 60) 0x200
    Put16 ($optional + 68) 3
    Put32 ($optional + 108) 16
    [Text.Encoding]::ASCII.GetBytes('.rdata').CopyTo($bytes, 0x188)
    Put32 0x190 0x600
    Put32 0x194 0x1000
    Put32 0x198 0x600
    Put32 0x19c 0x200
    Put32 0x1ac 0x40000040
    $nameOffset = 0x400
    foreach ($kind in @('normal', 'delay')) {
        $names = @(if ($kind -eq 'normal') { $Imports } else { $DelayImports })
        if ($names.Count -eq 0) { continue }
        $table = if ($kind -eq 'normal') { 0x200 } else { 0x300 }
        $directoryOffset = if ($kind -eq 'normal') { $optional + 120 } else { $optional + 216 }
        $descriptorSize = if ($kind -eq 'normal') { 20 } else { 32 }
        if (($names.Count + 1) * $descriptorSize -gt 0x100) { throw 'Too many synthetic fixture imports.' }
        Put32 $directoryOffset ($table + 0xe00)
        Put32 ($directoryOffset + 4) (($names.Count + 1) * $descriptorSize)
        for ($index = 0; $index -lt $names.Count; $index++) {
            $descriptor = $table + $index * $descriptorSize
            $nameBytes = [Text.Encoding]::ASCII.GetBytes($names[$index] + [char]0)
            if ($nameOffset + $nameBytes.Length -gt $bytes.Length) { throw 'Synthetic fixture names exceed the PE section.' }
            $nameBytes.CopyTo($bytes, $nameOffset)
            if ($kind -eq 'normal') { Put32 ($descriptor + 12) ($nameOffset + 0xe00) }
            else { Put32 $descriptor 1; Put32 ($descriptor + 4) ($nameOffset + 0xe00) }
            $nameOffset += $nameBytes.Length
        }
    }
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllBytes($Path, $bytes)
}
function New-CompletePayload([string]$Name) {
    $directory = Join-Path $taskRoot $Name
    New-PeFixture -Path (Join-Path $directory 'mutool.exe') -Imports @('MSVCP140.dll', 'KERNEL32.dll', 'msvcrt.dll')
    New-PeFixture -Path (Join-Path $directory 'python.exe') -Imports @('VCRUNTIME140.dll')
    New-PeFixture -Path (Join-Path $directory 'msvcp140.dll') -Imports @('VCRUNTIME140_1.dll')
    New-PeFixture -Path (Join-Path $directory 'vcruntime140.dll')
    New-PeFixture -Path (Join-Path $directory 'vcruntime140_1.dll')
    New-PeFixture -Path (Join-Path $directory 'Lib/site-packages/PIL/_imaging.pyd') -Imports @('VCRUNTIME140.dll')
    New-PeFixture -Path (Join-Path $directory 'app/lib/bin/native.dll') -DelayImports @('MSVCP140.dll')
    return $directory
}
function New-FfmpegPayload {
    param([string]$Name, [int]$Machine = 0x8664)
    $directory = Join-Path $taskRoot $Name
    New-PeFixture -Path (Join-Path $directory 'ffmpeg.exe') -Machine $Machine -Imports @('avcodec-62.dll', 'avformat-62.dll', 'KERNEL32.dll', 'msvcrt.dll')
    New-PeFixture -Path (Join-Path $directory 'ffprobe.exe') -Machine $Machine -Imports @('avformat-62.dll', 'api-ms-win-crt-runtime-l1-1-0.dll', 'LibInputHost.dll', 'msvcp_win.dll')
    New-PeFixture -Path (Join-Path $directory 'avcodec-62.dll') -Machine $Machine -Imports @('avutil-60.dll', 'swresample-6.dll')
    New-PeFixture -Path (Join-Path $directory 'avformat-62.dll') -Machine $Machine -Imports @('avcodec-62.dll', 'avutil-60.dll')
    New-PeFixture -Path (Join-Path $directory 'avutil-60.dll') -Machine $Machine -Imports @('libgcc_s_seh-1.dll') -DelayImports @('libwinpthread-1.dll')
    New-PeFixture -Path (Join-Path $directory 'swresample-6.dll') -Machine $Machine -Imports @('avutil-60.dll')
    New-PeFixture -Path (Join-Path $directory 'libgcc_s_seh-1.dll') -Machine $Machine -Imports @('KERNEL32.dll', 'ucrtbase.dll')
    New-PeFixture -Path (Join-Path $directory 'libwinpthread-1.dll') -Machine $Machine -Imports @('KERNEL32.dll', 'ntdll.dll')
    return $directory
}

Test-Case 'Normal and delay imports are both read from a PE image' {
    $path = Join-Path $taskRoot 'parser.exe'
    New-PeFixture -Path $path -Imports @('MSVCP140.dll') -DelayImports @('VCRUNTIME140_1.dll')
    $result = Get-EnginePeImports -Path $path
    if ($result.Machine -ne 0x8664 -or $result.Magic -ne 0x20b -or $result.Imports.Count -ne 2 -or 'MSVCP140.dll' -notin $result.Imports -or 'VCRUNTIME140_1.dll' -notin $result.Imports) { throw 'Normal/delay import parsing returned incorrect metadata.' }
}
Test-Case 'All native payload files and app-local transitive runtimes validate' {
    Assert-CajPeDependencies -Directory (New-CompletePayload 'complete')
}
Test-Case 'Missing MSVCP140 is rejected even when host system PATH can supply it' {
    $directory = New-CompletePayload 'missing-msvcp'
    $hostDirectory = Join-Path $taskRoot 'host-runtime'
    New-PeFixture -Path (Join-Path $hostDirectory 'msvcp140.dll')
    Remove-Item -LiteralPath (Join-Path $directory 'msvcp140.dll')
    $previousPath = $env:PATH
    try {
        $env:PATH = $hostDirectory + [IO.Path]::PathSeparator + $previousPath
        Reject { Assert-CajPeDependencies -Directory $directory } 'Missing app-local CAJ dependency.*MSVCP140.dll'
    } finally { $env:PATH = $previousPath }
}
Test-Case 'Missing transitive runtime imported only by another runtime DLL is rejected' {
    $directory = New-CompletePayload 'missing-transitive'
    Remove-Item -LiteralPath (Join-Path $directory 'vcruntime140_1.dll')
    Reject { Assert-CajPeDependencies -Directory $directory } 'Missing app-local CAJ dependency.*VCRUNTIME140_1.dll.*msvcp140.dll'
}
Test-Case 'Delay-loaded runtime dependency cannot be omitted' {
    $directory = Join-Path $taskRoot 'missing-delay'
    New-PeFixture -Path (Join-Path $directory 'mutool.exe') -DelayImports @('VCOMP140.dll')
    Reject { Assert-CajPeDependencies -Directory $directory } 'Missing app-local CAJ dependency.*VCOMP140.dll'
}
Test-Case 'Numbered and debug VC runtime families all require app-local files' {
    foreach ($name in @('MSVCP140_2.dll', 'MSVCP140D.dll', 'VCRUNTIME140_1D.dll', 'CONCRT140.dll', 'VCOMP140D.dll', 'MSVCR120.dll', 'MSVCR120D.dll')) {
        $directory = Join-Path $taskRoot ('family-' + $name)
        New-PeFixture -Path (Join-Path $directory 'mutool.exe') -Imports @($name)
        Reject { Assert-CajPeDependencies -Directory $directory } ('Missing app-local CAJ dependency.*' + [regex]::Escape($name))
    }
}
Test-Case 'A runtime hidden in an unrelated subdirectory cannot satisfy an import' {
    $directory = New-CompletePayload 'wrong-directory'
    $unrelated = Join-Path $directory 'unrelated'
    [void][IO.Directory]::CreateDirectory($unrelated)
    Move-Item -LiteralPath (Join-Path $directory 'msvcp140.dll') -Destination $unrelated
    Reject { Assert-CajPeDependencies -Directory $directory } 'Missing app-local CAJ dependency.*MSVCP140.dll'
}
Test-Case 'A native DLL can resolve its runtime from its own directory' {
    $directory = Join-Path $taskRoot 'dll-directory'
    New-PeFixture -Path (Join-Path $directory 'python.exe')
    New-PeFixture -Path (Join-Path $directory 'app/lib/bin/native.dll') -Imports @('MSVCP140.dll')
    New-PeFixture -Path (Join-Path $directory 'app/lib/bin/msvcp140.dll')
    Assert-CajPeDependencies -Directory $directory
}
Test-Case 'A nested executable must resolve its runtime beside that executable' {
    $directory = Join-Path $taskRoot 'exe-directory'
    New-PeFixture -Path (Join-Path $directory 'bin/helper.exe') -Imports @('MSVCP140.dll')
    New-PeFixture -Path (Join-Path $directory 'msvcp140.dll')
    Reject { Assert-CajPeDependencies -Directory $directory } 'Missing app-local CAJ dependency.*MSVCP140.dll'
}
Test-Case 'Wrong architecture in an undeclared Python extension or runtime is rejected' {
    foreach ($relative in @('Lib/site-packages/PIL/_imaging.pyd', 'vcruntime140_1.dll')) {
        $directory = New-CompletePayload ('wrong-architecture-' + [IO.Path]::GetFileName($relative))
        New-PeFixture -Path (Join-Path $directory $relative) -Machine 0xaa64
        Reject { Assert-CajPeDependencies -Directory $directory } 'Wrong x64 CAJ PE architecture'
    }
}
Test-Case 'Malformed import RVAs and unterminated tables fail closed' {
    $path = Join-Path $taskRoot 'malformed.exe'
    New-PeFixture -Path $path -Imports @('MSVCP140.dll')
    $bytes = [IO.File]::ReadAllBytes($path)
    [BitConverter]::GetBytes([uint32]0x1800).CopyTo($bytes, 0x20c)
    [IO.File]::WriteAllBytes($path, $bytes)
    Reject { Get-EnginePeImports -Path $path } 'Import RVA is outside'
    New-PeFixture -Path $path -Imports @('MSVCP140.dll')
    $bytes = [IO.File]::ReadAllBytes($path)
    [BitConverter]::GetBytes([uint32]20).CopyTo($bytes, 0x114)
    [IO.File]::WriteAllBytes($path, $bytes)
    Reject { Get-EnginePeImports -Path $path } 'Unterminated ImportTableDirectory'
}
Test-Case 'Prepared receipts and MSIX checks repeat the CAJ dependency gate' {
    $bundleRoot = Join-Path $taskRoot 'receipt'
    $directory = Join-Path $bundleRoot 'caj2pdf'
    New-PeFixture -Path (Join-Path $directory 'mutool.exe') -Imports @('MSVCP140.dll')
    New-PeFixture -Path (Join-Path $directory 'msvcp140.dll')
    $engine = [pscustomobject]@{id='caj2pdf'; runtimeArchitecture='x64'; requiredFiles=@('mutool.exe'); peFiles=@('mutool.exe')}
    Write-EngineReceipt -EngineRoot $bundleRoot -Architecture x64 -Fingerprint synthetic -Engines @($engine)
    [void](Assert-EngineReceipt -EngineRoot $bundleRoot -Architecture x64)
    $package = Join-Path $taskRoot 'synthetic.msix'
    $packageStage = Join-Path $taskRoot 'package-stage'
    [void][IO.Directory]::CreateDirectory($packageStage)
    Copy-Item -LiteralPath $bundleRoot -Destination (Join-Path $packageStage 'Engines') -Recurse
    [IO.Compression.ZipFile]::CreateFromDirectory($packageStage, $package)
    Assert-MsixEnginePayload -PackagePath $package -EngineRoot $bundleRoot -Architecture x64
    Remove-Item -LiteralPath (Join-Path $directory 'msvcp140.dll')
    # Recompute a perfectly matching receipt to demonstrate that hashes and the
    # existing peFiles list cannot substitute for dependency closure validation.
    Write-EngineReceipt -EngineRoot $bundleRoot -Architecture x64 -Fingerprint synthetic -Engines @($engine)
    Reject { Assert-EngineReceipt -EngineRoot $bundleRoot -Architecture x64 } 'Missing app-local CAJ dependency.*MSVCP140.dll'
    Reject { Assert-MsixEnginePayload -PackagePath $package -EngineRoot $bundleRoot -Architecture x64 } 'Missing app-local CAJ dependency.*MSVCP140.dll'
}
Test-Case 'Shared FFmpeg closure validates for x64 and ARM64 without rejecting Windows imports' {
    foreach ($architecture in @('x64', 'arm64')) {
        $machine = @{x64=0x8664; arm64=0xaa64}[$architecture]
        Assert-FfmpegPeDependencies -Directory (New-FfmpegPayload -Name ('ffmpeg-complete-' + $architecture) -Machine $machine) -RuntimeArchitecture $architecture
    }
}
Test-Case 'An extracted FFmpeg bin directory validates from its archive root' {
    $directory = New-FfmpegPayload 'ffmpeg-extracted/bin'
    Assert-FfmpegPeDependencies -Directory ([IO.Path]::GetDirectoryName($directory)) -RuntimeArchitecture x64
}
Test-Case 'Windows CRT DLL imports do not require copied operating-system files' {
    $directory = Join-Path $taskRoot 'ffmpeg-windows-crt'
    New-PeFixture -Path (Join-Path $directory 'ffmpeg.exe') -Imports @('msvcrt.dll', 'msvcp_win.dll', 'ucrtbase.dll', 'api-ms-win-crt-runtime-l1-1-0.dll', 'KERNEL32.dll')
    Assert-FfmpegPeDependencies -Directory $directory -RuntimeArchitecture x64
}
Test-Case 'Shared FFmpeg rejects missing direct and transitive libraries' {
    foreach ($name in @('avformat-62.dll', 'swresample-6.dll')) {
        $directory = New-FfmpegPayload ('ffmpeg-missing-' + $name)
        Remove-Item -LiteralPath (Join-Path $directory $name)
        Reject { Assert-FfmpegPeDependencies -Directory $directory -RuntimeArchitecture x64 } ('Missing app-local FFmpeg dependency.*' + [regex]::Escape($name))
    }
}
Test-Case 'A missing shared FFmpeg MinGW DLL cannot be satisfied from host PATH' {
    $directory = New-FfmpegPayload 'ffmpeg-host-path'
    $hostDirectory = Join-Path $taskRoot 'host-mingw'
    New-PeFixture -Path (Join-Path $hostDirectory 'libgcc_s_seh-1.dll')
    Remove-Item -LiteralPath (Join-Path $directory 'libgcc_s_seh-1.dll')
    $previousPath = $env:PATH
    try {
        $env:PATH = $hostDirectory + [IO.Path]::PathSeparator + $previousPath
        Reject { Assert-FfmpegPeDependencies -Directory $directory -RuntimeArchitecture x64 } 'Missing app-local FFmpeg dependency.*libgcc_s_seh-1.dll.*avutil-60.dll'
    } finally { $env:PATH = $previousPath }
}
Test-Case 'A missing shared FFmpeg delay-loaded pthread DLL is rejected' {
    $directory = New-FfmpegPayload 'ffmpeg-missing-pthread'
    Remove-Item -LiteralPath (Join-Path $directory 'libwinpthread-1.dll')
    Reject { Assert-FfmpegPeDependencies -Directory $directory -RuntimeArchitecture x64 } 'Missing app-local FFmpeg dependency.*libwinpthread-1.dll.*avutil-60.dll'
}
Test-Case 'FFmpeg private library names and MinGW runtime families require app-local files' {
    foreach ($name in @('libavcodec-62.dll', 'avdevice-62.dll', 'avfilter-11.dll', 'avresample-4.dll', 'swscale-9.dll', 'postproc-59.dll', 'libswresample-6.dll', 'libgcc_s_sjlj-1.dll', 'libgcc_s_dw2-1.dll', 'libgcc_s_aarch64-1.dll', 'libstdc++-6.dll', 'libssp-0.dll', 'libgomp-1.dll', 'libquadmath-0.dll', 'libgfortran-5.dll', 'libatomic-1.dll', 'VCRUNTIME140.dll')) {
        $directory = Join-Path $taskRoot ('ffmpeg-family-' + $name)
        New-PeFixture -Path (Join-Path $directory 'ffmpeg.exe') -Imports @($name)
        Reject { Assert-FfmpegPeDependencies -Directory $directory -RuntimeArchitecture x64 } ('Missing app-local FFmpeg dependency.*' + [regex]::Escape($name))
    }
}
Test-Case 'FFmpeg validates architecture of both executables and all DLLs including undeclared ones' {
    foreach ($relative in @('ffmpeg.exe', 'ffprobe.exe', 'avutil-60.dll', 'libwinpthread-1.dll', 'unused-helper.dll')) {
        $directory = New-FfmpegPayload ('ffmpeg-wrong-architecture-' + $relative)
        New-PeFixture -Path (Join-Path $directory $relative) -Machine 0xaa64
        Reject { Assert-FfmpegPeDependencies -Directory $directory -RuntimeArchitecture x64 } 'Wrong x64 FFmpeg PE architecture'
    }
}
Test-Case 'FFmpeg cannot resolve a shared library hidden in an unrelated directory' {
    $directory = New-FfmpegPayload 'ffmpeg-wrong-directory'
    $unrelated = Join-Path $directory 'unrelated'
    [void][IO.Directory]::CreateDirectory($unrelated)
    Move-Item -LiteralPath (Join-Path $directory 'avformat-62.dll') -Destination $unrelated
    Reject { Assert-FfmpegPeDependencies -Directory $directory -RuntimeArchitecture x64 } 'Missing app-local FFmpeg dependency.*avformat-62.dll.*ffmpeg.exe'
}
Test-Case 'Packaged FFmpeg libraries outside the standard families also need a resolvable local directory' {
    $directory = New-FfmpegPayload 'ffmpeg-other-local-module'
    New-PeFixture -Path (Join-Path $directory 'avcodec-62.dll') -Imports @('other-codec.dll')
    New-PeFixture -Path (Join-Path $directory 'unrelated/other-codec.dll')
    Reject { Assert-FfmpegPeDependencies -Directory $directory -RuntimeArchitecture x64 } 'Missing app-local FFmpeg dependency.*other-codec.dll.*avcodec-62.dll'
}
Test-Case 'Prepared FFmpeg and compatibility receipts repeat the shared dependency gate in MSIX checks' {
    foreach ($engineId in @('ffmpeg', 'ffmpeg-compatibility')) {
        $bundleRoot = Join-Path $taskRoot ('receipt-' + $engineId)
        $directory = New-FfmpegPayload ('receipt-' + $engineId + '/' + $engineId)
        # Keep peFiles deliberately short: the import closure must still inspect
        # every shared DLL instead of trusting this legacy executable-only list.
        $engine = [pscustomobject]@{id=$engineId; runtimeArchitecture='x64'; requiredFiles=@('ffmpeg.exe','ffprobe.exe'); peFiles=@('ffmpeg.exe','ffprobe.exe')}
        Write-EngineReceipt -EngineRoot $bundleRoot -Architecture x64 -Fingerprint synthetic -Engines @($engine)
        [void](Assert-EngineReceipt -EngineRoot $bundleRoot -Architecture x64)
        $package = Join-Path $taskRoot ($engineId + '-synthetic.msix')
        $packageStage = Join-Path $taskRoot ($engineId + '-package-stage')
        [void][IO.Directory]::CreateDirectory($packageStage)
        Copy-Item -LiteralPath $bundleRoot -Destination (Join-Path $packageStage 'Engines') -Recurse
        [IO.Compression.ZipFile]::CreateFromDirectory($packageStage, $package)
        Assert-MsixEnginePayload -PackagePath $package -EngineRoot $bundleRoot -Architecture x64
        Remove-Item -LiteralPath (Join-Path $directory 'swresample-6.dll')
        Write-EngineReceipt -EngineRoot $bundleRoot -Architecture x64 -Fingerprint synthetic -Engines @($engine)
        Reject { Assert-EngineReceipt -EngineRoot $bundleRoot -Architecture x64 } 'Missing app-local FFmpeg dependency.*swresample-6.dll.*avcodec-62.dll'
        Reject { Assert-MsixEnginePayload -PackagePath $package -EngineRoot $bundleRoot -Architecture x64 } 'Missing app-local FFmpeg dependency.*swresample-6.dll.*avcodec-62.dll'
    }
}
Write-Output "Engine packaging regression tests: $script:passed passed. Synthetic fixtures: $taskRoot. No payloads launched or packages installed."
