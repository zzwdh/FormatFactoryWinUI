#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('(?-i:\A[0-9a-f]{32}\z)')][string]$RunId,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidateSet('msix-x64','msix-arm64','candidate','release')][string]$Kind
)

$ErrorActionPreference = 'Stop'
try {
    . (Join-Path $PSScriptRoot 'PackageScratch.ps1')
    $workRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'work'
    $queue = Join-Path $workRoot 'package-completions'
    Assert-PackageDirectoryHasNoLinks $queue
    [void][IO.Directory]::CreateDirectory($queue)

    $record = [ordered]@{
        runId = $RunId
        kind = $Kind
        version = $Version
        outputDirectory = Resolve-PackageOutputDirectory $OutputDirectory
    }
    $temporary = Join-Path $queue "$RunId.tmp"
    $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($record | ConvertTo-Json))
        $stream.Write($bytes, 0, $bytes.Length)
    } finally { $stream.Dispose() }
    [IO.File]::Move($temporary, (Join-Path $queue "$RunId.json"))
}
catch {
    Write-Warning "打包已完成，但无法写入自动清理通知：$($_.Exception.Message)" -WarningAction Continue
    return
}

$packageExitCode = Get-Variable LASTEXITCODE -Scope Global -ValueOnly -ErrorAction Ignore
try {
    & (Join-Path $PSScriptRoot 'Clear-CompletedPackageScratch.ps1') -RunId $RunId
}
catch {
    Write-Warning "打包已完成，暂存清理待重试：$($_.Exception.Message)" -WarningAction Continue
}
finally {
    # Cleanup uses Git. Preserve the native exit status observed by package callers.
    if ($null -ne $packageExitCode) { $global:LASTEXITCODE = $packageExitCode }
}
