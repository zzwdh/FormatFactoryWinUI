[CmdletBinding()]
param(
    [ValidateSet('x64','arm64','all')][string]$Architecture = 'all',
    [string]$CacheDirectory = (Join-Path $PSScriptRoot '../../work/engine-downloads'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../../work/prepared-archive'),
    [switch]$Offline
)
$ErrorActionPreference = 'Stop'
$packagingRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $packagingRoot 'EnginePackaging.ps1')
$workRoot = [IO.Path]::GetFullPath((Join-Path $packagingRoot '../work'))
$outputRoot = Assert-EngineChildPath -Path $OutputDirectory -Root $workRoot
$cacheRoot = [IO.Path]::GetFullPath($CacheDirectory)
$manifest = Get-Content -LiteralPath (Join-Path $packagingRoot 'engine-sources.json') -Raw | ConvertFrom-Json
$engine = @($manifest.engines | Where-Object id -CEQ '7zip')
if ($engine.Count -ne 1) { throw 'The source manifest must contain exactly one 7zip engine.' }
$engine = $engine[0]
$fingerprint = Get-EnginePreparationFingerprint -PackagingDirectory $packagingRoot
$architectures = if ($Architecture -eq 'all') { @('x64','arm64') } else { @($Architecture) }
foreach ($arch in $architectures) {
    $scratch = Join-Path $workRoot ('archive-preparation-' + [Guid]::NewGuid().ToString('N'))
    $destination = Join-Path $scratch 'Engines/7zip'
    try {
        $artifact = $engine.artifacts.$arch
        $archivePath = Get-LockedEngineArchive -Artifact $artifact -CacheDirectory $cacheRoot -Offline:$Offline
        Expand-SevenZipEngine -Engine $engine -ArchivePath $archivePath -Destination $destination -ExtractDirectory (Join-Path $scratch 'extract') -CacheDirectory $cacheRoot -Offline:$Offline
        Assert-EngineFiles -Directory $destination -RequiredFiles @($engine.requiredFiles) -PeFiles @($engine.peFiles) -RuntimeArchitecture $artifact.runtimeArchitecture
        $archRoot = Join-Path $outputRoot $arch
        Reset-EngineDirectory -Path $archRoot -Root $outputRoot
        $engineRoot = Join-Path $archRoot 'Engines'
        [void][IO.Directory]::CreateDirectory($engineRoot)
        Move-Item -LiteralPath $destination -Destination (Join-Path $engineRoot '7zip')
        $receipt = [ordered]@{id=$engine.id; version=$engine.version; payloadMode=$engine.payloadMode; runtimeArchitecture=$artifact.runtimeArchitecture; sourceUrl=$artifact.url; archiveSha256=$artifact.sha256; requiredFiles=@($engine.requiredFiles); peFiles=@($engine.peFiles)}
        Write-EngineReceipt -EngineRoot $engineRoot -Architecture $arch -Fingerprint $fingerprint -Engines @($receipt)
        [void](Assert-EngineReceipt -EngineRoot $engineRoot -Architecture $arch -Fingerprint $fingerprint)
        Write-Output "Prepared and verified $arch archive engine: $(Join-Path $engineRoot '7zip/7z.exe')"
    } finally {
        $safeScratch = Assert-EngineChildPath -Path $scratch -Root $workRoot
        if (Test-Path -LiteralPath $safeScratch) { Remove-Item -LiteralPath $safeScratch -Recurse -Force }
    }
}
