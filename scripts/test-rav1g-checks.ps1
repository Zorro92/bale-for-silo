# Exercises restore-rav1g.ps1 and check-rav1g-package.ps1 on small local fixtures (no network): the empty pin, a
# verified restore, tampered assets, a wrong interface version, unexpected licence entries and the MSIX checks.
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
    Set-Content (Join-Path $assets 'rav1g.h') "#pragma once`n#define RAV1G_ABI_VERSION 3`n" -NoNewline
    $licenseFiles = @{}
    foreach ($name in $Rav1gLicenseFiles) { $licenseFiles[$name] = "text of $name" }
    New-Zip (Join-Path $assets 'rav1g-licenses.zip') $licenseFiles

    $pin = [ordered]@{
        version = '0.4.0-alpha.1'; release_repository = 'Zorro92/rav1g'; release_tag = 'v0.4.0-alpha.1'
        source_commit = '3f330a19965457a8973574059d317837f5b40e1e'; abi_version = 3
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
    $bad = [ordered]@{} + $pin; $bad.abi_version = 2; Write-Pin $bad
    Assert-Throws { Get-Rav1gPin $pinPath } 'Invalid or inconsistent rav1g pin'
    $bad = [ordered]@{} + $pin; $bad.release_repository = 'x/y?z'; Write-Pin $bad
    Assert-Throws { Get-Rav1gPin $pinPath } 'Invalid or inconsistent rav1g pin'
    $header = Join-Path $root 'header'
    New-Item -ItemType Directory $header | Out-Null
    Copy-Item (Join-Path $assets '*') $header
    Set-Content (Join-Path $header 'rav1g.h') "#define RAV1G_ABI_VERSION 2`n" -NoNewline
    $bad = [ordered]@{} + $pin; $bad.header_sha256 = Hash (Join-Path $header 'rav1g.h'); Write-Pin $bad
    Assert-Throws { & "$PSScriptRoot/restore-rav1g.ps1" -PinPath $pinPath -OutDirectory $out -AssetDirectory $header } 'RAV1G_ABI_VERSION 3'
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
    function Test-Package([hashtable]$Files, [string]$DebugServer, [string]$Pin = $pinPath) {
        $msix = Join-Path $root "p-$([guid]::NewGuid()).msix"
        New-Zip $msix $Files
        & "$PSScriptRoot/check-rav1g-package.ps1" -PackagePath $msix -DebugServer $DebugServer -PinPath $Pin
    }
    if ((Test-Package $packaged 'true') -notmatch 'SHA256 matches the pin') { throw 'A correct package must pass' }
    $noDll = @{ 'AppxManifest.xml' = $vclibs }
    if ((Test-Package $noDll 'false') -notmatch 'production build') { throw 'Production carries no rav1g.dll' }
    Assert-Throws { Test-Package $packaged 'false' } 'only in debug-server builds'
    Assert-Throws { Test-Package $noDll 'true' } 'rav1g.dll missing'
    $changed = $packaged.Clone(); $changed['rav1g.dll'] = 'other'
    Assert-Throws { Test-Package $changed 'true' } 'does not match the pin'
    $noVcLibs = $packaged.Clone(); $noVcLibs['AppxManifest.xml'] = '<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" />'
    Assert-Throws { Test-Package $noVcLibs 'true' } 'Microsoft.VCLibs.140.00'
    $noLicense = $packaged.Clone(); $noLicense.Remove('Licenses/rav1g/LICENSE.txt')
    Assert-Throws { Test-Package $noLicense 'true' } 'Licenses/rav1g/LICENSE.txt missing'
    $emptyPin = Join-Path $root 'empty.json'
    '{ "release_tag": null }' | Set-Content $emptyPin
    if ((Test-Package $noDll 'true' $emptyPin) -notmatch 'no release pinned') { throw 'An empty pin packages nothing' }
    Assert-Throws { Test-Package $packaged 'true' $emptyPin } 'only in debug-server builds'
    Write-Output 'rav1g restore and package checks: fixtures pass'
}
finally { Remove-Item $root -Recurse -Force }
