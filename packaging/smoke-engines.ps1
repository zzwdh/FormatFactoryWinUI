[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$EngineDirectory,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../work/engine-smoke'),
    [ValidateRange(30, 600)][int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
$engineRoot = [IO.Path]::GetFullPath($EngineDirectory)
$python = Join-Path $engineRoot 'caj2pdf/python.exe'
if (!(Test-Path -LiteralPath $python -PathType Leaf)) { throw "Bundled Python is missing: $python" }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
# The verifier itself uses the bundled, isolated interpreter and only its standard library.
& $python -I -B (Join-Path $PSScriptRoot 'smoke-engines.py') --engines $engineRoot --output $outputRoot --timeout $TimeoutSeconds
if ($LASTEXITCODE -ne 0) { throw "Bundled conversion smoke tests failed (exit $LASTEXITCODE). See $outputRoot." }
# Exercise the richer document/ebook conversion profile on every new package,
# and bind its detailed evidence to the standard release-check smoke report.
$profileOutput = Join-Path $outputRoot 'office-ebook'
& $python -I -B (Join-Path $PSScriptRoot 'smoke-office-ebook-pruning.py') --after $engineRoot --output $profileOutput --timeout $TimeoutSeconds
if ($LASTEXITCODE -ne 0) { throw "Office/ebook conversion profile tests failed (exit $LASTEXITCODE). See $profileOutput." }
$profileReportPath = Join-Path $profileOutput 'latest-results.json'
$profile = Get-Content -LiteralPath $profileReportPath -Raw | ConvertFrom-Json
if ($profile.status -cne 'passed' -or @($profile.results | Where-Object status -ne 'passed').Count -gt 0) { throw 'Office/ebook conversion profile report did not pass.' }
$reportPath = Join-Path $outputRoot 'latest-results.json'
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
$report.results = @($report.results) + @([pscustomobject]@{
    engine='office-ebook-profile'; status='passed'
    details=[ordered]@{cases=@($profile.results).Count; report=$profileReportPath; sha256=(Get-FileHash -LiteralPath $profileReportPath -Algorithm SHA256).Hash.ToLowerInvariant()}
})
$report | Add-Member -NotePropertyName officeEbookProfile -NotePropertyValue $profile
$report.completedAt = [DateTimeOffset]::UtcNow.ToString('o')
$report | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM
$runReportPath = Join-Path $report.workingDirectory 'results.json'
Copy-Item -LiteralPath $reportPath -Destination $runReportPath -Force
