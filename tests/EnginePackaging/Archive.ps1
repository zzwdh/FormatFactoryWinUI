[CmdletBinding()]
param([switch]$Offline)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$packagingRoot = Join-Path $repoRoot 'packaging'
. (Join-Path $packagingRoot 'EnginePackaging.ps1')
$testRoot = Join-Path $repoRoot ('work/archive-packaging-tests-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRoot)
$manifest = Get-Content -LiteralPath (Join-Path $packagingRoot 'engine-sources.json') -Raw | ConvertFrom-Json
$engine = @($manifest.engines | Where-Object id -CEQ '7zip')[0]
$script:passed = 0
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "FAIL $Name" }
    $script:passed++
    Write-Output "PASS $Name"
}
function Must-Reject([scriptblock]$Action, [string]$Pattern, [string]$Name) {
    $rejected = $false
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw }
        $rejected = $true
    }
    Check $rejected $Name
}

& (Join-Path $packagingRoot 'archive/Prepare-ArchiveEngine.ps1') -Architecture all -OutputDirectory (Join-Path $testRoot 'prepared') -Offline:$Offline
foreach ($arch in @('x64','arm64')) {
    $engineRoot = Join-Path $testRoot "prepared/$arch/Engines"
    $payload = Join-Path $engineRoot '7zip'
    $receipt = Assert-EngineReceipt -EngineRoot $engineRoot -Architecture $arch -Fingerprint (Get-EnginePreparationFingerprint -PackagingDirectory $packagingRoot)
    Check ($receipt.engines.Count -eq 1 -and $receipt.engines[0].id -ceq '7zip') "$arch receipt binds the archive engine"
    $expectedMachine = @{x64=0x8664;arm64=0xaa64}[$arch]
    Check ((Get-EnginePeMachine (Join-Path $payload '7z.exe')) -eq $expectedMachine -and
        (Get-EnginePeMachine (Join-Path $payload '7z.dll')) -eq $expectedMachine) "$arch console and codec DLL have native PE architecture"
    Check (@(Get-ChildItem -LiteralPath $payload -Recurse -File).Count -eq @($engine.requiredFiles).Count) "$arch payload contains only the required runtime and license files"
    $source = Join-Path $payload ('licenses/' + $engine.source.fileName)
    Check ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ieq $engine.source.sha256) "$arch includes the exact matching source archive"
    Check ((Get-Content -LiteralPath (Join-Path $payload 'licenses/COPYING.LGPL2.1.txt') -Raw).Contains('GNU LESSER GENERAL PUBLIC LICENSE')) "$arch includes the complete LGPL license"
    Check ((Get-Content -LiteralPath (Join-Path $payload 'License.txt') -Raw).Contains('unRAR')) "$arch includes upstream component copyright and unRAR notices"
    $readme = Join-Path $payload 'readme.txt'
    [IO.File]::AppendAllText($readme, 'synthetic tamper')
    Must-Reject { Assert-EngineReceipt -EngineRoot $engineRoot -Architecture $arch } 'missing/modified' "$arch receipt rejects changed runtime bytes"
}

$console = Join-Path $testRoot 'prepared/x64/Engines/7zip/7z.exe'
if ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq [Runtime.InteropServices.Architecture]::X64) {
    $info = & $console i '-sccUTF-8' 2>&1
    Check ($LASTEXITCODE -eq 0 -and ($info -join "`n").Contains('26.05')) 'x64 runtime starts without installing 7-Zip'
    Check (($info -join "`n") -match '(?m)\bRar5?\b') 'full codec DLL provides RAR reading'
}
$fakeCache = Join-Path $testRoot 'bad-cache'
[void][IO.Directory]::CreateDirectory($fakeCache)
[IO.File]::WriteAllText((Join-Path $fakeCache $engine.bootstrap.fileName), 'not the official bootstrap')
Must-Reject { Get-LockedEngineArchive -Artifact $engine.bootstrap -CacheDirectory $fakeCache -Offline } 'SHA256 mismatch' 'bootstrap digest mismatch fails before execution'
Must-Reject { Get-LockedEngineArchive -Artifact $engine.source -CacheDirectory $fakeCache -Offline } 'missing locked archive' 'offline preparation requires the matching source archive'
Write-Output "Archive packaging tests passed: $script:passed"
Write-Output "Evidence retained in $testRoot"
