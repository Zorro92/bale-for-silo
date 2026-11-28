# Exercises restore-rav1g.ps1 and check-rav1g-package.ps1 on small local fixtures (no network): the empty pin, a
# verified restore, tampered assets, a wrong interface version, unexpected licence entries, the MSIX checks of
# debug-server and production packages, and a debug-only test build (CI's rav1g_test_* inputs) in place of the pin.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/rav1g-common.ps1"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Join-Path ([System.IO.Path]::GetTempPath()) "bale-rav1g-test-$([guid]::NewGuid())"
New-Item -ItemType Directory $root | Out-Null

function New-Zip {
    param([string]$Path, [hashtable]$Files)
    $zip = [System.IO.Compression.ZipFile]::Open($Path, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $Files.Keys) {
            $writer = [System.IO.StreamWriter]::new($zip.CreateEntry($name).Open())
            try { $writer.Write($Files[$name]) } finally { $writer.Dispose() }
        }
    }
    finally { $zip.Dispose() }
}

function Hash([string]$Path) { (Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

function Assert-Throws([scriptblock]$Block, [string]$Message) {
    $thrown = $false
    try { & $Block | Out-Null } catch {
        if (-not $_.Exception.Message.Contains($Message)) { throw "Expected '$Message', got '$($_.Exception.Message)'" }
        $thrown = $true
    }
    if (-not $thrown) { throw "Expected a failure: $Message" }
}

try {
    $assets = Join-Path $root 'assets'
    New-Item -ItemType Directory $assets | Out-Null
    Set-Content (Join-Path $assets 'rav1g.dll') 'fixture dll' -NoNewline
    Set-Content (Join-Path $assets 'rav1g.h') "#pragma once`n#define RAV1G_ABI_VERSION 4`n" -NoNewline
    $licenseFiles = @{}
    foreach ($name in $Rav1gLicenseFiles) { $licenseFiles[$name] = "text of $name" }
    New-Zip (Join-Path $assets 'rav1g-licenses.zip') $licenseFiles

    $pin = [ordered]@{
        version = '0.4.0-alpha.1'; release_repository = 'Zorro92/rav1g'; release_tag = 'v0.4.0-alpha.1'
        source_commit = '3f330a19965457a8973574059d317837f5b40e1e'; abi_version = 4
        dll_asset = 'rav1g.dll'; dll_sha256 = Hash (Join-Path $assets 'rav1g.dll')
        header_asset = 'rav1g.h'; header_sha256 = Hash (Join-Path $assets 'rav1g.h')
        licenses_asset = 'rav1g-licenses.zip'; licenses_sha256 = Hash (Join-Path $assets 'rav1g-licenses.zip')
    }
    $pinPath = Join-Path $root 'rav1g.json'
    function Write-Pin($Values) { $Values | ConvertTo-Json | Set-Content $pinPath }
    $out = Join-Path $root 'out'

    # The repository's own pin parses (empty or complete).
    [void](Get-Rav1gPin (Join-Path $PSScriptRoot 'rav1g.json'))

    # Empty pin: nothing restored, a stale DLL removed.
    Write-Pin @{ release_tag = $null }
    New-Item -ItemType Directory $out | Out-Null
    Set-Content (Join-Path $out 'rav1g.dll') 'stale'
    $message = & "$PSScriptRoot/restore-rav1g.ps1" -PinPath $pinPath -OutDirectory $out -AssetDirectory $assets
    if ($message -notmatch 'no release pinned' -or (Test-Path $out)) { throw 'An empty pin must restore nothing' }

    # Complete pin: the DLL and exactly the licence files.
    Write-Pin $pin
    & "$PSScriptRoot/restore-rav1g.ps1" -PinPath $pinPath -OutDirectory $out -AssetDirectory $assets | Out-Null
    if ((Hash (Join-Path $out 'rav1g.dll')) -cne $pin.dll_sha256) { throw 'Restored rav1g.dll differs' }
    foreach ($name in $Rav1gLicenseFiles) {
        if (-not (Test-Path (Join-Path $out "licenses/$name"))) { throw "Missing restored licence $name" }
    }

    # Tampered assets, inconsistent pins, another interface version and unexpected licence entries are refused.
    $bad = [ordered]@{} + $pin; $bad.dll_sha256 = '0' * 64; Write-Pin $bad
    Assert-Throws { & "$PSScriptRoot/restore-rav1g.ps1" -PinPath $pinPath -OutDirectory $out -AssetDirectory $assets } 'SHA256 mismatch'
    $bad = [ordered]@{} + $pin; $bad.release_tag = 'v0.4.0'; Write-Pin $bad
    Assert-Throws { Get-Rav1gPin $pinPath } 'Invalid or inconsistent rav1g pin'
    $bad = [ordered]@{} + $pin; $bad.abi_version = 3; Write-Pin $bad
    Assert-Throws { Get-Rav1gPin $pinPath } 'Invalid or inconsistent rav1g pin'
    $bad = [ordered]@{} + $pin; $bad.release_repository = 'x/y?z'; Write-Pin $bad
    Assert-Throws { Get-Rav1gPin $pinPath } 'Invalid or inconsistent rav1g pin'
    $header = Join-Path $root 'header'
    New-Item -ItemType Directory $header | Out-Null
    Copy-Item (Join-Path $assets '*') $header
    Set-Content (Join-Path $header 'rav1g.h') "#define RAV1G_ABI_VERSION 3`n" -NoNewline
    $bad = [ordered]@{} + $pin; $bad.header_sha256 = Hash (Join-Path $header 'rav1g.h'); Write-Pin $bad
    Assert-Throws { & "$PSScriptRoot/restore-rav1g.ps1" -PinPath $pinPath -OutDirectory $out -AssetDirectory $header } 'RAV1G_ABI_VERSION 4'
    $extra = Join-Path $root 'extra'
    New-Item -ItemType Directory $extra | Out-Null
    Copy-Item (Join-Path $assets '*') $extra
    Remove-Item (Join-Path $extra 'rav1g-licenses.zip')
    New-Zip (Join-Path $extra 'rav1g-licenses.zip') ($licenseFiles + @{ '../escape.txt' = 'x' })
    $bad = [ordered]@{} + $pin; $bad.licenses_sha256 = Hash (Join-Path $extra 'rav1g-licenses.zip'); Write-Pin $bad
    Assert-Throws { & "$PSScriptRoot/restore-rav1g.ps1" -PinPath $pinPath -OutDirectory $out -AssetDirectory $extra } 'must hold exactly'

    # MSIX checks.
    Write-Pin $pin
    $vclibs = '<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Dependencies>' +
        '<PackageDependency Name="Microsoft.VCLibs.140.00" MinVersion="14.0.0.0" Publisher="CN=x" /></Dependencies></Package>'
    $packaged = @{ 'rav1g.dll' = 'fixture dll'; 'AppxManifest.xml' = $vclibs }
    foreach ($name in $Rav1gLicenseFiles) { $packaged["Licenses/rav1g/$name"] = "text of $name" }
    function Test-Package([hashtable]$Files, [string]$DebugServer, [string]$Pin = $pinPath, [hashtable]$Test = @{}) {
        $msix = Join-Path $root "p-$([guid]::NewGuid()).msix"
        New-Zip $msix $Files
        & "$PSScriptRoot/check-rav1g-package.ps1" -PackagePath $msix -DebugServer $DebugServer -PinPath $Pin @Test
    }
    # rav1g is the default AV1 decoder in every build (owner, 2026-11-26): debug-server and production packages alike.
    foreach ($debugServer in @('true', 'false')) {
        if ((Test-Package $packaged $debugServer) -notmatch 'SHA256 matches the pin') { throw "A correct package must pass (debug=$debugServer)" }
        $noDll = @{ 'AppxManifest.xml' = $vclibs }
        Assert-Throws { Test-Package $noDll $debugServer } 'rav1g.dll missing'
        $changed = $packaged.Clone(); $changed['rav1g.dll'] = 'other'
        Assert-Throws { Test-Package $changed $debugServer } 'does not match the pin'
        $noVcLibs = $packaged.Clone(); $noVcLibs['AppxManifest.xml'] = '<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" />'
        Assert-Throws { Test-Package $noVcLibs $debugServer } 'Microsoft.VCLibs.140.00'
        $noLicense = $packaged.Clone(); $noLicense.Remove('Licenses/rav1g/LICENSE.txt')
        Assert-Throws { Test-Package $noLicense $debugServer } 'Licenses/rav1g/LICENSE.txt missing'
        $emptyPin = Join-Path $root 'empty.json'
        '{ "release_tag": null }' | Set-Content $emptyPin
        if ((Test-Package $noDll $debugServer $emptyPin) -notmatch 'no release pinned') { throw "An empty pin packages nothing (debug=$debugServer)" }
        Assert-Throws { Test-Package $packaged $debugServer $emptyPin } 'only in builds with a pinned release'
    }
    if ((Test-Package $packaged 'false') -notmatch 'production build') { throw 'The result names the production build' }

    # A test build (CI's manual rav1g_test_* inputs): another release, restored and packaged in place of the pin for a
    # debug-server package only, verified the same way; the pin file stays unchanged.
    $testAssets = Join-Path $root 'test-assets'
    New-Item -ItemType Directory $testAssets | Out-Null
    Copy-Item (Join-Path $assets '*') $testAssets
    Set-Content (Join-Path $testAssets 'rav1g.dll') 'test build dll' -NoNewline
    $test = @{
        TestTag = 'v0.9.0-test.1'
        # Either case, as Get-FileHash or sha256sum prints it.
        TestDllSha256 = (Hash (Join-Path $testAssets 'rav1g.dll')).ToUpperInvariant()
        TestHeaderSha256 = Hash (Join-Path $testAssets 'rav1g.h')
        TestLicensesSha256 = Hash (Join-Path $testAssets 'rav1g-licenses.zip')
    }
    # Over a complete pin and an empty one alike.
    foreach ($basePin in @($pin, @{ release_tag = $null })) {
        Write-Pin $basePin
        $pinHash = Hash $pinPath
        $message = (& "$PSScriptRoot/restore-rav1g.ps1" -PinPath $pinPath -OutDirectory $out -AssetDirectory $testAssets -DebugServer true @test) -join "`n"
        if ($message -notmatch 'TEST rav1g build from tag v0\.9\.0-test\.1 \(Zorro92/rav1g\) instead of the scripts/rav1g\.json pin') {
            throw "A test build must say so first: $message"
        }
        if ($message -notmatch 'TEST rav1g build from tag v0\.9\.0-test\.1 verified') { throw "A test build must say it was verified: $message" }
        if ((Hash (Join-Path $out 'rav1g.dll')) -cne (Hash (Join-Path $testAssets 'rav1g.dll'))) { throw 'The test build was not restored' }
        foreach ($name in $Rav1gLicenseFiles) {
            if (-not (Test-Path (Join-Path $out "licenses/$name"))) { throw "Missing restored licence $name (test build)" }
        }
        if ((Hash $pinPath) -cne $pinHash) { throw 'A test build must leave the pin file unchanged' }
    }
    Write-Pin $pin
    # It downloads from the pin's repository, at the test tag.
    $testPin = Get-Rav1gTestPin -Tag ' v0.9.0-test.1 ' -DebugServer true -DllSha256 $test.TestDllSha256 `
        -HeaderSha256 $test.TestHeaderSha256 -LicensesSha256 $test.TestLicensesSha256
    if ((Get-Rav1gAssetUrl $testPin 'rav1g.dll') -cne 'https://github.com/Zorro92/rav1g/releases/download/v0.9.0-test.1/rav1g.dll') {
        throw "Unexpected test build URL: $(Get-Rav1gAssetUrl $testPin 'rav1g.dll')"
    }
    $repositoryPin = Get-Rav1gPin (Join-Path $PSScriptRoot 'rav1g.json')
    if ($repositoryPin -and $repositoryPin.release_repository -cne $Rav1gTestRepository) { throw 'Test builds must come from the pin''s repository' }
    if ((Get-Rav1gAssetUrl (Get-Rav1gPin $pinPath) 'rav1g.h') -cne 'https://github.com/Zorro92/rav1g/releases/download/v0.4.0-alpha.1/rav1g.h') {
        throw 'The pin''s download URL changed'
    }

    # Refused: production or an unnamed build, a missing or malformed hash, a malformed tag, hashes without a tag, a
    # tampered asset and another interface version.
    function Restore-Test([hashtable]$Values, [string]$DebugServer, [string]$Assets = $testAssets) {
        if ($DebugServer) { & "$PSScriptRoot/restore-rav1g.ps1" -PinPath $pinPath -OutDirectory $out -AssetDirectory $Assets -DebugServer $DebugServer @Values }
        else { & "$PSScriptRoot/restore-rav1g.ps1" -PinPath $pinPath -OutDirectory $out -AssetDirectory $Assets @Values }
    }
    Assert-Throws { Restore-Test $test 'false' } 'test builds are for debug-server packages only'
    Assert-Throws { Restore-Test $test '' } 'test builds are for debug-server packages only'
    foreach ($name in @('Dll', 'Header', 'Licenses')) {
        $bad = $test.Clone(); $bad["Test$($name)Sha256"] = ''
        Assert-Throws { Restore-Test $bad 'true' } "rav1g_test_$($name.ToLowerInvariant())_sha256 must be"
        $bad = $test.Clone(); $bad["Test$($name)Sha256"] = 'g' * 64
        Assert-Throws { Restore-Test $bad 'true' } "rav1g_test_$($name.ToLowerInvariant())_sha256 must be"
    }
    foreach ($tag in @('../v1', 'v1/x', 'v1 x', '-v1', "v1`nx", ('v' * 101))) {
        $bad = $test.Clone(); $bad.TestTag = $tag
        Assert-Throws { Restore-Test $bad 'true' } 'must be a release tag name'
    }
    $bad = $test.Clone(); $bad.Remove('TestTag')
    Assert-Throws { Restore-Test $bad 'true' } 'given without a test tag'
    $bad = $test.Clone(); $bad.TestDllSha256 = $pin.dll_sha256
    Assert-Throws { Restore-Test $bad 'true' } 'SHA256 mismatch'
    $testHeader = Join-Path $root 'test-header'
    New-Item -ItemType Directory $testHeader | Out-Null
    Copy-Item (Join-Path $testAssets '*') $testHeader
    Set-Content (Join-Path $testHeader 'rav1g.h') "#define RAV1G_ABI_VERSION 5`n" -NoNewline
    $bad = $test.Clone(); $bad.TestHeaderSha256 = Hash (Join-Path $testHeader 'rav1g.h')
    Assert-Throws { Restore-Test $bad 'true' $testHeader } 'RAV1G_ABI_VERSION 4'

    # The package check takes the same values: the test build's DLL passes in a debug-server package, the pin's DLL
    # does not, and production refuses a test build. Without them the test DLL fails against the pin, as before.
    $testPackaged = $packaged.Clone(); $testPackaged['rav1g.dll'] = 'test build dll'
    $result = Test-Package $testPackaged 'true' -Test $test
    if ($result -notmatch 'TEST rav1g build from tag v0\.9\.0-test\.1 packaged \(debug-server build\): rav1g\.dll SHA256 matches the test build') {
        throw "A debug-server package with the test build must pass: $result"
    }
    Assert-Throws { Test-Package $packaged 'true' -Test $test } 'does not match the TEST build from tag v0.9.0-test.1'
    Assert-Throws { Test-Package $testPackaged 'false' -Test $test } 'test builds are for debug-server packages only'
    Assert-Throws { Test-Package $testPackaged 'true' } 'does not match the pin'
    Write-Output 'rav1g restore and package checks: fixtures pass'
}
finally { Remove-Item $root -Recurse -Force }
