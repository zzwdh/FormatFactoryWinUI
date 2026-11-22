param(
    [Parameter(Mandatory)][string]$Baseline,
    [string]$Candidate,
    [double]$MaximumRegressionPercent = 10,
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([double]::IsNaN($MaximumRegressionPercent) -or [double]::IsInfinity($MaximumRegressionPercent) -or $MaximumRegressionPercent -lt 0) { throw 'MaximumRegressionPercent must be finite and nonnegative.' }

function Assert-FiniteNumber($Value, [string]$Name, [switch]$Positive, [switch]$Integer) {
    if ($null -eq $Value -or $Value -is [string] -or $Value -is [bool]) { throw "Performance report requires numeric $Name." }
    $number = [double]$Value
    if ([double]::IsNaN($number) -or [double]::IsInfinity($number) -or $number -lt 0 -or ($Positive -and $number -le 0) -or
        ($Integer -and $number -ne [Math]::Truncate($number))) { throw "Performance report has invalid $Name." }
}

function Assert-PerformanceReport($Report) {
    if ($Report.schemaVersion -notin @(1, 2)) { throw 'Unsupported performance report schema.' }
    $requiredEnvironment = @('machine','os','runtime','architecture','processors','processor','winUiAssembly',
        'width','height','scale','language','theme','highContrast','animation','configuration','samples')
    foreach ($name in $requiredEnvironment) {
        if ($name -cnotin $Report.environment.PSObject.Properties.Name) { throw "Performance report missing environment.$name." }
    }
    foreach ($name in @('machine','os','runtime','architecture','processor','winUiAssembly','language','theme','configuration')) {
        if ([string]::IsNullOrWhiteSpace([string]$Report.environment.$name)) { throw "Performance report has empty environment.$name." }
    }
    foreach ($name in @('processors','samples')) { Assert-FiniteNumber $Report.environment.$name "environment.$name" -Positive -Integer }
    foreach ($name in @('width','height','scale')) { Assert-FiniteNumber $Report.environment.$name "environment.$name" -Positive }
    foreach ($name in @('highContrast','animation')) { if ($Report.environment.$name -isnot [bool]) { throw "Performance report requires boolean environment.$name." } }
    foreach ($name in @('paragraphs','charactersPerParagraph','inputBytes')) { Assert-FiniteNumber $Report.documentFixture.$name "documentFixture.$name" -Positive -Integer }
    if ([string]::IsNullOrWhiteSpace([string]$Report.documentFixture.engine)) { throw 'Performance report missing documentFixture.engine.' }

    $requiredMetrics = @('ui_construction','first_settings_initialization','large_docx_to_epub',
        'queue_1000_filter_roundtrip','queue_10000_filter_roundtrip')
    if ($Report.schemaVersion -eq 2) {
        $requiredMetrics += @('queue_1000_waiting_admission','queue_10000_waiting_admission',
            'queue_1000_failed_admission','queue_10000_failed_admission','queue_10000_mixed_filter_roundtrip')
    }
    if (@($Report.metrics).Count -ne $requiredMetrics.Count -or
        @(Compare-Object -ReferenceObject $requiredMetrics -DifferenceObject @($Report.metrics.Name) -CaseSensitive).Count -ne 0) {
        throw "Performance report schema $($Report.schemaVersion) requires exactly its $($requiredMetrics.Count) construction, settings, document and queue metrics."
    }
    foreach ($metric in $Report.metrics) {
        Assert-FiniteNumber $metric.P95Milliseconds "$($metric.Name).P95Milliseconds" -Positive
        Assert-FiniteNumber $metric.P95PrivateBytes "$($metric.Name).P95PrivateBytes" -Positive -Integer
        Assert-FiniteNumber $metric.MaxRealizedContainers "$($metric.Name).MaxRealizedContainers" -Integer
        $samples = @($metric.SamplesMilliseconds)
        if ($samples.Count -ne $Report.environment.samples) { throw "Performance report requires all recorded samples for $($metric.Name)." }
        foreach ($sample in $samples) { Assert-FiniteNumber $sample "$($metric.Name).SamplesMilliseconds" -Positive }
        $p95 = @($samples | Sort-Object)[[Math]::Ceiling($samples.Count * .95) - 1]
        if ([Math]::Abs([double]$p95 - [double]$metric.P95Milliseconds) -gt 0.0000001) {
            throw "Performance P95 does not match the recorded samples: $($metric.Name)."
        }
    }
}

$before = Get-Content -LiteralPath $Baseline -Raw | ConvertFrom-Json
Assert-PerformanceReport $before
if ($ValidateOnly) { return }
if ([string]::IsNullOrWhiteSpace($Candidate)) { throw 'Candidate is required when comparing performance reports.' }
$after = Get-Content -LiteralPath $Candidate -Raw | ConvertFrom-Json
Assert-PerformanceReport $after
if ($before.schemaVersion -ne $after.schemaVersion -or
    @(Compare-Object -ReferenceObject @($before.metrics.Name) -DifferenceObject @($after.metrics.Name) -CaseSensitive).Count -ne 0) {
    throw 'Performance reports are not comparable: schema or measured metrics differ.'
}
if (@(Compare-Object -ReferenceObject @($before.environment.PSObject.Properties.Name) -DifferenceObject @($after.environment.PSObject.Properties.Name)).Count -ne 0) {
    throw 'Performance reports are not comparable: environment fields differ.'
}
foreach ($field in $before.environment.PSObject.Properties.Name) {
    if ($before.environment.$field -ne $after.environment.$field) {
        throw "Performance reports are not comparable: environment.$field differs."
    }
}
foreach ($field in 'paragraphs', 'charactersPerParagraph', 'inputBytes', 'engine') {
    if ($before.documentFixture.$field -ne $after.documentFixture.$field) {
        throw "Performance reports are not comparable: documentFixture.$field differs."
    }
}
$failures = @()
$rows = foreach ($baselineMetric in $before.metrics) {
    $candidateMetric = @($after.metrics | Where-Object Name -eq $baselineMetric.Name)
    if ($candidateMetric.Count -ne 1) { throw "Missing or duplicate metric: $($baselineMetric.Name)" }
    foreach ($measure in 'P95Milliseconds', 'P95PrivateBytes', 'MaxRealizedContainers') {
        $old = [double]$baselineMetric.$measure
        $new = [double]($candidateMetric[0].$measure)
        if ($old -eq 0 -and $new -eq 0) { continue }
        $percent = if ($old -eq 0) { [double]::PositiveInfinity } else { (($new / $old) - 1) * 100 }
        $passed = $percent -le ($MaximumRegressionPercent + 0.000000001)
        if (!$passed) { $failures += "$($baselineMetric.Name).$measure" }
        [PSCustomObject]@{ Metric=$baselineMetric.Name; Measure=$measure; Baseline=$old; Candidate=$new; ChangePercent=[Math]::Round($percent,2); Passed=$passed }
    }
}
$rows | Format-Table -AutoSize
if ($failures.Count -gt 0) { throw "Performance regression exceeds $MaximumRegressionPercent percent: $($failures -join ', ')" }
