[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][ValidateSet('x64','arm64')][string]$Architecture
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApplicationManifest.ps1')
$minimumWindowsVersion = Get-FormatFactoryMinimumWindowsVersion
Add-Type -AssemblyName System.IO.Compression.FileSystem
$package = [IO.Path]::GetFullPath($PackagePath)
if (!(Test-Path -LiteralPath $package -PathType Leaf)) { throw "MSIX does not exist: $package" }
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $entries = [Collections.Generic.Dictionary[string,IO.Compression.ZipArchiveEntry]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries) { $entries.Add($entry.FullName.Replace('\','/'), $entry) }
    function Require-Entry([string]$name) {
        if (!$entries.ContainsKey($name) -or $entries[$name].Length -eq 0) { throw "MSIX is missing or has an empty $name" }
        return $entries[$name]
    }
    function Read-EntryText([string]$name) {
        $reader = [IO.StreamReader]::new((Require-Entry $name).Open())
        try { return $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    $required = @(
        'FormatFactoryWinUI.exe','FormatFactory.Core.dll','FormatFactory.Engine.dll','FormatFactory.Documents.dll',
        'FormatFactory.Presentation.dll','FormatFactory.ConversionWorker.exe','FormatFactory.FilterWorker.exe',
        'FormatFactory.ConversionWorker.licenses.zip',
        'FormatFactory.FilterWorker.dll','FormatFactory.FilterWorker.deps.json',
        'FormatFactory.FilterWorker.runtimeconfig.json',
        'FormatFactoryWinUI.pri','App.xbf','MainWindow.xbf',
        'Views/HistoryView.xbf','Views/SettingsView.xbf','Views/QueueView.xbf','Views/ArchiveConversionView.xbf',
        'Views/PageResources.xbf','Engines/bundle-manifest.json',
        'FormatFactoryShellExtension.Native.dll','native-shell-build.json','FormatFactoryShellExtension.Native.licenses.zip'
    )
    foreach ($name in $required) { [void](Require-Entry $name) }
    foreach ($component in @('FormatFactory.ConversionWorker','FormatFactoryShellExtension.Native')) {
        $licenses = [IO.Compression.ZipArchive]::new((Require-Entry "$component.licenses.zip").Open())
        try {
            foreach ($name in @('COPYRIGHT-library.html','licenses/Apache-2.0.txt','licenses/MIT.txt')) {
                $notice = $licenses.GetEntry("rust-standard-library/$name")
                if (!$notice -or !$notice.Length) { throw "Missing $component Rust standard library notice: $name" }
            }
            foreach ($crate in @('flate2','crc32fast')) {
                if (!($licenses.Entries | Where-Object { $_.FullName -like "crates/$crate-*/*" -and $_.Length -gt 0 } | Select-Object -First 1)) {
                    throw "Missing $component notices for $crate."
                }
            }
        }
        finally { $licenses.Dispose() }
    }
    foreach ($legacy in @('FormatFactoryShellExtension.comhost.dll','FormatFactoryShellExtension.dll',
        'FormatFactoryShellExtension.deps.json','FormatFactoryShellExtension.runtimeconfig.json',
        'FormatFactoryShellExtension.X.manifest')) {
        if ($entries.ContainsKey($legacy)) { throw "MSIX still contains retired managed Shell payload: $legacy" }
    }
    [xml]$manifest = Read-EntryText 'AppxManifest.xml'
    $identity = $manifest.DocumentElement.GetElementsByTagName('Identity')[0]
    if ($null -eq $identity -or $identity.ProcessorArchitecture -cne $Architecture) {
        throw 'MSIX architecture does not match its manifest.'
    }
    $namespaces = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespaces.AddNamespace('appx', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $families = @($manifest.SelectNodes('/appx:Package/appx:Dependencies/appx:TargetDeviceFamily', $namespaces))
    if ($families.Count -ne 1 -or $families[0].Name -cne 'Windows.Desktop' -or $families[0].MinVersion -cne $minimumWindowsVersion) {
        throw "MSIX must declare one Windows.Desktop target with minimum version $minimumWindowsVersion."
    }
    Assert-FormatFactoryShellRegistration -Manifest $manifest

    function Assert-PeMachine([string]$name) {
        $entry = Require-Entry $name
        $reader = [IO.BinaryReader]::new($entry.Open())
        try {
            $header = $reader.ReadBytes(0x40)
            if ($header.Length -ne 0x40 -or $header[0] -ne 77 -or $header[1] -ne 90) { throw "Invalid PE: $name" }
            $offset = [BitConverter]::ToInt32($header, 0x3c)
            if ($offset -lt 0x40 -or $offset -gt 1MB -or [long]$offset + 6 -gt $entry.Length) {
                throw "Invalid PE header offset: $name"
            }
            # Only read the header; avoid decompressing an entire executable for its machine field.
            [void]$reader.ReadBytes($offset - 0x40)
            if ($reader.ReadUInt32() -ne 0x4550) { throw "Invalid PE signature: $name" }
            $expectedMachine = @{x64=0x8664;arm64=0xaa64}[$Architecture]
            if ($reader.ReadUInt16() -ne $expectedMachine) { throw "Wrong PE architecture: $name" }
        }
        finally { $reader.Dispose() }
    }
    foreach ($name in @('FormatFactoryWinUI.exe','FormatFactory.ConversionWorker.exe','FormatFactory.FilterWorker.exe','FormatFactoryShellExtension.Native.dll')) {
        Assert-PeMachine $name
    }
    $receipt = Read-EntryText 'native-shell-build.json' | ConvertFrom-Json
    $repository = Split-Path $PSScriptRoot -Parent
    $shellLock = (Get-FileHash -LiteralPath (Join-Path $repository 'src/FormatFactoryShellExtension.Native/rust/Cargo.lock')).Hash
    $rustVersion = (Get-Content -LiteralPath (Join-Path $repository 'build/toolchain.lock.json') -Raw | ConvertFrom-Json).rustVersion
    if ($receipt.architecture -cne $Architecture -or $receipt.configuration -cne 'Release' -or
        $receipt.dllBytes -ne $entries['FormatFactoryShellExtension.Native.dll'].Length -or $receipt.formatCount -le 0 -or
        $receipt.epubParser -cne 'rust' -or $receipt.rustLockSha256 -ine $shellLock -or $receipt.rustVersion -cne $rustVersion -or $receipt.dllSha256 -notmatch '^[0-9A-Fa-f]{64}$') {
        throw 'Native Shell build metadata does not match the Release payload.'
    }
    $stream = $entries['FormatFactoryShellExtension.Native.dll'].Open()
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = [Convert]::ToHexString($sha.ComputeHash($stream)) } finally { $sha.Dispose(); $stream.Dispose() }
    if ($hash -ine $receipt.dllSha256) { throw 'Native Shell SHA256 differs from the tested build.' }
    if (-not $receipt.testsExecuted) { Write-Warning 'Native tests were not executed for this architecture. This package is a candidate; matching device acceptance evidence is required for release.' }
    Write-Output "Validated C++ Shell $Architecture MSIX: $($entries.Count) entries; native COM registration, payload and PE architecture passed."
}
finally { $archive.Dispose() }
