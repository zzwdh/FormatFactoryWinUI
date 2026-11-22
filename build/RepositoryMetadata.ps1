function Get-RepositoryReleaseVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot
    )

    $versionPath = Join-Path $RepositoryRoot "VERSION"
    if (-not (Test-Path -LiteralPath $versionPath -PathType Leaf)) {
        throw "缺少唯一版本源：$versionPath"
    }

    $version = (Get-Content -LiteralPath $versionPath -Raw).Trim()
    if ($version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        throw "VERSION 必须是四段数字版本号：$version"
    }

    foreach ($segment in $version.Split(".")) {
        $value = 0
        if (-not [int]::TryParse($segment, [ref]$value) -or $value -lt 0 -or $value -gt 65535) {
            throw "VERSION 的每一段必须在 0 到 65535 之间：$version"
        }
    }

    return $version
}

function Get-RepositoryInputExtensions {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot
    )

    $catalogPath = Join-Path $RepositoryRoot "src\FormatFactory.Core\Models\formats.json"
    if (-not (Test-Path -LiteralPath $catalogPath -PathType Leaf)) {
        throw "缺少唯一格式目录：$catalogPath"
    }

    $formats = @(Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json)
    $extensions = @($formats |
        Where-Object { @($_.capabilities) -contains "Import" } |
        ForEach-Object { @($_.extensions) } |
        ForEach-Object { [string]$_ })
    if ($extensions.Count -eq 0) {
        throw "格式目录必须至少包含一个可导入扩展。"
    }

    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($extensionValue in $extensions) {
        $extension = [string]$extensionValue
        if ($extension -cnotmatch '^\.[a-z0-9]+$') {
            throw "输入扩展必须是小写字母或数字，并以点开头：$extension"
        }
        if (-not $seen.Add($extension)) {
            throw "格式目录包含重复输入扩展：$extension"
        }
    }

    return $extensions
}

function Get-RepositoryMediaExtensions {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot
    )

    return Get-RepositoryInputExtensions -RepositoryRoot $RepositoryRoot
}
