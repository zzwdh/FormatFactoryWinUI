[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $repo 'packaging/ReleaseGate.ps1')
$taskRoot = Join-Path $repo ('work/release-gate-tests-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($taskRoot)
$script:passed = 0
function Test-Case([string]$Name, [scriptblock]$Action) { & $Action; $script:passed++; Write-Output "PASS $Name" }
function Reject([scriptblock]$Action) { $rejected=$false; try { & $Action | Out-Null } catch { $rejected=$true }; if (-not $rejected) { throw 'Gate accepted invalid evidence.' } }
function Write-Json([string]$Path,$Value) { $Value | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $Path -Encoding utf8 }
function Evidence([string]$Directory,[string]$Name) {
    $path = Join-Path $Directory $Name
    'SYNTHETIC TEST EVIDENCE. Never use this fixture as real device acceptance.' | Set-Content -LiteralPath $path -Encoding utf8
    return [pscustomobject]@{path=$Name;bytes=(Get-Item -LiteralPath $path).Length;sha256=(Get-FileHash -LiteralPath $path).Hash}
}

Test-Case 'Locked compiler, SDK tools and miniz match the installed baseline' {
    $lock = Get-ToolchainLock -RepositoryRoot $repo
    $vc = Resolve-LockedVcTools -RepositoryRoot $repo -Lock $lock
    [void](Assert-LockedToolVersion -Path (Join-Path $vc 'bin/Hostx64/x64/cl.exe') -ExpectedVersion $lock.compilerFileVersion)
    [void](Get-LockedWindowsSdkTool -RepositoryRoot $repo -Name makeappx.exe)
    [void](Get-LockedWindowsSdkTool -RepositoryRoot $repo -Name signtool.exe)
    [void](Assert-VendoredMiniz -VendorDirectory (Join-Path $repo 'src/FormatFactoryShellExtension.Native/vendor/miniz'))
    Reject { Assert-LockedToolVersion -Path (Join-Path $vc 'bin/Hostx64/x64/cl.exe') -ExpectedVersion '0.0.0.0' }
}
Test-Case 'Changed vendored source fails before compilation' {
    $vendor = Join-Path $taskRoot 'miniz'
    [void][IO.Directory]::CreateDirectory($vendor)
    Copy-Item -Path (Join-Path $repo 'src/FormatFactoryShellExtension.Native/vendor/miniz/*') -Destination $vendor
    [void](Assert-VendoredMiniz -VendorDirectory $vendor)
    Add-Content -LiteralPath (Join-Path $vendor 'miniz.c') -Value '/* synthetic corruption */'
    Reject { Assert-VendoredMiniz -VendorDirectory $vendor }
}
Test-Case 'Locked runtime cutoff follows Asia/Shanghai midnight and includes its final supported day' {
    $lock = Get-ToolchainLock -RepositoryRoot $repo
    if ($lock.supportTimeZone -cne 'Asia/Shanghai') { throw 'Runtime support must follow the Asia/Shanghai calendar.' }
    $lastSupportedDay = [DateTimeOffset]::ParseExact(($lock.dotnetSupportedUntil + 'T00:00:00+08:00'),
        "yyyy-MM-dd'T'HH:mm:sszzz",[Globalization.CultureInfo]::InvariantCulture)
    $firstUnsupportedInstant = $lastSupportedDay.AddDays(1).ToUniversalTime()
    [void](Assert-ReleaseRuntimeSupport -RepositoryRoot $repo -At $lastSupportedDay.ToUniversalTime())
    [void](Assert-ReleaseRuntimeSupport -RepositoryRoot $repo -At $firstUnsupportedInstant.AddTicks(-1))
    Reject { Assert-ReleaseRuntimeSupport -RepositoryRoot $repo -At $firstUnsupportedInstant }
}
Test-Case 'Expired runtime reports the locked release and support cutoff' {
    $lock = Get-ToolchainLock -RepositoryRoot $repo
    $expiredAt = [DateTimeOffset]::ParseExact(($lock.dotnetSupportedUntil + 'T00:00:00+08:00'),
        "yyyy-MM-dd'T'HH:mm:sszzz",[Globalization.CultureInfo]::InvariantCulture).AddDays(2)
    $message = $null
    try { [void](Assert-ReleaseRuntimeSupport -RepositoryRoot $repo -At $expiredAt) }
    catch { $message = $_.Exception.Message }
    if (-not $message -or -not $message.Contains($lock.dotnetRelease) -or
        -not $message.Contains($lock.dotnetSupportedUntil) -or -not $message.Contains('Asia/Shanghai')) {
        throw 'Expired runtime must report the locked release, support cutoff and calendar.'
    }
}
$source = Get-ReleaseSourceSnapshot -RepositoryRoot $repo
$packages = @()
foreach ($arch in 'x64','arm64') {
    $path = Join-Path $taskRoot "synthetic-$arch.msix"
    "SYNTHETIC PACKAGE $arch. Not an installable application." | Set-Content -LiteralPath $path
    $artifact = Get-ReleaseArtifact -Path $path
    $artifact | Add-Member -NotePropertyName architecture -NotePropertyValue $arch
    $packages += $artifact
}
$bundle = Join-Path $taskRoot 'synthetic.msixbundle'
'SYNTHETIC BUNDLE. Not an installable application.' | Set-Content -LiteralPath $bundle
$candidate = [pscustomobject]@{schemaVersion=1;kind='candidate';sourceCommit=$source.commit;sourceFingerprint=$source.fingerprint;dirtyWorktree=$true;version=(Get-Content -LiteralPath (Join-Path $repo 'VERSION') -Raw).Trim();toolchainLockSha256=(Get-FileHash -LiteralPath (Join-Path $repo 'build/toolchain.lock.json')).Hash;builtAt=[DateTimeOffset]::UtcNow.AddMinutes(-1).ToString('o');packages=$packages;bundle=(Get-ReleaseArtifact -Path $bundle)}
$manifest = Join-Path $taskRoot 'synthetic.candidate.json'
Write-Json $manifest $candidate
Test-Case 'Candidate inspection succeeds; dirty source cannot be promoted' {
    [void](Read-ReleaseCandidate -ManifestPath $manifest -RepositoryRoot $repo)
    Reject { Read-ReleaseCandidate -ManifestPath $manifest -RepositoryRoot $repo -RequireCleanSource }
}
Test-Case 'Package byte changes invalidate the candidate' {
    $path = Join-Path $taskRoot 'synthetic-x64.msix'
    $bytes = [IO.File]::ReadAllBytes($path)
    try { Add-Content -LiteralPath $path -Value 'changed'; Reject { Read-ReleaseCandidate -ManifestPath $manifest -RepositoryRoot $repo } }
    finally { [IO.File]::WriteAllBytes($path,$bytes) }
}
Test-Case 'Final promotion rechecks the original package identities' {
    $accepted = Read-ReleaseCandidate -ManifestPath $manifest -RepositoryRoot $repo
    $manifestHash = (Get-FileHash -LiteralPath $manifest).Hash
    Assert-ReleaseCandidateUnchanged -ManifestPath $manifest -ManifestSha256 $manifestHash -Candidate $accepted -RepositoryRoot $repo
    $path = Join-Path $taskRoot 'synthetic-x64.msix'
    $bytes = [IO.File]::ReadAllBytes($path)
    try {
        Add-Content -LiteralPath $path -Value 'replacement after automated checks'
        Reject { Assert-ReleaseCandidateUnchanged -ManifestPath $manifest -ManifestSha256 $manifestHash -Candidate $accepted -RepositoryRoot $repo }
    } finally { [IO.File]::WriteAllBytes($path,$bytes) }
}
Test-Case 'A new same-source candidate cannot replace the accepted manifest and packages' {
    $accepted = Read-ReleaseCandidate -ManifestPath $manifest -RepositoryRoot $repo
    $manifestBytes = [IO.File]::ReadAllBytes($manifest)
    $manifestHash = (Get-FileHash -LiteralPath $manifest).Hash
    $replacement = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
    $replacementPath = Join-Path $taskRoot 'replacement-synthetic-x64.msix'
    'SYNTHETIC replacement package from the same source commit.' | Set-Content -LiteralPath $replacementPath
    $replacementArtifact = Get-ReleaseArtifact -Path $replacementPath
    $replacementArtifact | Add-Member -NotePropertyName architecture -NotePropertyValue x64
    $replacement.packages[0] = $replacementArtifact
    try {
        Write-Json $manifest $replacement
        # Ordinary inspection would accept the replacement: only the remembered
        # acceptance identity distinguishes it from the candidate already tested.
        [void](Read-ReleaseCandidate -ManifestPath $manifest -RepositoryRoot $repo)
        Reject { Assert-ReleaseCandidateUnchanged -ManifestPath $manifest -ManifestSha256 $manifestHash -Candidate $accepted -RepositoryRoot $repo }
    } finally { [IO.File]::WriteAllBytes($manifest,$manifestBytes) }
}
function New-DeviceFixture([string]$Target, [string]$Architecture, [string]$OsVersion, [string[]]$AdditionalChecks) {
    $deviceChecks = @('clean-install','core-conversion','explorer-activation','uninstall','native-tests','engine-corpus') + $AdditionalChecks
    $package = @($packages | Where-Object architecture -CEQ $Architecture)[0]
    [pscustomobject]@{
        schemaVersion=2; kind='deviceAcceptance'; synthetic=$false; target=$Target; architecture=$Architecture
        sourceCommit=$candidate.sourceCommit; sourceFingerprint=$candidate.sourceFingerprint; bundleSha256=$candidate.bundle.sha256; packageSha256=$package.sha256
        recordedAt=[DateTimeOffset]::UtcNow.ToString('o'); operator='SYNTHETIC FIXTURE OPERATOR'
        attestation='Performed on this clean device using the identified bundle and package'
        machine=[pscustomobject]@{osArchitecture=$Architecture;osVersion=$OsVersion;osProductType='WinNT';cleanBaseline=$true}
        checks=@($deviceChecks | ForEach-Object { [pscustomobject]@{name=$_;status='passed';evidence=@(Evidence $taskRoot ($_+'.log'))} })
    }
}
$fixtureReport = New-DeviceFixture 'windows11-x64' 'x64' '10.0.22000.0' @('upgrade','office-macro-disabled')
$reportPath = Join-Path $taskRoot 'fixture-device-windows11-x64.json'
Write-Json $reportPath $fixtureReport
Test-Case 'Evidence schema accepts a matching fixture, without approving an upload' { [void](Assert-DeviceAcceptanceReport -ReportPath $reportPath -Candidate $candidate -Target windows11-x64) }
Test-Case 'Each OS and architecture needs a separate matching report' {
    $targetIds = @(Get-ReleaseDeviceTargets | ForEach-Object id)
    if (($targetIds -join ',') -cne 'windows10-x64,windows10-arm64,windows11-x64,windows11-arm64') { throw 'Unexpected release acceptance targets.' }
    $win10Report = Join-Path $taskRoot 'fixture-device-windows10-x64.json'
    $win10ArmReport = Join-Path $taskRoot 'fixture-device-windows10-arm64.json'
    $armReport = Join-Path $taskRoot 'fixture-device-windows11-arm64.json'
    Reject { Assert-DeviceAcceptanceReport -ReportPath $win10Report -Candidate $candidate -Target windows10-x64 }
    $win10Fixture = New-DeviceFixture 'windows10-x64' 'x64' '10.0.19045.5965' @('restart-persistence','accessibility','office-macro-disabled')
    $win10ArmFixture = New-DeviceFixture 'windows10-arm64' 'arm64' '10.0.19045.5965' @('restart-persistence','accessibility','arm64-engine-fallback')
    Write-Json $win10Report $win10Fixture
    Write-Json $win10ArmReport $win10ArmFixture
    Write-Json $armReport (New-DeviceFixture 'windows11-arm64' 'arm64' '10.0.26100.0' @('upgrade'))
    [void](Assert-DeviceAcceptanceReport -ReportPath $win10Report -Candidate $candidate -Target windows10-x64)
    [void](Assert-DeviceAcceptanceReport -ReportPath $win10ArmReport -Candidate $candidate -Target windows10-arm64)
    [void](Assert-DeviceAcceptanceReport -ReportPath $armReport -Candidate $candidate -Target windows11-arm64)
    Reject { Assert-DeviceAcceptanceReport -ReportPath $reportPath -Candidate $candidate -Target windows10-x64 }
    Reject { Assert-DeviceAcceptanceReport -ReportPath $win10Report -Candidate $candidate -Target windows11-x64 }
    Reject { Assert-DeviceAcceptanceReport -ReportPath $win10Report -Candidate $candidate -Target windows10-arm64 }
    Reject { Assert-DeviceAcceptanceReport -ReportPath $win10ArmReport -Candidate $candidate -Target windows11-arm64 }

    $win10ArmFixture.checks = @($win10ArmFixture.checks | Where-Object name -CNE 'arm64-engine-fallback')
    Write-Json $win10ArmReport $win10ArmFixture
    Reject { Assert-DeviceAcceptanceReport -ReportPath $win10ArmReport -Candidate $candidate -Target windows10-arm64 }

    $originalChecks = $win10Fixture.checks
    foreach ($name in 'restart-persistence','accessibility') {
        $win10Fixture.checks = @($originalChecks | Where-Object name -CNE $name)
        Write-Json $win10Report $win10Fixture
        Reject { Assert-DeviceAcceptanceReport -ReportPath $win10Report -Candidate $candidate -Target windows10-x64 }
    }
    $win10Fixture.checks = $originalChecks
    foreach ($version in '10.0.19044.0','10.0.22000.0','Windows 10') {
        $win10Fixture.machine.osVersion = $version
        Write-Json $win10Report $win10Fixture
        Reject { Assert-DeviceAcceptanceReport -ReportPath $win10Report -Candidate $candidate -Target windows10-x64 }
    }
    $win10Fixture.machine.osVersion = '10.0.19045.5965'
    $win10Fixture.machine.osProductType = 'ServerNT'
    Write-Json $win10Report $win10Fixture
    Reject { Assert-DeviceAcceptanceReport -ReportPath $win10Report -Candidate $candidate -Target windows10-x64 }
}
Test-Case 'Office macro execution evidence cannot be replaced by script inspection' {
    $originalChecks = $fixtureReport.checks
    try {
        $fixtureReport.checks = @($originalChecks | Where-Object name -NE 'office-macro-disabled')
        Write-Json $reportPath $fixtureReport
        Reject { Assert-DeviceAcceptanceReport -ReportPath $reportPath -Candidate $candidate -Target windows11-x64 }
    }
    finally { $fixtureReport.checks = $originalChecks; Write-Json $reportPath $fixtureReport }
}
Test-Case 'Final promotion refuses replacement device reports and changed Office evidence' {
    $acceptedReportArtifact = Get-ReleaseArtifact -Path $reportPath
    $reportBytes = [IO.File]::ReadAllBytes($reportPath)
    try {
        $fixtureReport.operator = 'A DIFFERENT SYNTHETIC OPERATOR'
        Write-Json $reportPath $fixtureReport
        [void](Assert-DeviceAcceptanceReport -ReportPath $reportPath -Candidate $candidate -Target windows11-x64)
        Reject { Assert-ReleaseReportUnchanged -ReportPath $reportPath -Artifact $acceptedReportArtifact }
    } finally {
        $fixtureReport.operator = 'SYNTHETIC FIXTURE OPERATOR'
        [IO.File]::WriteAllBytes($reportPath,$reportBytes)
    }
    $officePath = Join-Path $taskRoot 'office-macro-disabled.log'
    $officeBytes = [IO.File]::ReadAllBytes($officePath)
    try {
        Add-Content -LiteralPath $officePath -Value 'changed after device acceptance'
        Assert-ReleaseReportUnchanged -ReportPath $reportPath -Artifact $acceptedReportArtifact
        Reject { Assert-DeviceAcceptanceReport -ReportPath $reportPath -Candidate $candidate -Target windows11-x64 }
    } finally { [IO.File]::WriteAllBytes($officePath,$officeBytes) }
}
Test-Case 'Missing ARM64 report cannot pass' { Reject { Assert-DeviceAcceptanceReport -ReportPath (Join-Path $taskRoot 'missing-arm64.json') -Candidate $candidate -Target windows11-arm64 } }
Test-Case 'Synthetic marker is rejected by real acceptance gate' {
    $fixtureReport.synthetic=$true; Write-Json $reportPath $fixtureReport
    Reject { Assert-DeviceAcceptanceReport -ReportPath $reportPath -Candidate $candidate -Target windows11-x64 }
    $fixtureReport.synthetic=$false; Write-Json $reportPath $fixtureReport
}
Test-Case 'A report for the wrong architecture or source is rejected' {
    Reject { Assert-DeviceAcceptanceReport -ReportPath $reportPath -Candidate $candidate -Target windows11-arm64 }
    $fixtureReport.sourceCommit='0'*40; Write-Json $reportPath $fixtureReport
    Reject { Assert-DeviceAcceptanceReport -ReportPath $reportPath -Candidate $candidate -Target windows11-x64 }
    $fixtureReport.sourceCommit=$candidate.sourceCommit; Write-Json $reportPath $fixtureReport
}
Test-Case 'Device acceptance applies to the exact tested bundle' {
    $originalBundleHash = $fixtureReport.bundleSha256
    try {
        $fixtureReport.bundleSha256 = '0' * 64
        Write-Json $reportPath $fixtureReport
        Reject { Assert-DeviceAcceptanceReport -ReportPath $reportPath -Candidate $candidate -Target windows11-x64 }
    } finally { $fixtureReport.bundleSha256 = $originalBundleHash; Write-Json $reportPath $fixtureReport }
}
Test-Case 'Failed scenarios and modified evidence are rejected' {
    $fixtureReport.checks[0].status='failed'; Write-Json $reportPath $fixtureReport
    Reject { Assert-DeviceAcceptanceReport -ReportPath $reportPath -Candidate $candidate -Target windows11-x64 }
    $fixtureReport.checks[0].status='passed'; Write-Json $reportPath $fixtureReport
    Add-Content -LiteralPath (Join-Path $taskRoot 'clean-install.log') -Value 'changed'
    Reject { Assert-DeviceAcceptanceReport -ReportPath $reportPath -Candidate $candidate -Target windows11-x64 }
}
Test-Case 'Automated gate requires every suite and the exact package hashes' {
    $auto = [pscustomobject]@{schemaVersion=1;kind='automatedReleaseChecks';synthetic=$false;sourceCommit=$candidate.sourceCommit;sourceFingerprint=$candidate.sourceFingerprint;packages=@($packages | Select-Object architecture,sha256);checks=@(@('managed-regression','ui-smoke','startup-readiness','performance-regression','engine-corpus','caj-corpus') | ForEach-Object { [pscustomobject]@{name=$_;status='passed';evidence=@(Evidence $taskRoot ($_+'.log'))} })}
    $autoPath = Join-Path $taskRoot 'fixture-auto.json'
    Write-Json $autoPath $auto
    [void](Assert-AutomatedReleaseReport -ReportPath $autoPath -Candidate $candidate)
    $autoArtifact = Get-ReleaseArtifact -Path $autoPath
    $auto | Add-Member -NotePropertyName completedAt -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('o'))
    Write-Json $autoPath $auto
    [void](Assert-AutomatedReleaseReport -ReportPath $autoPath -Candidate $candidate)
    Reject { Assert-ReleaseReportUnchanged -ReportPath $autoPath -Artifact $autoArtifact }
    $auto.checks=$auto.checks | Where-Object name -NE 'engine-corpus'; Write-Json $autoPath $auto
    Reject { Assert-AutomatedReleaseReport -ReportPath $autoPath -Candidate $candidate }
}
function New-PerformanceFixture {
    param([int]$SchemaVersion = 1)
    $metricNames = @('ui_construction','first_settings_initialization','large_docx_to_epub',
        'queue_1000_filter_roundtrip','queue_10000_filter_roundtrip')
    if ($SchemaVersion -eq 2) {
        $metricNames += @('queue_1000_waiting_admission','queue_10000_waiting_admission',
            'queue_1000_failed_admission','queue_10000_failed_admission','queue_10000_mixed_filter_roundtrip')
    }
    [pscustomobject]@{
        schemaVersion=$SchemaVersion
        environment=[pscustomobject]@{machine='SYNTHETIC';os='SYNTHETIC';runtime='SYNTHETIC';architecture='X64';processors=1;processor='SYNTHETIC';winUiAssembly='SYNTHETIC';width=1280;height=900;scale=1;language='en-US';theme='Light';highContrast=$false;animation=$false;configuration='Release';samples=9}
        documentFixture=[pscustomobject]@{paragraphs=2500;charactersPerParagraph=1536;inputBytes=1000;engine='SYNTHETIC'}
        metrics=@($metricNames | ForEach-Object { [pscustomobject]@{Name=$_;P95Milliseconds=9;P95PrivateBytes=1000000;MaxRealizedContainers=0;SamplesMilliseconds=@(1..9)} })
        source=[pscustomobject]@{commit=('f'*40);fingerprint=('e'*64);dirty=$false}
        capturedAt=[DateTimeOffset]::Parse($candidate.builtAt).AddMinutes(-1).ToString('o')
    }
}
$performancePath = Join-Path $taskRoot 'synthetic-performance.json'
$comparePerformance = Join-Path $repo 'tests/WinUiSmoke/Compare-Performance.ps1'
Test-Case 'Complete historical performance remains comparable without source metadata' {
    $performance = New-PerformanceFixture
    $performance.PSObject.Properties.Remove('source')
    $performance.PSObject.Properties.Remove('capturedAt')
    Write-Json $performancePath $performance
    & $comparePerformance -Baseline $performancePath -Candidate $performancePath | Out-Null
}
Test-Case 'Default Windows PowerShell can fingerprint sources and validate complete performance reports' {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe'
    if (-not (Test-Path -LiteralPath $windowsPowerShell -PathType Leaf)) { throw 'Default Windows PowerShell regression requires the Windows interpreter.' }
    $compatibilityScript = Join-Path $taskRoot 'windows-powershell-compatibility.ps1'
    @'
param([string]$RepositoryRoot, [string]$PerformancePath)
$ErrorActionPreference = 'Stop'
. (Join-Path $RepositoryRoot 'packaging/ReleaseGate.ps1')
$snapshot = Get-ReleaseSourceSnapshot -RepositoryRoot $RepositoryRoot
if ($snapshot.commit -notmatch '^[0-9a-f]{40}$' -or $snapshot.fingerprint -notmatch '^[0-9a-f]{64}$') { throw 'Invalid source fingerprint on Windows PowerShell.' }
& (Join-Path $RepositoryRoot 'tests/WinUiSmoke/Compare-Performance.ps1') -Baseline $PerformancePath -ValidateOnly
$snapshot | ConvertTo-Json -Compress
'@ | Set-Content -LiteralPath $compatibilityScript -Encoding utf8
    $legacyOutput = @(& $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $compatibilityScript -RepositoryRoot $repo -PerformancePath $performancePath)
    if ($LASTEXITCODE -ne 0) { throw 'Windows PowerShell source/performance compatibility failed.' }
    $legacySnapshot = $legacyOutput[-1] | ConvertFrom-Json
    $currentSnapshot = Get-ReleaseSourceSnapshot -RepositoryRoot $repo
    if ($legacySnapshot.commit -cne $currentSnapshot.commit -or $legacySnapshot.fingerprint -cne $currentSnapshot.fingerprint) { throw 'Windows PowerShell source fingerprint differs from PowerShell 7.' }
}
Test-Case 'Expanded performance reports validate and compare without silently dropping new measurements' {
    $performance = New-PerformanceFixture -SchemaVersion 2
    Write-Json $performancePath $performance
    & $comparePerformance -Baseline $performancePath -ValidateOnly
    & $comparePerformance -Baseline $performancePath -Candidate $performancePath | Out-Null
    $legacyPath = Join-Path $taskRoot 'synthetic-performance-legacy.json'
    Write-Json $legacyPath (New-PerformanceFixture)
    Reject { & $comparePerformance -Baseline $legacyPath -Candidate $performancePath }
    Reject { & $comparePerformance -Baseline $performancePath -Candidate $legacyPath }
    $performance.metrics = @($performance.metrics | Where-Object Name -NE 'queue_10000_mixed_filter_roundtrip')
    Write-Json $performancePath $performance
    Reject { & $comparePerformance -Baseline $performancePath -ValidateOnly }
    $performance = New-PerformanceFixture -SchemaVersion 2
    $performance.metrics[9].Name = $performance.metrics[5].Name
    Write-Json $performancePath $performance
    Reject { & $comparePerformance -Baseline $performancePath -ValidateOnly }
    $performance = New-PerformanceFixture -SchemaVersion 2
    $performance.metrics[9].Name = 'unrecognized_queue_measurement'
    Write-Json $performancePath $performance
    Reject { & $comparePerformance -Baseline $performancePath -ValidateOnly }
}
Test-Case 'Expanded performance reports retain sample and percentile validation for admission metrics' {
    $performance = New-PerformanceFixture -SchemaVersion 2
    $performance.metrics[5].SamplesMilliseconds = @(1..8)
    Write-Json $performancePath $performance
    Reject { & $comparePerformance -Baseline $performancePath -ValidateOnly }
    $performance = New-PerformanceFixture -SchemaVersion 2
    $performance.metrics[8].P95Milliseconds = 0.1
    Write-Json $performancePath $performance
    Reject { & $comparePerformance -Baseline $performancePath -ValidateOnly }
}
Test-Case 'Empty environment, missing or duplicate metrics cannot bypass performance regression' {
    $performance = New-PerformanceFixture
    $performance.environment = [pscustomobject]@{}
    $performance.metrics = @()
    Write-Json $performancePath $performance
    Reject { & $comparePerformance -Baseline $performancePath -ValidateOnly }
    $performance = New-PerformanceFixture
    $performance.metrics = @($performance.metrics | Select-Object -First 4)
    Write-Json $performancePath $performance
    Reject { & $comparePerformance -Baseline $performancePath -ValidateOnly }
    $performance = New-PerformanceFixture
    $performance.metrics[4].Name = $performance.metrics[0].Name
    Write-Json $performancePath $performance
    Reject { & $comparePerformance -Baseline $performancePath -ValidateOnly }
}
Test-Case 'Missing samples, non-finite values and invented P95 are rejected' {
    $performance = New-PerformanceFixture
    $performance.metrics[0].SamplesMilliseconds = @()
    Write-Json $performancePath $performance
    Reject { & $comparePerformance -Baseline $performancePath -ValidateOnly }
    $performance = New-PerformanceFixture
    $performance.metrics[0].P95Milliseconds = 'NaN'
    Write-Json $performancePath $performance
    Reject { & $comparePerformance -Baseline $performancePath -ValidateOnly }
    $performance = New-PerformanceFixture
    $performance.metrics[0].SamplesMilliseconds[0] = [double]::PositiveInfinity
    Write-Json $performancePath $performance
    Reject { & $comparePerformance -Baseline $performancePath -ValidateOnly }
    $performance = New-PerformanceFixture
    $performance.metrics[0].P95Milliseconds = 0.1
    Write-Json $performancePath $performance
    Reject { & $comparePerformance -Baseline $performancePath -ValidateOnly }
}
Test-Case 'Formal baseline requires earlier clean source and capture time' {
    $performance = New-PerformanceFixture
    Write-Json $performancePath $performance
    Assert-ReleasePerformanceBaseline -ReportPath $performancePath -Candidate $candidate
    $performance.source.commit = $candidate.sourceCommit
    Write-Json $performancePath $performance
    Reject { Assert-ReleasePerformanceBaseline -ReportPath $performancePath -Candidate $candidate }
    $performance = New-PerformanceFixture
    $performance.source.dirty = $true
    Write-Json $performancePath $performance
    Reject { Assert-ReleasePerformanceBaseline -ReportPath $performancePath -Candidate $candidate }
    $performance.source.dirty = 'false'
    Write-Json $performancePath $performance
    Reject { Assert-ReleasePerformanceBaseline -ReportPath $performancePath -Candidate $candidate }
    $performance = New-PerformanceFixture
    $performance.capturedAt = [DateTimeOffset]::Parse($candidate.builtAt).AddSeconds(1).ToString('o')
    Write-Json $performancePath $performance
    Reject { Assert-ReleasePerformanceBaseline -ReportPath $performancePath -Candidate $candidate }
    $performance = New-PerformanceFixture
    $performance.source.fingerprint = 'invalid'
    Write-Json $performancePath $performance
    Reject { Assert-ReleasePerformanceBaseline -ReportPath $performancePath -Candidate $candidate }
}
Test-Case 'A genuine regression over ten percent stops performance comparison' {
    $performance = New-PerformanceFixture
    Write-Json $performancePath $performance
    $regressedPath = Join-Path $taskRoot 'synthetic-performance-regressed.json'
    $performance.metrics[0].SamplesMilliseconds = @($performance.metrics[0].SamplesMilliseconds | ForEach-Object { $_ * 1.11 })
    $performance.metrics[0].P95Milliseconds = 9 * 1.11
    Write-Json $regressedPath $performance
    Reject { & $comparePerformance -Baseline $performancePath -Candidate $regressedPath }
}
Test-Case 'Exactly ten percent remains inside the performance threshold' {
    $performance = New-PerformanceFixture
    Write-Json $performancePath $performance
    $boundaryPath = Join-Path $taskRoot 'synthetic-performance-boundary.json'
    $performance.metrics[0].SamplesMilliseconds = @($performance.metrics[0].SamplesMilliseconds | ForEach-Object { $_ * 1.1 })
    $performance.metrics[0].P95Milliseconds = 9 * 1.1
    Write-Json $boundaryPath $performance
    & $comparePerformance -Baseline $performancePath -Candidate $boundaryPath | Out-Null
}
Test-Case 'Formal entrypoint refuses missing evidence and leaves no upload' {
    $out = Join-Path $taskRoot 'refused-promotion'
    Reject { & (Join-Path $repo 'packaging/build-store-msix.ps1') -OutputDirectory $out -SkipRepositoryValidation }
    if (@(Get-ChildItem -LiteralPath $taskRoot -Filter '*.msixupload' -Recurse -ErrorAction SilentlyContinue).Count -ne 0) { throw 'Refused gate generated an upload.' }
}
Test-Case 'Package validator rejects a same-length replacement of the tested native DLL' {
    . (Join-Path $repo 'build/RepositoryMetadata.ps1')
    . (Join-Path $repo 'packaging/ApplicationManifest.ps1')
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $config = Get-Content -LiteralPath (Join-Path $repo 'packaging/msix-config.json') -Raw | ConvertFrom-Json
    $manifestText = New-FormatFactoryAppxManifest -TemplatePath (Join-Path $repo 'packaging/AppxManifest.xml.template') -InputExtensions @(Get-RepositoryInputExtensions -RepositoryRoot $repo) -Config $config -Version $candidate.version -Architecture x64
    $pe = [byte[]]::new(128); $pe[0]=77; $pe[1]=90; $pe[0x3c]=0x40; $pe[0x40]=80; $pe[0x41]=69; $pe[0x44]=0x64; $pe[0x45]=0x86
    $nativeHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($pe))
    $nativeReceipt = [ordered]@{architecture='x64';configuration='Release';dllBytes=$pe.Length;dllSha256=$nativeHash;formatCount=56;minizVerified=$true;testsExecuted=$true}
    $names = @('FormatFactoryWinUI.exe','FormatFactory.Core.dll','FormatFactory.Engine.dll','FormatFactory.Documents.dll','FormatFactory.Presentation.dll','FormatFactory.FilterWorker.exe','FormatFactory.FilterWorker.dll','FormatFactory.FilterWorker.deps.json','FormatFactory.FilterWorker.runtimeconfig.json','FormatFactoryWinUI.pri','App.xbf','MainWindow.xbf','Views/HistoryView.xbf','Views/SettingsView.xbf','Views/QueueView.xbf','Views/ArchiveConversionView.xbf','Views/PageResources.xbf','Engines/bundle-manifest.json','FormatFactoryShellExtension.Native.dll','native-shell-build.json','miniz.LICENSE.txt','AppxManifest.xml')
    foreach ($corrupt in @($false,$true)) {
        $path = Join-Path $taskRoot "synthetic-native-$corrupt.msix"
        $archive = [IO.Compression.ZipFile]::Open($path,[IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($name in $names) {
                $entry = $archive.CreateEntry($name)
                $stream = $entry.Open()
                try {
                    $payload = [Text.Encoding]::UTF8.GetBytes('SYNTHETIC payload, never install')
                    if ($name -in @('FormatFactoryWinUI.exe','FormatFactory.FilterWorker.exe','FormatFactoryShellExtension.Native.dll')) { $payload=$pe.Clone(); if ($corrupt -and $name -eq 'FormatFactoryShellExtension.Native.dll') { $payload[100]=1 } }
                    elseif ($name -eq 'native-shell-build.json') { $payload=[Text.Encoding]::UTF8.GetBytes(($nativeReceipt | ConvertTo-Json)) }
                    elseif ($name -eq 'AppxManifest.xml') { $payload=[Text.Encoding]::UTF8.GetBytes($manifestText) }
                    $stream.Write($payload,0,$payload.Length)
                } finally { $stream.Dispose() }
            }
        } finally { $archive.Dispose() }
        if ($corrupt) { Reject { & (Join-Path $repo 'packaging/validate-msix.ps1') -PackagePath $path -Architecture x64 } }
        else { & (Join-Path $repo 'packaging/validate-msix.ps1') -PackagePath $path -Architecture x64 | Out-Null }
    }
}
Write-Output "Release gate synthetic tests: $script:passed passed. Fixtures: $taskRoot. No packages installed and no upload generated."
