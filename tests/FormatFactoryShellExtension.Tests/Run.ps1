[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64', 'all')][string]$Architecture = 'x64',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$VCToolsRoot,
    [switch]$ValidateManifest
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$arguments = @{
    Architecture = $Architecture
    Configuration = $Configuration
    RunTests = $true
}
if ($VCToolsRoot) { $arguments.VCToolsRoot = $VCToolsRoot }
& (Join-Path $repoRoot 'src\FormatFactoryShellExtension.Native\build.ps1') @arguments
if ($ValidateManifest) { & (Join-Path $PSScriptRoot 'Validate-Package.ps1') }
