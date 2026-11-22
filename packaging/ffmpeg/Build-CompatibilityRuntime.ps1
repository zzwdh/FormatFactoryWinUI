#requires -Version 7.2
[CmdletBinding()]
param(
    [string]$PythonPath,
    [string]$SourceArchivePath,
    [string]$PrimaryEngineDirectory,
    [string]$CacheDirectory = (Join-Path $PSScriptRoot '../../work/engine-downloads'),
    [string]$VcRuntimeCacheDirectory,
    [string]$BuildDirectory,
    [string]$OutputDirectory,
    [string]$GitRoot,
    [string]$VCToolsRoot,
    [string]$WindowsSdkRoot = (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'),
    [ValidateRange(1,32)][int]$Jobs = 8,
    [switch]$Offline,
    [switch]$SkipDependencyTests,
    [switch]$ConfigureOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $repo 'build/Toolchain.ps1')
. (Join-Path $PSScriptRoot '../EnginePackaging.ps1')
$sourceLockPath = Join-Path $PSScriptRoot 'sources.lock.json'
$sourceLock = Get-Content -LiteralPath $sourceLockPath -Raw | ConvertFrom-Json
if ($sourceLock.schemaVersion -ne 1 -or $sourceLock.architecture -cne 'x64') { throw 'Unsupported compatibility build lock.' }
$toolchain = Get-ToolchainLock -RepositoryRoot $repo
$vc = Resolve-LockedVcTools -RepositoryRoot $repo -Lock $toolchain -VCToolsRoot $VCToolsRoot
$compilerDirectory = Join-Path $vc 'bin/Hostx64/x64'
[void](Assert-LockedToolVersion -Path (Join-Path $compilerDirectory 'cl.exe') -ExpectedVersion $toolchain.compilerFileVersion)
[void](Assert-LockedToolVersion -Path (Join-Path $compilerDirectory 'link.exe') -ExpectedVersion $toolchain.linkerFileVersion)
if (!(Test-Path -LiteralPath (Join-Path $WindowsSdkRoot ('Include/'+$toolchain.windowsSdkVersion+'/um/windows.h')))) { throw 'The locked Windows SDK headers and libraries are required.' }
if (!$PythonPath) {
    $pythonCandidates = @($env:FORMAT_FACTORY_FFMPEG_BUILD_PYTHON,(Join-Path $repo 'work/prepared-engines/x64/Engines/caj2pdf/python.exe'))
    foreach ($name in @('python.exe','python3.exe')) { $command=Get-Command $name -ErrorAction SilentlyContinue; if($command){$pythonCandidates += $command.Source} }
    $resolvedPython = @($pythonCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) -and (Get-Item -LiteralPath $_).Length -gt 0 -and $_ -notmatch '[\\/]Microsoft[\\/]WindowsApps[\\/]' } | Select-Object -First 1)
    if (!$resolvedPython.Count) { throw 'Pass -PythonPath or set FORMAT_FACTORY_FFMPEG_BUILD_PYTHON to a usable Python 3 installation.' }
    $PythonPath = $resolvedPython[0]
}
$PythonPath = (Get-Item -LiteralPath $PythonPath -ErrorAction Stop).FullName
if (!$GitRoot) { $GitRoot = Split-Path (Split-Path (Get-Command git -ErrorAction Stop).Source -Parent) -Parent }
$GitRoot = [IO.Path]::GetFullPath($GitRoot)
$shell = Join-Path $GitRoot 'usr/bin/sh.exe'
if (!(Test-Path -LiteralPath $shell -PathType Leaf)) { throw 'Git for Windows Bash is required; pass -GitRoot to its root directory.' }
$identityInputs = @($sourceLockPath,(Join-Path $repo 'build/toolchain.lock.json'),$PSCommandPath,(Join-Path $PSScriptRoot 'Build-Dependencies.ps1'),(Join-Path $PSScriptRoot '../EnginePackaging.ps1'),(Join-Path $PSScriptRoot '../vcredist/Prepare-VcRuntime.ps1'),(Join-Path $PSScriptRoot '../vcredist/sources.x64.lock.json'),(Join-Path $PSScriptRoot '../vcredist/README.md'),$PythonPath,$shell,(Join-Path $GitRoot 'usr/bin/msys-2.0.dll'))
$identityText = (@($identityInputs | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }) + @('dependencyTests='+(-not $SkipDependencyTests))) -join "`n"
$buildId = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($identityText))).Substring(0,16).ToLowerInvariant()
if (!$BuildDirectory) { $BuildDirectory = Join-Path ([IO.Path]::GetTempPath()) ('FormatFactory-Compatibility-'+$buildId) }
$BuildDirectory = [IO.Path]::GetFullPath($BuildDirectory)
if ($BuildDirectory -match '[^\x21-\x7e]') { throw 'Use an ASCII build directory without spaces; Windows NASM cannot resolve this checkout''s Unicode absolute paths.' }
$allowedBuildRoots = @([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[IO.Path]::GetFullPath((Join-Path $repo 'work')))
if (!@($allowedBuildRoots | Where-Object { $BuildDirectory.StartsWith($_.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) }).Count) { throw 'BuildDirectory must be inside the Windows temporary directory or this repository''s work directory.' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo ('work/ffmpeg-compatibility-runtime/'+$buildId) }
$OutputDirectory = Assert-EngineChildPath -Path $OutputDirectory -Root (Join-Path $repo 'work')
if ((Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count) { throw 'Use a fresh runtime output directory; existing runtime files are never deleted or overwritten.' }
if ($SourceArchivePath) {
    $SourceArchivePath = (Get-Item -LiteralPath $SourceArchivePath -ErrorAction Stop).FullName
    if ((Get-FileHash -LiteralPath $SourceArchivePath).Hash -ine $sourceLock.sourceBundle.sha256) { throw 'SourceArchivePath does not match the locked source bundle SHA256.' }
}
if ((Test-Path -LiteralPath $BuildDirectory) -and @(Get-ChildItem -LiteralPath $BuildDirectory -Force).Count) {
    $marker = Join-Path $BuildDirectory 'build-identity.txt'
    if (!(Test-Path -LiteralPath $marker -PathType Leaf) -or (Get-Content -LiteralPath $marker -Raw).Trim() -cne $buildId) { throw 'BuildDirectory contains unrelated or differently configured files. Choose a fresh directory.' }
}
foreach ($directory in @($BuildDirectory,$OutputDirectory)) {
    $cursor = [IO.DirectoryInfo]::new($directory)
    while ($cursor) {
        if ($cursor.Exists -and ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Refusing a build/output path through a reparse point: $($cursor.FullName)" }
        $cursor = $cursor.Parent
    }
}
[void][IO.Directory]::CreateDirectory($BuildDirectory)
[IO.File]::WriteAllText((Join-Path $BuildDirectory 'build-identity.txt'),$buildId,[Text.UTF8Encoding]::new($false))
$logs = Join-Path $BuildDirectory 'logs'
[void][IO.Directory]::CreateDirectory($logs)

function Invoke-BuildCommand([string]$Executable,[string[]]$Arguments,[string]$LogName) {
    & $Executable @Arguments *> (Join-Path $logs $LogName)
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath (Join-Path $logs $LogName) -Tail 25 | Write-Host
        throw "$LogName failed with exit $LASTEXITCODE; see $logs"
    }
}
function ConvertTo-MsysPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path).Replace('\','/')
    if ($full -notmatch '^[A-Za-z]:/') { throw 'MSYS build paths must use a local Windows drive.' }
    return '/'+$full.Substring(0,1).ToLowerInvariant()+$full.Substring(2)
}
function Expand-BuildArchive($Artifact,[string]$Destination,[string[]]$Members=@()) {
    $archive = if ($SourceArchivePath -and $Artifact.sha256 -ceq $sourceLock.sourceBundle.sha256) { $SourceArchivePath } else { Get-LockedEngineArchive -Artifact $Artifact -CacheDirectory $CacheDirectory -Offline:$Offline }
    $marker = Join-Path $Destination '.archive-sha256'
    if (Test-Path -LiteralPath $marker -PathType Leaf) {
        if ((Get-Content -LiteralPath $marker -Raw).Trim() -ine $Artifact.sha256) { throw 'Cached source/tool extraction belongs to a different archive.' }
        return
    }
    if ((Test-Path -LiteralPath $Destination) -and @(Get-ChildItem -LiteralPath $Destination -Force).Count) { throw "Incomplete extraction exists; choose a fresh BuildDirectory: $Destination" }
    [void][IO.Directory]::CreateDirectory($Destination)
    if ($Artifact.fileName.EndsWith('.zip',[StringComparison]::OrdinalIgnoreCase)) {
        Expand-SafeEngineZip -ArchivePath $archive -Destination $Destination
    } else {
        $excludeArguments = @()
        if ($Artifact.sha256 -ceq $sourceLock.sourceBundle.sha256) {
            # The fixed release bundle includes one generated Libtool link and
            # disabled JPEG XL benchmark wrappers. Omit these exact entries;
            # every remaining archive path and extracted reparse point is still
            # checked. No broad symlink or generated-directory exception exists.
            foreach ($excluded in $sourceLock.excludedSourceEntries) {
                if ($excluded -notmatch '^\./sources/[A-Za-z0-9._/-]+$' -or $excluded -match '(^|/)\.\.(/|$)') { throw 'Invalid exact source exclusion in the build lock.' }
                $excludeArguments += '--exclude='+$excluded
            }
        }
        $entries = @(& tar.exe -tf $archive @excludeArguments @Members)
        if ($LASTEXITCODE -ne 0 -or !$entries.Count) { throw "Cannot inspect locked archive: $archive" }
        foreach ($entry in $entries) {
            $name = $entry.Replace('\','/')
            if ($name -match '(^/|:|(^|/)\.\.(/|$))') { throw "Unsafe archive path: $name" }
        }
        & tar.exe -xf $archive -C $Destination @excludeArguments @Members
        if ($LASTEXITCODE -ne 0) { throw "Cannot expand locked archive: $archive" }
    }
    $links = @(Get-ChildItem -LiteralPath $Destination -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
    if ($links.Count) { throw "Locked build archive contains a reparse point: $($links[0].FullName)" }
    [IO.File]::WriteAllText($marker,[string]$Artifact.sha256,[Text.UTF8Encoding]::new($false))
}
function Assert-BuiltRuntime([string]$Directory) {
    Assert-FfmpegPeDependencies -Directory $Directory -RuntimeArchitecture x64
    foreach ($kind in @('encoders','decoders')) {
        $start=[Diagnostics.ProcessStartInfo]::new()
        $start.FileName=Join-Path $Directory 'ffmpeg.exe'; $start.UseShellExecute=$false; $start.CreateNoWindow=$true
        $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true; $start.Environment['PATH']=Join-Path $env:SystemRoot 'System32'
        [void]$start.ArgumentList.Add('-hide_banner'); [void]$start.ArgumentList.Add('-'+$kind)
        $process=[Diagnostics.Process]::new(); $process.StartInfo=$start
        try {
            [void]$process.Start(); $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
            if(!$process.WaitForExit(30000)){ $process.Kill($true); throw "Built runtime $kind enumeration timed out." }
            $text=$stdout.GetAwaiter().GetResult()+"`n"+$stderr.GetAwaiter().GetResult()
            if($process.ExitCode -ne 0){ throw "Built runtime $kind enumeration failed." }
        } finally { $process.Dispose() }
        $required=if($kind -eq 'encoders'){@('ffv1','pcm_s32le','pcm_s16le','wrapped_avframe')}else{@('bvc2','ac4','braw','binkvideo2','libdav1d','libjxl','libjxl_anim')}
        foreach($name in $required) { if($text -notmatch ('(?m)^\s*[VAS][A-Z.]{5}\s+'+[regex]::Escape($name)+'\s+')) { throw "Built runtime is missing required $kind implementation: $name" } }
    }
}
function Invoke-RuntimeSelfCheck([string]$Directory) {
    if (!$PrimaryEngineDirectory) { return }
    $primary=Join-Path ([IO.Path]::GetFullPath($PrimaryEngineDirectory)) 'ffmpeg.exe'
    if (!(Test-Path -LiteralPath $primary -PathType Leaf)) { throw 'PrimaryEngineDirectory does not contain ffmpeg.exe.' }
    $reportOutput=Join-Path $logs ('runtime-selfcheck-'+[Guid]::NewGuid().ToString('N'))
    Invoke-BuildCommand $PythonPath @('-I','-B',(Join-Path $PSScriptRoot 'smoke-compatibility.py'),'--baseline',(Join-Path $Directory 'ffmpeg.exe'),'--candidate',(Join-Path $Directory 'ffmpeg.exe'),'--primary',$primary,'--output',$reportOutput) ('runtime-selfcheck-'+[Guid]::NewGuid().ToString('N')+'.log')
}
$cachePointerPath=Join-Path $BuildDirectory 'runtime-cache.json'
if (!$ConfigureOnly -and (Test-Path -LiteralPath $cachePointerPath -PathType Leaf)) {
    $pointer=Get-Content -LiteralPath $cachePointerPath -Raw | ConvertFrom-Json
    if ($pointer.buildId -cne $buildId -or [IO.Path]::IsPathRooted($pointer.directory)) { throw 'Runtime cache identity is invalid.' }
    $cached=Assert-EngineChildPath -Path (Join-Path $BuildDirectory $pointer.directory) -Root $BuildDirectory
    $receiptPath=Join-Path $cached 'build-receipt.json'
    if ((Get-FileHash -LiteralPath $receiptPath).Hash -ine $pointer.receiptSha256) { throw 'Runtime cache receipt changed.' }
    $receipt=Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    if ($receipt.buildId -cne $buildId -or $receipt.sourceBundleSha256 -ine $sourceLock.sourceBundle.sha256) { throw 'Runtime cache belongs to different build inputs.' }
    foreach($file in $receipt.files) {
        $path=Assert-EngineChildPath -Path (Join-Path $cached $file.path) -Root $cached
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $file.bytes -or (Get-FileHash -LiteralPath $path).Hash -ine $file.sha256) { throw "Runtime cache file changed: $path" }
    }
    if (@(Get-ChildItem -LiteralPath $cached -Recurse -File).Count -ne @($receipt.files).Count+1) { throw 'Runtime cache contains untracked files.' }
    Assert-BuiltRuntime $cached
    Copy-EngineTree -Source $cached -Destination $OutputDirectory
    Assert-BuiltRuntime $OutputDirectory
    Invoke-RuntimeSelfCheck $OutputDirectory
    Write-Host "Reused byte-verified compatibility runtime: $OutputDirectory"
    return
}

$savedEnvironment = @{}
foreach ($name in @('PATH','INCLUDE','LIB','CC','CXX','VSLANG','PYTHONDONTWRITEBYTECODE','PKG_CONFIG_PATH','PKG_CONFIG_LIBDIR','MSYS_NO_PATHCONV','CHERE_INVOKING')) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name,'Process') }
try {
    foreach ($tool in $sourceLock.tools) { Expand-BuildArchive $tool (Join-Path $BuildDirectory ('tools/'+$tool.id)) }
    $sources = Join-Path $BuildDirectory 'source-bundle'
    $members = @($sourceLock.dependencies | ForEach-Object { './sources/'+$_.id }) + @('./sources/librempeg')
    Expand-BuildArchive $sourceLock.sourceBundle $sources $members
    $sourceDirectory = Join-Path $sources 'sources'
    $prefix = Join-Path $BuildDirectory 'prefix'
    $cmake = Join-Path $BuildDirectory 'tools/cmake/cmake-3.31.8-windows-x86_64/bin/cmake.exe'
    $ninja = Join-Path $BuildDirectory 'tools/ninja/ninja.exe'
    $meson = Join-Path $BuildDirectory 'tools/meson/meson-1.9.1/meson.py'
    $nasmDirectory = Join-Path $BuildDirectory 'tools/nasm/nasm-3.01'
    $make = Join-Path $BuildDirectory 'tools/make/usr/bin/make.exe'
    $env:PATH = @($compilerDirectory,(Join-Path $WindowsSdkRoot ('bin/'+$toolchain.windowsSdkVersion+'/x64')),$nasmDirectory,(Split-Path $ninja -Parent),(Split-Path $cmake -Parent),(Join-Path $BuildDirectory 'tools/pkgconf/usr/bin'),(Split-Path $make -Parent),(Join-Path $BuildDirectory 'tools/coreutils/usr/bin'),(Join-Path $GitRoot 'usr/bin'),$savedEnvironment.PATH) -join ';'
    $env:INCLUDE = (@((Join-Path $vc 'include'))+@('ucrt','shared','um','winrt' | ForEach-Object { Join-Path $WindowsSdkRoot ('Include/'+$toolchain.windowsSdkVersion+'/'+$_) })) -join ';'
    $env:LIB = @((Join-Path $vc 'lib/x64'),(Join-Path $WindowsSdkRoot ('Lib/'+$toolchain.windowsSdkVersion+'/um/x64')),(Join-Path $WindowsSdkRoot ('Lib/'+$toolchain.windowsSdkVersion+'/ucrt/x64'))) -join ';'
    $env:CC='cl.exe'; $env:CXX='cl.exe'; $env:VSLANG='1033'; $env:PYTHONDONTWRITEBYTECODE='1'; $env:CHERE_INVOKING='1'
    Remove-Item Env:MSYS_NO_PATHCONV -ErrorAction SilentlyContinue
    & (Join-Path $PSScriptRoot 'Build-Dependencies.ps1') -SourceDirectory $sourceDirectory -BuildDirectory (Join-Path $BuildDirectory 'dependencies') -Prefix $prefix -CMakePath $cmake -NinjaPath $ninja -MesonPath $meson -PythonPath $PythonPath -ShellPath $shell -LogDirectory $logs -Jobs $Jobs -SkipTests:$SkipDependencyTests
    # MSYS pkg-config treats a DOS drive colon as a path-list separator.
    $env:PKG_CONFIG_PATH = ConvertTo-MsysPath (Join-Path $prefix 'lib/pkgconfig')
    $env:PKG_CONFIG_LIBDIR = $env:PKG_CONFIG_PATH
    $build = Join-Path $BuildDirectory 'librempeg-build'
    [void][IO.Directory]::CreateDirectory($build)
    $configure = @((Join-Path $sourceDirectory 'librempeg/configure').Replace('\','/')) + @($sourceLock.configure)
    $configure += @('--pkg-config=pkg-config','--pkg-config-flags=--static',('--extra-cflags=-MD -I'+(Join-Path $prefix 'include').Replace('\','/')),'--extra-cxxflags=-MD',('--extra-ldflags=-LIBPATH:'+(Join-Path $prefix 'lib').Replace('\','/')))
    Push-Location $build
    try {
        Invoke-BuildCommand $shell $configure 'librempeg-configure.log'
        if ($ConfigureOnly) { Write-Host "Configured compatibility profile. Build directory: $BuildDirectory"; return }
        Invoke-BuildCommand $make @('-j'+$Jobs) 'librempeg-build.log'
    } finally { Pop-Location }
    [void][IO.Directory]::CreateDirectory($OutputDirectory)
    foreach ($name in @('ffmpeg.exe','ffprobe.exe')) { Copy-Item -LiteralPath (Join-Path $build $name) -Destination $OutputDirectory }
    $libraries = @(Get-ChildItem -LiteralPath $build -Recurse -File -Filter '*.dll' | Where-Object Name -Match '^(avcodec|avformat|avfilter|avutil|swscale|swresample)-\d+\.dll$')
    if ($libraries.Count -ne 6 -or @($libraries.Name | Sort-Object -Unique).Count -ne 6) { throw 'Expected exactly six shared conversion libraries; inspect the build before staging.' }
    foreach ($library in $libraries) { Copy-Item -LiteralPath $library.FullName -Destination $OutputDirectory }
    if (!$VcRuntimeCacheDirectory) { $VcRuntimeCacheDirectory = $CacheDirectory }
    & (Join-Path $PSScriptRoot '../vcredist/Prepare-VcRuntime.ps1') -Destination $OutputDirectory -CacheDirectory $VcRuntimeCacheDirectory -Architecture x64 -Offline:$Offline
    foreach ($component in @($sourceLock.dependencies.id)+@('librempeg')) {
        $componentRoot = Join-Path $sourceDirectory $component
        foreach ($notice in Get-ChildItem -LiteralPath $componentRoot -Recurse -File | Where-Object Name -Match '^(COPYING|COPYRIGHT|LICENSE|NOTICE|AUTHORS|PATENTS)([._-].*)?$') {
            $relative = [IO.Path]::GetRelativePath($componentRoot,$notice.FullName)
            $target = Assert-EngineChildPath -Path (Join-Path $OutputDirectory ('licenses/'+$component+'/'+$relative)) -Root $OutputDirectory
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            Copy-Item -LiteralPath $notice.FullName -Destination $target
        }
    }
    Copy-Item -LiteralPath $sourceLockPath -Destination (Join-Path $OutputDirectory 'sources.lock.json')
    [ordered]@{schemaVersion=1;name='FormatFactory LibreMPEG compatibility runtime';profile=$sourceLock.profile;license='AGPL-3.0-or-later';platform='win32-x64';mediaBackend='librempeg';sourceCommits=[ordered]@{librempeg=$sourceLock.librempegCommit};sourceBundle=[ordered]@{url=$sourceLock.sourceBundle.url;sha256=$sourceLock.sourceBundle.sha256};role='Input decoding and probing, null preflight, FFV1/PCM Matroska intermediate. Primary FFmpeg performs final encoding.'} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'runtime.json') -Encoding utf8NoBOM
    Assert-BuiltRuntime $OutputDirectory
    Invoke-RuntimeSelfCheck $OutputDirectory
    $files = @(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File | Sort-Object FullName | ForEach-Object { [ordered]@{path=[IO.Path]::GetRelativePath($OutputDirectory,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()} })
    [ordered]@{schemaVersion=1;kind='compatibilityRuntimeBuild';profile=$sourceLock.profile;architecture='x64';buildId=$buildId;sourceCommit=$sourceLock.librempegCommit;sourceBundleSha256=$sourceLock.sourceBundle.sha256;builtAt=[DateTimeOffset]::UtcNow.ToString('o');toolchainLockSha256=(Get-FileHash -LiteralPath (Join-Path $repo 'build/toolchain.lock.json')).Hash.ToLowerInvariant();dependencyTests=if($SkipDependencyTests){'skipped'}else{'passed'};conversionValidation=if($PrimaryEngineDirectory){'candidate-selfcheck-passed'}else{'not-performed-by-build-script'};configure=$configure;logs=$logs;files=$files} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'build-receipt.json') -Encoding utf8NoBOM
    $cacheRelative='runtime-'+[Guid]::NewGuid().ToString('N')
    $cacheDirectory=Assert-EngineChildPath -Path (Join-Path $BuildDirectory $cacheRelative) -Root $BuildDirectory
    Copy-EngineTree -Source $OutputDirectory -Destination $cacheDirectory
    [ordered]@{buildId=$buildId;directory=$cacheRelative;receiptSha256=(Get-FileHash -LiteralPath (Join-Path $cacheDirectory 'build-receipt.json')).Hash.ToLowerInvariant()} | ConvertTo-Json | Set-Content -LiteralPath $cachePointerPath -Encoding utf8NoBOM
    Write-Host "Built compatibility runtime: $OutputDirectory"
    Write-Host 'Run the capability comparison and real conversion corpus before replacing a prepared engine or packaging it.'
} finally {
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name,$savedEnvironment[$name],'Process') }
}
