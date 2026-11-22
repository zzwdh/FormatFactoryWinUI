[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64', 'all')][string]$Architecture = 'all',
    [string]$CacheDirectory = (Join-Path $PSScriptRoot '../work/engine-downloads'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../work/prepared-engines'),
    [switch]$Offline,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
. (Join-Path $PSScriptRoot 'EnginePackaging.ps1')
. (Join-Path $PSScriptRoot 'LibreOfficePruning.ps1')
. (Join-Path $PSScriptRoot 'CalibrePruning.ps1')
$repoRoot = Split-Path $PSScriptRoot -Parent
$workRoot = Join-Path $repoRoot 'work'
$outputRoot = Assert-EngineChildPath -Path $OutputDirectory -Root $workRoot
$cacheRoot = [IO.Path]::GetFullPath($CacheDirectory)
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'engine-sources.json') -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1) { throw 'Unsupported engine source schema.' }
$fingerprint = Get-EnginePreparationFingerprint -PackagingDirectory $PSScriptRoot
$architectures = if ($Architecture -eq 'all') { @('x64', 'arm64') } else { @($Architecture) }

function Copy-LibreOfficeConversionRuntime {
    param([string]$Source, [string]$Destination, [string]$RuntimeArchitecture)
    Copy-EngineTree -Source $Source -Destination $Destination -ExcludeRoots @('help', 'System', 'System64', 'Fonts')
    # Administrative MSI images include another MSI. It is not part of the runtime.
    Get-ChildItem -LiteralPath $Destination -File -Filter '*.msi' | Remove-Item -Force
    $program = Join-Path $Destination 'program'
    $machine = @{x64=0x8664; arm64=0xaa64}[$RuntimeArchitecture]
    if ($RuntimeArchitecture -eq 'arm64') {
        # This MSI's System64 folder contains x64 runtime DLLs despite its native ARM64 application.
        & (Join-Path $PSScriptRoot 'vcredist/Prepare-VcRuntime.ps1') -Destination $program -CacheDirectory $cacheRoot -Architecture arm64 -Offline:$Offline
    } else {
        $vcFiles = @(Get-ChildItem -LiteralPath (Join-Path $Source 'System64') -File -Filter '*.dll')
        if ($vcFiles.Count -eq 0) { throw 'LibreOffice VC runtime is missing from the administrative image.' }
        foreach ($file in $vcFiles) {
            if ((Get-EnginePeMachine $file.FullName) -ne $machine) { throw "Wrong architecture VC runtime: $($file.FullName)" }
            Copy-Item -LiteralPath $file.FullName -Destination $program
        }
    }
    # LibreOffice also searches its private font directory; no system font installation is needed.
    $fonts = Join-Path $Destination 'share/fonts/truetype'
    [void][IO.Directory]::CreateDirectory($fonts)
    Get-ChildItem -LiteralPath (Join-Path $Source 'Fonts') -File | Copy-Item -Destination $fonts
    foreach ($relative in @('share/gallery', 'share/template', 'share/wizards', 'share/extensions/nlpsolver', 'share/extensions/wiki-publisher', 'program/shell', 'program/shlxthdl')) {
        $path = Assert-EngineChildPath -Path (Join-Path $Destination $relative) -Root $Destination
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
    # The backend has no interactive UI. Keep English resources and all language layout/filter data.
    Get-ChildItem -LiteralPath (Join-Path $program 'resource') -Directory | Where-Object Name -ne 'en-US' | Remove-Item -Recurse -Force
    Get-ChildItem -LiteralPath (Join-Path $Destination 'share/registry/res') -File | Where-Object Name -NotLike '*_en-US.xcd' | Remove-Item -Force
    Get-ChildItem -LiteralPath (Join-Path $Destination 'share/config') -File -Filter 'images*.zip' | Where-Object Name -NotIn @('images.zip','images_colibre.zip') | Remove-Item -Force
    foreach ($name in @('sbase.exe','scalc.exe','sdraw.exe','simpress.exe','smath.exe','swriter.exe','unopkg.exe','unopkg.com','senddoc.exe','updater.exe')) {
        $path = Join-Path $program $name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
    # Keep every hyphenation dictionary because it can affect line/page breaks.
    # Spell-check/thesaurus data is only used while editing. Remove its registry entries too.
    foreach ($directory in Get-ChildItem -LiteralPath (Join-Path $Destination 'share/extensions') -Directory -Filter 'dict-*') {
        $configPath = Join-Path $directory.FullName 'dictionaries.xcu'
        if (-not (Test-Path -LiteralPath $configPath)) { continue }
        $xml = [Xml.XmlDocument]::new(); $xml.PreserveWhitespace = $true; $xml.Load($configPath)
        $ns = [Xml.XmlNamespaceManager]::new($xml.NameTable); $ns.AddNamespace('oor','http://openoffice.org/2001/registry')
        $retained = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($node in @($xml.SelectNodes('//node[prop[@oor:name="Format"]]', $ns))) {
            $format = $node.SelectSingleNode('prop[@oor:name="Format"]/value', $ns).InnerText
            if ($format -eq 'DICT_HYPH') {
                foreach ($location in ($node.SelectSingleNode('prop[@oor:name="Locations"]/value', $ns).InnerText -split '\s+')) {
                    [void]$retained.Add([IO.Path]::GetFileName($location))
                }
            } else { [void]$node.ParentNode.RemoveChild($node) }
        }
        $xml.Save($configPath)
        foreach ($file in Get-ChildItem -LiteralPath $directory.FullName -Recurse -File | Where-Object Extension -In @('.dic','.aff','.dat','.idx')) {
            if (-not $retained.Contains($file.Name)) { Remove-Item -LiteralPath $file.FullName -Force }
        }
    }
}

foreach ($arch in $architectures) {
    $targetRoot = Join-Path $outputRoot "$arch/Engines"
    if (-not $Force -and (Test-Path -LiteralPath (Join-Path $targetRoot 'bundle-manifest.json'))) {
        $existing = Get-Content -LiteralPath (Join-Path $targetRoot 'bundle-manifest.json') -Raw | ConvertFrom-Json
        if ($existing.preparationFingerprint -ceq $fingerprint) {
            [void](Assert-EngineReceipt -EngineRoot $targetRoot -Architecture $arch -Fingerprint $fingerprint)
            Write-Host "Verified existing $arch engines: $targetRoot"
            continue
        }
    }
    $scratch = Join-Path $workRoot ('engine-preparation-' + $arch + '-' + [Guid]::NewGuid().ToString('N'))
    $staging = Join-Path $scratch 'Engines'
    [void][IO.Directory]::CreateDirectory($staging)
    $receipts = [Collections.Generic.List[object]]::new()
    try {
        # The CAJ interpreter is also the isolated Python used by the source
        # build tools. Prepare it before the compatibility decoder on a clean checkout.
        & (Join-Path $PSScriptRoot 'caj/Prepare-CajEngine.ps1') -Destination (Join-Path $staging 'caj2pdf') -CacheDirectory $cacheRoot -Architecture $arch -Offline:$Offline
        foreach ($engine in $manifest.engines) {
            $nativeModuleNames = @()
            $architectureExceptions = @()
            $artifact = $engine.artifacts.$arch
            $archivePath = Get-LockedEngineArchive -Artifact $artifact -CacheDirectory $cacheRoot -Offline:$Offline
            $destination = Join-Path $staging $engine.id
            [void][IO.Directory]::CreateDirectory($destination)
            $extract = Join-Path $scratch ('extract-' + $engine.id)
            [void][IO.Directory]::CreateDirectory($extract)
            Write-Host "Preparing $arch $($engine.id) $($engine.version)..."
            switch ($engine.strategy) {
                'librempegConversionBuild' {
                    & (Join-Path $PSScriptRoot 'ffmpeg/Build-CompatibilityRuntime.ps1') -SourceArchivePath $archivePath `
                        -PythonPath (Join-Path $staging 'caj2pdf/python.exe') -CacheDirectory $cacheRoot `
                        -OutputDirectory $destination -Offline:$Offline
                    Assert-FfmpegPeDependencies -Directory $destination -RuntimeArchitecture $artifact.runtimeArchitecture
                }
                'sevenZipInstaller' {
                    Expand-SevenZipEngine -Engine $engine -ArchivePath $archivePath -Destination $destination -ExtractDirectory $extract -CacheDirectory $cacheRoot -Offline:$Offline
                }
                'librempegSevenZip' {
                    # Extract only locked, known regular files, never arbitrary paths from an archive.
                    $files = @($engine.requiredFiles)
                    & tar.exe -xf $archivePath -C $extract -- @files
                    if ($LASTEXITCODE -ne 0) { throw 'Could not extract the compatibility decoder runtime.' }
                    Copy-EngineTree -Source $extract -Destination $destination
                    $decoders = & (Join-Path $destination 'ffmpeg.exe') -hide_banner -decoders 2>&1
                    if ($LASTEXITCODE -ne 0 -or -not ($decoders -match '\bbvc2\s')) { throw 'The compatibility engine lacks the required BVC2 decoder.' }
                }
                'ffmpegZip' {
                    Expand-SafeEngineZip -ArchivePath $archivePath -Destination $extract
                    $binaries = @(Get-ChildItem -LiteralPath $extract -Recurse -File -Filter 'ffmpeg.exe')
                    if ($binaries.Count -ne 1) { throw 'Expected exactly one FFmpeg executable.' }
                    $binaryDirectory = $binaries[0].DirectoryName
                    $sharedLibraries = @(Get-ChildItem -LiteralPath $binaryDirectory -File -Filter '*.dll')
                    $lockedLibraries = @($engine.peFiles | Where-Object { [IO.Path]::GetExtension($_) -ieq '.dll' })
                    if ($lockedLibraries.Count -eq 0 -or $sharedLibraries.Count -ne $lockedLibraries.Count -or
                        @(Compare-Object -ReferenceObject $lockedLibraries -DifferenceObject @($sharedLibraries.Name)).Count -ne 0) {
                        throw 'FFmpeg shared DLLs differ from the locked runtime dependency list.'
                    }
                    Copy-Item -LiteralPath $binaries[0].FullName -Destination $destination
                    Copy-Item -LiteralPath (Join-Path $binaryDirectory 'ffprobe.exe') -Destination $destination
                    # Both tools use the same complete GPL shared runtime; ffplay is not needed.
                    foreach ($library in $sharedLibraries) {
                        Copy-Item -LiteralPath $library.FullName -Destination $destination
                    }
                    $archiveRoot = Split-Path $binaryDirectory -Parent
                    foreach ($file in Get-ChildItem -LiteralPath $archiveRoot -File | Where-Object Name -Match '^(LICENSE|COPYING|README|NOTICE)') {
                        Copy-Item -LiteralPath $file.FullName -Destination $destination
                    }
                    foreach ($directory in Get-ChildItem -LiteralPath $archiveRoot -Directory | Where-Object Name -Match '^(licenses?|notices?)$') {
                        Copy-Item -LiteralPath $directory.FullName -Destination $destination -Recurse
                    }
                    Assert-FfmpegPeDependencies -Directory $destination -RuntimeArchitecture $artifact.runtimeArchitecture
                }
                'libreofficeMsi' {
                    Expand-EngineMsi -ArchivePath $archivePath -Destination $extract
                    $nativeModuleNames = @(Get-EngineNativeModuleNames -Directory $extract)
                    Copy-LibreOfficeConversionRuntime -Source $extract -Destination $destination -RuntimeArchitecture $artifact.runtimeArchitecture
                    $architectureExceptions = @(Get-EngineForeignArchitectureFiles -Directory $destination -RuntimeArchitecture $artifact.runtimeArchitecture)
                    $pruning = Optimize-LibreOfficeConversionRuntime -Directory $destination
                    Write-Host "LibreOffice conversion profile removed $($pruning.RemovedFiles.Count) files ($([Math]::Round($pruning.SavedBytes / 1MB, 2)) MiB)."
                }
                'calibreMsi' {
                    Expand-EngineMsi -ArchivePath $archivePath -Destination $extract
                    $roots = @(Get-ChildItem -LiteralPath $extract -Recurse -File -Filter 'ebook-convert.exe')
                    if ($roots.Count -ne 1) { throw 'Expected exactly one Calibre runtime.' }
                    $calibreRoot = $roots[0].DirectoryName
                    $nativeModuleNames = @(Get-EngineNativeModuleNames -Directory $calibreRoot)
                    # Keep the frozen conversion modules and then remove only the
                    # separately validated reader/browser/TTS runtime components.
                    Copy-Item -LiteralPath (Join-Path $calibreRoot 'app') -Destination $destination -Recurse
                    foreach ($name in @('ebook-convert.exe','ebook-meta.exe','calibre-parallel.exe','LICENSE')) {
                        Copy-Item -LiteralPath (Join-Path $calibreRoot $name) -Destination $destination
                    }
                    $architectureExceptions = @(Get-EngineForeignArchitectureFiles -Directory $destination -RuntimeArchitecture $artifact.runtimeArchitecture)
                    $pruning = Optimize-CalibreConversionRuntime -Directory $destination
                    Write-Host "Calibre conversion profile removed $($pruning.RemovedFiles.Count) files ($([Math]::Round($pruning.SavedBytes / 1MB, 2)) MiB)."
                }
                'installer' { Copy-Item -LiteralPath $archivePath -Destination $destination }
                default { throw "Unknown engine preparation strategy: $($engine.strategy)" }
            }
            Assert-EngineFiles -Directory $destination -RequiredFiles @($engine.requiredFiles) -PeFiles @($engine.peFiles) -RuntimeArchitecture $artifact.runtimeArchitecture
            $engineReceipt = [ordered]@{id=$engine.id; version=$engine.version; payloadMode=$engine.payloadMode; runtimeArchitecture=$artifact.runtimeArchitecture; sourceUrl=$artifact.url; archiveSha256=$artifact.sha256; requiredFiles=@($engine.requiredFiles); peFiles=@($engine.peFiles)}
            if ($nativeModuleNames.Count -gt 0) {
                Assert-PrunedEngineNativeClosure -Directory $destination -RuntimeArchitecture $artifact.runtimeArchitecture -KnownPrivateModules $nativeModuleNames -AllowedArchitectureFiles $architectureExceptions
                $engineReceipt.nativeModuleNames = $nativeModuleNames
                $engineReceipt.allowedArchitectureFiles = $architectureExceptions
                $engineReceipt.pruningProfile = 'formatfactory-conversion-only-v1'
            }
            $receipts.Add($engineReceipt)
            $safeExtract = Assert-EngineChildPath -Path $extract -Root $scratch
            Remove-Item -LiteralPath $safeExtract -Recurse -Force
        }
        $receipts.Add([ordered]@{id='caj2pdf'; version='6c4bc32b15ce748d211f45d536f5d5511ef9f368'; payloadMode='runtime'; runtimeArchitecture='x64'; requiredFiles=@('python.exe','python312.dll','python312.zip','python312._pth','launcher.py','mutool.exe','msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll','app/cajparser.py','app/lib/bin/libjbigdec-w64.dll','app/lib/bin/libjbig2codec-w64.dll','sources.lock.json'); peFiles=@('python.exe','python312.dll','mutool.exe','msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll','app/lib/bin/libjbigdec-w64.dll','app/lib/bin/libjbig2codec-w64.dll')})
        & (Join-Path $PSScriptRoot 'optical/Prepare-OpticalEngines.ps1') -Destination $staging -CacheDirectory $cacheRoot -Architecture $arch -Offline:$Offline
        foreach ($optical in @(@{id='cdda2wav'; exe='icedax.exe'}, @{id='readom'; exe='readom.exe'})) {
            $receipts.Add([ordered]@{id=$optical.id; version='cdrkit-1.1.11-1/cygwin-3.6.10-1'; payloadMode='runtime'; runtimeArchitecture='x64'; requiredFiles=@($optical.exe,'cygwin1.dll','licenses/CDRKIT_COPYING.txt','licenses/CYGWIN_COPYING.txt','licenses/CYGWIN_LICENSE','sources.lock.json'); peFiles=@($optical.exe,'cygwin1.dll')})
        }
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'engine-sources.json') -Destination $staging
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'BUNDLED-ENGINES.md') -Destination $staging
        Write-EngineReceipt -EngineRoot $staging -Architecture $arch -Fingerprint $fingerprint -Engines $receipts.ToArray()
        [void](Assert-EngineReceipt -EngineRoot $staging -Architecture $arch -Fingerprint $fingerprint)
        $archRoot = Join-Path $outputRoot $arch
        Reset-EngineDirectory -Path $archRoot -Root $outputRoot
        Move-Item -LiteralPath $staging -Destination $targetRoot
        $bytes = (Get-ChildItem -LiteralPath $targetRoot -Recurse -File | Measure-Object Length -Sum).Sum
        Write-Host "Prepared $arch engines: $targetRoot ($([math]::Round($bytes / 1MB, 2)) MiB)"
    } finally {
        $safeScratch = Assert-EngineChildPath -Path $scratch -Root $workRoot
        if (Test-Path -LiteralPath $safeScratch) { Remove-Item -LiteralPath $safeScratch -Recurse -Force }
    }
}
