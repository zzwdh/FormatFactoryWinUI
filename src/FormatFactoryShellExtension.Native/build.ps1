[CmdletBinding()]
param(
    [ValidateSet('x64','arm64','all')][string]$Architecture = 'x64',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$VCToolsRoot,
    [string]$WindowsSdkRoot = (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'),
    [string]$OutputDirectory,
    [switch]$RunTests
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $repoRoot 'build/Toolchain.ps1')
$toolchain = Get-ToolchainLock -RepositoryRoot $repoRoot
$VCToolsRoot = Resolve-LockedVcTools -RepositoryRoot $repoRoot -Lock $toolchain -VCToolsRoot $VCToolsRoot
$sdkVersion = $toolchain.windowsSdkVersion
if (!(Test-Path -LiteralPath (Join-Path $WindowsSdkRoot "Include/$sdkVersion/um/windows.h"))) { throw "Locked Windows SDK $sdkVersion headers and libraries are required." }
$lock = Assert-VendoredMiniz -VendorDirectory (Join-Path $PSScriptRoot 'vendor/miniz')
$formats = Get-Content -LiteralPath (Join-Path $repoRoot 'src/FormatFactory.Core/Models/formats.json') -Raw | ConvertFrom-Json
$kinds = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($format in $formats) { if ($format.capabilities -contains 'Import') { foreach ($extension in $format.extensions) { if ($extension) { $kinds[$extension.ToLowerInvariant()] = $format.contentKind } } } }
$originalPath=$env:PATH; $originalInclude=$env:INCLUDE; $originalLib=$env:LIB
try {
    foreach ($arch in $(if ($Architecture -eq 'all') { @('x64','arm64') } else { @($Architecture) })) {
        $output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory); } else { Join-Path $repoRoot "work/native-shell/$Configuration/$arch" }
        if ($Architecture -eq 'all' -and $OutputDirectory) { $output = Join-Path $output $arch }
        New-Item -ItemType Directory -Force -Path $output | Out-Null
        $compilerDirectory = Join-Path $VCToolsRoot "bin/Hostx64/$arch"
        $compiler = Join-Path $compilerDirectory 'cl.exe'; $linker = Join-Path $compilerDirectory 'link.exe'
        [void](Assert-LockedToolVersion -Path $compiler -ExpectedVersion $toolchain.compilerFileVersion)
        [void](Assert-LockedToolVersion -Path $linker -ExpectedVersion $toolchain.linkerFileVersion)
        $env:PATH = $compilerDirectory + ';' + (Join-Path $VCToolsRoot 'bin/Hostx64/x64') + ';' + $originalPath
        $env:INCLUDE = (@((Join-Path $VCToolsRoot 'include')) + @('ucrt','shared','um','winrt') | ForEach-Object { if ($_ -in @('ucrt','shared','um','winrt')) { Join-Path $WindowsSdkRoot "Include/$sdkVersion/$_" } else { $_ } }) -join ';'
        $env:LIB = @((Join-Path $VCToolsRoot "lib/$arch"),(Join-Path $WindowsSdkRoot "Lib/$sdkVersion/um/$arch"),(Join-Path $WindowsSdkRoot "Lib/$sdkVersion/ucrt/$arch")) -join ';'
        $rustProfile = if ($Configuration -eq 'Release') { 'release' } else { 'debug' }
        $cargoTarget = Join-Path $repoRoot 'work/cargo-shell'
        & (Join-Path $repoRoot 'build/build-conversion-worker.ps1') -ShellLibrary -RuntimeIdentifier "win-$arch" -Profile $rustProfile -TargetDirectory $cargoTarget
        $triple = if ($arch -eq 'x64') { 'x86_64-pc-windows-msvc' } else { 'aarch64-pc-windows-msvc' }
        $rustDirectory = Join-Path $cargoTarget "$triple/$rustProfile"
        $rustLibrary = Join-Path $rustDirectory 'format_factory_shell_epub.lib'
        Copy-Item -LiteralPath (Join-Path $rustDirectory 'FormatFactoryShellExtension.Native.licenses.zip') -Destination $output -Force
        $rows = @($kinds.Keys | Sort-Object | ForEach-Object { '    { L"' + $_.Replace('\','\\').Replace('"','\"') + '", L"' + $kinds[$_] + '" },' })
        $header = '#pragma once' + "`nnamespace ff { struct Format { const wchar_t* extension; const wchar_t* kind; }; inline constexpr Format Formats[]{`n" + ($rows -join "`n") + "`n}; }`n"
        [IO.File]::WriteAllText((Join-Path $output 'Formats.generated.h'),$header,[Text.UTF8Encoding]::new($false))
        $common = @('/nologo','/c','/EHsc','/utf-8','/guard:cf','/DUNICODE','/D_UNICODE','/DWINVER=0x0A00','/D_WIN32_WINNT=0x0A00',('/I'+$PSScriptRoot),('/I'+$output),('/I'+(Join-Path $PSScriptRoot 'vendor/miniz')))
        $common += if ($Configuration -eq 'Debug') { @('/Od','/MTd','/Zi','/D_DEBUG',('/Fd'+(Join-Path $output 'native-shell.pdb'))) } else { @('/O2','/MT','/DNDEBUG') }
        $sources = @('Epub.cpp','Imaging.cpp','Explorer.cpp','Preview.cpp')
        if ($RunTests) { $sources += 'Tests.cpp' }
        foreach ($source in $sources) {
            $object=Join-Path $output ([IO.Path]::GetFileNameWithoutExtension($source)+'.obj')
            & $compiler @common /std:c++20 /W4 ('/Fo'+$object) (Join-Path $PSScriptRoot $source)
            if ($LASTEXITCODE -ne 0) { throw "C++ compilation failed: $arch $source" }
        }
        $libraries=@($rustLibrary,'kernel32.lib','user32.lib','gdi32.lib','ole32.lib','uuid.lib','shell32.lib','shlwapi.lib','comctl32.lib','windowscodecs.lib','xmllite.lib','ws2_32.lib','advapi32.lib','bcrypt.lib','ntdll.lib','userenv.lib')
        $linkOptions=@('/NOLOGO','/DYNAMICBASE','/NXCOMPAT','/GUARD:CF',('/MACHINE:'+$arch))
        if ($Configuration -eq 'Debug') { $linkOptions += '/DEBUG' }
        if ($arch -eq 'x64') { $linkOptions += '/CETCOMPAT' }
        $objects=@('Epub','Imaging','Explorer' | ForEach-Object { Join-Path $output ($_+'.obj') })
        $dll=Join-Path $output 'FormatFactoryShellExtension.Native.dll'
        & $linker @linkOptions /DLL ('/DEF:'+(Join-Path $PSScriptRoot 'exports.def')) ('/OUT:'+$dll) @objects (Join-Path $output 'Preview.obj') @libraries
        if ($LASTEXITCODE -ne 0) { throw "Native DLL link failed: $arch" }
        $machine = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
        if ($RunTests) {
            # miniz writes independent ZIP fixtures for the native tests.
            & $compiler @common /TC /W3 ('/Fo'+(Join-Path $output 'miniz.obj')) (Join-Path $PSScriptRoot 'vendor/miniz/miniz.c')
            if ($LASTEXITCODE -ne 0) { throw "Fixture writer compilation failed: $arch" }
            $test=Join-Path $output 'FormatFactoryShellExtension.Native.Tests.exe'
            & $linker @linkOptions ('/OUT:'+$test) @objects (Join-Path $output 'Tests.obj') (Join-Path $output 'miniz.obj') @libraries
            if ($LASTEXITCODE -ne 0) { throw "Native tests link failed: $arch" }
            if ($machine -eq $arch -or ($machine -eq 'arm64' -and $arch -eq 'x64')) { & $test $dll; if ($LASTEXITCODE -ne 0) { throw "Native tests failed: $arch" } }
            else { Write-Warning "$arch was compiled but cannot execute on $machine. Physical ARM64 installation acceptance remains required." }
        }
        [ordered]@{ architecture=$arch; configuration=$Configuration; compiler=$toolchain.compilerFileVersion; sdk=$sdkVersion; toolchainLockSha256=(Get-FileHash -LiteralPath (Join-Path $repoRoot 'build/toolchain.lock.json')).Hash; epubParser="rust"; rustVersion=$toolchain.rustVersion; rustLockSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot "rust/Cargo.lock")).Hash; miniz=$lock.version; minizVerified=$true; dllBytes=(Get-Item -LiteralPath $dll).Length; dllSha256=(Get-FileHash -LiteralPath $dll).Hash; formatCount=$kinds.Count; testsExecuted=[bool]($RunTests -and ($machine -eq $arch -or ($machine -eq 'arm64' -and $arch -eq 'x64'))) } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'native-shell-build.json') -Encoding utf8
        Write-Output "Built Rust/C++ Shell ($Configuration): $dll"
    }
}
finally { $env:PATH=$originalPath; $env:INCLUDE=$originalInclude; $env:LIB=$originalLib }
