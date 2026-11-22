#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateManifestPath,
    [Parameter(Mandatory)][string]$PerformanceBaselinePath,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'ReleaseGate.ps1')
. (Join-Path $PSScriptRoot 'EnginePackaging.ps1')
$candidate = Read-ReleaseCandidate -ManifestPath $CandidateManifestPath -RepositoryRoot $repoRoot -RequireCleanSource
[void](Assert-ReleaseRuntimeSupport -RepositoryRoot $repoRoot)
if (-not (Test-Path -LiteralPath $PerformanceBaselinePath -PathType Leaf)) { throw 'Formal release requires a comparable performance baseline; do not create it from the current candidate.' }
$performanceBaselineArtifact = Get-ReleaseArtifact -Path $PerformanceBaselinePath
Assert-ReleasePerformanceBaseline -ReportPath $PerformanceBaselinePath -Candidate $candidate
Assert-ReleaseReportUnchanged -ReportPath $PerformanceBaselinePath -Artifact $performanceBaselineArtifact
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot ('work/release-checks/' + [Guid]::NewGuid().ToString('N')) }
$OutputDirectory = Assert-EngineChildPath -Path $OutputDirectory -Root (Join-Path $repoRoot 'work')
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new release-check directory; previous reports must not masquerade as current checks.' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$checks = [Collections.Generic.List[object]]::new()
function Add-Check([string]$Name, [string[]]$Paths) {
    $evidence = @($Paths | ForEach-Object {
        $path = $_
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing generated $Name report: $path" }
        $relative = [IO.Path]::GetRelativePath($OutputDirectory,[IO.Path]::GetFullPath($path)).Replace('\','/')
        [ordered]@{ path=$relative; bytes=(Get-Item -LiteralPath $path).Length; sha256=(Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() }
    })
    $checks.Add([ordered]@{ name=$Name; status='passed'; evidence=$evidence })
}

$managedDirectory = Join-Path $OutputDirectory 'managed'
$managedArguments = @(
    'test', 'tests/FormatFactoryWinUI.Tests/FormatFactoryWinUI.Tests.csproj', '-c', 'Release',
    '-p:RestoreLockedMode=true', '--logger', 'trx;LogFileName=regression.trx', '--results-directory', $managedDirectory
)
Invoke-LockedDotnet -RepositoryRoot $repoRoot -Arguments $managedArguments *> (Join-Path $OutputDirectory 'managed.log')
if ($LASTEXITCODE -ne 0) { throw "Managed release regression failed; see $OutputDirectory/managed.log" }
$trxPath = Join-Path $managedDirectory 'regression.trx'
[xml]$trx = Get-Content -LiteralPath $trxPath -Raw
$counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
if ($null -eq $counters -or [int]$counters.total -le 0 -or [int]$counters.failed -ne 0 -or [int]$counters.passed -ne [int]$counters.total) { throw 'Managed release gate requires a nonempty regression run with no failed/skipped tests.' }
Add-Check 'managed-regression' $trxPath

$runName = 'release-' + [Guid]::NewGuid().ToString('N')
& (Join-Path $repoRoot 'tests/WinUiSmoke/Run.ps1') -RunName $runName -TimeoutSeconds 180 *> (Join-Path $OutputDirectory 'ui-smoke.log')
$uiResult = Join-Path $repoRoot "work/ui-regression/$runName/results.json"
$ui = Get-Content -LiteralPath $uiResult -Raw | ConvertFrom-Json
if ($ui.failures -ne 0 -or @($ui.results).Count -eq 0 -or @($ui.results | Where-Object passed -NE $true).Count -gt 0) { throw 'WinUI release smoke did not pass all checks.' }
$uiCopy = Join-Path $OutputDirectory 'ui-results.json'
Copy-Item -LiteralPath $uiResult -Destination $uiCopy
Add-Check 'ui-smoke' $uiCopy

$startupRun = 'release-startup-' + [Guid]::NewGuid().ToString('N')
& (Join-Path $repoRoot 'tests/WinUiSmoke/Run.ps1') -RunName $startupRun -Startup -TimeoutSeconds 180 *> (Join-Path $OutputDirectory 'startup.log')
$startupResult = Join-Path $repoRoot "work/ui-regression/$startupRun/startup.json"
$startupReports = @(Get-Content -LiteralPath $startupResult -Raw | ConvertFrom-Json)
if ($startupReports.Count -ne 3 -or @($startupReports.persistedCount | Sort-Object -Unique).Count -ne 3) { throw 'Startup gate requires 0, 100 and 1000 persisted task scenarios.' }
foreach ($count in @(0,100,1000)) {
    $measurement = @($startupReports | Where-Object persistedCount -EQ $count)
    if ($measurement.Count -ne 1 -or $measurement[0].restoredCount -ne $count) { throw 'Startup gate lost restored tasks or a scenario.' }
    foreach ($stage in @('first-interactive','activation-ready','first-restored-task','all-restored')) {
        if ($stage -cnotin $measurement[0].milestonesMilliseconds.PSObject.Properties.Name -or [double]$measurement[0].milestonesMilliseconds.$stage -lt 0) { throw "Startup gate missing a valid $stage measurement." }
    }
}
$startupCopy = Join-Path $OutputDirectory 'startup.json'
Copy-Item -LiteralPath $startupResult -Destination $startupCopy
Add-Check 'startup-readiness' @($startupCopy,(Join-Path $OutputDirectory 'startup.log'))

$performanceRun = 'release-performance-' + [Guid]::NewGuid().ToString('N')
& (Join-Path $repoRoot 'tests/WinUiSmoke/Run.ps1') -RunName $performanceRun -Performance -TimeoutSeconds 600 *> (Join-Path $OutputDirectory 'performance.log')
$performanceCopy = Join-Path $OutputDirectory 'performance.json'
$baselineCopy = Join-Path $OutputDirectory 'performance-baseline.json'
Copy-Item -LiteralPath (Join-Path $repoRoot "work/ui-regression/$performanceRun/performance.json") -Destination $performanceCopy
Copy-Item -LiteralPath $PerformanceBaselinePath -Destination $baselineCopy
Assert-ReleaseReportUnchanged -ReportPath $PerformanceBaselinePath -Artifact $performanceBaselineArtifact
if ((Get-FileHash -LiteralPath $baselineCopy).Hash -ine $performanceBaselineArtifact.sha256) { throw 'Performance baseline changed during evidence copying.' }
Assert-ReleasePerformanceBaseline -ReportPath $baselineCopy -Candidate $candidate
$comparisonLog = Join-Path $OutputDirectory 'performance-comparison.log'
& (Join-Path $repoRoot 'tests/WinUiSmoke/Compare-Performance.ps1') -Baseline $baselineCopy -Candidate $performanceCopy -MaximumRegressionPercent 10 *> $comparisonLog
Add-Check 'performance-regression' @($performanceCopy,$baselineCopy,$comparisonLog)

$hostArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
if ($hostArchitecture -cnotin @('x64','arm64')) { throw 'Release checks require an x64 or ARM64 Windows host.' }
$package = @($candidate.packages | Where-Object architecture -CEQ $hostArchitecture)[0]
$engines = Join-Path $repoRoot "work/prepared-engines/$hostArchitecture/Engines"
# Tests use prepared engines only after proving they are byte-identical to this package.
Assert-MsixEnginePayload -PackagePath $package.fullPath -EngineRoot $engines -Architecture $hostArchitecture
$engineOutput = Join-Path $OutputDirectory 'engines'
& (Join-Path $PSScriptRoot 'smoke-engines.ps1') -EngineDirectory $engines -OutputDirectory $engineOutput *> (Join-Path $OutputDirectory 'engines.log')
$engineResult = Join-Path $engineOutput 'latest-results.json'
$engineReport = Get-Content -LiteralPath $engineResult -Raw | ConvertFrom-Json
if (@($engineReport.results).Count -eq 0 -or @($engineReport.results | Where-Object status -CNE 'passed').Count -gt 0) { throw 'Packaged engine corpus failed.' }
Add-Check 'engine-corpus' $engineResult
$python = Join-Path $engines 'caj2pdf/python.exe'
$cajLog = Join-Path $OutputDirectory 'caj-corpus.log'
& $python -I -B (Join-Path $PSScriptRoot 'caj/smoke-test.py') --engine (Join-Path $engines 'caj2pdf') *> $cajLog
if ($LASTEXITCODE -ne 0) { throw "CAJ release corpus failed; see $cajLog" }
Add-Check 'caj-corpus' $cajLog
$after = Get-ReleaseSourceSnapshot -RepositoryRoot $repoRoot
if ($after.dirty -or $after.commit -cne $candidate.sourceCommit -or $after.fingerprint -cne $candidate.sourceFingerprint) { throw 'Source changed during release checks; discard these results.' }
$report = [ordered]@{ schemaVersion=1; kind='automatedReleaseChecks'; synthetic=$false; sourceCommit=$candidate.sourceCommit; sourceFingerprint=$candidate.sourceFingerprint; completedAt=[DateTimeOffset]::UtcNow.ToString('o'); hostArchitecture=$hostArchitecture; packages=@($candidate.packages | Select-Object architecture,sha256); checks=$checks.ToArray() }
$reportPath = Join-Path $OutputDirectory 'release-checks.json'
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding utf8
[void](Assert-AutomatedReleaseReport -ReportPath $reportPath -Candidate $candidate)
Write-Output $reportPath
