#requires -Version 7.2
[CmdletBinding()]
param(
    [ValidatePattern('^[0-9a-f]{32}$')]
    [string]$RunId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$workRoot = Join-Path $repoRoot 'work'
$scratchRoot = Join-Path $workRoot 'package-scratch'
$queueRoot = Join-Path $workRoot 'package-completions'

function Assert-UnlinkedPath([string]$Path) {
    for ($current = $Path; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction Ignore
        if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing to follow a linked path: $current"
        }
    }
}

function Clear-RecordedRun([string]$Id) {
    $recordPath = Join-Path $queueRoot "$Id.json"
    $target = [IO.Path]::GetFullPath((Join-Path $scratchRoot $Id))
    if ([IO.Path]::GetDirectoryName($target) -ne $scratchRoot -or $Id -cnotmatch '^[0-9a-f]{32}$') {
        throw "Invalid scratch directory: $target"
    }
    Assert-UnlinkedPath $recordPath
    Assert-UnlinkedPath $target
    # Deny a second reader while this run is being cleaned. The owner can
    # unlink the processed record before releasing its handle on Windows.
    try {
        $stream = [IO.File]::Open($recordPath, 'Open', 'Read', 'Delete')
    } catch [IO.FileNotFoundException] { return }
    try {
        $reader = [IO.StreamReader]::new($stream)
        $record = $reader.ReadToEnd() | ConvertFrom-Json
        if ($record.runId -cne $Id -or $record.kind -cnotin 'msix-x64', 'msix-arm64', 'candidate', 'release' -or
            -not $record.version -or -not [IO.Path]::IsPathFullyQualified($record.outputDirectory)) {
            throw "Invalid package completion record: $recordPath"
        }
        $output = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($record.outputDirectory))
        foreach ($reserved in $scratchRoot, $queueRoot) {
            if ($output -eq $reserved -or $output.StartsWith($reserved + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                throw "The package output overlaps reserved cleanup storage: $output"
            }
        }
        $tracked = & git -C $repoRoot ls-files --cached -- "work/package-scratch/$Id" "work/package-completions/$Id.json"
        if ($LASTEXITCODE -ne 0 -or $tracked) { throw "Cannot confirm that run $Id contains only untracked generated files." }
        if (Test-Path -LiteralPath $target) {
            if (-not [IO.Directory]::Exists($target)) { throw "Expected a scratch directory: $target" }
            foreach ($item in Get-ChildItem -LiteralPath $target -Force) {
                if (-not $item.PSIsContainer -or $item.Name -cnotmatch '^(?:(?:publish-msix|msix-staging|publish-shell-msix)-(?:x64|arm64)|msix-bundle|release-upload)$') {
                    throw "Unexpected entry in package scratch: $($item.FullName)"
                }
            }
            # PowerShell 7 enumerates links without following them unless
            # -FollowSymlink is requested. Keep the whole run if any exist.
            $link = Get-ChildItem -LiteralPath $target -Recurse -Force -Attributes ReparsePoint | Select-Object -First 1
            if ($link) { throw "Linked entry in package scratch: $($link.FullName)" }
            Remove-Item -LiteralPath $target -Recurse -Force
        }
        [IO.File]::Delete($recordPath)
        Write-Host "已清理 $($record.version) 的打包暂存目录 $Id"
    } finally {
        $stream.Dispose()
    }
}

Assert-UnlinkedPath $queueRoot
if (-not (Test-Path -LiteralPath $queueRoot)) { return }
if ($RunId) { Clear-RecordedRun $RunId; return }
$failed = 0
foreach ($file in Get-ChildItem -LiteralPath $queueRoot -File -Filter '*.json' -Force) {
    if ($file.BaseName -cnotmatch '^[0-9a-f]{32}$') { continue }
    try { Clear-RecordedRun $file.BaseName } catch {
        $failed++
        Write-Warning "$($file.Name): $($_.Exception.Message)"
    }
}
if ($failed) { throw "$failed package cleanup record(s) remain pending." }
