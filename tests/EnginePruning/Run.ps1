[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $repo 'packaging/EnginePackaging.ps1')
. (Join-Path $repo 'packaging/LibreOfficePruning.ps1')
. (Join-Path $repo 'packaging/CalibrePruning.ps1')
# Reuse the synthetic PE builder without running the separate packaging suite.
$fixtureTokens = $null; $fixtureErrors = $null
$fixtureAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'tests/EnginePackaging/Run.ps1'), [ref]$fixtureTokens, [ref]$fixtureErrors)
if ($fixtureErrors.Count -ne 0) { throw 'Cannot parse the shared synthetic PE fixture builder.' }
$fixtureFunction = $fixtureAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'New-PeFixture' }, $true)
if ($null -eq $fixtureFunction) { throw 'Missing shared synthetic PE fixture builder.' }
. ([scriptblock]::Create($fixtureFunction.Extent.Text))
$taskRoot = Join-Path $repo ('work/engine-pruning-tests-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($taskRoot)
$script:passed = 0
function Test-Case([string]$Name, [scriptblock]$Action) {
    & $Action
    $script:passed++
    Write-Output "PASS $Name"
}
function Reject([scriptblock]$Action, [string]$Pattern) {
    $failed = $false
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw "Unexpected rejection: $($_.Exception.Message)" }
        $failed = $true
    }
    if (-not $failed) { throw 'Pruning accepted an unsafe or incomplete runtime.' }
}
function Write-FixtureFile([string]$Root, [string]$Relative, [string]$Value = 'immutable fixture bytes') {
    $path = Join-Path $Root $Relative
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllText($path, $Value, [Text.UTF8Encoding]::new($false))
}
function Snapshot([string]$Root) {
    return @(Get-ChildItem -LiteralPath $Root -Recurse -File | Sort-Object FullName | ForEach-Object {
        [IO.Path]::GetRelativePath($Root, $_.FullName) + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash
    }) -join "`n"
}
function Assert-ContentPresent([string]$Root, [string]$Value) {
    $found = @(Get-ChildItem -LiteralPath $Root -Recurse -File | Where-Object {
        [IO.File]::ReadAllText($_.FullName).Contains($Value)
    })
    if ($found.Count -eq 0) { throw "Required resource or license content was removed: $Value" }
}
function New-OfficeFixture([string]$Name) {
    $root = Join-Path $taskRoot $Name
    foreach ($relative in @('program/soffice.bin','program/soffice.com','program/soffice.exe','program/fundamental.ini',
        'program/swlo.dll','program/chartcorelo.dll','program/smlo.dll','program/basiclo.dll','program/vbaobjlo.dll',
        'program/jvmaccesslo.dll','program/jvmfwklo.dll','share/fonts/truetype/fixture.ttf',
        'share/extensions/dict-fixture/hyph_fixture.dic','share/registry/writer.xcd','license.txt')) {
        Write-FixtureFile $root $relative ('preserved:' + $relative)
    }
    $services = '<components xmlns="http://openoffice.org/2010/uno-components"><component loader="com.sun.star.loader.SharedLibrary" uri="vnd.sun.star.expand:$LO_LIB_DIR/swlo.dll"><implementation name="WriterNative"><service name="com.sun.star.text.TextDocument"/></implementation></component><component loader="com.sun.star.loader.SharedLibrary" uri="vnd.sun.star.expand:$LO_LIB_DIR/chartcorelo.dll"><implementation name="ChartNative"/></component><component loader="com.sun.star.loader.Python" uri="vnd.sun.star.expand:$LO_LIB_DIR/optional.py"><implementation name="OptionalPython"/></component></components>'
    Write-FixtureFile $root 'program/services.rdb' $services
    Write-FixtureFile $root 'program/services/services.rdb' $services
    Write-FixtureFile $root 'program/python-core-3.12.0/lib/LICENSE.txt' 'PYTHON ORIGINAL LICENSE MUST SURVIVE'
    Write-FixtureFile $root 'program/python-core-3.12.0/lib/unused.py' 'optional Python implementation'
    Write-FixtureFile $root 'program/classes/example.jar' 'optional Java implementation'
    return $root
}
function New-CalibreFixture([string]$Name) {
    $root = Join-Path $taskRoot $Name
    foreach ($relative in @('ebook-convert.exe','ebook-meta.exe','calibre-parallel.exe','LICENSE',
        'app/bin/calibre-launcher.dll','app/bin/python-lib.bypy.frozen','app/bin/python3.dll',
        'app/bin/Qt6Core.dll','app/bin/Qt6Gui.dll','app/bin/Qt6Svg.dll','app/bin/extension-unlisted.pyd',
        'app/plugins/platforms/qwindows.dll','app/plugins/imageformats/qsvg.dll',
        'app/resources/fonts/liberation/fixture.ttf','app/resources/hyphenation/fixture.dic',
        'app/resources/images/fixture.png','app/resources/templates/fixture.xhtml')) {
        Write-FixtureFile $root $relative ('preserved:' + $relative)
    }
    Write-FixtureFile $root 'app/bin/Qt6WebEngineCore.dll' 'optional GUI PDF implementation'
    Write-FixtureFile $root 'app/share/espeak-ng-data/COPYING' 'ESPEAK ORIGINAL LICENSE MUST SURVIVE'
    Write-FixtureFile $root 'app/share/espeak-ng-data/voice.bin' 'optional speech data'
    Write-FixtureFile $root 'app/resources/catalog/gui-template.xhtml' 'optional GUI catalog'
    return $root
}
function Optimize([string]$Kind, [string]$Directory) {
    if ($Kind -eq 'office') { return Optimize-LibreOfficeConversionRuntime -Directory $Directory }
    return Optimize-CalibreConversionRuntime -Directory $Directory
}
function New-Payload([string]$Kind, [string]$Name) {
    if ($Kind -eq 'office') { return New-OfficeFixture $Name }
    return New-CalibreFixture $Name
}
function New-AnyCpuFixture([string]$Path, [uint32]$CorFlags = 1) {
    New-PeFixture -Path $Path -Machine 0x14c
    $bytes = [IO.File]::ReadAllBytes($Path)
    # Turn the shared PE32+ fixture into PE32 and add a genuine CLI header.
    # No managed metadata/code is executed; PEReader only needs the CLR flags.
    $section = [byte[]]::new(40)
    [Array]::Copy($bytes, 0x188, $section, 0, 40)
    [Array]::Clear($bytes, 0x178, 56)
    [Array]::Copy($section, 0, $bytes, 0x178, 40)
    [BitConverter]::GetBytes([uint16]224).CopyTo($bytes, 0x94)
    [BitConverter]::GetBytes([uint16]0x2022).CopyTo($bytes, 0x96)
    [BitConverter]::GetBytes([uint16]0x10b).CopyTo($bytes, 0x98)
    [BitConverter]::GetBytes([uint32]0x1000).CopyTo($bytes, 0x98 + 24)
    [BitConverter]::GetBytes([uint32]0x400000).CopyTo($bytes, 0x98 + 28)
    [Array]::Clear($bytes, 0x98 + 96, 128)
    [BitConverter]::GetBytes([uint32]16).CopyTo($bytes, 0x98 + 92)
    [BitConverter]::GetBytes([uint32]0x1400).CopyTo($bytes, 0x98 + 208)
    [BitConverter]::GetBytes([uint32]72).CopyTo($bytes, 0x98 + 212)
    [BitConverter]::GetBytes([uint32]72).CopyTo($bytes, 0x600)
    [BitConverter]::GetBytes([uint16]2).CopyTo($bytes, 0x604)
    [BitConverter]::GetBytes([uint16]5).CopyTo($bytes, 0x606)
    [BitConverter]::GetBytes([uint32]0x1480).CopyTo($bytes, 0x608)
    [BitConverter]::GetBytes([uint32]0x80).CopyTo($bytes, 0x60c)
    [BitConverter]::GetBytes($CorFlags).CopyTo($bytes, 0x610)
    [IO.File]::WriteAllBytes($Path, $bytes)
}

Test-Case 'Writer native registrations, chart/Math/VBA, fonts, dictionaries and licenses survive pruning' {
    $root = New-OfficeFixture 'office-preservation'
    $protected = @('program/soffice.bin','program/swlo.dll','program/chartcorelo.dll','program/smlo.dll',
        'program/basiclo.dll','program/vbaobjlo.dll','program/jvmaccesslo.dll','program/jvmfwklo.dll',
        'share/fonts/truetype/fixture.ttf','share/extensions/dict-fixture/hyph_fixture.dic','share/registry/writer.xcd','license.txt')
    $hashes = @{}; foreach ($relative in $protected) { $hashes[$relative] = (Get-FileHash -LiteralPath (Join-Path $root $relative)).Hash }
    $result = Optimize office $root
    if ($result.SavedBytes -le 0) { throw 'Fixture did not exercise actual pruning.' }
    foreach ($relative in $protected) {
        if ((Get-FileHash -LiteralPath (Join-Path $root $relative)).Hash -cne $hashes[$relative]) { throw "Protected resource changed: $relative" }
    }
    foreach ($relative in @('program/services.rdb','program/services/services.rdb')) {
        [xml]$xml = Get-Content -LiteralPath (Join-Path $root $relative) -Raw
        $components = @($xml.SelectNodes('//*[local-name()="component"]'))
        if (@($components | Where-Object { $_.uri -match 'swlo.dll|chartcorelo.dll' }).Count -ne 2 -or
            @($components | Where-Object loader -EQ 'com.sun.star.loader.Python').Count -ne 0) { throw 'Native registrations were damaged or removed Python registration is dangling.' }
    }
    Assert-ContentPresent $root 'PYTHON ORIGINAL LICENSE MUST SURVIVE'
}
Test-Case 'Calibre preserves frozen plugins, ebook workers, fonts, SVG and necessary resources byte-for-byte' {
    $root = New-CalibreFixture 'calibre-preservation'
    $protected = @('ebook-convert.exe','ebook-meta.exe','calibre-parallel.exe','LICENSE','app/bin/python-lib.bypy.frozen',
        'app/bin/calibre-launcher.dll','app/bin/Qt6Svg.dll','app/bin/extension-unlisted.pyd',
        'app/plugins/platforms/qwindows.dll','app/plugins/imageformats/qsvg.dll',
        'app/resources/fonts/liberation/fixture.ttf','app/resources/hyphenation/fixture.dic','app/resources/images/fixture.png','app/resources/templates/fixture.xhtml')
    $hashes = @{}; foreach ($relative in $protected) { $hashes[$relative] = (Get-FileHash -LiteralPath (Join-Path $root $relative)).Hash }
    $result = Optimize calibre $root
    if ($result.SavedBytes -le 0) { throw 'Fixture did not exercise actual pruning.' }
    foreach ($relative in $protected) {
        if ((Get-FileHash -LiteralPath (Join-Path $root $relative)).Hash -cne $hashes[$relative]) { throw "Protected resource changed: $relative" }
    }
    Assert-ContentPresent $root 'ESPEAK ORIGINAL LICENSE MUST SURVIVE'
}
foreach ($kind in @('office','calibre')) {
    Test-Case "$kind pruning is idempotent, including notices and registration rewrites" {
        $root = New-Payload $kind ($kind + '-idempotent')
        [void](Optimize $kind $root)
        $first = Snapshot $root
        $second = Optimize $kind $root
        if ($first -cne (Snapshot $root)) { throw 'Second pruning changed an already optimized runtime.' }
        $bytes = $second.SavedBytes
        if ($bytes -ne 0) { throw 'Second pruning reported additional removed bytes.' }
    }
    Test-Case "$kind refuses incomplete or unrelated directories without any mutation" {
        $root = Join-Path $taskRoot ($kind + '-unrelated')
        Write-FixtureFile $root 'program/classes/sentinel.txt' 'unrelated directory must remain intact'
        $before = Snapshot $root
        Reject { Optimize $kind $root } 'anchor|missing'
        if ($before -cne (Snapshot $root)) { throw 'Incomplete runtime was modified before validation.' }
    }
    Test-Case "$kind refuses root and descendant junctions before modifying any payload or target" {
        $root = New-Payload $kind ($kind + '-junction-payload')
        $outside = Join-Path $taskRoot ($kind + '-junction-target')
        Write-FixtureFile $outside 'sentinel.txt' 'junction target must remain intact'
        $linkedRoot = Join-Path $taskRoot ($kind + '-linked-root')
        [void](New-Item -ItemType Junction -Path $linkedRoot -Target $root)
        $before = Snapshot $root
        Reject { Optimize $kind $linkedRoot } 'reparse|ordinary directory'
        if ($before -cne (Snapshot $root)) { throw 'Root junction target was modified.' }
        $link = Join-Path $root 'unexpected-junction'
        [void](New-Item -ItemType Junction -Path $link -Target $outside)
        Reject { Optimize $kind $root } 'reparse'
        if ([IO.File]::ReadAllText((Join-Path $outside 'sentinel.txt')) -cne 'junction target must remain intact') { throw 'Descendant junction target was modified.' }
    }
    Test-Case "$kind refuses an ordinary runtime reached through an ancestor junction" {
        $parent = Join-Path $taskRoot ($kind + '-ancestor-target')
        [void][IO.Directory]::CreateDirectory($parent)
        $root = New-Payload $kind ($kind + '-ancestor-target/runtime')
        $linkedParent = Join-Path $taskRoot ($kind + '-linked-parent')
        [void](New-Item -ItemType Junction -Path $linkedParent -Target $parent)
        $before = Snapshot $root
        Reject { Optimize $kind (Join-Path $linkedParent 'runtime') } 'reparse'
        if ($before -cne (Snapshot $root)) { throw 'Ancestor junction target was modified.' }
    }
}
Test-Case 'Native closure permits Windows imports and rejects missing direct and transitive private DLLs' {
    $root = Join-Path $taskRoot 'native-closure'
    New-PeFixture -Path (Join-Path $root 'tool.exe') -Imports @('private-a.dll','KERNEL32.dll','msvcp_win.dll','api-ms-win-crt-runtime-l1-1-0.dll')
    New-PeFixture -Path (Join-Path $root 'private-a.dll') -DelayImports @('private-b.dll')
    New-PeFixture -Path (Join-Path $root 'private-b.dll') -Imports @('ucrtbase.dll')
    $original = @(Get-EngineNativeModuleNames -Directory $root)
    Assert-PrunedEngineNativeClosure -Directory $root -RuntimeArchitecture x64 -KnownPrivateModules $original
    Remove-Item -LiteralPath (Join-Path $root 'private-a.dll')
    Reject { Assert-PrunedEngineNativeClosure -Directory $root -RuntimeArchitecture x64 -KnownPrivateModules $original } 'removed private dependency.*private-a.dll.*tool.exe'
    New-PeFixture -Path (Join-Path $root 'private-a.dll') -DelayImports @('private-b.dll')
    Remove-Item -LiteralPath (Join-Path $root 'private-b.dll')
    $hostRuntime = Join-Path $taskRoot 'native-host-path'
    New-PeFixture -Path (Join-Path $hostRuntime 'private-b.dll')
    $previousPath = $env:PATH
    try {
        $env:PATH = $hostRuntime + [IO.Path]::PathSeparator + $previousPath
        Reject { Assert-PrunedEngineNativeClosure -Directory $root -RuntimeArchitecture x64 -KnownPrivateModules $original } 'removed private dependency.*private-b.dll.*private-a.dll'
    } finally { $env:PATH = $previousPath }
}
Test-Case 'Native closure inspects architecture of unlisted retained plugins and rejects escaped inventory names' {
    $root = Join-Path $taskRoot 'native-closure-architecture'
    New-PeFixture -Path (Join-Path $root 'tool.exe')
    $original = @(Get-EngineNativeModuleNames -Directory $root)
    New-PeFixture -Path (Join-Path $root 'unlisted-plugin.pyd') -Machine 0xaa64
    Reject { Assert-PrunedEngineNativeClosure -Directory $root -RuntimeArchitecture x64 -KnownPrivateModules $original } 'Wrong x64 pruned engine PE architecture'
    Reject { Assert-PrunedEngineNativeClosure -Directory $root -RuntimeArchitecture x64 -KnownPrivateModules @('../private.dll') } 'must be leaf filenames'
}
Test-Case 'AnyCPU CLI DLLs pass while required-32-bit managed or native x86 DLLs remain rejected' {
    $root = Join-Path $taskRoot 'native-closure-anycpu'
    New-PeFixture -Path (Join-Path $root 'tool.exe')
    $module = Join-Path $root 'managed.dll'
    New-AnyCpuFixture -Path $module
    $metadata = Get-EnginePeImports -Path $module
    if (-not $metadata.IsAnyCpu -or $metadata.Machine -ne 0x14c -or $metadata.Magic -ne 0x10b) { throw 'Synthetic CLI header was not recognized as AnyCPU.' }
    $original = @(Get-EngineNativeModuleNames -Directory $root)
    Assert-PrunedEngineNativeClosure -Directory $root -RuntimeArchitecture x64 -KnownPrivateModules $original
    foreach ($flags in @(3, 0x11)) {
        New-AnyCpuFixture -Path $module -CorFlags $flags
        Reject { Assert-PrunedEngineNativeClosure -Directory $root -RuntimeArchitecture x64 -KnownPrivateModules $original } 'Wrong x64 pruned engine PE architecture'
    }
    New-PeFixture -Path $module -Machine 0x14c
    Reject { Assert-PrunedEngineNativeClosure -Directory $root -RuntimeArchitecture x64 -KnownPrivateModules $original } 'Wrong x64 pruned engine PE architecture'
}
Test-Case 'Original foreign helper exceptions require the same relative path, PE shape and complete bytes' {
    $root = Join-Path $taskRoot 'native-closure-foreign-helper'
    New-PeFixture -Path (Join-Path $root 'tool.exe')
    New-AnyCpuFixture -Path (Join-Path $root 'neutral.dll')
    $helper = Join-Path $root 'helpers/legacy-helper.exe'
    New-PeFixture -Path $helper -Machine 0x14c -Imports @('KERNEL32.dll')
    $original = @(Get-EngineNativeModuleNames -Directory $root)
    $allowed = @(Get-EngineForeignArchitectureFiles -Directory $root -RuntimeArchitecture x64)
    if ($allowed.Count -ne 1 -or $allowed[0].path -cne 'helpers/legacy-helper.exe') { throw 'Foreign inventory included a host or neutral assembly, or lost the exact helper path.' }
    $captured = $allowed[0]
    if ($captured.machine -ne 0x14c -or $captured.magic -ne 0x20b -or $captured.sha256 -ine (Get-FileHash -LiteralPath $helper).Hash) { throw 'Foreign inventory did not bind the original PE architecture and bytes.' }
    Assert-PrunedEngineNativeClosure -Directory $root -RuntimeArchitecture x64 -KnownPrivateModules $original -AllowedArchitectureFiles $allowed
    $bytes = [IO.File]::ReadAllBytes($helper)
    $changed = [byte[]]$bytes.Clone()
    $changed[$changed.Length - 1] = $changed[$changed.Length - 1] -bxor 1
    [IO.File]::WriteAllBytes($helper, $changed)
    Reject { Assert-PrunedEngineNativeClosure -Directory $root -RuntimeArchitecture x64 -KnownPrivateModules $original -AllowedArchitectureFiles $allowed } 'Wrong x64 pruned engine PE architecture|foreign.*mismatch'
    [IO.File]::WriteAllBytes($helper, $bytes)
    foreach ($field in @('machine','magic','sha256')) {
        $invalid = $captured | ConvertTo-Json | ConvertFrom-Json
        $invalid.$field = switch ($field) { machine { 0xaa64 }; magic { 0x10b }; sha256 { '0' * 64 } }
        Reject { Assert-PrunedEngineNativeClosure -Directory $root -RuntimeArchitecture x64 -KnownPrivateModules $original -AllowedArchitectureFiles @($invalid) } 'Wrong x64 pruned engine PE architecture|foreign.*mismatch'
    }
    $moved = Join-Path $root 'helpers/renamed-helper.exe'
    Move-Item -LiteralPath $helper -Destination $moved
    Reject { Assert-PrunedEngineNativeClosure -Directory $root -RuntimeArchitecture x64 -KnownPrivateModules $original -AllowedArchitectureFiles $allowed } 'Wrong x64 pruned engine PE architecture|foreign.*mismatch'
}
Test-Case 'Foreign helper exception receipts reject escaped paths, invalid hashes and duplicate records' {
    $root = Join-Path $taskRoot 'native-closure-foreign-receipt'
    New-PeFixture -Path (Join-Path $root 'tool.exe')
    $helper = Join-Path $root 'legacy-helper.exe'
    New-PeFixture -Path $helper -Machine 0x14c
    $original = @(Get-EngineNativeModuleNames -Directory $root)
    $allowed = @(Get-EngineForeignArchitectureFiles -Directory $root -RuntimeArchitecture x64)
    foreach ($path in @('../legacy-helper.exe','nested/../legacy-helper.exe',$helper)) {
        $invalid = $allowed[0] | ConvertTo-Json | ConvertFrom-Json
        $invalid.path = $path
        Reject { Assert-PrunedEngineNativeClosure -Directory $root -RuntimeArchitecture x64 -KnownPrivateModules $original -AllowedArchitectureFiles @($invalid) } 'Invalid original architecture exception path'
    }
    $invalid = $allowed[0] | ConvertTo-Json | ConvertFrom-Json
    $invalid.sha256 = 'not-a-sha256'
    Reject { Assert-PrunedEngineNativeClosure -Directory $root -RuntimeArchitecture x64 -KnownPrivateModules $original -AllowedArchitectureFiles @($invalid) } 'Invalid or duplicate original architecture exception'
    Reject { Assert-PrunedEngineNativeClosure -Directory $root -RuntimeArchitecture x64 -KnownPrivateModules $original -AllowedArchitectureFiles @($allowed[0],$allowed[0]) } 'Invalid or duplicate original architecture exception'
}
Test-Case 'Foreign helper architecture exceptions never waive private dependency closure' {
    $root = Join-Path $taskRoot 'native-closure-foreign-dependencies'
    New-PeFixture -Path (Join-Path $root 'tool.exe')
    New-PeFixture -Path (Join-Path $root 'legacy-helper.exe') -Machine 0x14c -DelayImports @('private-helper.dll')
    New-PeFixture -Path (Join-Path $root 'private-helper.dll') -Machine 0x14c
    $original = @(Get-EngineNativeModuleNames -Directory $root)
    $allowed = @(Get-EngineForeignArchitectureFiles -Directory $root -RuntimeArchitecture x64)
    Assert-PrunedEngineNativeClosure -Directory $root -RuntimeArchitecture x64 -KnownPrivateModules $original -AllowedArchitectureFiles $allowed
    Remove-Item -LiteralPath (Join-Path $root 'private-helper.dll')
    Reject { Assert-PrunedEngineNativeClosure -Directory $root -RuntimeArchitecture x64 -KnownPrivateModules $original -AllowedArchitectureFiles $allowed } 'removed private dependency.*private-helper.dll.*legacy-helper.exe'
}
Write-Output "Engine pruning tests: $script:passed passed. Synthetic resources only; no executables launched. Fixtures retained at $taskRoot."
