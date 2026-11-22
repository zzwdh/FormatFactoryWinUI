[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Destination,
    [string]$CacheDirectory = (Join-Path $PSScriptRoot '../../work/engine-downloads'),
    [ValidateSet('x64', 'arm64')][string]$Architecture = 'x64',
    [switch]$Offline
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$destinationPath = [IO.Path]::GetFullPath($Destination).TrimEnd([IO.Path]::DirectorySeparatorChar)
$workRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../work')).TrimEnd([IO.Path]::DirectorySeparatorChar)
if (!$destinationPath.StartsWith($workRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The CAJ destination must be a component directory inside this repository work directory.'
}
# Replacement and cleanup are restricted to this workspace. Reject junctions
# along the destination path so they cannot redirect a recursive operation.
$ancestor = $destinationPath
while ($ancestor.Length -ge $workRoot.Length) {
    if (Test-Path -LiteralPath $ancestor) {
        $item = Get-Item -LiteralPath $ancestor -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse-point destination is not allowed: $ancestor" }
    }
    $ancestor = Split-Path -Parent $ancestor
}
function Assert-RegularZipEntry($Entry) {
    $unixType = ($Entry.ExternalAttributes -shr 16) -band 0xF000
    if ($unixType -eq 0xA000 -or ($Entry.ExternalAttributes -band 0x400) -ne 0) {
        throw "Archive links are not allowed: $($Entry.FullName)"
    }
}
function Expand-CheckedZip([string]$Archive, [string]$Directory) {
    $root = [IO.Path]::GetFullPath($Directory).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        foreach ($entry in $zip.Entries) {
            Assert-RegularZipEntry $entry
            $target = [IO.Path]::GetFullPath((Join-Path $Directory $entry.FullName))
            if (!$target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "Archive path escapes extraction directory: $($entry.FullName)" }
        }
    } finally { $zip.Dispose() }
    [IO.Compression.ZipFile]::ExtractToDirectory($Archive, $Directory)
}
$cachePath = [IO.Path]::GetFullPath($CacheDirectory)
if ($destinationPath -eq [IO.Path]::GetPathRoot($destinationPath)) { throw 'The destination cannot be a filesystem root.' }
$parentPath = Split-Path -Parent $destinationPath
New-Item -ItemType Directory -Force -Path $cachePath, $parentPath | Out-Null
$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'sources.lock.json') -Raw | ConvertFrom-Json
if ($lock.schemaVersion -ne 1 -or $lock.architecture -ne 'x64') { throw 'Unsupported CAJ source lockfile.' }
$archives = @{}
foreach ($source in $lock.sources) {
    if ($source.file -ne [IO.Path]::GetFileName($source.file) -or $source.file -match '[:\\/]') { throw 'Invalid dependency archive filename.' }
    if ($source.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or $source.url -notmatch '^https://') { throw 'Invalid dependency URL or SHA256.' }
    $archive = Join-Path $cachePath $source.file
    if (!(Test-Path -LiteralPath $archive)) {
        if ($Offline) { throw "Missing cached dependency: $($source.file)" }
        $download = "$archive.download"
        Invoke-WebRequest -Uri $source.url -OutFile $download
        if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $source.sha256) {
            throw "SHA256 mismatch for downloaded dependency $($source.file)"
        }
        Move-Item -LiteralPath $download -Destination $archive -Force
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $source.sha256) {
        throw "SHA256 mismatch for cached dependency $($source.file). Remove or replace the invalid cache file."
    }
    $archives[$source.id] = $archive
}
$staging = Join-Path $parentPath ('.caj-stage-' + [guid]::NewGuid().ToString('N'))
$staging = [IO.Path]::GetFullPath($staging)
if (!$staging.StartsWith($parentPath.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid staging path.' }
New-Item -ItemType Directory -Path $staging | Out-Null
try {
    Expand-CheckedZip $archives['python'] $staging
    # No system Python, pip, registry lookup, user site, or PYTHONPATH is needed.
    "python312.zip`n.`nLib/site-packages`napp`n" | Set-Content -LiteralPath (Join-Path $staging 'python312._pth') -Encoding ascii
    foreach ($unused in @('pythonw.exe')) {
        $file = Join-Path $staging $unused
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
    }
    $site = Join-Path $staging 'Lib/site-packages'
    New-Item -ItemType Directory -Path $site -Force | Out-Null
    foreach ($wheel in @('PyPDF2','imagesize','Pillow')) {
        Expand-CheckedZip $archives[$wheel] $site
    }
    $sourceExtract = Join-Path $staging '_source'
    Expand-CheckedZip $archives['caj2pdf'] $sourceExtract
    $sourceRoot = (Get-ChildItem -LiteralPath $sourceExtract -Directory | Select-Object -First 1).FullName
    $app = Join-Path $staging 'app'
    $native = Join-Path $app 'lib/bin'
    New-Item -ItemType Directory -Path $app,$native -Force | Out-Null
    foreach ($name in @('caj2pdf','cajparser.py','HNParsePage.py','jbig2dec.py','jbigdec.py','pdfwutils.py','utils.py','LICENSE','README.md','requirements.txt')) {
        Copy-Item -LiteralPath (Join-Path $sourceRoot $name) -Destination $app
    }
    foreach ($name in @('libjbigdec-w64.dll','libjbig2codec-w64.dll')) {
        Copy-Item -LiteralPath (Join-Path $sourceRoot "lib/bin/$name") -Destination $native
    }
    # Native DLL paths in upstream are relative to CWD; bind them to app/ instead.
    # This also allows private scratch directories to be removed while DLLs remain loaded.
    foreach ($module in @('jbigdec','jbig2dec')) {
        $modulePath = Join-Path $app "$module.py"
        $text = Get-Content -LiteralPath $modulePath -Raw
        if ($module -eq 'jbig2dec') { $text = "import os`n" + $text }
        foreach ($name in @('libjbigdec-w64.dll','libjbigdec-w32.dll','libjbig2codec-w64.dll','libjbig2codec-w32.dll')) {
            $text = $text.Replace('cdll.LoadLibrary("./lib/bin/' + $name + '")', 'cdll.LoadLibrary(os.path.join(os.path.dirname(os.path.abspath(__file__)), "lib", "bin", "' + $name + '"))')
        }
        [IO.File]::WriteAllText($modulePath, $text, [Text.UTF8Encoding]::new($false))
    }
    $licenses = Join-Path $staging 'licenses'
    New-Item -ItemType Directory -Path $licenses -Force | Out-Null
    Copy-Item -Path (Join-Path $PSScriptRoot 'licenses/*') -Destination $licenses
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'lib') -Destination (Join-Path $licenses 'caj2pdf-native-source') -Recurse
    $duplicateBins = Join-Path $licenses 'caj2pdf-native-source/bin'
    if (Test-Path -LiteralPath $duplicateBins) { Remove-Item -LiteralPath $duplicateBins -Recurse -Force }
    $mupdfZip = [IO.Compression.ZipFile]::OpenRead($archives['mutool'])
    try {
        foreach ($entry in $mupdfZip.Entries) { Assert-RegularZipEntry $entry }
        $mutool = @($mupdfZip.Entries | Where-Object { $_.Name -eq 'mutool.exe' })
        if ($mutool.Count -ne 1) { throw 'The MuPDF archive must contain exactly one mutool.exe.' }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($mutool[0], (Join-Path $staging 'mutool.exe'))
        foreach ($entry in $mupdfZip.Entries) {
            if ($entry.Name -match '^(COPYING|LICENSE|AUTHORS|README|CHANGES)(\..*)?$') {
                $target = Join-Path $licenses ('mupdf-' + $entry.Name)
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
            }
        }
    } finally { $mupdfZip.Dispose() }
    # mutool imports the x64 VC++ runtime even in an ARM64 application package.
    # Carry signed private DLLs beside it so a clean Windows install needs no redist.
    & (Join-Path $PSScriptRoot '../vcredist/Prepare-VcRuntime.ps1') -Destination $staging -CacheDirectory $cachePath -Architecture x64 -Offline:$Offline
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'launcher.py') -Destination $staging
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'sources.lock.json') -Destination $staging
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $staging 'BUNDLE-NOTICES.md')
    Remove-Item -LiteralPath $sourceExtract -Recurse -Force
    . (Join-Path $PSScriptRoot '../EnginePackaging.ps1')
    Assert-CajPeDependencies -Directory $staging -RuntimeArchitecture x64
    $probe = [Diagnostics.ProcessStartInfo]::new()
    $probe.FileName = Join-Path $staging 'python.exe'
    $probe.UseShellExecute = $false
    $probe.CreateNoWindow = $true
    $probe.RedirectStandardOutput = $true
    $probe.RedirectStandardError = $true
    foreach ($argument in @('-I', '-B', (Join-Path $staging 'launcher.py'), '--version')) { $probe.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($probe)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(30000)) { $process.Kill($true); throw 'CAJ dependency self-check timed out.' }
        if ($process.ExitCode -ne 0) { throw "The complete CAJ dependency self-check failed: $($stderr.GetAwaiter().GetResult())" }
        Write-Host $stdout.GetAwaiter().GetResult().Trim()
    } finally { $process.Dispose() }
    @{ schemaVersion = 1; engine = 'caj2pdf'; architecture = 'x64'; targetArchitecture = $Architecture; sourceRevision = $lock.sources[0].version } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $staging '.formatfactory-caj-bundle.json') -Encoding utf8
    if (Test-Path -LiteralPath $destinationPath) {
        if (!(Test-Path -LiteralPath (Join-Path $destinationPath '.formatfactory-caj-bundle.json'))) {
            if (@(Get-ChildItem -LiteralPath $destinationPath -Force).Count -gt 0) { throw 'Refusing to replace a non-empty directory without the CAJ ownership marker.' }
        }
        Remove-Item -LiteralPath $destinationPath -Recurse -Force
    }
    Move-Item -LiteralPath $staging -Destination $destinationPath
    $bytes = (Get-ChildItem -LiteralPath $destinationPath -File -Recurse | Measure-Object -Property Length -Sum).Sum
    Write-Host "CAJ bundle ready: $destinationPath ($([math]::Round($bytes / 1MB, 2)) MiB; x64 runtime for $Architecture)"
} finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
