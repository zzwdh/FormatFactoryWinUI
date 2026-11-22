param(
    [string]$BaselinePath,
    [string]$HostSourcePath,
    [string]$NuGetConfigPath,
    [string]$RunName = ('run-' + (Get-Date -Format 'yyyyMMdd-HHmmss')),
    [int]$TimeoutSeconds = 90,
    [switch]$Capture,
    [switch]$Performance,
    [switch]$Startup,
    [switch]$ArchiveOnly,
    [switch]$MotionOnly,
    [switch]$BuildOnly,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
if ($Startup -and ($Performance -or $Capture)) { throw 'Startup measurements run separately from capture and synthetic performance checks.' }
if ($ArchiveOnly -and ($Performance -or $Startup)) { throw 'Archive-only checks run separately from startup and performance measurements.' }
if ($MotionOnly -and (!$Capture -or $Performance -or $Startup -or $ArchiveOnly)) { throw 'Motion-only checks require Capture and run separately from archive, startup and performance checks.' }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if ($Performance) {
    . (Join-Path $repoRoot 'packaging/ReleaseGate.ps1')
    $performanceSourceBefore = Get-ReleaseSourceSnapshot -RepositoryRoot $repoRoot
}
$hostSource = if ($HostSourcePath) { [IO.Path]::GetFullPath($HostSourcePath) } else { Join-Path $repoRoot 'src/FormatFactoryWinUI' }
$windowSource = if ($BaselinePath) { [IO.Path]::GetFullPath($BaselinePath) } else { $hostSource }
$windowSettingsSource = Join-Path $windowSource 'Windowing/MainWindow/Settings/MainWindow.Settings.cs'
if (!(Test-Path -LiteralPath $windowSettingsSource)) { $windowSettingsSource = Join-Path $windowSource 'MainWindow.Settings.cs' }
if (!(Test-Path -LiteralPath $windowSettingsSource)) { throw "Window settings source is missing: $windowSource" }
$windowMotionSource = Join-Path $windowSource 'Windowing/MainWindow/Navigation/MainWindow.Motion.cs'
if (!(Test-Path -LiteralPath $windowMotionSource)) { $windowMotionSource = Join-Path $windowSource 'MainWindow.Motion.cs' }
if ($RunName -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunName must contain only letters, digits, hyphens, or underscores.' }
$runRoot = Join-Path $repoRoot "work/ui-regression/$RunName"
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$xmlEscape = { param($value) [System.Security.SecurityElement]::Escape($value) }
[xml]$hostProject = Get-Content -LiteralPath (Join-Path $hostSource 'FormatFactoryWinUI.csproj') -Raw
$sdkReferences = @($hostProject.SelectNodes('//PackageReference') | Where-Object { $_.GetAttribute('Include') -like 'Microsoft.WindowsAppSDK*' })
if ($sdkReferences.Count -eq 0) { throw 'The production host must declare its Windows App SDK component references.' }
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
    <PlatformTarget>x64</PlatformTarget>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <UseWinUI>true</UseWinUI>
    <WindowsPackageType>None</WindowsPackageType>
    <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
    <WindowsAppSDKBackgroundTask>true</WindowsAppSDKBackgroundTask>
    <SelfContained>true</SelfContained>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <EnableDefaultPageItems>false</EnableDefaultPageItems>
    <EnableDefaultApplicationDefinition>false</EnableDefaultApplicationDefinition>
  </PropertyGroup>
  <ItemGroup>
WINDOWS_APP_SDK_REFERENCES
    <ProjectReference Include="REPO_ROOT/src/FormatFactory.Core/FormatFactory.Core.csproj" />
    <ProjectReference Include="REPO_ROOT/src/FormatFactory.Documents/FormatFactory.Documents.csproj" />
    <ProjectReference Include="REPO_ROOT/src/FormatFactory.Engine/FormatFactory.Engine.csproj" />
    <ProjectReference Include="REPO_ROOT/src/FormatFactory.Presentation/FormatFactory.Presentation.csproj" />
    <Compile Include="HOST_SOURCE/**/*.cs" Exclude="HOST_SOURCE/bin/**/*.cs;HOST_SOURCE/obj/**/*.cs;HOST_SOURCE/App.xaml.cs;HOST_SOURCE/MainWindow.xaml.cs;HOST_SOURCE/**/MainWindow.Settings.cs;HOST_SOURCE/**/MainWindow.Motion.cs" />
    <Compile Include="WINDOW_SOURCE/MainWindow.xaml.cs;WINDOW_SETTINGS_SOURCE" />
    <Compile Include="WINDOW_MOTION_SOURCE" Condition="Exists('WINDOW_MOTION_SOURCE')" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/SmokeApp.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/NativeWorkflowSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/PdfOcrDimensionsSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/PdfDocxSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/DocumentCompatibilitySmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/ScanPrintSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/PerformanceSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/ImportExperienceSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/ResultSummarySmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/NamingPreviewSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/StartupSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/QueueSessionSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/DiscRouteRecoverySmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/EpubRouteSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/EpubActivationSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/ClipboardSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/AutomationSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/AutomationUpgradeSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/ConversionOptionsSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/SettingsActionsSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/SettingsValidationSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/OpenLogsSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/OpenOutputLocationSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/AudioTrackBindingSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/OutputFolderPickerSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/CompatibleAudioTrackSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/FeedbackSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/TelemetrySmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/ArchiveConversionSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/TaskExperienceSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/FailureActionsSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/StoreUpdateSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/DesktopUpgradeSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/TrayNavigationSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/QueueExperienceSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/NavigationMotionSmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/QueueDensitySmoke.cs" />
    <Compile Include="REPO_ROOT/tests/WinUiSmoke/ProgressStateColorSmoke.cs" />
    <ApplicationDefinition Include="HOST_SOURCE/App.xaml" Link="App.xaml" />
    <Page Include="WINDOW_SOURCE/MainWindow.xaml" Link="MainWindow.xaml" />
    <Page Include="HOST_SOURCE/Views/**/*.xaml" Link="Views/%(RecursiveDir)%(Filename)%(Extension)" />
    <Content Include="WINDOW_SOURCE/MainWindow.xaml" Link="Inputs/MainWindow.xaml" CopyToOutputDirectory="PreserveNewest" />
    <Content Include="HOST_SOURCE/Views/**/*.xaml" Link="Inputs/Views/%(RecursiveDir)%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
    <Content Include="HOST_SOURCE/Assets/*" Link="Assets/%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
    <Content Include="HOST_SOURCE/feedback-config.json" Link="feedback-config.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
'@
$project = $project.Replace('HOST_SOURCE', (& $xmlEscape $hostSource)).Replace('WINDOW_SOURCE', (& $xmlEscape $windowSource)).Replace('WINDOW_SETTINGS_SOURCE', (& $xmlEscape $windowSettingsSource)).Replace('WINDOW_MOTION_SOURCE', (& $xmlEscape $windowMotionSource)).Replace('REPO_ROOT', (& $xmlEscape $repoRoot)).Replace('WINDOWS_APP_SDK_REFERENCES', $sdkReferenceXml)
$projectPath = Join-Path $runRoot 'UiSmoke.csproj'
Set-Content -LiteralPath $projectPath -Value $project -Encoding utf8
$artifactsRoot = Join-Path $runRoot 'artifacts'
if (!$SkipBuild) {
    $buildArguments = @('build', $projectPath, '-c', 'Release', '-p:Platform=x64', '--artifacts-path', $artifactsRoot, '--nologo')
    if ($NuGetConfigPath) { $buildArguments += ('-p:RestoreConfigFile=' + [IO.Path]::GetFullPath($NuGetConfigPath)) }
    & dotnet @buildArguments
    if ($LASTEXITCODE -ne 0) { throw "UI smoke build failed: $LASTEXITCODE" }
}
$executable = Get-ChildItem -LiteralPath (Join-Path $artifactsRoot 'bin/UiSmoke') -Filter 'FormatFactoryWinUI.exe' -Recurse | Select-Object -First 1
if (!$executable) { throw 'UI smoke executable was not produced.' }
if ($BuildOnly) { Write-Output "UI smoke build: $($executable.FullName)"; return }
$resultPath = Join-Path $runRoot 'results.json'
if (Test-Path -LiteralPath $resultPath) { Remove-Item -LiteralPath $resultPath }
$previousRoot = $env:FORMAT_FACTORY_UI_SMOKE_ROOT
$previousCapture = $env:FORMAT_FACTORY_UI_SMOKE_CAPTURE
$previousPerformance = $env:FORMAT_FACTORY_UI_SMOKE_PERFORMANCE
$previousStartup = $env:FORMAT_FACTORY_UI_SMOKE_STARTUP
$previousArchiveOnly = $env:FORMAT_FACTORY_UI_SMOKE_ARCHIVE_ONLY
$previousMotionOnly = $env:FORMAT_FACTORY_UI_SMOKE_MOTION_ONLY
try {
    $env:FORMAT_FACTORY_UI_SMOKE_CAPTURE = if ($Capture) { '1' } else { '0' }
    $env:FORMAT_FACTORY_UI_SMOKE_PERFORMANCE = if ($Performance) { '1' } else { '0' }
    $env:FORMAT_FACTORY_UI_SMOKE_STARTUP = $null
    $env:FORMAT_FACTORY_UI_SMOKE_ARCHIVE_ONLY = if ($ArchiveOnly) { '1' } else { '0' }
    $env:FORMAT_FACTORY_UI_SMOKE_MOTION_ONLY = if ($MotionOnly) { '1' } else { '0' }
    $reports = @()
    $runs = if ($Performance) { 1..9 } elseif ($Startup) { @(0, 100, 1000) } else { @(0) }
    foreach ($sample in $runs) {
        $sampleRoot = if ($Performance) { Join-Path $runRoot ('measurement-{0:D2}' -f $sample) } elseif ($Startup) { Join-Path $runRoot "startup-$sample" } else { $runRoot }
        if ($Startup) { $env:FORMAT_FACTORY_UI_SMOKE_STARTUP = [string]$sample }
        $env:FORMAT_FACTORY_UI_SMOKE_ROOT = $sampleRoot
        $sampleResult = Join-Path $sampleRoot 'results.json'
        foreach ($generated in @($sampleResult, (Join-Path $sampleRoot 'performance.json'), (Join-Path $sampleRoot 'startup.json'))) {
            if (Test-Path -LiteralPath $generated) { Remove-Item -LiteralPath $generated }
        }
        if ($Performance) { Write-Output "UI performance sample $sample/9" }
        $process = Start-Process -FilePath $executable.FullName -WorkingDirectory $executable.DirectoryName -WindowStyle Hidden -PassThru
        if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill()
            throw "UI smoke timed out after $TimeoutSeconds seconds."
        }
        if (!(Test-Path -LiteralPath $sampleResult)) { throw "UI smoke exited with $($process.ExitCode), without results: $sampleRoot" }
        $result = Get-Content -LiteralPath $sampleResult -Raw | ConvertFrom-Json
        if ($process.ExitCode -ne 0 -or $result.failures -gt 0) { throw "UI smoke failed. See $sampleResult" }
        if ($Performance) { $reports += Get-Content -LiteralPath (Join-Path $sampleRoot 'performance.json') -Raw | ConvertFrom-Json }
        if ($Startup) { $reports += Get-Content -LiteralPath (Join-Path $sampleRoot 'startup.json') -Raw | ConvertFrom-Json }
    }
    if ($Performance) {
        $aggregate = $reports[0]
        foreach ($report in $reports) {
            if (($report.environment | ConvertTo-Json -Compress) -ne ($aggregate.environment | ConvertTo-Json -Compress)) {
                throw 'Performance environment changed between samples.'
            }
            $fixture = $report.documentFixture | Select-Object paragraphs, charactersPerParagraph, inputBytes, engine
            $firstFixture = $aggregate.documentFixture | Select-Object paragraphs, charactersPerParagraph, inputBytes, engine
            if (($fixture | ConvertTo-Json -Compress) -ne ($firstFixture | ConvertTo-Json -Compress)) {
                throw 'Large-document input or engine changed between samples.'
            }
        }
        $aggregate.environment.samples = 9
        foreach ($metric in $aggregate.metrics) {
            $name = $metric.Name
            $parts = @($reports | ForEach-Object { $_.metrics | Where-Object Name -eq $name })
            $timings = @($parts | ForEach-Object { $_.SamplesMilliseconds })
            $metric.SamplesMilliseconds = $timings
            $metric.P95Milliseconds = @($timings | Sort-Object)[[Math]::Ceiling($timings.Count * .95) - 1]
            $metric.P95PrivateBytes = @($parts.P95PrivateBytes | Sort-Object)[[Math]::Ceiling($parts.Count * .95) - 1]
            $metric.MaxRealizedContainers = ($parts.MaxRealizedContainers | Measure-Object -Maximum).Maximum
        }
        $beforeHandles = @($reports | ForEach-Object { [int]$_.handleCounts.beforeWindow })
        $afterConstructionHandles = @($reports | ForEach-Object { [int]$_.handleCounts.afterConstruction })
        $afterCloseHandles = @($reports | ForEach-Object { [int]$_.handleCounts.afterWindowClose })
        $closeDeltas = @($reports | ForEach-Object { [int]$_.handleCounts.deltaAfterClose })
        $aggregate.handleCounts = [PSCustomObject]@{
            beforeWindow = $beforeHandles
            afterConstruction = $afterConstructionHandles
            afterWindowClose = $afterCloseHandles
            deltaAfterClose = $closeDeltas
            p95DeltaAfterClose = @($closeDeltas | Sort-Object)[[Math]::Ceiling($closeDeltas.Count * .95) - 1]
        }
        $performanceSourceAfter = Get-ReleaseSourceSnapshot -RepositoryRoot $repoRoot
        if ($performanceSourceBefore.commit -cne $performanceSourceAfter.commit -or
            $performanceSourceBefore.fingerprint -cne $performanceSourceAfter.fingerprint -or
            $performanceSourceBefore.dirty -ne $performanceSourceAfter.dirty) { throw 'Source changed during performance sampling; discard this report.' }
        # Alternate window files are useful for diagnostics, but their bytes are
        # outside this repository snapshot and cannot identify a clean release baseline.
        if ($HostSourcePath -or $BaselinePath) {
            $performanceSourceAfter.dirty = $true
            $performanceSourceAfter | Add-Member -NotePropertyName hasSourceOverrides -NotePropertyValue $true -Force
        }
        $aggregate | Add-Member -NotePropertyName source -NotePropertyValue $performanceSourceAfter -Force
        $aggregate | Add-Member -NotePropertyName capturedAt -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('o')) -Force
        $aggregate | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $runRoot 'performance.json') -Encoding utf8
        $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $resultPath -Encoding utf8
    }
    if ($Startup) {
        $reports | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $runRoot 'startup.json') -Encoding utf8
        $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $resultPath -Encoding utf8
    }
    $result.results | Select-Object name, passed | Format-Table -AutoSize
    Write-Output "Results: $resultPath"
    if ($Capture) { Write-Output "Captures: $(Join-Path $runRoot 'captures')" }
    if ($process.ExitCode -ne 0) { throw "UI smoke exited with $($process.ExitCode). See $resultPath" }
    if ($result.failures -gt 0) { throw "UI smoke failed $($result.failures) check(s). See $resultPath" }
}
finally {
    $env:FORMAT_FACTORY_UI_SMOKE_ROOT = $previousRoot
    $env:FORMAT_FACTORY_UI_SMOKE_CAPTURE = $previousCapture
    $env:FORMAT_FACTORY_UI_SMOKE_PERFORMANCE = $previousPerformance
    $env:FORMAT_FACTORY_UI_SMOKE_STARTUP = $previousStartup
    $env:FORMAT_FACTORY_UI_SMOKE_ARCHIVE_ONLY = $previousArchiveOnly
    $env:FORMAT_FACTORY_UI_SMOKE_MOTION_ONLY = $previousMotionOnly
}




