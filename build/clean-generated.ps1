#requires -Version 7.2
[CmdletBinding()]
param(
    [switch]$Apply,
    [string]$ReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$repoPrefix = $repoRoot.TrimEnd([char[]]'\/') + [IO.Path]::DirectorySeparatorChar

function Get-RepositoryRelativePath([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    if (-not $absolute.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the repository: $absolute"
    }
    return [IO.Path]::GetRelativePath($repoRoot, $absolute).Replace('\', '/')
}

function Get-ReparseAncestor([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { return $current }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
    return $null
}

function Get-TrackedPaths {
    $output = & git -C $repoRoot ls-files --cached -z
    if ($LASTEXITCODE -ne 0) { throw 'Cannot verify Git-tracked files; cleanup refused.' }
    return @(([string]($output -join "`n")).Split([char]0, [StringSplitOptions]::RemoveEmptyEntries))
}

function Test-TrackedPath([string]$Relative, [string[]]$Tracked) {
    foreach ($path in $Tracked) {
        if ($path.Equals($Relative, [StringComparison]::OrdinalIgnoreCase) -or
            $path.StartsWith($Relative + '/', [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

function Get-ActiveBuildProcesses {
    return @(Get-Process | Where-Object ProcessName -Match '^(dotnet|MSBuild|FormatFactoryWinUI|FormatFactory\.FilterWorker|testhost(?:\..+)?)$' |
        Select-Object Id, ProcessName)
}

function Get-ChildDirectories([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    if (Get-ReparseAncestor $Path) { Write-Warning "Ignoring linked discovery directory: $Path"; return }
    Get-ChildItem -LiteralPath $Path -Directory -Force | Where-Object {
        -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint)
    }
}

function Test-ProjectDirectory([string]$Path) {
    if ([IO.Path]::GetFullPath($Path).Equals((Join-Path $repoRoot 'src/FormatFactoryShellExtension.Native'),
            [StringComparison]::OrdinalIgnoreCase)) { return $true }
    return @(Get-ChildItem -LiteralPath $Path -File -Force | Where-Object Extension -In '.csproj', '.fsproj', '.vbproj', '.vcxproj').Count -gt 0
}

function Test-AllowlistedTarget([string]$Relative) {
    if ($Relative -match '^(src|tests)/[^/]+/(bin|obj|TestResults)$') {
        return Test-ProjectDirectory (Split-Path (Join-Path $repoRoot $Relative) -Parent)
    }
    return $Relative -match '^work/ui-regression/[^/]+/artifacts/(bin|obj)$' -or
        $Relative -match '^work/(engine-packaging-tests|release-gate-tests)-[a-f0-9]{32}$' -or
        $Relative -match '^work/(msix-staging-(x64|arm64)|publish-msix-(x64|arm64)|msix-bundle)$'
}

function Get-TargetInspection([string]$Path, [string[]]$Tracked) {
    $relative = Get-RepositoryRelativePath $Path
    $result = [ordered]@{ path = $relative; bytes = [long]0; files = [long]0; status = 'planned'; reason = $null }
    $linked = Get-ReparseAncestor $Path
    if ($linked) { $result.status = 'skipped'; $result.reason = "Reparse point in target or ancestors: $linked"; return [pscustomobject]$result }
    if (-not (Test-AllowlistedTarget $relative)) { throw "Target is not allowlisted: $relative" }
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        $result.status = 'skipped'; $result.reason = 'Directory no longer exists'; return [pscustomobject]$result
    }
    if (Test-TrackedPath $relative $Tracked) {
        $result.status = 'skipped'; $result.reason = 'Contains Git-tracked files'; return [pscustomobject]$result
    }
    # Enumerate one directory at a time so junctions are never traversed.
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Path)
    while ($pending.Count) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                $result.status = 'skipped'
                $result.reason = "Reparse point in subtree: $($item.FullName)"
            } elseif ($item.PSIsContainer) {
                $pending.Push($item.FullName)
            } else {
                $result.files++
                $result.bytes += $item.Length
            }
        }
    }
    return [pscustomobject]$result
}

$candidates = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($sourceRoot in 'src', 'tests') {
    foreach ($project in Get-ChildDirectories (Join-Path $repoRoot $sourceRoot)) {
        if (Test-ProjectDirectory $project.FullName) {
            foreach ($name in 'bin', 'obj', 'TestResults') {
                $path = Join-Path $project.FullName $name
                if (Test-Path -LiteralPath $path) { [void]$candidates.Add($path) }
            }
        }
    }
}
foreach ($run in Get-ChildDirectories (Join-Path $repoRoot 'work/ui-regression')) {
    foreach ($name in 'bin', 'obj') {
        $path = Join-Path $run.FullName "artifacts/$name"
        if (Test-Path -LiteralPath $path) { [void]$candidates.Add($path) }
    }
}
foreach ($directory in Get-ChildDirectories (Join-Path $repoRoot 'work')) {
    if ($directory.Name -match '^(engine-packaging-tests|release-gate-tests)-[a-f0-9]{32}$' -or
        $directory.Name -match '^(msix-staging-(x64|arm64)|publish-msix-(x64|arm64)|msix-bundle)$') {
        [void]$candidates.Add($directory.FullName)
    }
}

$tracked = @(Get-TrackedPaths)
$resolvedReportPath = $null
if ($ReportPath) {
    $resolvedReportPath = [IO.Path]::GetFullPath($ReportPath, $repoRoot)
    $reportRelative = Get-RepositoryRelativePath $resolvedReportPath
    if ([IO.Path]::GetExtension($resolvedReportPath) -ine '.json') { throw 'ReportPath must end with .json.' }
    if (Test-Path -LiteralPath $resolvedReportPath) { throw 'ReportPath already exists; choose a new report filename.' }
    if ((Get-ReparseAncestor $resolvedReportPath) -or (Test-TrackedPath $reportRelative $tracked)) {
        throw 'ReportPath must not use linked directories or overwrite tracked files.'
    }
    foreach ($candidate in $candidates) {
        if ($resolvedReportPath.StartsWith($candidate.TrimEnd([char[]]'\/') + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) { throw 'ReportPath must be outside cleanup targets.' }
    }
}

$report = [ordered]@{
    schemaVersion = 1; repositoryRoot = $repoRoot; mode = $(if ($Apply) { 'apply' } else { 'preview' })
    startedAt = [DateTimeOffset]::UtcNow.ToString('o'); completedAt = $null; status = 'preview'
    targets = @(); plannedBytes = [long]0; plannedFiles = [long]0; removedBytes = [long]0; removedFiles = [long]0
    error = $null
}
$failure = $null
try {
    if ($Apply) {
        $active = @(Get-ActiveBuildProcesses)
        if ($active.Count) { throw "Close build, test and application processes before applying cleanup: $($active | ConvertTo-Json -Compress)" }
    }
    $report.targets = @(foreach ($candidate in $candidates | Sort-Object) { Get-TargetInspection $candidate $tracked })
    foreach ($target in $report.targets) {
        Write-Output ('{0}: {1} ({2} files, {3} bytes){4}' -f $target.status, $target.path, $target.files, $target.bytes,
            $(if ($target.reason) { ' - ' + $target.reason } else { '' }))
        if ($target.status -eq 'planned') { $report.plannedBytes += $target.bytes; $report.plannedFiles += $target.files }
    }
    if ($Apply) {
        foreach ($target in $report.targets | Where-Object status -EQ 'planned') {
            if (@(Get-ActiveBuildProcesses).Count) { throw 'A build, test or application process started; remaining cleanup refused.' }
            # Recheck the complete target and Git index immediately before removal.
            $path = Join-Path $repoRoot $target.path
            $current = Get-TargetInspection $path @(Get-TrackedPaths)
            if ($current.status -ne 'planned') {
                $target.status = 'skipped'; $target.reason = $current.reason
                Write-Warning "Skipped $($target.path): $($target.reason)"
                continue
            }
            try { Remove-Item -LiteralPath $path -Recurse -Force }
            catch { $target.status = 'failed'; $target.reason = $_.Exception.Message; throw }
            $target.status = 'removed'
            $target.bytes = $current.bytes; $target.files = $current.files
            $report.removedBytes += $current.bytes; $report.removedFiles += $current.files
            Write-Output "removed: $($target.path)"
        }
        $report.status = 'completed'
    }
} catch {
    $failure = $_
    $report.status = 'failed'
    $report.error = $_.Exception.Message
} finally {
    $report.completedAt = [DateTimeOffset]::UtcNow.ToString('o')
    if ($resolvedReportPath) {
        if ((Test-Path -LiteralPath $resolvedReportPath) -or (Get-ReparseAncestor $resolvedReportPath)) {
            throw 'ReportPath changed during cleanup; report write refused.'
        }
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedReportPath))
        $stream = [IO.File]::Open($resolvedReportPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($report | ConvertTo-Json -Depth 8))
            $stream.Write($bytes, 0, $bytes.Length)
        } finally { $stream.Dispose() }
        Write-Output "Report: $resolvedReportPath"
    }
}
Write-Output ('{0}: planned {1} files / {2} bytes; removed {3} files / {4} bytes.' -f $report.status,
    $report.plannedFiles, $report.plannedBytes, $report.removedFiles, $report.removedBytes)
if ($failure) { throw $failure }
