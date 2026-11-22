[CmdletBinding()]
param(
    [Parameter(Mandatory)][Alias('Baseline')][string]$BaselineCompatibilityPath,
    [Parameter(Mandatory)][Alias('Candidate')][string]$CandidateCompatibilityPath,
    [Parameter(Mandatory)][Alias('Primary')][string]$PrimaryPath,
    [ValidateSet('dshow','gdigrab','lavfi','vfwcap')][string[]]$AllowedRemovedInputDevices = @('dshow','gdigrab','lavfi','vfwcap'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot ('../../work/ffmpeg-pruning/capabilities-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))),
    [ValidateRange(5,180)][int]$TimeoutSeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    if (@(Get-ChildItem -LiteralPath $output -Force).Count) { throw 'Use a fresh output directory so earlier capability logs cannot masquerade as this run.' }
}
[void][IO.Directory]::CreateDirectory($output)
$commands = [Collections.Generic.List[object]]::new()

function Invoke-CapabilityCommand([string]$Role, [string]$Executable, [string]$Kind) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['PATH'] = Join-Path $env:SystemRoot 'System32'
    [void]$start.ArgumentList.Add('-hide_banner')
    [void]$start.ArgumentList.Add('-' + $Kind)
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $timedOut = $false
    try {
        if (-not $process.Start()) { throw "Cannot start $Role $Kind enumeration." }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $timedOut = $true
            $process.Kill($true)
            $process.WaitForExit()
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $exitCode = $process.ExitCode
    } finally { $process.Dispose(); $timer.Stop() }
    $stdoutFile = "$Role-$Kind.stdout.txt"
    $stderrFile = "$Role-$Kind.stderr.txt"
    [IO.File]::WriteAllText((Join-Path $output $stdoutFile), $stdout, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $output $stderrFile), $stderr, [Text.UTF8Encoding]::new($false))
    $record = [ordered]@{
        role=$Role; executable=$Executable; arguments=@('-hide_banner',('-'+$Kind))
        kind=$Kind; exitCode=$exitCode; timedOut=$timedOut; seconds=[Math]::Round($timer.Elapsed.TotalSeconds,3)
        stdout=$stdoutFile; stderr=$stderrFile
        stdoutSha256=(Get-FileHash -LiteralPath (Join-Path $output $stdoutFile)).Hash.ToLowerInvariant()
        stderrSha256=(Get-FileHash -LiteralPath (Join-Path $output $stderrFile)).Hash.ToLowerInvariant()
    }
    $commands.Add($record)
    if ($timedOut -or $exitCode -ne 0) { throw "$Role -$Kind failed; see $output. Exit=$exitCode timeout=$timedOut" }
    return $stdout + [Environment]::NewLine + $stderr
}

function Get-CodecRows([string]$Text) {
    foreach ($line in $Text -split '\r?\n') {
        if ($line -match '^\s*(?<flags>[D.][E.][VASDT.][I.][L.][S.])\s+(?<name>[A-Za-z0-9_]+)\s+(?<description>.+)$') {
            [pscustomobject]@{id=$Matches.name; decode=$Matches.flags[0] -ceq 'D'; encode=$Matches.flags[1] -ceq 'E'; flags=$Matches.flags; description=$Matches.description}
        }
    }
}
function Get-CoderNames([string]$Text) {
    @(@(foreach ($line in $Text -split '\r?\n') {
        if ($line -match '^\s*[VAS][A-Z.]{5}\s+(?<name>[A-Za-z0-9_+.-]+)\s+.+$') { $Matches.name }
    }) | Sort-Object -Unique)
}
function Get-FormatNames([string]$Text) {
    @(@(foreach ($line in $Text -split '\r?\n') {
        if ($line -match '^\s*[D.E d]{1,3}\s+(?<name>[A-Za-z0-9_][A-Za-z0-9_,.+-]*)\s+.+$') {
            $Matches.name -split ','
        }
    }) | Sort-Object -Unique)
}
function Get-FilterNames([string]$Text) {
    @(@(foreach ($line in $Text -split '\r?\n') {
        if ($line -match '^\s*[TSCF.]{2,4}\s+(?<name>[A-Za-z0-9_]+)\s+[AVN|]+->[AVN|]+\s+') { $Matches.name }
    }) | Sort-Object -Unique)
}
function Get-ProtocolNames([string]$Text) {
    $direction = ''
    $inputNames = [Collections.Generic.List[string]]::new()
    $outputNames = [Collections.Generic.List[string]]::new()
    foreach ($line in $Text -split '\r?\n') {
        if ($line -cmatch '^Input:$') { $direction='input'; continue }
        if ($line -cmatch '^Output:$') { $direction='output'; continue }
        if ($direction -and $line -match '^\s+(?<name>[a-zA-Z0-9_]+)\s*$') {
            if ($direction -eq 'input') { $inputNames.Add($Matches.name) } else { $outputNames.Add($Matches.name) }
        }
    }
    [pscustomobject]@{input=@($inputNames | Sort-Object -Unique);output=@($outputNames | Sort-Object -Unique)}
}
function Get-SimpleList([string]$Text) {
    @(@(foreach ($line in $Text -split '\r?\n') {
        if ($line -match '^\s*(?<name>[a-z0-9_]+)\s*$') { $Matches.name }
    }) | Sort-Object -Unique)
}
function Get-Missing([object[]]$Expected, [object[]]$Actual) {
    @($Expected | Where-Object { $_ -notin $Actual } | Sort-Object -Unique)
}
function Get-Snapshot([string]$Role, [string]$Path) {
    $file = Get-Item -LiteralPath $Path
    $executable = $file.FullName
    $text = @{}
    foreach ($kind in @('version','buildconf','codecs','decoders','encoders','demuxers','muxers','filters','protocols','bsfs')) {
        $text[$kind] = Invoke-CapabilityCommand $Role $executable $kind
    }
    $codecRows = @(Get-CodecRows $text.codecs)
    $snapshot = [ordered]@{
        executable=$executable;bytes=$file.Length;sha256=(Get-FileHash -LiteralPath $executable).Hash.ToLowerInvariant()
        codecRows=$codecRows
        decoderCodecIds=@($codecRows | Where-Object decode | Select-Object -ExpandProperty id | Sort-Object -Unique)
        decoderNames=@(Get-CoderNames $text.decoders);encoderNames=@(Get-CoderNames $text.encoders)
        demuxers=@(Get-FormatNames $text.demuxers);muxers=@(Get-FormatNames $text.muxers)
        filters=@(Get-FilterNames $text.filters);protocols=Get-ProtocolNames $text.protocols
        bitstreamFilters=@(Get-SimpleList $text.bsfs)
    }
    foreach ($property in @('decoderCodecIds','decoderNames','encoderNames','demuxers','muxers','filters','bitstreamFilters')) {
        if (@($snapshot[$property]).Count -eq 0) { throw "$Role $property enumeration produced no parsed entries." }
    }
    if (@($snapshot.protocols.input).Count -eq 0 -or @($snapshot.protocols.output).Count -eq 0) { throw "$Role protocol enumeration was empty." }
    return $snapshot
}

try {
    $baseline = Get-Snapshot 'baseline' $BaselineCompatibilityPath
    $candidate = Get-Snapshot 'candidate' $CandidateCompatibilityPath
    $primary = Get-Snapshot 'primary' $PrimaryPath
    $requiredEncoders = @('ffv1','pcm_s32le','pcm_s16le','wrapped_avframe')
    $missingDecoderIds = @(Get-Missing $baseline.decoderCodecIds $candidate.decoderCodecIds)
    $allMissingDemuxers = @(Get-Missing $baseline.demuxers $candidate.demuxers)
    # These exact optional devices are outside the compatibility file-decoding
    # contract. No file/container demuxer can be waived by this parameter.
    $removedInputDevices = @($allMissingDemuxers | Where-Object { $_ -in $AllowedRemovedInputDevices })
    $missingDemuxers = @($allMissingDemuxers | Where-Object { $_ -notin $AllowedRemovedInputDevices })
    $missingMuxers = @(Get-Missing $baseline.muxers $candidate.muxers)
    $missingBsfs = @(Get-Missing $baseline.bitstreamFilters $candidate.bitstreamFilters)
    $missingRequiredEncoders = @(Get-Missing $requiredEncoders $candidate.encoderNames)
    # LibreMPEG inserts asf2sf on every audio output, and negotiates channel,
    # sample-rate and alpha conversions through these named filters.
    $requiredFilters = @('buffer','buffersink','abuffer','abuffersink','format','aformat','aresample','scale','null','anull','asf2sf','acl2cl','ardftsrc','premultiply_dynamic','transpose','hflip','vflip','rotate','trim','atrim')
    $missingRequiredFilters = @(Get-Missing $requiredFilters $candidate.filters)
    $missingRequiredProtocols = @(Get-Missing @('file','pipe') (@($candidate.protocols.input)+@($candidate.protocols.output)))
    $featureChecks = @(foreach ($feature in @(
        @{feature='BVC2';codecId='bvc2';decoder='bvc2'},
        @{feature='AC-4';codecId='ac4';decoder='ac4'},
        @{feature='Blackmagic RAW';codecId='braw';decoder='braw'},
        @{feature='Bink 2';codecId='binkvideo2';decoder='binkvideo2'}
    )) {
        [ordered]@{
            feature=$feature.feature;codecId=$feature.codecId;requiredDecoder=$feature.decoder
            baselineSupports=$feature.decoder -in $baseline.decoderNames
            candidateSupports=$feature.decoder -in $candidate.decoderNames
            primarySupports=$feature.decoder -in $primary.decoderNames
            note='Canonical codec ID and corresponding native decoder are checked.'
        }
    })
    $missingVariants = @($featureChecks | Where-Object { $_.baselineSupports -and -not $_.candidateSupports } | ForEach-Object requiredDecoder)
    $failed = $missingDecoderIds.Count + $missingDemuxers.Count + $missingMuxers.Count + $missingBsfs.Count + $missingRequiredEncoders.Count + $missingRequiredFilters.Count + $missingRequiredProtocols.Count + $missingVariants.Count
    $report = [ordered]@{
        schemaVersion=1;status=if($failed){'failed'}else{'passed'};completedAt=[DateTimeOffset]::UtcNow.ToString('o')
        baseline=$baseline;candidate=$candidate;primary=$primary
        requirements=[ordered]@{encoders=$requiredEncoders;filters=$requiredFilters;allowedRemovedInputDevices=@($AllowedRemovedInputDevices | Sort-Object -Unique);decoderPolicy='Preserve every baseline decodable canonical codec ID, plus explicit native implementations for four important fallback codecs.';demuxerPolicy='Preserve every baseline file/container demuxer name/alias. Only the explicitly listed optional input devices may be removed.';muxerPolicy='Preserve every baseline muxer name/alias for initial prototype.';parserPolicy='FFmpeg CLI does not enumerate parsers. Compare generated CONFIG_*_PARSER settings against pinned source/build independently.'}
        comparison=[ordered]@{
            missingDecoderCodecIds=$missingDecoderIds;missingDecoderNamesInformational=@(Get-Missing $baseline.decoderNames $candidate.decoderNames)
            missingDemuxers=$missingDemuxers;removedInputDevices=$removedInputDevices;allMissingDemuxersInformational=$allMissingDemuxers;missingMuxers=$missingMuxers;missingBitstreamFilters=$missingBsfs
            missingRequiredEncoders=$missingRequiredEncoders;missingRequiredFilters=$missingRequiredFilters;missingRequiredProtocols=$missingRequiredProtocols;missingExplicitVariants=$missingVariants
            baselineDecodeIdsMissingFromPrimary=@(Get-Missing $baseline.decoderCodecIds $primary.decoderCodecIds)
            filtersRemovedInformational=@(Get-Missing $baseline.filters $candidate.filters)
            inputProtocolsRemovedInformational=@(Get-Missing $baseline.protocols.input $candidate.protocols.input)
            outputProtocolsRemovedInformational=@(Get-Missing $baseline.protocols.output $candidate.protocols.output)
        }
        explicitFeatures=$featureChecks;commands=$commands.ToArray()
        isolatedPath=Join-Path $env:SystemRoot 'System32'
        scope='Capability enumeration only; it does not establish successful decoding of every codec, file compatibility, GPU runtime support, parser equivalence or packaging correctness.'
    }
    $report | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $output 'report.json') -Encoding utf8NoBOM
    Write-Host "$($report.status): baseline=$($baseline.decoderCodecIds.Count) candidate=$($candidate.decoderCodecIds.Count) primary=$($primary.decoderCodecIds.Count) decoded codec IDs; missing=$($missingDecoderIds.Count)."
    Write-Host "Report: $(Join-Path $output 'report.json')"
    if ($failed) { exit 1 }
} catch {
    [ordered]@{schemaVersion=1;status='error';completedAt=[DateTimeOffset]::UtcNow.ToString('o');error=$_.Exception.Message;commands=$commands.ToArray()} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'report.json') -Encoding utf8NoBOM
    throw
}
