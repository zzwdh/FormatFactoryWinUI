Set-StrictMode -Version Latest

function Get-ToolchainLock {
    param([string]$RepositoryRoot)
    $lock = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'build/toolchain.lock.json') -Raw | ConvertFrom-Json
    $sdk = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'global.json') -Raw | ConvertFrom-Json
    if ($lock.schemaVersion -ne 1 -or $sdk.sdk.version -cne $lock.dotnetSdkVersion) {
        throw 'Toolchain lock and global.json must agree; update both and rerun regression validation.'
    }
    return $lock
}

function Invoke-LockedDotnet {
    param([string]$RepositoryRoot, [string[]]$Arguments)
    $lock = Get-ToolchainLock -RepositoryRoot $RepositoryRoot
    $sdkAssembly = "sdk/$($lock.dotnetSdkVersion)/dotnet.dll"
    $portableRoot = Join-Path $RepositoryRoot 'work/dotnet-ga/sdk'
    $hostCommand = Get-Command dotnet.exe -CommandType Application -All -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path (Split-Path $_.Source) $sdkAssembly) -PathType Leaf } |
        Select-Object -First 1
    $dotnet = if ($hostCommand) {
        $hostCommand.Source
    } elseif ((Test-Path -LiteralPath (Join-Path $portableRoot $sdkAssembly) -PathType Leaf) -and
              (Test-Path -LiteralPath (Join-Path $portableRoot 'dotnet.exe') -PathType Leaf)) {
        Join-Path $portableRoot 'dotnet.exe'
    } else {
        throw "Install .NET SDK $($lock.dotnetSdkVersion) or extract it to $portableRoot."
    }

    # Invoke the matching host so framework-dependent test processes can also find the runtime.
    Push-Location -LiteralPath $RepositoryRoot
    try { & $dotnet @Arguments }
    finally { Pop-Location }
}

function Assert-LockedToolVersion {
    param([string]$Path, [string]$ExpectedVersion)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Locked tool missing: $Path" }
    $info = (Get-Item -LiteralPath $Path).VersionInfo
    $version = '{0}.{1}.{2}.{3}' -f $info.FileMajorPart,$info.FileMinorPart,$info.FileBuildPart,$info.FilePrivatePart
    if ($version -cne $ExpectedVersion) { throw "Tool version mismatch: $Path; expected $ExpectedVersion, found $version. Install the locked version; do not select a newer tool implicitly." }
    return [IO.Path]::GetFullPath($Path)
}

function Resolve-LockedVcTools {
    param([string]$RepositoryRoot, $Lock, [string]$VCToolsRoot)
    if ($VCToolsRoot) { return [IO.Path]::GetFullPath($VCToolsRoot) }
    $candidates = [Collections.Generic.List[string]]::new()
    if ($env:VCToolsInstallDir) { $candidates.Add($env:VCToolsInstallDir) }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        foreach ($installation in @(& $vswhere -all -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)) {
            if ($installation) { $candidates.Add((Join-Path $installation "VC/Tools/MSVC/$($Lock.msvcDirectoryVersion)")) }
        }
    }
    $candidates.Add((Join-Path $RepositoryRoot "work/native-toolchain/VC/Tools/MSVC/$($Lock.msvcDirectoryVersion)"))
    foreach ($candidate in $candidates) {
        if ((Split-Path $candidate.TrimEnd('\','/') -Leaf) -ceq $Lock.msvcDirectoryVersion -and
            (Test-Path -LiteralPath (Join-Path $candidate 'bin/Hostx64/x64/cl.exe'))) { return [IO.Path]::GetFullPath($candidate) }
    }
    throw "Locked MSVC $($Lock.msvcDirectoryVersion) required. Pass -VCToolsRoot or install the exact tools; no automatic installation is performed."
}

function Get-LockedWindowsSdkTool {
    param([string]$RepositoryRoot, [ValidateSet('makeappx.exe','signtool.exe')][string]$Name,
        [string]$WindowsSdkRoot = (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'))
    $lock = Get-ToolchainLock -RepositoryRoot $RepositoryRoot
    return Assert-LockedToolVersion -Path (Join-Path $WindowsSdkRoot "bin/$($lock.windowsSdkVersion)/x64/$Name") -ExpectedVersion $lock.sdkToolFileVersion
}

function Assert-VendoredMiniz {
    param([string]$VendorDirectory)
    $lock = Get-Content -LiteralPath (Join-Path $VendorDirectory 'sources.lock.json') -Raw | ConvertFrom-Json
    foreach ($name in @('miniz.c','miniz.h','LICENSE')) {
        $expected = [string]$lock.files.$name
        $path = Join-Path $VendorDirectory $name
        if ($expected -notmatch '^[0-9A-Fa-f]{64}$' -or -not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $expected) {
            throw "Vendored miniz checksum mismatch: $name. Update sources.lock.json only with a reviewed upstream source update."
        }
    }
    return $lock
}

function Assert-ReleaseRuntimeSupport {
    param([string]$RepositoryRoot, [DateTimeOffset]$At = [DateTimeOffset]::UtcNow)
    $lock = Get-ToolchainLock -RepositoryRoot $RepositoryRoot
    # Release dates follow the user's Asia/Shanghai calendar, including midnight.
    $zone = [TimeZoneInfo]::FindSystemTimeZoneById('China Standard Time')
    $today = [TimeZoneInfo]::ConvertTime($At,$zone).Date
    $deadline = [DateTime]::ParseExact($lock.dotnetSupportedUntil,'yyyy-MM-dd',[Globalization.CultureInfo]::InvariantCulture)
    if ($today -gt $deadline) {
        throw ".NET $($lock.dotnetRelease) support expired on $($lock.dotnetSupportedUntil) (Asia/Shanghai). Keep .NET 11, update global.json and build/toolchain.lock.json together to a supported release, verify the official policy, and rerun all regression checks before release. $($lock.supportPolicyUrl)"
    }
    return $lock
}
