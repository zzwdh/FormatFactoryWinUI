[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Destination,
    [string]$CacheDirectory = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'work/engine-downloads'),
    [ValidateSet('x64','arm64')][string]$Architecture = 'x64',
    [switch]$Offline
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'EnginePackaging.ps1')
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$work = Join-Path $repo 'work'
$Destination = Assert-EngineChildPath -Path $Destination -Root $work
$CacheDirectory = [IO.Path]::GetFullPath($CacheDirectory)
[void][IO.Directory]::CreateDirectory($Destination)
[void][IO.Directory]::CreateDirectory($CacheDirectory)
$lockPath = Join-Path $PSScriptRoot 'sources.lock.json'
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
if ($lock.schemaVersion -ne 1) { throw 'Unsupported optical source lock.' }
function Get-VerifiedSource([string]$Url, [string]$Hash) {
    $uri = [Uri]$Url
    if ($uri.Scheme -ne 'https' -or $Hash -notmatch '^[a-fA-F0-9]{128}$') { throw 'Invalid optical source URL/hash.' }
    $name = [IO.Path]::GetFileName($uri.AbsolutePath)
    $path = Join-Path $CacheDirectory $name
    if (-not (Test-Path -LiteralPath $path)) {
        if ($Offline) { throw "Offline build is missing $name" }
        $partial = "$path.$([Guid]::NewGuid().ToString('N')).partial"
        try {
            Invoke-WebRequest -Uri $Url -OutFile $partial
            if ((Get-FileHash -LiteralPath $partial -Algorithm SHA512).Hash -ine $Hash) { throw "Source hash mismatch: $name" }
            Move-Item -LiteralPath $partial -Destination $path
        } finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force } }
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA512).Hash -ine $Hash) { throw "Cached source hash mismatch: $name" }
    return $path
}
function Expand-SelectedFiles([string]$Archive, [string]$Target, [string[]]$Entries) {
    [void][IO.Directory]::CreateDirectory($Target)
    foreach ($entry in $Entries) {
        if ($entry -match '(^/|:|\\|(^|/)\.\.(/|$))') { throw "Unsafe tar entry: $entry" }
        $listing = @(& tar -tvf $Archive $entry 2>&1)
        if ($LASTEXITCODE -ne 0 -or $listing.Count -ne 1 -or -not ([string]$listing[0]).StartsWith('-')) { throw "Tar entry is not a regular file: $entry" }
        & tar -xf $Archive -C $Target $entry
        if ($LASTEXITCODE -ne 0) { throw "Cannot extract $entry" }
    }
}
$temp = Join-Path $work ('optical-unpack-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
try {
    $archives = @{}
    foreach ($source in $lock.sources) {
        $artifact = [pscustomobject]@{fileName=$source.archiveName;url=$source.url;sha256=$source.sha256}
        $archive = Get-LockedEngineArchive -Artifact $artifact -CacheDirectory $CacheDirectory -Offline:$Offline
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ine $source.sha512) { throw "Optical archive SHA512 mismatch: $($source.id)" }
        $archives[$source.id] = $archive
    }
    Expand-SelectedFiles $archives.icedax $temp @('usr/bin/icedax.exe')
    Expand-SelectedFiles $archives.wodim $temp @('usr/bin/readom.exe')
    Expand-SelectedFiles $archives.cygwin $temp @('usr/bin/cygwin1.dll','usr/share/doc/Cygwin/COPYING','usr/share/doc/Cygwin/CYGWIN_LICENSE')
    $cdrSource = Get-VerifiedSource $lock.sources[0].sourceUrl $lock.sources[0].sourceSha512
    $null = Get-VerifiedSource $lock.sources[2].sourceUrl $lock.sources[2].sourceSha512
    Expand-SelectedFiles $cdrSource $temp @('cdrkit-1.1.11.tar.gz')
    Expand-SelectedFiles (Join-Path $temp 'cdrkit-1.1.11.tar.gz') $temp @('cdrkit-1.1.11/COPYING')
    foreach ($pair in @(@('cdda2wav','icedax.exe'),@('readom','readom.exe'))) {
        $dir = Join-Path $Destination $pair[0]
        Reset-EngineDirectory -Path $dir -Root $Destination
        $licenseDir = Join-Path $dir 'licenses'
        [void][IO.Directory]::CreateDirectory($licenseDir)
        Copy-Item -LiteralPath (Join-Path $temp ('usr/bin/' + $pair[1])) -Destination $dir
        Copy-Item -LiteralPath (Join-Path $temp 'usr/bin/cygwin1.dll') -Destination $dir
        Copy-Item -LiteralPath (Join-Path $temp 'usr/share/doc/Cygwin/COPYING') -Destination (Join-Path $licenseDir 'CYGWIN_COPYING.txt')
        Copy-Item -LiteralPath (Join-Path $temp 'usr/share/doc/Cygwin/CYGWIN_LICENSE') -Destination $licenseDir
        Copy-Item -LiteralPath (Join-Path $temp 'cdrkit-1.1.11/COPYING') -Destination (Join-Path $licenseDir 'CDRKIT_COPYING.txt')
        Copy-Item -LiteralPath $lockPath -Destination $dir
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $dir
        Assert-EngineFiles -Directory $dir -RequiredFiles @($pair[1],'cygwin1.dll','licenses/CDRKIT_COPYING.txt','licenses/CYGWIN_COPYING.txt','licenses/CYGWIN_LICENSE') -PeFiles @($pair[1],'cygwin1.dll') -RuntimeArchitecture x64
        # x64 helpers also run through Windows 11 x64 emulation on ARM64.
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName = Join-Path $dir $pair[1]
        $start.Arguments = '-version'
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $process = [Diagnostics.Process]::Start($start)
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        try {
            if (-not $process.WaitForExit(15000)) { $process.Kill(); throw "Optical helper probe timed out: $($pair[1])" }
            $text = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
            if ($process.ExitCode -ne 0 -or $text -notmatch '1\.1\.11') { throw "Optical helper probe failed: $($pair[1]): $text" }
        } finally { $process.Dispose() }
        Write-Host "Prepared $($pair[0]) 1.1.11 for $Architecture (x64 runtime)."
    }
} finally {
    $safeTemp = Assert-EngineChildPath -Path $temp -Root $work
    if (Test-Path -LiteralPath $safeTemp) { Remove-Item -LiteralPath $safeTemp -Recurse -Force }
}
