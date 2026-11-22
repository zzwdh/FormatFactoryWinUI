function Get-FormatFactoryMinimumWindowsVersion {
    [xml]$buildProperties = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../Directory.Build.props') -Raw
    $properties = @($buildProperties.SelectNodes('/Project/PropertyGroup/MinimumDesktopWindowsVersion'))
    if ($properties.Count -ne 1 -or $properties[0].InnerText -notmatch '^10\.0\.\d+\.0$') {
        throw 'Directory.Build.props must declare one valid MinimumDesktopWindowsVersion.'
    }
    return $properties[0].InnerText
}

function New-FormatFactoryAppxManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$TemplatePath,
        [Parameter(Mandatory)][string[]]$InputExtensions,
        [Parameter(Mandatory)][object]$Config,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string]$Architecture
    )

    $manifest = Get-Content -LiteralPath $TemplatePath -Raw
    $supportedFileTypes = @($InputExtensions | ForEach-Object {
        "              <uap:FileType>$([System.Security.SecurityElement]::Escape([string]$_))</uap:FileType>"
    }) -join [Environment]::NewLine
    $explorerItemTypes = @($InputExtensions | ForEach-Object {
        $escapedExtension = [System.Security.SecurityElement]::Escape([string]$_)
        "            <desktop5:ItemType Type=`"$escapedExtension`">`r`n              <desktop5:Verb Id=`"FormatFactoryConvert`" Clsid=`"B23F9D12-9ED4-4A40-B854-7A2B0C7D9A14`" />`r`n            </desktop5:ItemType>"
    }) -join [Environment]::NewLine
    $values = @{
        '__PACKAGE_NAME__' = [System.Security.SecurityElement]::Escape([string]$Config.PackageName)
        '__PUBLISHER__' = [System.Security.SecurityElement]::Escape([string]$Config.Publisher)
        '__VERSION__' = [System.Security.SecurityElement]::Escape($Version)
        '__ARCHITECTURE__' = $Architecture
        '__MINIMUM_WINDOWS_VERSION__' = Get-FormatFactoryMinimumWindowsVersion
        '__DISPLAY_NAME__' = [System.Security.SecurityElement]::Escape([string]$Config.DisplayName)
        '__PUBLISHER_DISPLAY_NAME__' = [System.Security.SecurityElement]::Escape([string]$Config.PublisherDisplayName)
        '__DESCRIPTION__' = [System.Security.SecurityElement]::Escape([string]$Config.Description)
    }
    foreach ($entry in $values.GetEnumerator()) {
        $manifest = $manifest.Replace($entry.Key, $entry.Value)
    }
    $epubExtensions = @($InputExtensions | Where-Object { $_ -eq '.epub' })
    if ($epubExtensions.Count -ne 1) { throw '格式目录必须包含 EPUB 导入能力才能注册 EPUB 预览。' }
    $associatedFileTypes = @($InputExtensions | Where-Object { $_ -ne '.epub' } | ForEach-Object {
        "              <uap:FileType>$([System.Security.SecurityElement]::Escape([string]$_))</uap:FileType>"
    }) -join [Environment]::NewLine
    $previewFileTypes = @($epubExtensions | ForEach-Object {
        "              <uap:FileType>$([System.Security.SecurityElement]::Escape([string]$_))</uap:FileType>"
    }) -join [Environment]::NewLine
    $manifest = $manifest.Replace('__ASSOCIATED_FILE_TYPES__', $associatedFileTypes)
    $manifest = $manifest.Replace('__PREVIEW_FILE_TYPES__', $previewFileTypes)
    $manifest = $manifest.Replace('__SUPPORTED_FILE_TYPES__', $supportedFileTypes)
    $manifest = $manifest.Replace('__EXPLORER_ITEM_TYPES__', $explorerItemTypes)
    if ($manifest -match '__[A-Z_]+__') { throw 'AppxManifest 仍包含未替换的模板占位符。' }
    Assert-FormatFactoryShellRegistration -Manifest $manifest
    return $manifest
}

function Assert-FormatFactoryShellRegistration {
    [CmdletBinding()]
    param([Parameter(Mandatory)][xml]$Manifest)

    $namespaces = [Xml.XmlNamespaceManager]::new($Manifest.NameTable)
    $namespaces.AddNamespace('com', 'http://schemas.microsoft.com/appx/manifest/com/windows10')
    $namespaces.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')
    $namespaces.AddNamespace('desktop2', 'http://schemas.microsoft.com/appx/manifest/desktop/windows10/2')
    $namespaces.AddNamespace('desktop5', 'http://schemas.microsoft.com/appx/manifest/desktop/windows10/5')
    $expected = @{
        'B23F9D12-9ED4-4A40-B854-7A2B0C7D9A14' = 'Both'
        '7657D52B-308A-4B9F-821F-5D839DB74A11' = 'STA'
        '34E71D22-EBB7-49E1-A7B3-CB9186CC4D20' = 'STA'
    }
    $classes = @($Manifest.SelectNodes('//com:SurrogateServer/com:Class', $namespaces))
    $actualIds = @($classes | ForEach-Object { ([string]$_.Id).ToUpperInvariant() })
    if ($classes.Count -ne $expected.Count -or
        @(Compare-Object @($expected.Keys) $actualIds).Count -ne 0) {
        throw 'MSIX must register exactly the three distinct C++ Shell COM classes.'
    }
    foreach ($class in $classes) {
        $id = ([string]$class.Id).ToUpperInvariant()
        if ($class.Path -cne 'FormatFactoryShellExtension.Native.dll' -or
            $class.ThreadingModel -cne $expected[$id]) {
            throw "Unexpected native Shell path or threading model: $id"
        }
    }
    $associations = @($Manifest.SelectNodes("//uap:FileTypeAssociation[@Name='formatfactory-epub-preview']", $namespaces))
    if ($associations.Count -ne 1) { throw 'MSIX must have one EPUB preview association.' }
    $previewTypes = @($associations[0].SelectNodes('uap:SupportedFileTypes/uap:FileType', $namespaces))
    if ($previewTypes.Count -ne 1 -or $previewTypes[0].InnerText -cne '.epub') {
        throw 'Shell preview handlers must be scoped to EPUB only.'
    }
    foreach ($handler in @(
        @{ Path='//desktop2:ThumbnailHandler'; Id='7657D52B-308A-4B9F-821F-5D839DB74A11' },
        @{ Path='//desktop2:DesktopPreviewHandler'; Id='34E71D22-EBB7-49E1-A7B3-CB9186CC4D20' }
    )) {
        $nodes = @($Manifest.SelectNodes($handler.Path, $namespaces))
        if ($nodes.Count -ne 1 -or $nodes[0].Clsid -cne $handler.Id -or
            $nodes[0].ParentNode -ne $associations[0]) {
            throw "Unexpected EPUB Shell handler registration: $($handler.Id)"
        }
    }
    $verbs = @($Manifest.SelectNodes('//desktop5:Verb', $namespaces))
    if ($verbs.Count -eq 0 -or @($verbs | Where-Object {
        $_.Clsid -cne 'B23F9D12-9ED4-4A40-B854-7A2B0C7D9A14' -or $_.Id -cne 'FormatFactoryConvert'
    }).Count -gt 0) { throw 'Explorer verbs must target the native conversion command.' }
}
