param(
    [string]$RunName = ('startup-' + (Get-Date -Format 'yyyyMMdd-HHmmss')),
    [ValidateRange(1, 20)][int]$Samples = 3,
    [ValidateSet(0, 100, 1000)][int[]]$QueueCounts = @(0, 100, 1000),
    [string]$NuGetConfigPath,
    [string]$EngineRoot,
    [ValidateRange(10, 600)][int]$TimeoutSeconds = 180,
    [switch]$BuildOnly,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if ($RunName -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunName may contain letters, digits, hyphens, and underscores.' }
$runRoot = Join-Path $repoRoot "work/ui-regression/$RunName"
$hostSource = Join-Path $repoRoot 'src/FormatFactoryWinUI'
if (!$EngineRoot) { $EngineRoot = Join-Path $repoRoot 'work/prepared-engines/x64/Engines' }
$EngineRoot = [IO.Path]::GetFullPath($EngineRoot)
if (!(Test-Path -LiteralPath $EngineRoot -PathType Container)) { throw "Prepared x64 engines are missing: $EngineRoot" }
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

function Get-StartupSourceSnapshot {
    $paths = @(& rg --files (Join-Path $repoRoot 'src') -g '*.cs' -g '*.xaml' -g '*.csproj' -g '*.json' -g '*.manifest' -g '!**/bin/**' -g '!**/obj/**')
    $paths += @('Directory.Build.props', 'Directory.Packages.props', 'global.json', 'VERSION',
        'tests/WinUiSmoke/Measure-Startup.ps1', 'tests/WinUiSmoke/StartupMeasurementApp.cs') | ForEach-Object { Join-Path $repoRoot $_ }
    $entries = @($paths | Sort-Object -Unique | ForEach-Object {
        [IO.Path]::GetRelativePath($repoRoot, $_) + ':' + (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
    })
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($entries -join "`n")))).ToLowerInvariant()
    [pscustomobject]@{ commit = [string](& git -C $repoRoot rev-parse HEAD); fingerprint = $hash; files = $entries.Count }
}

$sourceBefore = Get-StartupSourceSnapshot
$buildSourcePath = Join-Path $runRoot 'build-source.json'
if ($SkipBuild) {
    if (!(Test-Path -LiteralPath $buildSourcePath)) { throw 'SkipBuild requires a completed build-source.json from this tool.' }
    $builtSource = Get-Content -LiteralPath $buildSourcePath -Raw | ConvertFrom-Json
    if ($builtSource.fingerprint -cne $sourceBefore.fingerprint) { throw 'The source differs from the saved build; rebuild before measuring.' }
}
$applicationSource = (Get-Content -LiteralPath (Join-Path $hostSource 'App.xaml.cs') -Raw).Replace(
    'FindOrRegisterForKey("zzwdh.WinUI")', 'FindOrRegisterForKey("zzwdh.WinUI.startup-measurement")')
if ($applicationSource -notmatch 'zzwdh.WinUI.startup-measurement') { throw 'Production activation key was not isolated.' }
Set-Content -LiteralPath (Join-Path $runRoot 'MeasuredApp.xaml.cs') -Value $applicationSource -Encoding utf8
[xml]$hostProject = Get-Content -LiteralPath (Join-Path $hostSource 'FormatFactoryWinUI.csproj') -Raw
$sdkReferences = @($hostProject.SelectNodes('//PackageReference') | Where-Object { $_.GetAttribute('Include') -like 'Microsoft.WindowsAppSDK*' })
if ($sdkReferences.Count -eq 0) { throw 'Production Windows App SDK references were not found.' }
$sdkReferenceXml = ($sdkReferences | ForEach-Object { '    ' + $_.OuterXml }) -join [Environment]::NewLine
$project = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net11.0-windows10.0.26100.0</TargetFramework>
    <TargetPlatformMinVersion>$(MinimumDesktopWindowsVersion)</TargetPlatformMinVersion>
    <RootNamespace>FormatFactoryWinUI</RootNamespace>
    <AssemblyName>FormatFactoryWinUI</AssemblyName>
    <ApplicationManifest>HOST_SOURCE/app.manifest</ApplicationManifest>
    <PlatformTarget>x64</PlatformTarget><RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <UseWinUI>true</UseWinUI><WindowsPackageType>None</WindowsPackageType>
    <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained><WindowsAppSDKBackgroundTask>true</WindowsAppSDKBackgroundTask>
    <SelfContained>true</SelfContained><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems><EnableDefaultPageItems>false</EnableDefaultPageItems>
    <EnableDefaultApplicationDefinition>false</EnableDefaultApplicationDefinition>
  </PropertyGroup>
  <ItemGroup>
WINDOWS_APP_SDK_REFERENCES
    <ProjectReference Include="REPO_ROOT/src/FormatFactory.Core/FormatFactory.Core.csproj" />
    <ProjectReference Include="REPO_ROOT/src/FormatFactory.Documents/FormatFactory.Documents.csproj" />
    <ProjectReference Include="REPO_ROOT/src/FormatFactory.Engine/FormatFactory.Engine.csproj" />
    <ProjectReference Include="REPO_ROOT/src/FormatFactory.Presentation/FormatFactory.Presentation.csproj" />
    <Compile Include="HOST_SOURCE/**/*.cs" Exclude="HOST_SOURCE/bin/**/*.cs;HOST_SOURCE/obj/**/*.cs;HOST_SOURCE/App.xaml.cs;HOST_SOURCE/Platform/Services/WindowsAppDataStorage.cs" />
    <Compile Include="RUN_ROOT/MeasuredApp.xaml.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/StartupMeasurementApp.cs" />
    <ApplicationDefinition Include="HOST_SOURCE/App.xaml" Link="App.xaml" />
    <Page Include="HOST_SOURCE/MainWindow.xaml" Link="MainWindow.xaml" />
    <Page Include="HOST_SOURCE/Views/**/*.xaml" Link="Views/%(RecursiveDir)%(Filename)%(Extension)" />
    <Content Include="HOST_SOURCE/Assets/*" Link="Assets/%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
    <Content Include="HOST_SOURCE/feedback-config.json" Link="feedback-config.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
  <Import Project="REPO_ROOT/build/ConversionWorker.targets" />
</Project>
'@
foreach ($replacement in @{ HOST_SOURCE = $hostSource; REPO_ROOT = $repoRoot; RUN_ROOT = $runRoot }.GetEnumerator()) {
    $project = $project.Replace($replacement.Key, [Security.SecurityElement]::Escape($replacement.Value))
}
$project = $project.Replace('WINDOWS_APP_SDK_REFERENCES', $sdkReferenceXml)
$projectPath = Join-Path $runRoot 'StartupMeasurement.csproj'
Set-Content -LiteralPath $projectPath -Value $project -Encoding utf8
$artifactsRoot = Join-Path $runRoot 'artifacts'
if (!$SkipBuild) {
    $buildArguments = @('build', $projectPath, '-c', 'Release', '-p:Platform=x64', '--artifacts-path', $artifactsRoot, '--nologo')
    if ($NuGetConfigPath) { $buildArguments += '-p:RestoreConfigFile=' + [IO.Path]::GetFullPath($NuGetConfigPath) }
    & dotnet @buildArguments
    if ($LASTEXITCODE -ne 0) { throw "Startup measurement build failed: $LASTEXITCODE" }
    $sourceAfterBuild = Get-StartupSourceSnapshot
    if ($sourceBefore.fingerprint -cne $sourceAfterBuild.fingerprint) { throw 'Compiled source changed during the build; rebuild before measuring.' }
    $sourceAfterBuild | ConvertTo-Json | Set-Content -LiteralPath $buildSourcePath -Encoding utf8
}
$executable = Get-ChildItem -LiteralPath (Join-Path $artifactsRoot 'bin/StartupMeasurement') -Filter FormatFactoryWinUI.exe -Recurse | Select-Object -First 1
if (!$executable) { throw 'The startup measurement executable was not built.' }
$engineLink = Join-Path $executable.DirectoryName 'Engines'
if (!(Test-Path -LiteralPath $engineLink)) { New-Item -ItemType Junction -Path $engineLink -Target $EngineRoot | Out-Null }
elseif ([IO.Path]::GetFullPath((Get-Item -LiteralPath $engineLink).Target) -ine $EngineRoot) { throw 'An existing Engines link points to a different engine set.' }
if ($BuildOnly) { Write-Output "Startup measurement executable: $($executable.FullName)"; return }

# The process creation and native file cache are retained between samples. No reboot,
# cache eviction, or claim of a disk-cold launch is made by this tool.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class StartupCpuTimes {
  [DllImport("kernel32.dll", SetLastError=true)]
  static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
  public static long[] Read() {
    if (!GetSystemTimes(out long idle, out long kernel, out long user)) throw new System.ComponentModel.Win32Exception();
    return new long[] { idle, kernel, user };
  }
}
'@
function Wait-StartupIdle {
    $history = @()
    for ($attempt = 0; $attempt -lt 15; $attempt++) {
        $before = [StartupCpuTimes]::Read()
        Start-Sleep -Milliseconds 1000
        $after = [StartupCpuTimes]::Read()
        $total = ($after[1] - $before[1]) + ($after[2] - $before[2])
        $busy = if ($total -gt 0) { 100.0 * ($total - ($after[0] - $before[0])) / $total } else { 0.0 }
        $history += [Math]::Round($busy, 2)
        if ($busy -lt 5) { break }
    }
    [pscustomobject]@{ samplesPercent = $history; belowFivePercent = ($history[-1] -lt 5) }
}
function Invoke-StartupProcess {
    param([string]$SampleRoot, [int]$Count, [bool]$Prepare)
    $env:FORMAT_FACTORY_STARTUP_ROOT = $SampleRoot
    $env:FORMAT_FACTORY_STARTUP_COUNT = [string]$Count
    $env:FORMAT_FACTORY_STARTUP_PREPARE = if ($Prepare) { '1' } else { '0' }
    $process = Start-Process -FilePath $executable.FullName -WorkingDirectory $executable.DirectoryName -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill()
        throw "Startup process timed out after $TimeoutSeconds seconds: $SampleRoot"
    }
    if ($process.ExitCode -ne 0) {
        $failurePath = Join-Path $SampleRoot 'failure.txt'
        if (Test-Path -LiteralPath $failurePath) { Get-Content -LiteralPath $failurePath }
        throw "Startup process failed with $($process.ExitCode): $SampleRoot"
    }
}

$environmentNames = @('FORMAT_FACTORY_STARTUP_ROOT', 'FORMAT_FACTORY_STARTUP_COUNT', 'FORMAT_FACTORY_STARTUP_PREPARE', 'FORMAT_FACTORY_DISABLE_TRAY')
$previousEnvironment = @{}
foreach ($name in $environmentNames) { $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $env:FORMAT_FACTORY_DISABLE_TRAY = '1'
    $reports = @()
    # Round-robin order prevents every large-queue measurement being the latest/warmest.
    for ($sample = 1; $sample -le $Samples; $sample++) {
        foreach ($count in $QueueCounts) {
            $sampleRoot = Join-Path $runRoot ('queue-{0}-sample-{1:D2}' -f $count, $sample)
            if (Test-Path -LiteralPath $sampleRoot) { throw "A sample directory already exists; choose a new RunName: $sampleRoot" }
            New-Item -ItemType Directory -Path $sampleRoot | Out-Null
            Invoke-StartupProcess -SampleRoot $sampleRoot -Count $count -Prepare $true
            $idle = Wait-StartupIdle
            Write-Output "Measuring visible production startup: $count tasks, sample $sample/$Samples (CPU $($idle.samplesPercent[-1])%)"
            Invoke-StartupProcess -SampleRoot $sampleRoot -Count $count -Prepare $false
            $report = Get-Content -LiteralPath (Join-Path $sampleRoot 'startup.json') -Raw | ConvertFrom-Json
            $report | Add-Member -NotePropertyName sample -NotePropertyValue $sample
            $report | Add-Member -NotePropertyName prelaunchCpu -NotePropertyValue $idle
            $report | Add-Member -NotePropertyName processOrder -NotePropertyValue ($reports.Count + 1)
            $reports += $report
        }
    }
    $sourceAfter = Get-StartupSourceSnapshot
    if ($sourceBefore.fingerprint -cne $sourceAfter.fingerprint) { throw 'Compiled source changed during measurement; repeat this run.' }
    $aggregate = foreach ($count in $QueueCounts) {
        $group = @($reports | Where-Object persistedCount -eq $count)
        $metrics = foreach ($name in @('window-visible', 'add-files-ready', 'recovery-complete')) {
            $times = @($group | ForEach-Object { [double]$_.milestonesMilliseconds.$name })
            $sorted = @($times | Sort-Object)
            $middle = [int][Math]::Floor($sorted.Count / 2)
            $median = if ($sorted.Count % 2) { $sorted[$middle] } else { ($sorted[$middle - 1] + $sorted[$middle]) / 2 }
            [pscustomobject]@{ name = $name; samplesMilliseconds = $times; medianMilliseconds = $median; minMilliseconds = $sorted[0]; maxMilliseconds = $sorted[-1] }
        }
        [pscustomobject]@{ queueCount = $count; samples = $group.Count; metrics = @($metrics) }
    }
    $engineReceipts = @(Get-ChildItem -LiteralPath $EngineRoot -Filter '*receipt*.json' -Recurse | ForEach-Object {
        [pscustomobject]@{ path = [IO.Path]::GetRelativePath($EngineRoot, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() }
    })
    $result = [pscustomobject]@{
        schemaVersion = 1; kind = 'localStartupMeasurement'; capturedAt = [DateTimeOffset]::UtcNow.ToString('o')
        source = $sourceAfter; executableSha256 = (Get-FileHash -LiteralPath $executable.FullName).Hash.ToLowerInvariant()
        engineRoot = $EngineRoot; engineReceipts = $engineReceipts
        launchConditions = 'Independent visible processes, no reboot or cache eviction; first process recorded separately by order. Preparation uses a separate process. Not disk-cold or MSIX/device acceptance evidence.'
        summary = @($aggregate); samples = $reports
    }
    $result | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $runRoot 'startup.json') -Encoding utf8
    $aggregate | ForEach-Object {
        $count = $_.queueCount
        $_.metrics | Select-Object @{Name='queueCount';Expression={$count}}, name, medianMilliseconds, minMilliseconds, maxMilliseconds
    } | Format-Table -AutoSize
    Write-Output "Startup measurement report: $(Join-Path $runRoot 'startup.json')"
}
finally {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process') }
}
