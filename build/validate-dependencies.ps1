[CmdletBinding()]
param([switch]$Quiet)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
[xml]$central = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Packages.props') -Raw
$versions = @{}
foreach ($package in $central.SelectNodes('//PackageVersion')) {
    if ($versions.ContainsKey($package.Include)) { throw "Duplicate central package: $($package.Include)" }
    $versions[$package.Include] = $package.Version
}
foreach ($folder in 'src', 'tests') {
    foreach ($project in Get-ChildItem -LiteralPath (Join-Path $repoRoot $folder) -Filter '*.csproj' -Recurse -File) {
        if ($project.FullName -match '[\\/](bin|obj)[\\/]') { continue }
        $lockPath = Join-Path $project.DirectoryName 'packages.lock.json'
        if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
            throw "Missing NuGet lock file: $lockPath"
        }
        if ($folder -eq 'src') {
            foreach ($rid in 'win-x64', 'win-arm64') {
                $ridLockPath = Join-Path $project.DirectoryName "packages.$rid.lock.json"
                if (-not (Test-Path -LiteralPath $ridLockPath -PathType Leaf)) {
                    throw "Missing NuGet $rid lock file: $ridLockPath"
                }
                $ridLock = Get-Content -LiteralPath $ridLockPath -Raw | ConvertFrom-Json
                $graphs = @($ridLock.dependencies.PSObject.Properties.Name)
                $ridGraphs = @($graphs | Where-Object { $_ -match '/win-(x64|arm64)$' })
                if ($ridLock.version -ne 2 -or $ridGraphs.Count -ne 1 -or
                    -not $ridGraphs[0].EndsWith("/$rid", [StringComparison]::Ordinal)) {
                    throw "NuGet $rid lock has an incorrect runtime graph: $ridLockPath"
                }
            }
        }
        [xml]$definition = Get-Content -LiteralPath $project.FullName -Raw
        foreach ($reference in $definition.SelectNodes('//PackageReference')) {
            if ($reference.HasAttribute('Version') -or $reference.SelectSingleNode('Version')) {
                throw "Package version must be central: $($project.FullName) / $($reference.Include)"
            }
            if (!$versions.ContainsKey($reference.Include)) { throw "Missing central version: $($reference.Include)" }
        }
    }
}
foreach ($releaseScript in @('build.ps1', 'packaging/build-msix.ps1')) {
    $scriptPath = Join-Path $repoRoot $releaseScript
    if ((Get-Content -LiteralPath $scriptPath -Raw).IndexOf('RestoreLockedMode=true', [StringComparison]::Ordinal) -lt 0) {
        throw "Release build must enforce NuGet locked restore: $scriptPath"
    }
}
if (!$Quiet) { Write-Host 'Central dependency versions validated.' }
