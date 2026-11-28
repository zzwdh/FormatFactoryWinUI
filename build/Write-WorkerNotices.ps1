param(
    [Parameter(Mandatory)][string]$Sysroot,
    [Parameter(Mandatory)]$Metadata,
    [Parameter(Mandatory)][string]$Destination
)
$ErrorActionPreference = 'Stop'
$files = [Collections.Generic.SortedDictionary[string,string]]::new([StringComparer]::Ordinal)
$rustDocs = Join-Path $Sysroot 'share/doc/rust'
$files['rust-standard-library/COPYRIGHT-library.html'] = Join-Path $rustDocs 'COPYRIGHT-library.html'
foreach ($file in (Get-ChildItem -LiteralPath (Join-Path $rustDocs 'licenses') -File)) {
    $files["rust-standard-library/licenses/$($file.Name)"] = $file.FullName
}
foreach ($package in $Metadata.packages) {
    if ($package.id -eq $Metadata.resolve.root) { continue }
    $root = Split-Path $package.manifest_path -Parent
    $before = $files.Count
    foreach ($file in (Get-ChildItem -LiteralPath $root -File -Recurse)) {
        $relative = [IO.Path]::GetRelativePath($root,$file.FullName).Replace('\','/')
        if ($file.Name -match '^(license|licence|copying|notice|copyright)([._-]|$)' -or
            $relative -match '(^|/)licenses?/' -or $relative -ceq $package.license_file) {
            $files["crates/$($package.name)-$($package.version)/$relative"] = $file.FullName
        }
    }
    if ($files.Count -eq $before) { throw "Review the license files for $($package.name) $($package.version) before distributing the worker." }
}

# Preserve upstream files byte-for-byte, with stable entry ordering and timestamps.
# Parallel app/test builds use the same Cargo target directory.
$temp = "$Destination.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    $archive = [IO.Compression.ZipFile]::Open($temp,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($item in $files.GetEnumerator()) {
            $entry = $archive.CreateEntry($item.Key)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
            $source = [IO.File]::OpenRead($item.Value)
            try {
                $stream = $entry.Open()
                try { $source.CopyTo($stream) } finally { $stream.Dispose() }
            }
            finally { $source.Dispose() }
        }
    }
    finally { $archive.Dispose() }
    if (!(Test-Path -LiteralPath $Destination) -or (Get-FileHash -LiteralPath $Destination).Hash -ne (Get-FileHash -LiteralPath $temp).Hash) {
        [IO.File]::Move($temp,$Destination,$true)
    }
}
finally { [IO.File]::Delete($temp) }
