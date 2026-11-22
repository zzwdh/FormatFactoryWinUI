#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateManifestPath,
    [Parameter(Mandatory)][string]$ChecksPath,
    [Parameter(Mandatory)][string]$OperatorName,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$CleanMachineBaseline
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'ReleaseGate.ps1')
$osVersion = [Environment]::OSVersion.Version.ToString()
$architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
if (-not $IsWindows -or -not $CleanMachineBaseline) { throw 'Record on the actual Windows acceptance device after establishing and documenting a clean baseline.' }
$osProductType = Get-ItemPropertyValue -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\ProductOptions' -Name ProductType
$target = Resolve-ReleaseDeviceTarget -Architecture $architecture -OsVersion $osVersion -ProductType $osProductType
if ([string]::IsNullOrWhiteSpace($OperatorName)) { throw 'Acceptance requires the operator who performed the checks.' }
$candidate = Read-ReleaseCandidate -ManifestPath $CandidateManifestPath -RepositoryRoot $repoRoot
$inputChecks = @(Get-Content -LiteralPath $ChecksPath -Raw | ConvertFrom-Json)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new evidence directory; reports are never overwritten.' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$evidenceDirectory = Join-Path $OutputDirectory 'evidence'
[void][IO.Directory]::CreateDirectory($evidenceDirectory)
$checks = @($inputChecks | ForEach-Object {
    $check = $_
    if ($check.name -notmatch '^[a-z-]+$' -or $check.status -cne 'passed' -or @($check.evidencePaths).Count -eq 0) { throw 'Only performed, successful checks with existing evidence can be recorded.' }
    $evidence = @($check.evidencePaths | ForEach-Object {
        $file = Get-Item -LiteralPath $_
        $name = $check.name + '-' + [Guid]::NewGuid().ToString('N') + $file.Extension
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $evidenceDirectory $name)
        [ordered]@{ path='evidence/' + $name; bytes=$file.Length; sha256=(Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant() }
    })
    [ordered]@{ name=$check.name; status=$check.status; evidence=$evidence }
})
$package = @($candidate.packages | Where-Object architecture -CEQ $architecture)[0]
$report = [ordered]@{
    schemaVersion=2; kind='deviceAcceptance'; synthetic=$false; target=$target.id; architecture=$architecture
    sourceCommit=$candidate.sourceCommit; sourceFingerprint=$candidate.sourceFingerprint; bundleSha256=$candidate.bundle.sha256; packageSha256=$package.sha256
    recordedAt=[DateTimeOffset]::UtcNow.ToString('o'); operator=$OperatorName
    attestation='Performed on this clean device using the identified bundle and package'
    machine=[ordered]@{ name=$env:COMPUTERNAME; osArchitecture=$architecture; osVersion=$osVersion; osProductType=$osProductType; cleanBaseline=$true }
    checks=$checks
}
$path = Join-Path $OutputDirectory "device-$($target.id).json"
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $path -Encoding utf8
[void](Assert-DeviceAcceptanceReport -ReportPath $path -Candidate $candidate -Target $target.id)
Write-Output $path
