# The application invokes native Writer import/export in --headless mode. Keep
# the complete rendering/filter DLL set, fonts, locale data and hyphenation.
# Remove only separate scripting runtimes and resources used while authoring.
function Optimize-LibreOfficeConversionRuntime {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Directory)
    $ErrorActionPreference = 'Stop'
    $root = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    if ($root -eq [IO.Path]::GetPathRoot($root).TrimEnd('\', '/') -or
        -not (Test-Path -LiteralPath $root -PathType Container)) {
        throw 'LibreOffice pruning requires an existing runtime directory, not a filesystem root.'
    }
    $prefix = $root + [IO.Path]::DirectorySeparatorChar
    # Check the root's ancestors as well as descendants before making changes.
    # A junction above a seemingly valid runtime must not redirect deletions.
    $ancestor = $root
    while ($ancestor) {
        $item = Get-Item -LiteralPath $ancestor -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "LibreOffice pruning refuses a reparse point: $ancestor" }
        $parent = Split-Path -Parent $ancestor
        if ($parent -eq $ancestor) { break }
        $ancestor = $parent
    }
    $initialFiles = @(Get-ChildItem -LiteralPath $root -Recurse -Force)
    $link = $initialFiles | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Select-Object -First 1
    if ($null -ne $link) { throw "LibreOffice pruning refuses a reparse point: $($link.FullName)" }
    function Resolve-LibreOfficePruningPath([string]$Relative) {
        if ([IO.Path]::IsPathRooted($Relative)) { throw "LibreOffice pruning expects a relative path: $Relative" }
        $full = [IO.Path]::GetFullPath((Join-Path $root $Relative))
        if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "LibreOffice pruning path escapes the runtime: $full" }
        return $full
    }
    foreach ($anchor in @('program/soffice.bin', 'program/fundamental.ini', 'program/services.rdb', 'program/services/services.rdb')) {
        if (-not (Test-Path -LiteralPath (Resolve-LibreOfficePruningPath $anchor) -PathType Leaf)) { throw "LibreOffice runtime anchor is missing: $anchor" }
    }
    $beforeBytes = [long](($initialFiles | Where-Object { -not $_.PSIsContainer } | Measure-Object Length -Sum).Sum)
    $removed = [Collections.Generic.List[object]]::new()
    $registrations = [Collections.Generic.List[object]]::new()
    $targets = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    function Add-LibreOfficePruningTarget([string]$Relative) {
        [void]$targets.Add((Resolve-LibreOfficePruningPath $Relative))
    }
    function Read-LibreOfficePruningXml([string]$Path) {
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Ignore
        $settings.XmlResolver = $null
        $reader = [Xml.XmlReader]::Create($Path, $settings)
        try {
            $xml = [Xml.XmlDocument]::new()
            $xml.PreserveWhitespace = $true
            $xml.XmlResolver = $null
            $xml.Load($reader)
            return ,$xml
        } finally { $reader.Dispose() }
    }
    function Save-LibreOfficePruningXml([Xml.XmlDocument]$Xml, [string]$Path) {
        $settings = [Xml.XmlWriterSettings]::new()
        $settings.Encoding = [Text.UTF8Encoding]::new($false)
        $settings.Indent = $false
        $writer = [Xml.XmlWriter]::Create($Path, $settings)
        try { $Xml.Save($writer) } finally { $writer.Dispose() }
    }
    foreach ($relative in @('program/classes', 'program/wizards', 'share/basic', 'share/Scripts',
        'share/autotext', 'share/autocorr', 'share/tipoftheday', 'share/glade', 'presets/basic', 'presets/autotext')) {
        Add-LibreOfficePruningTarget $relative
    }
    $program = Resolve-LibreOfficePruningPath 'program'
    foreach ($directoryItem in Get-ChildItem -LiteralPath $program -Directory -Filter 'python-core-*') {
        Add-LibreOfficePruningTarget ([IO.Path]::GetRelativePath($root, $directoryItem.FullName))
    }
    # jvmaccesslo.dll and jvmfwklo.dll are deliberately retained: mergedlo.dll
    # imports them even when no Java component is used by a conversion.
    $scriptingLibraries = @('pythonloaderlo.dll', 'pyuno.pyd', 'java_uno.dll', 'javaloaderlo.dll',
        'javavmlo.dll', 'jdbclo.dll', 'hsqldb.dll', 'officebean.dll', 'jpipe.dll', 'juh.dll', 'juhx.dll')
    foreach ($name in $scriptingLibraries + @('python.exe', 'pythonloader.py', 'pythonloader.uno.ini',
        'pythonscript.py', 'uno.py', 'unohelper.py', 'mailmerge.py', 'scriptforge.py', 'scriptforge.pyi', 'regview.exe', 'odbcconfig.exe')) {
        Add-LibreOfficePruningTarget ('program/' + $name)
    }
    foreach ($file in Get-ChildItem -LiteralPath $program -File -Filter 'python3*.dll') {
        Add-LibreOfficePruningTarget ([IO.Path]::GetRelativePath($root, $file.FullName))
    }
    # Built-in Basic/VBA interpreters, chart/Math/OLE support and every import /
    # export DLL remain. Notebookbars are interactive ribbon definitions only.
    $ui = Resolve-LibreOfficePruningPath 'share/config/soffice.cfg'
    if (Test-Path -LiteralPath $ui -PathType Container) {
        foreach ($file in Get-ChildItem -LiteralPath $ui -Recurse -File -Filter 'notebookbar*.ui') {
            Add-LibreOfficePruningTarget ([IO.Path]::GetRelativePath($root, $file.FullName))
        }
    }
    # Remove optional Python/Java UNO factories along with their implementations.
    # All native Writer/Calc/Draw/Math/filter factories are preserved verbatim.
    $serviceFiles = @((Resolve-LibreOfficePruningPath 'program/services.rdb')) +
        @(Get-ChildItem -LiteralPath (Resolve-LibreOfficePruningPath 'program/services') -File -Filter '*.rdb' | ForEach-Object FullName)
    foreach ($servicePath in $serviceFiles) {
        $xml = Read-LibreOfficePruningXml $servicePath
        $changed = $false
        foreach ($component in @($xml.SelectNodes('//*[local-name()="component"]'))) {
            $loader = $component.GetAttribute('loader')
            $uri = $component.GetAttribute('uri')
            $library = ($uri -split '[/\\]')[-1]
            if ($loader -in @('com.sun.star.loader.Python', 'com.sun.star.loader.Java', 'com.sun.star.loader.Java2') -or $library -in $scriptingLibraries) {
                $registrations.Add([ordered]@{path=[IO.Path]::GetRelativePath($root, $servicePath).Replace('\','/');uri=$uri;loader=$loader})
                [void]$component.ParentNode.RemoveChild($component)
                $changed = $true
            }
        }
        if ($changed) { Save-LibreOfficePruningXml $xml $servicePath }
    }
    # Lightproof is a Python grammar checker. Retain each dictionary extension,
    # its DICT_HYPH configuration, dictionary data, metadata and all notices.
    $extensions = Resolve-LibreOfficePruningPath 'share/extensions'
    if (Test-Path -LiteralPath $extensions -PathType Container) {
        foreach ($extension in Get-ChildItem -LiteralPath $extensions -Directory -Filter 'dict-*') {
            if (-not (Test-Path -LiteralPath (Join-Path $extension.FullName 'Lightproof.components'))) { continue }
            foreach ($name in @('pythonpath','dialog','help','Lightproof.components','Lightproof.py','Linguistic.xcu','test_compile_rules.py')) {
                Add-LibreOfficePruningTarget ([IO.Path]::GetRelativePath($root, (Join-Path $extension.FullName $name)))
            }
            $manifestPath = Resolve-LibreOfficePruningPath ([IO.Path]::GetRelativePath($root, (Join-Path $extension.FullName 'META-INF/manifest.xml')))
            if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
                $xml = Read-LibreOfficePruningXml $manifestPath
                $changed = $false
                foreach ($entry in @($xml.SelectNodes('//*[local-name()="file-entry"]'))) {
                    $entryPath = $entry.GetAttribute('full-path', 'http://openoffice.org/2001/manifest')
                    if ($entryPath -match '^(pythonpath|dialog|help)(/|$)' -or $entryPath -in @('Lightproof.components','Lightproof.py','Linguistic.xcu','test_compile_rules.py')) {
                        [void]$entry.ParentNode.RemoveChild($entry)
                        $changed = $true
                    }
                }
                if ($changed) { Save-LibreOfficePruningXml $xml $manifestPath }
            }
        }
    }
    foreach ($target in $targets) {
        # Validate the final absolute path immediately before a recursive delete.
        $safe = Resolve-LibreOfficePruningPath ([IO.Path]::GetRelativePath($root, $target))
        if (-not (Test-Path -LiteralPath $safe)) { continue }
        $item = Get-Item -LiteralPath $safe -Force
        $files = if ($item.PSIsContainer) { @(Get-ChildItem -LiteralPath $safe -Recurse -File -Force) } else { @($item) }
        foreach ($file in $files) {
            $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\','/')
            # Preserve original license/copyright texts even for removed bundles.
            if ($file.Name -match '(?i)(license|copying|notice|copyright|authors)') {
                $notice = Resolve-LibreOfficePruningPath ('pruning-notices/' + $relative)
                [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($notice))
                Copy-Item -LiteralPath $file.FullName -Destination $notice -Force
            }
            $removed.Add([ordered]@{path=$relative;bytes=$file.Length})
        }
        Remove-Item -LiteralPath $safe -Recurse -Force
    }
    $afterBytes = [long]((Get-ChildItem -LiteralPath $root -Recurse -File -Force | Measure-Object Length -Sum).Sum)
    return [pscustomobject]@{
        Engine = 'libreoffice'
        BeforeBytes = $beforeBytes
        AfterBytes = $afterBytes
        SavedBytes = $beforeBytes - $afterBytes
        RemovedFiles = $removed.ToArray()
        RemovedRegistrations = $registrations.ToArray()
    }
}
