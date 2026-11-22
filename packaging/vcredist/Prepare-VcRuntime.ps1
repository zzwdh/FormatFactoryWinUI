[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][string]$CacheDirectory,
    [ValidateSet('x64', 'arm64')][string]$Architecture = 'arm64',
    [switch]$Offline
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\EnginePackaging.ps1')
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$workRoot = Join-Path $repoRoot 'work'
$Destination = Assert-EngineChildPath -Path $Destination -Root $workRoot
$CacheDirectory = [IO.Path]::GetFullPath($CacheDirectory)
[void][IO.Directory]::CreateDirectory($Destination)
$cursor = Get-Item -LiteralPath $Destination
while ($cursor -and $cursor.FullName.Length -ge $repoRoot.Length) {
    if ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Runtime destination contains a reparse point: $($cursor.FullName)" }
    $cursor = $cursor.Parent
}
$lockName = if ($Architecture -eq 'arm64') { 'sources.lock.json' } else { 'sources.x64.lock.json' }
$lockPath = Join-Path $PSScriptRoot $lockName
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
if ($lock.schemaVersion -ne 1 -or $lock.architecture -cne $Architecture) { throw 'Unexpected VC runtime source lock.' }
$machine = @{x64=0x8664; arm64=0xaa64}[$Architecture]
$artifacts = @{runtime=$lock.artifact}
if ($lock.PSObject.Properties.Name -contains 'licenseArtifact') { $artifacts.license = $lock.licenseArtifact }
$archives = @{}
foreach ($name in $artifacts.Keys) {
    $archive = Get-LockedEngineArchive -Artifact $artifacts[$name] -CacheDirectory $CacheDirectory -Offline:$Offline
    $signature = Get-AuthenticodeSignature -LiteralPath $archive
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|, )O=Microsoft Corporation(,|$)') { throw 'VC runtime archive must have a valid Microsoft Authenticode signature.' }
    if ((Get-Item -LiteralPath $archive).VersionInfo.FileVersion -cne $lock.version) { throw 'Unexpected VC runtime version.' }
    $archives[$name] = $archive
}
$expand = Join-Path $env:WINDIR 'System32\expand.exe'
$unpack = Assert-EngineChildPath -Path (Join-Path $workRoot ('vcredist-extract-' + [Guid]::NewGuid().ToString('N'))) -Root $workRoot
[void][IO.Directory]::CreateDirectory($unpack)
function Expand-LockedCabEntry {
    param([string]$Cab, [string]$Entry, [string]$Directory)
    if ($Entry -notmatch '^[a-zA-Z0-9_.]+$') { throw "Invalid CAB entry: $Entry" }
    [void][IO.Directory]::CreateDirectory($Directory)
    $output = & $expand "-F:$Entry" $Cab $Directory 2>&1
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $Directory $Entry) -PathType Leaf)) { throw "CAB extraction failed for $Entry`: $output" }
    return (Join-Path $Directory $Entry)
}
try {
    # The archive hash pins these Burn CAB ranges; the installer is never executed.
    $cabs = @{}
    foreach ($container in $lock.containers) {
        $artifactName = if ($container.PSObject.Properties.Name -contains 'artifact') { [string]$container.artifact } else { 'runtime' }
        if (-not $archives.ContainsKey($artifactName)) { throw "Unknown VC runtime container archive: $artifactName" }
        $bytes = [IO.File]::ReadAllBytes($archives[$artifactName])
        if ($container.offset -lt 0 -or $container.bytes -le 0 -or $container.offset + $container.bytes -gt $bytes.Length) { throw 'CAB range is outside the pinned archive.' }
        $cabPath = Join-Path $unpack ($container.id + '.cab')
        $cabBytes = [byte[]]::new($container.bytes)
        [Array]::Copy($bytes, [int]$container.offset, $cabBytes, 0, [int]$container.bytes)
        if ([Text.Encoding]::ASCII.GetString($cabBytes, 0, 4) -cne 'MSCF') { throw 'Invalid pinned CAB header.' }
        [IO.File]::WriteAllBytes($cabPath, $cabBytes)
        if ((Get-FileHash -LiteralPath $cabPath -Algorithm SHA256).Hash -ine $container.sha256) { throw "CAB hash mismatch: $($container.id)" }
        $cabs[$container.id] = $cabPath
    }
    $runtimeCabEntry = if ($lock.PSObject.Properties.Name -contains 'runtimeCabEntry') { [string]$lock.runtimeCabEntry } else { 'a1' }
    $runtimeCab = Expand-LockedCabEntry -Cab $cabs.payload -Entry $runtimeCabEntry -Directory (Join-Path $unpack 'payload')
    $notices = Join-Path $Destination 'vcredist-notices'
    [void][IO.Directory]::CreateDirectory($notices)
    foreach ($license in $lock.licenses) {
        $source = Expand-LockedCabEntry -Cab $cabs.ux -Entry $license.entry -Directory (Join-Path $unpack 'licenses')
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ine $license.sha256) { throw "VC runtime license hash mismatch: $($license.entry)" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $notices $license.fileName) -Force
    }
    foreach ($runtime in $lock.files) {
        $source = Expand-LockedCabEntry -Cab $runtimeCab -Entry $runtime.entry -Directory (Join-Path $unpack 'runtime')
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ine $runtime.sha256 -or
            (Get-Item -LiteralPath $source).Length -ne $runtime.bytes -or
            (Get-EnginePeMachine $source) -ne $machine) { throw "VC runtime hash, length or $Architecture architecture mismatch: $($runtime.entry)" }
        $dllSignature = Get-AuthenticodeSignature -LiteralPath $source
        if ($dllSignature.Status -ne 'Valid' -or $dllSignature.SignerCertificate.Subject -notmatch '(^|, )O=Microsoft Corporation(,|$)') { throw "Invalid Microsoft DLL signature: $($runtime.entry)" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $Destination $runtime.fileName) -Force
    }
    Copy-Item -LiteralPath $lockPath -Destination $notices -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $notices -Force
    Write-Host "Prepared $($lock.files.Count) verified native $Architecture VC runtime DLLs ($($lock.version))."
} finally {
    $safeUnpack = Assert-EngineChildPath -Path $unpack -Root $workRoot
    if (Test-Path -LiteralPath $safeUnpack) { Remove-Item -LiteralPath $safeUnpack -Recurse -Force }
}
