#requires -Version 7.2

function Assert-PackageDirectoryHasNoLinks([string]$Path) {
    $directory = [IO.DirectoryInfo]::new($Path)
    while ($null -ne $directory) {
        if ($directory.Attributes -ne -1 -and ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "打包目录包含重解析点：$($directory.FullName)"
        }
        $directory = $directory.Parent
    }
}

function Resolve-PackageOutputDirectory([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path))
    Assert-PackageDirectoryHasNoLinks $resolved
    $workRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'work'
    foreach ($name in 'package-scratch', 'package-completions') {
        $reserved = Join-Path $workRoot $name
        $relative = [IO.Path]::GetRelativePath($reserved, $resolved)
        if ($relative -ne '..' -and -not $relative.StartsWith('..' + [IO.Path]::DirectorySeparatorChar) -and -not [IO.Path]::IsPathRooted($relative)) {
            throw "安装包输出目录位于自动清理保留目录：$resolved"
        }
    }
    return $resolved
}

function New-PackageScratchDirectory {
    $root = Join-Path (Split-Path $PSScriptRoot -Parent) 'work/package-scratch'
    Assert-PackageDirectoryHasNoLinks $root
    [void][IO.Directory]::CreateDirectory($root)
    $runId = [Guid]::NewGuid().ToString('N')
    return New-Item -ItemType Directory -Path (Join-Path $root $runId) -ErrorAction Stop
}
