[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$BuildDirectory,
    [Parameter(Mandatory)][string]$Prefix,
    [Parameter(Mandatory)][string]$CMakePath,
    [Parameter(Mandatory)][string]$NinjaPath,
    [Parameter(Mandatory)][string]$MesonPath,
    [Parameter(Mandatory)][string]$PythonPath,
    [Parameter(Mandatory)][string]$ShellPath,
    [Parameter(Mandatory)][string]$LogDirectory,
    [ValidateRange(1,32)][int]$Jobs=8,
    [switch]$SkipTests
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$ownerRoot=Split-Path ([IO.Path]::GetFullPath($BuildDirectory)) -Parent
if (!(Test-Path -LiteralPath (Join-Path $ownerRoot 'build-identity.txt') -PathType Leaf)) { throw 'Use Build-CompatibilityRuntime.ps1 to create an owned build directory.' }
foreach ($path in @($SourceDirectory,$Prefix,$LogDirectory)) {
    if (![IO.Path]::GetFullPath($path).StartsWith($ownerRoot.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Dependency paths must remain inside the owned build directory.' }
}
foreach ($path in @($SourceDirectory,$BuildDirectory,$Prefix)) { if ([IO.Path]::GetFullPath($path) -match '[^\x21-\x7e]') { throw 'Dependency source/build/prefix paths must be ASCII without spaces.' } }
foreach ($path in @($BuildDirectory,$Prefix,$LogDirectory)) { [void][IO.Directory]::CreateDirectory($path) }
function Invoke-DependencyCommand([string]$Executable,[string[]]$Arguments,[string]$LogName) {
    & $Executable @Arguments *> (Join-Path $LogDirectory $LogName)
    if($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $LogDirectory $LogName) -Tail 25 | Write-Host; throw "$LogName failed; see $LogDirectory" }
}
$common=@('-G','Ninja',('-DCMAKE_MAKE_PROGRAM='+$NinjaPath),'-DCMAKE_BUILD_TYPE=Release','-DBUILD_SHARED_LIBS=OFF','-DCMAKE_C_COMPILER=cl.exe','-DCMAKE_CXX_COMPILER=cl.exe','-DCMAKE_POLICY_DEFAULT_CMP0091=NEW','-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreadedDLL',('-DCMAKE_INSTALL_PREFIX='+$Prefix),('-DCMAKE_PREFIX_PATH='+$Prefix),'-DCMAKE_INSTALL_LIBDIR=lib','-DCMAKE_C_FLAGS=/utf-8','-DCMAKE_CXX_FLAGS=/utf-8')
function Build-CMakeDependency([string]$Name,[string[]]$Options) {
    $source=Join-Path $SourceDirectory $Name
    $build=Join-Path $BuildDirectory $Name
    Write-Host "Building static $Name..."
    Invoke-DependencyCommand $CMakePath (@('-S',$source,'-B',$build)+$common+$Options) ($Name+'-configure.log')
    Invoke-DependencyCommand $CMakePath @('--build',$build,'--parallel',[string]$Jobs) ($Name+'-build.log')
    Invoke-DependencyCommand $CMakePath @('--install',$build,'--config','Release') ($Name+'-install.log')
}
$dav1dBuild=Join-Path $BuildDirectory 'dav1d'
$dav1dSource=Join-Path $SourceDirectory 'dav1d'
$setup=@('-B',$MesonPath,'setup',$dav1dBuild,$dav1dSource,('--prefix='+$Prefix),'--libdir=lib','--buildtype=release','--default-library=static','--wrap-mode=nodownload','-Db_vscrt=md','-Denable_asm=true','-Denable_tools=false','-Denable_examples=false','-Denable_tests=false','-Dtestdata_tests=false')
if(Test-Path -LiteralPath (Join-Path $dav1dBuild 'meson-private/coredata.dat')) { $setup += '--reconfigure' }
Invoke-DependencyCommand $PythonPath $setup 'dav1d-configure.log'
Invoke-DependencyCommand $PythonPath @('-B',$MesonPath,'compile','-C',$dav1dBuild,('-j'+$Jobs)) 'dav1d-build.log'
if(!$SkipTests) {
    # Production trim_dsp omits reference C routines. Upstream checkasm needs
    # those references, so validate a separate test library without installing it.
    $dav1dTests=Join-Path $BuildDirectory 'dav1d-tests'
    $testSetup=@('-B',$MesonPath,'setup',$dav1dTests,$dav1dSource,'--buildtype=release','--default-library=static','--wrap-mode=nodownload','-Db_vscrt=md','-Denable_asm=true','-Denable_tools=false','-Denable_examples=false','-Denable_tests=true','-Dtestdata_tests=false','-Dtrim_dsp=false')
    if(Test-Path -LiteralPath (Join-Path $dav1dTests 'meson-private/coredata.dat')) { $testSetup += '--reconfigure' }
    Invoke-DependencyCommand $PythonPath $testSetup 'dav1d-test-configure.log'
    Invoke-DependencyCommand $PythonPath @('-B',$MesonPath,'compile','-C',$dav1dTests,('-j'+$Jobs)) 'dav1d-test-build.log'
    Invoke-DependencyCommand $PythonPath @('-B',$MesonPath,'compile','-C',$dav1dTests,('-j'+$Jobs),'checkasm:executable') 'dav1d-checkasm-build.log'
    Invoke-DependencyCommand $PythonPath @('-B',$MesonPath,'test','-C',$dav1dTests,'--no-rebuild','--print-errorlogs') 'dav1d-tests.log'
}
Invoke-DependencyCommand $PythonPath @('-B',$MesonPath,'install','-C',$dav1dBuild,'--no-rebuild') 'dav1d-install.log'
Copy-Item -LiteralPath (Join-Path $Prefix 'lib/libdav1d.a') -Destination (Join-Path $Prefix 'lib/dav1d.lib') -Force
Build-CMakeDependency 'zlib' @('-DZLIB_COMPAT=ON','-DWITH_GTEST=OFF',('-DBUILD_TESTING='+$(if($SkipTests){'OFF'}else{'ON'})),'-DINSTALL_UTILS=OFF')
if(!$SkipTests) {
    $ctest=Join-Path (Split-Path $CMakePath -Parent) 'ctest.exe'
    Invoke-DependencyCommand $ctest @('--test-dir',(Join-Path $BuildDirectory 'zlib'),'--output-on-failure','--output-junit',(Join-Path $LogDirectory 'zlib-tests.xml')) 'zlib-tests.log'
}
Copy-Item -LiteralPath (Join-Path $Prefix 'lib/zlibstatic.lib') -Destination (Join-Path $Prefix 'lib/z.lib') -Force
Build-CMakeDependency 'bzip2' @('-DENABLE_SHARED_LIB=OFF','-DENABLE_STATIC_LIB=ON','-DENABLE_LIB_ONLY=ON','-DENABLE_TESTS=OFF','-DENABLE_DOCS=OFF')
Copy-Item -LiteralPath (Join-Path $Prefix 'lib/bz2_static.lib') -Destination (Join-Path $Prefix 'lib/bz2.lib') -Force
# libiconv has no native CMake build. Generate its upstream configuration with
# the MSVC-aware Autoconf wrappers, then compile the three static library units
# directly. Extra encodings match the original compatibility runtime.
$iconvSource=Join-Path $SourceDirectory 'libiconv'
$iconvBuild=Join-Path $BuildDirectory 'libiconv'
[void][IO.Directory]::CreateDirectory($iconvBuild)
$iconvMsysSource=$iconvSource.Replace('\','/') -replace '^([A-Za-z]):/','/$1/'
$iconvConfigure=@(($iconvMsysSource+'/configure'),'--host=x86_64-w64-mingw32',('--prefix='+$Prefix.Replace('\','/')),'--enable-static','--disable-shared','--disable-nls','--disable-rpath','--enable-extra-encodings',('CC='+$iconvMsysSource+'/build-aux/compile cl.exe -nologo'),'CFLAGS=-MD -O2','CPPFLAGS=-D_WIN32_WINNT=0x0A00','LD=link.exe','NM=dumpbin.exe -symbols','STRIP=:',('AR='+$iconvMsysSource+'/build-aux/ar-lib lib.exe'),'RANLIB=:','RC=:')
Push-Location $iconvBuild
try {
    Invoke-DependencyCommand $ShellPath $iconvConfigure 'libiconv-configure.log'
    $iconvFlags=@('/nologo','/c','/TC','/MD','/O2','/utf-8','/D_WIN32_WINNT=0x0A00','/DBUILDING_LIBICONV','/DBUILDING_LIBCHARSET','/DHAVE_CONFIG_H',('/I'+$iconvBuild+'/include'),('/I'+$iconvSource+'/include'),('/I'+$iconvBuild),('/I'+$iconvSource+'/lib'),('/I'+$iconvBuild+'/libcharset/include'),('/I'+$iconvSource+'/libcharset/include'))
    $iconvUnits=[ordered]@{iconv=($iconvSource+'/lib/iconv.c');localcharset=($iconvSource+'/libcharset/lib/localcharset.c');compat=($iconvSource+'/lib/compat.c')}
    foreach($unit in $iconvUnits.Keys) { Invoke-DependencyCommand 'cl.exe' ($iconvFlags+@(('/Fo'+$iconvBuild+'/'+$unit+'.obj'),$iconvUnits[$unit])) ('libiconv-'+$unit+'-compile.log') }
    Invoke-DependencyCommand 'lib.exe' (@('/nologo',('/OUT:'+$iconvBuild+'/iconv.lib'))+@($iconvUnits.Keys | ForEach-Object { $iconvBuild+'/'+$_+'.obj' })) 'libiconv-archive.log'
} finally { Pop-Location }
Copy-Item -LiteralPath (Join-Path $iconvBuild 'iconv.lib') -Destination (Join-Path $Prefix 'lib/iconv.lib') -Force
Copy-Item -LiteralPath (Join-Path $iconvBuild 'include/iconv.h') -Destination (Join-Path $Prefix 'include/iconv.h') -Force
Copy-Item -LiteralPath (Join-Path $iconvBuild 'libcharset/include/localcharset.h') -Destination (Join-Path $Prefix 'include/localcharset.h') -Force
Build-CMakeDependency 'libxml2' @('-DLIBXML2_WITH_PYTHON=OFF','-DLIBXML2_WITH_PROGRAMS=OFF','-DLIBXML2_WITH_TESTS=OFF','-DLIBXML2_WITH_DOCS=OFF','-DLIBXML2_WITH_HTTP=OFF','-DLIBXML2_WITH_ICONV=ON','-DLIBXML2_WITH_ICU=OFF','-DLIBXML2_WITH_MODULES=OFF','-DLIBXML2_WITH_DEBUG=OFF','-DLIBXML2_WITH_ZLIB=ON',('-DIconv_INCLUDE_DIR='+$Prefix+'/include'),('-DIconv_LIBRARY='+$Prefix+'/lib/iconv.lib'),('-DZLIB_INCLUDE_DIR='+$Prefix+'/include'),('-DZLIB_LIBRARY='+$Prefix+'/lib/z.lib'))
Copy-Item -LiteralPath (Join-Path $Prefix 'lib/libxml2s.lib') -Destination (Join-Path $Prefix 'lib/xml2.lib') -Force
Build-CMakeDependency 'brotli' @('-DBROTLI_BUILD_TOOLS=OFF','-DBROTLI_DISABLE_TESTS=ON')
Build-CMakeDependency 'highway' @('-DHWY_ENABLE_TESTS=OFF','-DHWY_ENABLE_EXAMPLES=OFF','-DHWY_ENABLE_CONTRIB=OFF','-DHWY_ENABLE_INSTALL=ON')
Build-CMakeDependency 'lcms2' @('-DLCMS2_BUILD_SHARED=OFF','-DLCMS2_BUILD_STATIC=ON','-DLCMS2_BUILD_TOOLS=OFF','-DLCMS2_BUILD_TESTS=OFF','-DLCMS2_WITH_JPEG=OFF','-DLCMS2_WITH_TIFF=OFF','-DLCMS2_WITH_ZLIB=OFF')
Build-CMakeDependency 'libjxl' @('-DBUILD_TESTING=OFF','-DJPEGXL_ENABLE_FUZZERS=OFF','-DJPEGXL_ENABLE_DEVTOOLS=OFF','-DJPEGXL_ENABLE_TOOLS=OFF','-DJPEGXL_ENABLE_DOXYGEN=OFF','-DJPEGXL_ENABLE_MANPAGES=OFF','-DJPEGXL_ENABLE_BENCHMARK=OFF','-DJPEGXL_ENABLE_EXAMPLES=OFF','-DJPEGXL_ENABLE_JNI=OFF','-DJPEGXL_ENABLE_SJPEG=OFF','-DJPEGXL_ENABLE_OPENEXR=OFF','-DJPEGXL_ENABLE_SKCMS=OFF','-DJPEGXL_ENABLE_VIEWERS=OFF','-DJPEGXL_ENABLE_TCMALLOC=OFF','-DJPEGXL_ENABLE_PLUGINS=OFF','-DJPEGXL_ENABLE_TRANSCODE_JPEG=ON','-DJPEGXL_ENABLE_BOXES=ON','-DJPEGXL_FORCE_SYSTEM_BROTLI=ON','-DJPEGXL_FORCE_SYSTEM_HWY=ON','-DJPEGXL_FORCE_SYSTEM_LCMS2=ON',('-DBROTLI_INCLUDE_DIR='+$Prefix+'/include'),('-DBROTLICOMMON_LIBRARY='+$Prefix+'/lib/brotlicommon.lib'),('-DBROTLIDEC_LIBRARY='+$Prefix+'/lib/brotlidec.lib'),('-DBROTLIENC_LIBRARY='+$Prefix+'/lib/brotlienc.lib'),('-DLCMS2_INCLUDE_DIR='+$Prefix+'/include'),('-DLCMS2_LIBRARY='+$Prefix+'/lib/lcms2.lib'))
Copy-Item -LiteralPath (Join-Path $Prefix 'lib/jxl.lib') -Destination (Join-Path $Prefix 'lib/jxl-static.lib') -Force
# Keep byte-identical COFF archive aliases above; only normalize generated
# pkg-config metadata. MSVC's CRT supplies libm, and all prefixes remain local.
$backup=Join-Path $LogDirectory 'original-pkgconfig'
[void][IO.Directory]::CreateDirectory($backup)
foreach($pc in Get-ChildItem -LiteralPath (Join-Path $Prefix 'lib/pkgconfig') -File -Filter '*.pc') {
    Copy-Item -LiteralPath $pc.FullName -Destination (Join-Path $backup $pc.Name) -Force
    $text=[IO.File]::ReadAllText($pc.FullName)
    $text=[regex]::Replace($text,'(?m)^prefix=.*$','prefix=${pcfiledir}/../..')
    $text=[regex]::Replace($text,'(?m)^exec_prefix=.*$','exec_prefix=${prefix}')
    $text=[regex]::Replace($text,'(?<=\s)-lm(?=\s|$)','')
    [IO.File]::WriteAllText($pc.FullName,$text,[Text.UTF8Encoding]::new($false))
}
