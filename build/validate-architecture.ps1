[CmdletBinding()]
param([switch]$Quiet)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
$dependencies = [ordered]@{
    "FormatFactory.Core" = @()
    "FormatFactory.Engine" = @("FormatFactory.Core")
    "FormatFactory.Documents" = @("FormatFactory.Core", "FormatFactory.Engine")
    "FormatFactory.Presentation" = @("FormatFactory.Core", "FormatFactory.Engine")
    "FormatFactoryWinUI" = @("FormatFactory.Core", "FormatFactory.Documents", "FormatFactory.Engine", "FormatFactory.Presentation", "FormatFactory.FilterWorker")
    "FormatFactory.FilterWorker" = @()
}
foreach ($entry in $dependencies.GetEnumerator()) {
    $projectPath = Join-Path $repoRoot "src\$($entry.Key)\$($entry.Key).csproj"
    [xml]$project = Get-Content -LiteralPath $projectPath -Raw
    $actual = @($project.SelectNodes("//ProjectReference") | ForEach-Object {
        [IO.Path]::GetFileNameWithoutExtension($_.GetAttribute("Include"))
    })
    if ($actual.Count -ne $entry.Value.Count -or @($actual | Where-Object { $_ -notin $entry.Value }).Count -gt 0) {
        throw "$($entry.Key) 的项目依赖违反架构边界，期望：$($entry.Value -join ', ')；实际：$($actual -join ', ')。"
    }
    if ($project.SelectNodes("//Compile[@Include]").Count -gt 0) {
        throw "$($entry.Key) 不应链接其他项目的生产源码；请使用 ProjectReference。"
    }
    if ($entry.Key -in @("FormatFactory.Core", "FormatFactory.Documents", "FormatFactory.Engine", "FormatFactory.Presentation")) {
        if (@($project.SelectNodes("//TargetFramework") | Where-Object { $_.InnerText -ne "net11.0" }).Count -gt 0 -or
            $project.SelectNodes("//UseWinUI").Count -gt 0 -or
            @($project.SelectNodes("//PackageReference") | Where-Object { $_.GetAttribute("Include") -match "WindowsAppSDK|Windows.SDK" }).Count -gt 0) {
            throw "$($entry.Key) 必须可在不引用 WinUI / WinRT 的情况下编译。"
        }
    }
}
foreach ($name in @("FormatFactoryWinUI.Tests", "FormatFactoryWinUI.TestHelper")) {
    [xml]$testProject = Get-Content -LiteralPath (Join-Path $repoRoot "tests\$name\$name.csproj") -Raw
    if ($testProject.SelectNodes("//Compile[@Include]").Count -gt 0) {
        throw "$name 必须测试生产程序集，不应重新编译生产源码。"
    }
}
if (-not $Quiet) {
    Write-Host "Core、Engine、Documents、Presentation 与 Windows 宿主的依赖校验通过。"
}
