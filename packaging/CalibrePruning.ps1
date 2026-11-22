# The bundled Calibre profile serves the application's EPUB -> MOBI/AZW3 routes
# and ebook-meta validation. It is deliberately not a general Calibre installation:
# the Qt WebEngine PDF output/viewer, TTS, multimedia and desktop services are not
# shipped. Keep the frozen Python archive intact: built-in plugin enumeration
# imports modules beyond the selected route, and its index is part of the launcher.

function Optimize-CalibreConversionRuntime {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Directory)

    $runtimeRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Directory))
    $rootItem = Get-Item -LiteralPath $runtimeRoot -Force -ErrorAction Stop
    if (-not $rootItem.PSIsContainer -or ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Calibre runtime must be an ordinary directory: $runtimeRoot"
    }
    $ancestor = $rootItem.Parent
    while ($null -ne $ancestor) {
        if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Calibre runtime has a reparse-point ancestor: $($ancestor.FullName)"
        }
        $ancestor = $ancestor.Parent
    }
    $runtimePrefix = $runtimeRoot + [IO.Path]::DirectorySeparatorChar
    foreach ($required in @('ebook-convert.exe', 'ebook-meta.exe', 'calibre-parallel.exe', 'LICENSE',
            'app/bin/calibre-launcher.dll', 'app/bin/python-lib.bypy.frozen')) {
        if (-not (Test-Path -LiteralPath (Join-Path $runtimeRoot $required) -PathType Leaf)) {
            throw "Calibre conversion runtime is missing $required"
        }
    }
    # Reject junctions/symlinks before any recursive removal. A lexical prefix
    # alone cannot keep a reparse-point target inside the supplied runtime.
    $reparsePoint = Get-ChildItem -LiteralPath $runtimeRoot -Force -Recurse -ErrorAction Stop |
        Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } |
        Select-Object -First 1
    if ($reparsePoint) { throw "Calibre runtime contains a reparse point: $($reparsePoint.FullName)" }
    $beforeBytes = [long]((Get-ChildItem -LiteralPath $runtimeRoot -Recurse -File -Force | Measure-Object Length -Sum).Sum)

    $relativePaths = @(
        'app/translations/qtwebengine_locales',
        'app/share/espeak-ng-data',
        'app/plugins/multimedia',
        'app/plugins/texttospeech',
        'app/plugins/sqldrivers',
        'app/resources/catalog',
        'app/resources/content-server',
        'app/resources/editor-help',
        'app/resources/mathjax',
        'app/resources/quick_start',
        'app/resources/rapydscript',
        'app/resources/builtin_recipes.xml',
        'app/resources/builtin_recipes.zip',
        'app/resources/calibre-portable.bat',
        'app/resources/calibre-portable.sh',
        'app/resources/changelog.json',
        'app/resources/ebook-convert-complete.calibre_msgpack',
        'app/resources/editor-functions.json',
        'app/resources/editor.js',
        'app/resources/editor.js.map',
        'app/resources/lookup.js',
        'app/resources/piper-voices.json',
        'app/resources/qtwebengine_devtools_resources.pak',
        'app/resources/qtwebengine_resources_100p.pak',
        'app/resources/qtwebengine_resources_200p.pak',
        'app/resources/qtwebengine_resources.pak',
        'app/resources/icudtl.dat',
        'app/resources/v8_context_snapshot.bin',
        'app/resources/pdf-mathjax-loader.js',
        'app/resources/pdf-preprint.js',
        'app/resources/scraper.js',
        'app/resources/stylelint-bundle.min.js',
        'app/resources/stylelint.js',
        'app/resources/toc.js',
        'app/resources/viewer.html',
        'app/resources/viewer.js',
        'app/resources/viewer.js.map',
        'app/resources/localization/locales.zip',
        'app/plugins/imageformats/qpdf.dll'
    )
    $binaries = @(
        'Qt6WebEngineCore.dll', 'Qt6WebEngineWidgets.dll', 'QtWebEngineProcess.exe',
        'Qt6WebChannel.dll', 'pyqt6.QtWebChannel.pyd',
        'pyqt6.QtWebEngineCore.pyd', 'pyqt6.QtWebEngineWidgets.pyd',
        'Qt6Qml.dll', 'Qt6QmlMeta.dll', 'Qt6QmlModels.dll', 'Qt6QmlWorkerScript.dll',
        'Qt6Quick.dll', 'Qt6QuickWidgets.dll',
        'pyqt6.QtQml.pyd', 'pyqt6.QtQuick.pyd', 'pyqt6.QtQuickWidgets.pyd',
        'Qt6Multimedia.dll', 'Qt6MultimediaWidgets.dll', 'Qt6SpatialAudio.dll',
        'pyqt6.QtMultimedia.pyd', 'pyqt6.QtMultimediaWidgets.pyd', 'pyqt6.QtSpatialAudio.pyd',
        'Qt6TextToSpeech.dll', 'pyqt6.QtTextToSpeech.pyd',
        'Qt6Sensors.dll', 'Qt6Positioning.dll', 'pyqt6.QtSensors.pyd', 'pyqt6.QtPositioning.pyd',
        'Qt6Sql.dll', 'pyqt6.QtSql.pyd', 'pyqt6.QtPdf.pyd', 'pyqt6.QtPdfWidgets.pyd',
        'pyqt6.QtTest.pyd', 'pyqt6.QtDBus.pyd',
        'espeak-ng.dll', 'piper.pyd', 'onnxruntime.dll', 'onnxruntime_providers_shared.dll',
        'avcodec-61.dll', 'avdevice-61.dll', 'avfilter-10.dll', 'avformat-61.dll',
        'avutil-59.dll', 'swresample-5.dll', 'swscale-8.dll', 'postproc-58.dll', 'ffmpeg.pyd',
        'calibre-eject.exe', 'calibre-file-dialog.exe'
    )
    $relativePaths += @($binaries | ForEach-Object { 'app/bin/' + $_ })

    $removedPaths = [Collections.Generic.List[string]]::new()
    $preservedNotices = [Collections.Generic.List[string]]::new()
    $removedFiles = [Collections.Generic.List[object]]::new()
    $removedBytes = 0L
    foreach ($relative in $relativePaths) {
        $target = [IO.Path]::GetFullPath((Join-Path $runtimeRoot $relative))
        if (-not $target.StartsWith($runtimePrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Calibre pruning target escapes its runtime: $target"
        }
        if (-not (Test-Path -LiteralPath $target)) { continue }
        $item = Get-Item -LiteralPath $target -Force -ErrorAction Stop
        $files = @(if ($item.PSIsContainer) { Get-ChildItem -LiteralPath $target -Recurse -File -Force } else { $item })
        foreach ($notice in @($files | Where-Object { $_.Name -match '^(?:licen[sc]e|copying|notice|authors|copyright)(?:[._-].*)?$' })) {
            $noticeRelative = $notice.FullName.Substring($runtimePrefix.Length)
            $noticeDestination = [IO.Path]::GetFullPath((Join-Path $runtimeRoot ('pruning-notices/' + $noticeRelative)))
            if (-not $noticeDestination.StartsWith($runtimePrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Calibre notice destination escapes its runtime: $noticeDestination"
            }
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($noticeDestination))
            Copy-Item -LiteralPath $notice.FullName -Destination $noticeDestination -Force -ErrorAction Stop
            $preservedNotices.Add($noticeRelative)
        }
        foreach ($file in $files) {
            $removedBytes += $file.Length
            $removedFiles.Add([ordered]@{path=$file.FullName.Substring($runtimePrefix.Length).Replace('\', '/'); bytes=$file.Length})
        }
        Remove-Item -LiteralPath $target -Force -Recurse -ErrorAction Stop
        $removedPaths.Add($relative)
    }
    $afterBytes = [long]((Get-ChildItem -LiteralPath $runtimeRoot -Recurse -File -Force | Measure-Object Length -Sum).Sum)
    [pscustomobject]@{
        Engine = 'calibre'
        Profile = 'epub-to-mobi-azw3'
        BeforeBytes = $beforeBytes
        AfterBytes = $afterBytes
        SavedBytes = $beforeBytes - $afterBytes
        RemovedPaths = @($removedPaths)
        PreservedNoticePaths = @($preservedNotices)
        RemovedFiles = $removedFiles.ToArray()
        RemovedBytes = $removedBytes
    }
}
