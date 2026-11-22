Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../build/Toolchain.ps1')

function Get-ReleaseSourceSnapshot {
    param([string]$RepositoryRoot)
    $commit = & git -C $RepositoryRoot rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'Release provenance requires a Git commit.' }
    $status = @(& git -C $RepositoryRoot status --porcelain --untracked-files=normal)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect source worktree.' }
    $paths = @(& git -C $RepositoryRoot -c core.quotepath=false ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate release sources.' }
    # .NET Framework and modern .NET use different culture sort behavior.
    # Source identity must remain identical on Windows PowerShell and PowerShell 7.
    $orderedPaths = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    foreach ($relativePath in $paths) { [void]$orderedPaths.Add([string]$relativePath) }
    $lines = @($orderedPaths | ForEach-Object {
        $path = Join-Path $RepositoryRoot $_
        if (Test-Path -LiteralPath $path -PathType Leaf) { $_ + ':' + (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
        else { $_ + ':deleted' }
    })
    $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $fingerprint = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose() }
    return [pscustomobject]@{ commit=[string]$commit; fingerprint=$fingerprint; dirty=($status.Count -gt 0) }
}

function Get-ReleaseArtifact {
    param([string]$Path)
    $file = Get-Item -LiteralPath $Path
    return [pscustomobject]@{ fileName=$file.Name; bytes=$file.Length; sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}

function Assert-ReleaseArtifact {
    param($Artifact, [string]$Directory)
    $name = [string]$Artifact.fileName
    if ([IO.Path]::GetFileName($name) -cne $name -or $name -match '[:\\/]' -or $Artifact.sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'Invalid release artifact identity.' }
    $path = Join-Path $Directory $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $Artifact.bytes -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $Artifact.sha256) { throw "Release artifact is missing or changed: $name" }
    return [IO.Path]::GetFullPath($path)
}

function Read-ReleaseCandidate {
    param([string]$ManifestPath, [string]$RepositoryRoot, [switch]$RequireCleanSource)
    $candidate = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    if ($candidate.schemaVersion -ne 1 -or $candidate.kind -cne 'candidate' -or
        $candidate.sourceCommit -notmatch '^[0-9a-f]{40}$' -or $candidate.sourceFingerprint -notmatch '^[0-9a-f]{64}$' -or
        $candidate.packages.Count -ne 2 -or @($candidate.packages.architecture | Sort-Object -Unique).Count -ne 2 -or
        @($candidate.packages | Where-Object { $_.architecture -cnotin @('x64','arm64') }).Count -gt 0) { throw 'Invalid two-architecture candidate manifest.' }
    $version = (Get-Content -LiteralPath (Join-Path $RepositoryRoot 'VERSION') -Raw).Trim()
    $toolHash = (Get-FileHash -LiteralPath (Join-Path $RepositoryRoot 'build/toolchain.lock.json')).Hash
    if ($candidate.version -cne $version -or $candidate.toolchainLockSha256 -ine $toolHash) { throw 'Candidate version/toolchain differs from the current release source.' }
    if ($RequireCleanSource) {
        $source = Get-ReleaseSourceSnapshot -RepositoryRoot $RepositoryRoot
        if ($candidate.dirtyWorktree -isnot [bool] -or $candidate.dirtyWorktree -ne $false -or $source.dirty -or $source.commit -cne $candidate.sourceCommit -or $source.fingerprint -cne $candidate.sourceFingerprint) {
            throw 'Release requires a clean committed source matching the candidate. Commit reviewed changes, rebuild with -CandidateOnly, and repeat acceptance on those exact packages.'
        }
    }
    $directory = Split-Path ([IO.Path]::GetFullPath($ManifestPath)) -Parent
    foreach ($package in $candidate.packages) { $package | Add-Member -NotePropertyName fullPath -NotePropertyValue (Assert-ReleaseArtifact -Artifact $package -Directory $directory) -Force }
    $candidate.bundle | Add-Member -NotePropertyName fullPath -NotePropertyValue (Assert-ReleaseArtifact -Artifact $candidate.bundle -Directory $directory) -Force
    return $candidate
}

function Assert-ReleaseCandidateUnchanged {
    param([string]$ManifestPath, [string]$ManifestSha256, $Candidate, [string]$RepositoryRoot, [switch]$RequireCleanSource)
    # Keep the accepted identities in memory. Re-reading a mutable manifest and
    # accepting its new hashes would approve bytes that were never tested.
    if ($ManifestSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        (Get-FileHash -LiteralPath $ManifestPath -Algorithm SHA256).Hash -ine $ManifestSha256) {
        throw 'Candidate manifest changed after acceptance began; discard these checks and repeat acceptance on the exact new candidate.'
    }
    if ($RequireCleanSource) {
        $source = Get-ReleaseSourceSnapshot -RepositoryRoot $RepositoryRoot
        if ($Candidate.dirtyWorktree -isnot [bool] -or $Candidate.dirtyWorktree -ne $false -or $source.dirty -or
            $source.commit -cne $Candidate.sourceCommit -or $source.fingerprint -cne $Candidate.sourceFingerprint) {
            throw 'Source changed after acceptance began.'
        }
    }
    $directory = Split-Path ([IO.Path]::GetFullPath($ManifestPath)) -Parent
    foreach ($package in $Candidate.packages) { [void](Assert-ReleaseArtifact -Artifact $package -Directory $directory) }
    [void](Assert-ReleaseArtifact -Artifact $Candidate.bundle -Directory $directory)
    if ((Get-FileHash -LiteralPath $ManifestPath -Algorithm SHA256).Hash -ine $ManifestSha256) {
        throw 'Candidate manifest changed during the final artifact checks.'
    }
}

function Assert-ReleaseEvidenceFile {
    param($Evidence, [string]$ReportDirectory)
    $relative = [string]$Evidence.path
    if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[\\/])\.\.([\\/]|$)' -or $relative -match ':') { throw 'Acceptance evidence must be copied inside its report directory.' }
    $path = [IO.Path]::GetFullPath((Join-Path $ReportDirectory $relative))
    $prefix = [IO.Path]::GetFullPath($ReportDirectory).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    if (-not $path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or $Evidence.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        -not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $Evidence.bytes -or
        (Get-FileHash -LiteralPath $path).Hash -ine $Evidence.sha256) { throw "Acceptance evidence is missing/changed: $relative" }
}

function Assert-ReleaseReportUnchanged {
    param([string]$ReportPath, $Artifact)
    $path = Assert-ReleaseArtifact -Artifact $Artifact -Directory (Split-Path ([IO.Path]::GetFullPath($ReportPath)) -Parent)
    if ($path -ine [IO.Path]::GetFullPath($ReportPath)) { throw 'Acceptance report identity changed.' }
}

function Assert-ReleasePerformanceBaseline {
    param([string]$ReportPath, $Candidate)
    $baseline = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
    if ($baseline.source.commit -notmatch '^[0-9a-f]{40}$' -or
        $baseline.source.fingerprint -notmatch '^[0-9a-f]{64}$' -or $baseline.source.dirty -isnot [bool] -or $baseline.source.dirty -ne $false -or
        $baseline.source.commit -ceq $Candidate.sourceCommit) {
        throw 'Formal performance baseline requires a clean, identified earlier source commit; the current candidate cannot be its own baseline.'
    }
    $captured = [DateTimeOffset]::Parse($baseline.capturedAt,[Globalization.CultureInfo]::InvariantCulture)
    if ($captured -ge [DateTimeOffset]::Parse($Candidate.builtAt,[Globalization.CultureInfo]::InvariantCulture)) {
        throw 'Formal performance baseline must have been captured before the candidate was built.'
    }
    & (Join-Path $PSScriptRoot '../tests/WinUiSmoke/Compare-Performance.ps1') -Baseline $ReportPath -ValidateOnly | Out-Null
    if ($baseline.environment.samples -ne 9) { throw 'Formal performance baseline requires nine independent process samples.' }
}

function Get-ReleaseDeviceTargets {
    [pscustomobject]@{ id='windows10-x64'; architecture='x64'; minimumBuild=19045; maximumBuild=19045 }
    [pscustomobject]@{ id='windows10-arm64'; architecture='arm64'; minimumBuild=19045; maximumBuild=19045 }
    [pscustomobject]@{ id='windows11-x64'; architecture='x64'; minimumBuild=22000; maximumBuild=[int]::MaxValue }
    [pscustomobject]@{ id='windows11-arm64'; architecture='arm64'; minimumBuild=22000; maximumBuild=[int]::MaxValue }
}

function Resolve-ReleaseDeviceTarget {
    param([string]$Architecture, [string]$OsVersion, [string]$ProductType)
    $version = $null
    if ($ProductType -cne 'WinNT' -or -not [version]::TryParse($OsVersion, [ref]$version) -or
        $version.Major -ne 10 -or $version.Minor -ne 0) {
        throw 'Device acceptance requires a numeric Windows desktop OS version.'
    }
    $targets = @(Get-ReleaseDeviceTargets | Where-Object {
        $_.architecture -ceq $Architecture -and $version.Build -ge $_.minimumBuild -and $version.Build -le $_.maximumBuild
    })
    if ($targets.Count -ne 1) { throw "Unsupported device acceptance environment: Windows $OsVersion $Architecture." }
    return $targets[0]
}

function Assert-DeviceAcceptanceReport {
    param([string]$ReportPath, $Candidate, [Parameter(Mandatory)][string]$Target)
    $report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
    if ($report.schemaVersion -ne 2 -or $report.kind -cne 'deviceAcceptance' -or $report.target -cne $Target) {
        throw "Device acceptance report must identify the $Target verification environment."
    }
    $actualTarget = Resolve-ReleaseDeviceTarget -Architecture $report.machine.osArchitecture -OsVersion $report.machine.osVersion -ProductType $report.machine.osProductType
    if ($actualTarget.id -cne $Target) { throw "Device acceptance for $Target was recorded on $($actualTarget.id)." }
    $architecture = $actualTarget.architecture
    $package = @($Candidate.packages | Where-Object architecture -CEQ $Architecture)[0]
    if ($report.sourceCommit -cne $Candidate.sourceCommit -or $report.sourceFingerprint -cne $Candidate.sourceFingerprint -or
        $report.bundleSha256 -ine $Candidate.bundle.sha256 -or $report.packageSha256 -ine $package.sha256 -or $report.architecture -cne $Architecture -or
        $report.machine.cleanBaseline -isnot [bool] -or $report.machine.cleanBaseline -ne $true -or
        [string]::IsNullOrWhiteSpace($report.operator) -or $report.attestation -cne 'Performed on this clean device using the identified bundle and package' -or $report.synthetic -isnot [bool] -or $report.synthetic -ne $false) {
        throw "$Target device acceptance must describe the matching bundle, package, source and a real clean device."
    }
    $recorded = [DateTimeOffset]::Parse($report.recordedAt)
    if ($recorded -lt [DateTimeOffset]::Parse($Candidate.builtAt) -or $recorded -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) { throw 'Device acceptance timestamp is outside the candidate lifetime.' }
    $required = @('clean-install','core-conversion','explorer-activation','uninstall','native-tests','engine-corpus')
    if ($Target.StartsWith('windows10-', [StringComparison]::Ordinal)) {
        $required += @('restart-persistence','accessibility')
        if ($Architecture -ceq 'arm64') { $required += 'arm64-engine-fallback' }
    } else { $required += 'upgrade' }
    if ($Architecture -ceq 'x64') { $required += 'office-macro-disabled' }
    foreach ($name in $required) {
        $checks = @($report.checks | Where-Object name -CEQ $name)
        if ($checks.Count -ne 1 -or $checks[0].status -cne 'passed' -or @($checks[0].evidence).Count -eq 0) { throw "$Target acceptance missing a successful $name with evidence." }
        foreach ($evidence in $checks[0].evidence) { Assert-ReleaseEvidenceFile -Evidence $evidence -ReportDirectory (Split-Path ([IO.Path]::GetFullPath($ReportPath)) -Parent) }
    }
    return $report
}

function Assert-AutomatedReleaseReport {
    param([string]$ReportPath, $Candidate)
    $report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
    if ($report.schemaVersion -ne 1 -or $report.kind -cne 'automatedReleaseChecks' -or $report.sourceCommit -cne $Candidate.sourceCommit -or
        $report.sourceFingerprint -cne $Candidate.sourceFingerprint -or $report.synthetic -isnot [bool] -or $report.synthetic -ne $false) { throw 'Automated release report source does not match the candidate.' }
    foreach ($package in $Candidate.packages) {
        $found = @($report.packages | Where-Object architecture -CEQ $package.architecture)
        if ($found.Count -ne 1 -or $found[0].sha256 -ine $package.sha256) { throw 'Automated report refers to different packages.' }
    }
    foreach ($name in @('managed-regression','ui-smoke','startup-readiness','performance-regression','engine-corpus','caj-corpus')) {
        $checks = @($report.checks | Where-Object name -CEQ $name)
        if ($checks.Count -ne 1 -or $checks[0].status -cne 'passed' -or @($checks[0].evidence).Count -eq 0) { throw "Automated release gate missing $name." }
        foreach ($evidence in $checks[0].evidence) { Assert-ReleaseEvidenceFile -Evidence $evidence -ReportDirectory (Split-Path ([IO.Path]::GetFullPath($ReportPath)) -Parent) }
    }
    return $report
}
