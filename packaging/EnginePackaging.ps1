Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-EngineChildPath {
    param([string]$Path, [string]$Root)
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Path escapes the allowed engine directory: $full" }
    return $full
}

function Reset-EngineDirectory {
    param([string]$Path, [string]$Root)
    $full = Assert-EngineChildPath -Path $Path -Root $Root
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
    [void][IO.Directory]::CreateDirectory($full)
}

function Get-EnginePeMachine {
    param([string]$Path)
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5a4d) { throw "Not a PE file: $Path" }
        $stream.Position = 0x3c
        $offset = $reader.ReadInt32()
        if ($offset -lt 64 -or $offset -gt $stream.Length - 6) { throw "Invalid PE header: $Path" }
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x4550) { throw "Invalid PE signature: $Path" }
        return $reader.ReadUInt16()
    } finally { $reader.Dispose(); $stream.Dispose() }
}

function Get-EnginePeImports {
    param([string]$Path)
    # PEReader validates the PE headers. Import descriptors still need explicit
    # bounds checks: their RVAs must refer to actual file bytes, not virtual padding.
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream, [Text.Encoding]::ASCII, $true)
    $pe = $null
    try {
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream, [System.Reflection.PortableExecutable.PEStreamOptions]::LeaveOpen)
        $headers = $pe.PEHeaders
        if ($null -eq $headers.PEHeader) { throw "Not a PE image: $Path" }
        function Get-ImportFileRegion([long]$Rva, [long]$Length) {
            if ($Rva -lt 0 -or $Length -lt 1 -or $Rva + $Length -gt [uint32]::MaxValue) { throw "Invalid import RVA: $Path" }
            $regions = @()
            if ($Rva -lt $headers.PEHeader.SizeOfHeaders -and $Rva + $Length -le $headers.PEHeader.SizeOfHeaders) {
                $regions += [pscustomobject]@{Offset=$Rva; Available=[long]$headers.PEHeader.SizeOfHeaders-$Rva}
            }
            foreach ($section in $headers.SectionHeaders) {
                $relative = $Rva - [long]$section.VirtualAddress
                if ($relative -ge 0 -and $relative + $Length -le [long]$section.SizeOfRawData) {
                    $regions += [pscustomobject]@{Offset=[long]$section.PointerToRawData+$relative; Available=[long]$section.SizeOfRawData-$relative}
                }
            }
            if ($regions.Count -ne 1 -or $regions[0].Offset -lt 0 -or $regions[0].Offset + $regions[0].Available -gt $stream.Length) {
                throw "Import RVA is outside an unambiguous file-backed PE region: $Path"
            }
            return $regions[0]
        }
        function Get-ImportDllName([long]$Rva) {
            $region = Get-ImportFileRegion -Rva $Rva -Length 1
            $stream.Position = $region.Offset
            $bytes = [Collections.Generic.List[byte]]::new()
            # Windows module names are leaf filenames. A bounded read also rejects
            # unterminated strings instead of reading into a different PE section.
            for ($index = 0; $index -lt [Math]::Min($region.Available, 260); $index++) {
                $value = $reader.ReadByte()
                if ($value -eq 0) {
                    $name = [Text.Encoding]::ASCII.GetString($bytes.ToArray())
                    if (-not $name -or $name -match '[:\\/*?"<>|]') { throw "Invalid import module name in $Path" }
                    return $name
                }
                if ($value -lt 0x21 -or $value -gt 0x7e) { throw "Invalid import module name in $Path" }
                $bytes.Add($value)
            }
            throw "Unterminated import module name in $Path"
        }
        $imports = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($kind in @('ImportTableDirectory', 'DelayImportTableDirectory')) {
            $directory = $headers.PEHeader.$kind
            if ($directory.RelativeVirtualAddress -eq 0 -and $directory.Size -eq 0) { continue }
            $descriptorSize = if ($kind -eq 'ImportTableDirectory') { 20 } else { 32 }
            if ($directory.RelativeVirtualAddress -le 0 -or $directory.Size -lt $descriptorSize) { throw "Invalid $kind in $Path" }
            [void](Get-ImportFileRegion -Rva $directory.RelativeVirtualAddress -Length $directory.Size)
            $terminated = $false
            for ($offset = 0L; $offset + $descriptorSize -le $directory.Size; $offset += $descriptorSize) {
                $region = Get-ImportFileRegion -Rva ([long]$directory.RelativeVirtualAddress + $offset) -Length $descriptorSize
                $stream.Position = $region.Offset
                $fields = @()
                for ($field = 0; $field -lt $descriptorSize / 4; $field++) { $fields += $reader.ReadUInt32() }
                if (@($fields | Where-Object { $_ -ne 0 }).Count -eq 0) { $terminated = $true; break }
                if ($kind -eq 'ImportTableDirectory') { $nameRva = [long]$fields[3] }
                else {
                    # dlattrRva selects RVA pointers; the legacy format stores VAs.
                    if ($fields[0] -notin @(0, 1)) { throw "Unsupported delay import attributes in $Path" }
                    $nameRva = [long]$fields[1]
                    if ($fields[0] -eq 0) {
                        if ([ulong]$fields[1] -lt $headers.PEHeader.ImageBase) { throw "Invalid delay import VA in $Path" }
                        $nameRva = [long]([ulong]$fields[1] - $headers.PEHeader.ImageBase)
                    }
                }
                if ($nameRva -le 0) { throw "Missing import module name in $Path" }
                [void]$imports.Add((Get-ImportDllName -Rva $nameRva))
            }
            if (-not $terminated) { throw "Unterminated $kind in $Path" }
        }
        $anyCpu = $null -ne $headers.CorHeader -and
            [int]$headers.CoffHeader.Machine -eq 0x14c -and [int]$headers.PEHeader.Magic -eq 0x10b -and
            (([int]$headers.CorHeader.Flags -band 1) -ne 0) -and
            (([int]$headers.CorHeader.Flags -band 0x12) -eq 0) -and $headers.CorHeader.ManagedNativeHeaderDirectory.Size -eq 0
        return [pscustomobject]@{Machine=[int]$headers.CoffHeader.Machine; Magic=[int]$headers.PEHeader.Magic; Imports=@($imports | Sort-Object); IsAnyCpu=$anyCpu}
    } finally {
        if ($null -ne $pe) { $pe.Dispose() }
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Assert-CajPeDependencies {
    param([string]$Directory, [string]$RuntimeArchitecture = 'x64')
    $expectedMachine = @{x64 = 0x8664; arm64 = 0xaa64}[$RuntimeArchitecture]
    if ($null -eq $expectedMachine) { throw "Unknown CAJ runtime architecture: $RuntimeArchitecture" }
    $root = [IO.Path]::GetFullPath($Directory)
    $items = @((Get-Item -LiteralPath $root -Force)) + @(Get-ChildItem -LiteralPath $root -Recurse -Force)
    if (@($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0) { throw "CAJ payload contains a reparse point: $root" }
    $files = @($items | Where-Object { -not $_.PSIsContainer -and $_.Extension -in @('.exe', '.dll', '.pyd') })
    if ($files.Count -eq 0) { throw "CAJ payload contains no PE files: $root" }
    foreach ($file in $files) {
        $metadata = Get-EnginePeImports -Path $file.FullName
        if ($metadata.Machine -ne $expectedMachine -or $metadata.Magic -ne 0x20b) { throw "Wrong $RuntimeArchitecture CAJ PE architecture: $($file.FullName)" }
        foreach ($name in $metadata.Imports) {
            # msvcrt.dll belongs to the supported Windows baseline. The separate
            # redistributable families (including numbered and debug variants)
            # must never be satisfied by a developer machine's system directory.
            if ($name -ieq 'msvcrt.dll' -or $name -notmatch '^(msvcp|vcruntime|concrt|vcomp|msvcr)[a-z0-9_]*\.dll$') { continue }
            $searchDirectories = @($file.DirectoryName)
            # Python loads extension/native modules with their DLL directory and
            # its executable directory available; executables use their own folder.
            if ($file.Extension -ine '.exe' -and $file.DirectoryName -ine $root) { $searchDirectories += $root }
            $dependency = $null
            foreach ($searchDirectory in $searchDirectories) {
                $candidate = Join-Path $searchDirectory $name
                if (Test-Path -LiteralPath $candidate -PathType Leaf) { $dependency = $candidate; break }
            }
            if ($null -eq $dependency) { throw "Missing app-local CAJ dependency '$name' imported by $($file.FullName). Searched: $($searchDirectories -join ', ')" }
            # Every DLL is parsed above, including these runtime DLLs and their
            # transitive imports, irrespective of the receipt's short peFiles list.
        }
    }
}

function Assert-FfmpegPeDependencies {
    param([string]$Directory, [string]$RuntimeArchitecture)
    $expectedMachine = @{x64 = 0x8664; arm64 = 0xaa64}[$RuntimeArchitecture]
    if ($null -eq $expectedMachine) { throw "Unknown FFmpeg runtime architecture: $RuntimeArchitecture" }
    $root = [IO.Path]::GetFullPath($Directory)
    $items = @((Get-Item -LiteralPath $root -Force)) + @(Get-ChildItem -LiteralPath $root -Recurse -Force)
    if (@($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0) { throw "FFmpeg payload contains a reparse point: $root" }
    $files = @($items | Where-Object { -not $_.PSIsContainer -and $_.Extension -in @('.exe', '.dll') })
    if ($files.Count -eq 0) { throw "FFmpeg payload contains no PE files: $root" }
    $localModules = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $files) { [void]$localModules.Add($file.Name) }
    foreach ($file in $files) {
        $metadata = Get-EnginePeImports -Path $file.FullName
        if ($metadata.Machine -ne $expectedMachine -or $metadata.Magic -ne 0x20b) { throw "Wrong $RuntimeArchitecture FFmpeg PE architecture: $($file.FullName)" }
        foreach ($name in $metadata.Imports) {
            # FFmpeg's shared libraries and MinGW/VC runtimes are private payload,
            # never dependencies on a developer machine's PATH or system directory.
            # Windows' msvcrt.dll and msvcp_win.dll belong to the supported
            # operating-system baseline, rather than a private VC redistributable.
            $privateFamily = $name -match '^(?:(?:lib)?(?:avcodec|avdevice|avfilter|avformat|avutil|avresample|swresample|swscale|postproc)(?:-[a-z0-9_.-]+)?|lib(?:gcc_s(?:_[a-z0-9_]+)?|stdc\+\+|winpthread|ssp|gomp|quadmath|gfortran|atomic)(?:-\d+)?|(?:msvcp|vcruntime|concrt|vcomp|msvcr)[a-z0-9_]*)\.dll$'
            if ($name -in @('msvcrt.dll', 'msvcp_win.dll') -or (-not $privateFamily -and -not $localModules.Contains($name))) { continue }
            # BtbN's bin payload is flat. Each executable and DLL must resolve its
            # private imports beside itself; an unrelated nested directory cannot
            # accidentally satisfy the dependency check. This also accepts bin/
            # under an extracted archive root without relying on the host loader.
            $dependency = Join-Path $file.DirectoryName $name
            if (-not (Test-Path -LiteralPath $dependency -PathType Leaf)) {
                throw "Missing app-local FFmpeg dependency '$name' imported by $($file.FullName). Searched: $($file.DirectoryName)"
            }
            # Every DLL, including dependencies absent from peFiles, is parsed in
            # this loop, so transitive/delay imports and their architecture count.
        }
    }
}

function Get-EngineNativeModuleFiles {
    param([Parameter(Mandatory)][string]$Directory)
    foreach ($file in Get-ChildItem -LiteralPath $Directory -Recurse -File -Force) {
        if ($file.Extension -in @('.exe','.dll','.pyd')) { $file; continue }
        # LibreOffice's actual native entry point is soffice.bin. Some other .bin
        # files are resources, so select PE-shaped files rather than all .bin data.
        if ($file.Extension -notin @('.bin','.com') -or $file.Length -lt 2) { continue }
        $stream=[IO.File]::OpenRead($file.FullName)
        try { $isPe=$stream.ReadByte() -eq 0x4d -and $stream.ReadByte() -eq 0x5a } finally { $stream.Dispose() }
        if ($isPe) { $file }
    }
}

function Get-EngineNativeModuleNames {
    param([Parameter(Mandatory)][string]$Directory)
    return @(Get-EngineNativeModuleFiles -Directory $Directory |
        Select-Object -ExpandProperty Name | Sort-Object -Unique)
}

function Get-EngineForeignArchitectureFiles {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][ValidateSet('x64','arm64')][string]$RuntimeArchitecture
    )
    # Capture from the locked, unpruned source after its VC runtime correction.
    # LibreOffice includes intentional 32-bit helpers. Bind each exception to
    # its original relative path and bytes rather than broadly allowing x86.
    $expectedMachine = @{x64=0x8664; arm64=0xaa64}[$RuntimeArchitecture]
    foreach ($file in Get-EngineNativeModuleFiles -Directory $Directory) {
        $metadata = Get-EnginePeImports -Path $file.FullName
        if (($file.Extension -ieq '.dll' -and $metadata.IsAnyCpu) -or
            ($metadata.Machine -eq $expectedMachine -and $metadata.Magic -eq 0x20b)) { continue }
        [pscustomobject]@{
            path=[IO.Path]::GetRelativePath([IO.Path]::GetFullPath($Directory), $file.FullName).Replace('\','/')
            machine=$metadata.Machine; magic=$metadata.Magic
            sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
}

function Assert-PrunedEngineNativeClosure {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][ValidateSet('x64','arm64')][string]$RuntimeArchitecture,
        [Parameter(Mandatory)][string[]]$KnownPrivateModules,
        [object[]]$AllowedArchitectureFiles = @()
    )
    $expectedMachine = @{x64=0x8664; arm64=0xaa64}[$RuntimeArchitecture]
    $root = [IO.Path]::GetFullPath($Directory)
    $items = @((Get-Item -LiteralPath $root -Force)) + @(Get-ChildItem -LiteralPath $root -Recurse -Force)
    if (@($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0) { throw "Pruned engine contains a reparse point: $root" }
    $files = @(Get-EngineNativeModuleFiles -Directory $root)
    if ($files.Count -eq 0) { throw "Pruned engine contains no native modules: $root" }
    $original = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $KnownPrivateModules) {
        if (-not $name -or [IO.Path]::GetFileName($name) -cne $name -or $name -match '[:\\/]') { throw 'Original engine module names must be leaf filenames.' }
        [void]$original.Add($name)
    }
    $allowed = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($exception in $AllowedArchitectureFiles) {
        $relative = [string]$exception.path
        if (-not $relative -or [IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[\\/])\.\.?([\\/]|$)|:') { throw 'Invalid original architecture exception path.' }
        $exceptionPath = Assert-EngineChildPath -Path (Join-Path $root $relative) -Root $root
        if ([string]$exception.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $allowed.ContainsKey($exceptionPath)) { throw 'Invalid or duplicate original architecture exception.' }
        $allowed.Add($exceptionPath, $exception)
    }
    $retained = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $files) { [void]$retained.Add($file.Name) }
    foreach ($file in $files) {
        $metadata = Get-EnginePeImports -Path $file.FullName
        $neutralAssembly = $file.Extension -ieq '.dll' -and $metadata.IsAnyCpu
        if (-not $neutralAssembly -and ($metadata.Machine -ne $expectedMachine -or $metadata.Magic -ne 0x20b)) {
            $originalFile = if ($allowed.ContainsKey($file.FullName)) { $allowed[$file.FullName] } else { $null }
            if ($null -eq $originalFile -or $originalFile.machine -ne $metadata.Machine -or $originalFile.magic -ne $metadata.Magic -or
                (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ine $originalFile.sha256) {
                throw "Wrong $RuntimeArchitecture pruned engine PE architecture: $($file.FullName)"
            }
        }
        foreach ($name in $metadata.Imports) {
            # The original inventory distinguishes private modules from Windows
            # imports without an OS-version-specific DLL allowlist. Every retained
            # PE is inspected, including unused plugins and delayed dependencies.
            if ($original.Contains($name) -and -not $retained.Contains($name)) { throw "Pruning removed private dependency '$name' still imported by $($file.FullName)" }
        }
    }
}

function Assert-EngineFiles {
    param([string]$Directory, [string[]]$RequiredFiles, [string[]]$PeFiles = @(), [string]$RuntimeArchitecture = '')
    foreach ($name in $RequiredFiles) {
        $file = Assert-EngineChildPath -Path (Join-Path $Directory $name) -Root $Directory
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) { throw "Required engine file is missing or empty: $file" }
    }
    if ($PeFiles.Count -gt 0) {
        $expectedMachine = @{x64 = 0x8664; arm64 = 0xaa64}[$RuntimeArchitecture]
        if ($null -eq $expectedMachine) { throw "Unknown runtime architecture: $RuntimeArchitecture" }
        foreach ($name in $PeFiles) {
            $file = Assert-EngineChildPath -Path (Join-Path $Directory $name) -Root $Directory
            if ((Get-EnginePeMachine $file) -ne $expectedMachine) { throw "Wrong $RuntimeArchitecture PE architecture: $file" }
        }
    }
    $links = @(Get-ChildItem -LiteralPath $Directory -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
    if ($links.Count -gt 0) { throw "Engine payload contains a reparse point: $($links[0].FullName)" }
}

function Get-LockedEngineArchive {
    param($Artifact, [string]$CacheDirectory, [switch]$Offline)
    $name = [string]$Artifact.fileName
    if ([IO.Path]::GetFileName($name) -cne $name -or $name -match '[:\\/]') { throw "Archive name must be a leaf: $name" }
    if ([string]$Artifact.sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw "Archive must have a fixed SHA256: $name" }
    $uri = [Uri]$Artifact.url
    if (-not $uri.IsAbsoluteUri -or $uri.Scheme -ne 'https') { throw "Archive URL must use HTTPS: $name" }
    [void][IO.Directory]::CreateDirectory($CacheDirectory)
    $path = Join-Path $CacheDirectory $name
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $Artifact.sha256) { throw "Cached archive SHA256 mismatch; remove or replace the corrupt file: $path" }
        return $path
    }
    if ($Offline) { throw "Offline build is missing locked archive: $path" }
    $partial = "$path.$([Guid]::NewGuid().ToString('N')).partial"
    try {
        Invoke-WebRequest -Uri $uri.AbsoluteUri -OutFile $partial -MaximumRedirection 10
        if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ine $Artifact.sha256) { throw "Downloaded archive SHA256 mismatch: $name" }
        Move-Item -LiteralPath $partial -Destination $path
    } finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force } }
    return $path
}

function Expand-SafeEngineZip {
    param([string]$ArchivePath, [string]$Destination)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    try {
        foreach ($entry in $archive.Entries) {
            $relative = $entry.FullName.Replace('\', '/')
            if ($relative -match '(^/|:|(^|/)\.\.(/|$))' -or (($entry.ExternalAttributes -shr 16) -band 0xf000) -eq 0xa000) { throw "Unsafe ZIP entry: $relative" }
            $target = Assert-EngineChildPath -Path (Join-Path $Destination $relative) -Root $Destination
            if (-not $names.Add($target)) { throw "Duplicate ZIP entry: $relative" }
            if ($relative.EndsWith('/')) { [void][IO.Directory]::CreateDirectory($target); continue }
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $false)
        }
    } finally { $archive.Dispose() }
}

function Expand-EngineMsi {
    param([string]$ArchivePath, [string]$Destination)
    # Administrative extraction only; it does not install/register the product.
    $log = "$Destination.msi.log"
    $arguments = @('/a', ('"' + $ArchivePath + '"'), '/qn', ('TARGETDIR="' + $Destination + '"'), '/L*v', ('"' + $log + '"'))
    for ($attempt = 0; $attempt -lt 12; $attempt++) {
        $process = Start-Process -FilePath 'msiexec.exe' -ArgumentList $arguments -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit(600000)) { $process.Kill(); throw "MSI extraction timed out; see $log" }
        if ($process.ExitCode -eq 0) { return }
        if ($process.ExitCode -ne 1618) { throw "MSI extraction failed ($($process.ExitCode)); see $log" }
        Start-Sleep -Seconds 5
    }
    throw "Another MSI operation prevented extraction; see $log"
}

function Expand-SevenZipEngine {
    param($Engine, [string]$ArchivePath, [string]$Destination, [string]$ExtractDirectory,
        [string]$CacheDirectory, [switch]$Offline)
    # The official installer is a 7z SFX archive, never launched as an installer.
    # Windows libarchive lists it but cannot reliably decode its BCJ2 filter.
    # Use the independently SHA256-locked, public-domain official 7zr bootstrap.
    $bootstrap = Get-LockedEngineArchive -Artifact $Engine.bootstrap -CacheDirectory $CacheDirectory -Offline:$Offline
    $source = Get-LockedEngineArchive -Artifact $Engine.source -CacheDirectory $CacheDirectory -Offline:$Offline
    if ((Get-EnginePeMachine $bootstrap) -ne 0x14c) { throw 'Wrong architecture for the locked 7zr bootstrap.' }
    [void][IO.Directory]::CreateDirectory($ExtractDirectory)
    $runtimeFiles = @('7z.exe','7z.dll','License.txt','readme.txt','History.txt')
    & $bootstrap x $ArchivePath ('-o' + $ExtractDirectory) -y @runtimeFiles | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract the official 7-Zip runtime.' }
    [void][IO.Directory]::CreateDirectory($Destination)
    foreach ($name in $runtimeFiles) {
        $file = Assert-EngineChildPath -Path (Join-Path $ExtractDirectory $name) -Root $ExtractDirectory
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or ((Get-Item -LiteralPath $file).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Invalid 7-Zip runtime file: $name"
        }
        Copy-Item -LiteralPath $file -Destination $Destination
    }
    $licenses = Join-Path $Destination 'licenses'
    [void][IO.Directory]::CreateDirectory($licenses)
    # Retain the exact matching source archive as well as the complete license texts.
    Copy-Item -LiteralPath $source -Destination (Join-Path $licenses $Engine.source.fileName)
    & tar.exe -xf $source -C $ExtractDirectory -- DOC/copying.txt DOC/unRarLicense.txt
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract the 7-Zip license texts.' }
    Copy-Item -LiteralPath (Join-Path $ExtractDirectory 'DOC/copying.txt') -Destination (Join-Path $licenses 'COPYING.LGPL2.1.txt')
    Copy-Item -LiteralPath (Join-Path $ExtractDirectory 'DOC/unRarLicense.txt') -Destination (Join-Path $licenses 'unRarLicense.txt')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'archive/NOTICE.md') -Destination $Destination
}

function Copy-EngineTree {
    param([string]$Source, [string]$Destination, [string[]]$ExcludeRoots = @())
    [void][IO.Directory]::CreateDirectory($Destination)
    $links = @(Get-ChildItem -LiteralPath $Source -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
    if ($links.Count -gt 0) { throw "Refusing to copy reparse point: $($links[0].FullName)" }
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        if ($item.Name -in $ExcludeRoots) { continue }
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
    }
}

function Get-EnginePreparationFingerprint {
    param([string]$PackagingDirectory)
    $inputs = @(
        foreach ($name in @('EnginePackaging.ps1','prepare-engines.ps1','LibreOfficePruning.ps1','CalibrePruning.ps1','engine-sources.json','BUNDLED-ENGINES.md')) { Get-Item -LiteralPath (Join-Path $PackagingDirectory $name) }
        foreach ($name in @('caj','optical','vcredist','archive','ffmpeg')) { Get-ChildItem -LiteralPath (Join-Path $PackagingDirectory $name) -Recurse -File }
    ) | Sort-Object FullName
    $lines = @($inputs | ForEach-Object { $_.FullName.Substring($PackagingDirectory.Length) + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash })
    $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join [Environment]::NewLine))
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Write-EngineReceipt {
    param([string]$EngineRoot, [string]$Architecture, [string]$Fingerprint, [object[]]$Engines)
    $files = @(Get-ChildItem -LiteralPath $EngineRoot -Recurse -File | Where-Object { $_.FullName -ne (Join-Path $EngineRoot 'bundle-manifest.json') } | Sort-Object FullName | ForEach-Object {
        [ordered]@{path = [IO.Path]::GetRelativePath($EngineRoot, $_.FullName).Replace('\', '/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    $receipt = [ordered]@{schemaVersion = 1; architecture = $Architecture; preparationFingerprint = $Fingerprint; engines = $Engines; files = $files}
    [IO.File]::WriteAllText((Join-Path $EngineRoot 'bundle-manifest.json'), ($receipt | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
}

function Assert-EngineReceipt {
    param([string]$EngineRoot, [string]$Architecture, [string]$Fingerprint = '')
    $receiptPath = Join-Path $EngineRoot 'bundle-manifest.json'
    if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) { throw "Engine bundle receipt is missing: $receiptPath" }
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    if ($receipt.schemaVersion -ne 1 -or $receipt.architecture -cne $Architecture) { throw 'Engine bundle receipt has incorrect schema/architecture.' }
    if ($Fingerprint -and $receipt.preparationFingerprint -cne $Fingerprint) { throw 'Engine bundle is stale; prepare it again with the current locked sources/scripts.' }
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $receipt.files) {
        if (-not $expected.Add([string]$file.path)) { throw "Duplicate engine receipt entry: $($file.path)" }
        $path = Assert-EngineChildPath -Path (Join-Path $EngineRoot $file.path) -Root $EngineRoot
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $file.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $file.sha256) { throw "Engine bundle file is missing/modified: $path" }
    }
    $actualCount = @(Get-ChildItem -LiteralPath $EngineRoot -Recurse -File | Where-Object { $_.FullName -ne $receiptPath }).Count
    if ($actualCount -ne $expected.Count) { throw 'Engine bundle contains untracked files.' }
    foreach ($engine in $receipt.engines) {
        $directory = Join-Path $EngineRoot $engine.id
        Assert-EngineFiles -Directory $directory -RequiredFiles @($engine.requiredFiles) -PeFiles @($engine.peFiles) -RuntimeArchitecture $engine.runtimeArchitecture
        if ($engine.id -eq 'caj2pdf') { Assert-CajPeDependencies -Directory $directory -RuntimeArchitecture $engine.runtimeArchitecture }
        if ($engine.id -in @('ffmpeg', 'ffmpeg-compatibility')) { Assert-FfmpegPeDependencies -Directory $directory -RuntimeArchitecture $engine.runtimeArchitecture }
        if ($engine.PSObject.Properties.Name -contains 'nativeModuleNames') {
            $architectureExceptions = if ($engine.PSObject.Properties.Name -contains 'allowedArchitectureFiles') { @($engine.allowedArchitectureFiles) } else { @() }
            Assert-PrunedEngineNativeClosure -Directory $directory -RuntimeArchitecture $engine.runtimeArchitecture -KnownPrivateModules @($engine.nativeModuleNames) -AllowedArchitectureFiles $architectureExceptions
        }
    }
    return $receipt
}

function Assert-MsixEnginePayload {
    param([string]$PackagePath, [string]$EngineRoot, [string]$Architecture)
    # Repeat the staged PE dependency gates, then bind every packaged byte to that
    # validated payload below; packaged DLL omissions or substitutions cannot pass.
    $receipt = Assert-EngineReceipt -EngineRoot $EngineRoot -Architecture $Architecture
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entries = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $zip.Entries) {
            $name = [Uri]::UnescapeDataString($entry.FullName).Replace('\','/')
            if ($name.StartsWith('Engines/', [StringComparison]::OrdinalIgnoreCase) -and -not $name.EndsWith('/')) {
                if ($entries.ContainsKey($name)) { throw "Duplicate engine package entry: $name" }
                $entries.Add($name, $entry)
            }
        }
        $receiptFile = Get-Item -LiteralPath (Join-Path $EngineRoot 'bundle-manifest.json')
        $files = @($receipt.files) + @([pscustomobject]@{path='bundle-manifest.json';bytes=$receiptFile.Length;sha256=(Get-FileHash -LiteralPath $receiptFile.FullName -Algorithm SHA256).Hash})
        if ($entries.Count -ne $files.Count) { throw 'MSIX engine file count differs from the validated staging receipt.' }
        foreach ($file in $files) {
            $name = 'Engines/' + $file.path
            if (-not $entries.ContainsKey($name)) { throw "Missing engine package entry: $name" }
            $entry = $entries[$name]
            if ($entry.Length -ne $file.bytes) { throw "Incorrect engine package length: $name" }
            $stream = $entry.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $hash = [Convert]::ToHexString($sha.ComputeHash($stream)) } finally { $sha.Dispose(); $stream.Dispose() }
            if ($hash -ine $file.sha256) { throw "Engine package SHA256 mismatch: $name" }
        }
        Write-Host "Verified $($files.Count) engine payload files inside $Architecture MSIX."
    } finally { $zip.Dispose() }
}
