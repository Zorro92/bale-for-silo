# Exercises check-production-package.ps1 on small MSIX fixtures (no network): a good production package, each Store
# rule it must refuse, and the debug-server direction (markers and the capability required). With -BaleCoreLibraries
# <production>,<debug-server> (built bale_core libraries, e.g. Linux libbale_core.so from cargo with and without
# --features debug-probes) it also runs check-bale-core-calls.ps1 both ways.
param([string]$BaleCoreLibraries)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/rav1g-common.ps1"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Join-Path ([System.IO.Path]::GetTempPath()) "bale-production-test-$([guid]::NewGuid())"
New-Item -ItemType Directory $root | Out-Null

function New-Msix([hashtable]$Files) {
    $path = Join-Path $root "p-$([guid]::NewGuid()).msix"
    $zip = [System.IO.Compression.ZipFile]::Open($path, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $Files.Keys) {
            $bytes = if ($Files[$name] -is [byte[]]) { $Files[$name] } else { [System.Text.Encoding]::UTF8.GetBytes([string]$Files[$name]) }
            $stream = $zip.CreateEntry($name).Open()
            try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
        }
    }
    finally { $zip.Dispose() }
    return $path
}

function Manifest([string]$Version = '0.1.812.0', [string]$Family = 'Windows.Xbox', [string[]]$Extra = @(), [string]$Dependency = '') {
    $capabilities = '<Capability Name="internetClient" /><Capability Name="privateNetworkClientServer" />' +
        '<rescap:Capability Name="hevcPlayback" />' + (($Extra | ForEach-Object { "<rescap:Capability Name=`"$_`" />" }) -join '')
    $dependencies = "<TargetDeviceFamily Name=`"$Family`" MinVersion=`"10.0.19041.0`" MaxVersionTested=`"10.0.26100.0`" />" +
        '<PackageDependency Name="Microsoft.VCLibs.140.00" MinVersion="14.0.0.0" Publisher="CN=x" />' +
        $(if ($Dependency) { "<PackageDependency Name=`"$Dependency`" MinVersion=`"2.8.0.0`" Publisher=`"CN=x`" />" } else { '' })
    return '<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" ' +
        'xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities">' +
        "<Identity Name=`"Bale.ClientForSilo`" Publisher=`"CN=Bale Development`" Version=`"$Version`" />" +
        "<Dependencies>$dependencies</Dependencies><Capabilities>$capabilities</Capabilities></Package>"
}

function Binary([string[]]$Utf16 = @(), [string[]]$Utf8 = @()) {
    $bytes = [System.Collections.Generic.List[byte]]::new()
    $bytes.AddRange([byte[]](0x4D, 0x5A, 0, 0, 0xFF))
    foreach ($text in $Utf16) { $bytes.AddRange([System.Text.Encoding]::Unicode.GetBytes($text)); $bytes.AddRange([byte[]](0, 0)) }
    foreach ($text in $Utf8) { $bytes.AddRange([System.Text.Encoding]::UTF8.GetBytes($text)); $bytes.Add(0) }
    return , $bytes.ToArray()
}

function Package([hashtable]$Changes = @{}) {
    $files = @{
        'AppxManifest.xml' = Manifest
        'Bale.exe' = Binary -Utf16 @('/api/v2/playback/start')
        'bale_gpu.dll' = Binary -Utf8 @('bale_tonemap_create')
        'rav1g.dll' = 'fixture dll'
    }
    foreach ($name in $Rav1gLicenseFiles) { $files["Licenses/rav1g/$name"] = "text of $name" }
    foreach ($name in $Changes.Keys) {
        if ($null -eq $Changes[$name]) { $files.Remove($name) } else { $files[$name] = $Changes[$name] }
    }
    return New-Msix $files
}

# The check's output as one string (a production package with the development identity also prints a note).
function Check([string]$Package, [string]$DebugServer = 'false') {
    (& "$PSScriptRoot/check-production-package.ps1" -PackagePath $Package -DebugServer $DebugServer) -join "`n"
}

function Assert-Throws([scriptblock]$Block, [string]$Message) {
    $thrown = $false
    try { & $Block | Out-Null } catch {
        if (-not $_.Exception.Message.Contains($Message)) { throw "Expected '$Message', got '$($_.Exception.Message)'" }
        $thrown = $true
    }
    if (-not $thrown) { throw "Expected a failure: $Message" }
}

try {
    # A good production package passes, with a note while it has the development identity.
    $result = Check (Package)
    if ($result -notmatch 'PASS' -or $result -notmatch 'A Store upload needs the reserved one') { throw "A Store-ready production package must pass: $result" }
    $store = (Manifest).Replace('Name="Bale.ClientForSilo" Publisher="CN=Bale Development"', 'Name="12345Bale.Bale" Publisher="CN=00000000-0000-0000-0000-000000000000"')
    $result = Check (Package @{ 'AppxManifest.xml' = $store })
    if ($result -notmatch 'PASS' -or $result -match 'reserved one') { throw "The Store identity needs no note: $result" }

    # Store rules.
    Assert-Throws { Check (Package @{ 'AppxManifest.xml' = Manifest -Extra @('inputInjectionBrokered') }) } 'declares inputInjectionBrokered'
    Assert-Throws { Check (Package @{ 'AppxManifest.xml' = Manifest -Extra @('broadFileSystemAccess') }) } 'outside the allow-list: broadFileSystemAccess'
    Assert-Throws { Check (Package @{ 'AppxManifest.xml' = (Manifest).Replace('<rescap:Capability Name="hevcPlayback" />', '') }) } 'lacks the capabilities: hevcPlayback'
    Assert-Throws { Check (Package @{ 'AppxManifest.xml' = Manifest -Family 'Windows.Universal' }) } 'must target Windows.Xbox alone'
    $twoFamilies = (Manifest).Replace('<Dependencies>', '<Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" />')
    Assert-Throws { Check (Package @{ 'AppxManifest.xml' = $twoFamilies }) } 'must target Windows.Xbox alone'
    Assert-Throws { Check (Package @{ 'AppxManifest.xml' = Manifest -Version '0.1.812.1' }) } 'needs revision 0'
    Assert-Throws { Check (Package @{ 'Microsoft.UI.Xaml.dll' = 'winui' }) } 'carries WinUI 2'
    Assert-Throws { Check (Package @{ 'AppxManifest.xml' = Manifest -Dependency 'Microsoft.UI.Xaml.2.8' }) } 'carries WinUI 2'
    Assert-Throws { Check (Package @{ 'Bale.pdb' = 'symbols' }) } 'carries symbol files: Bale.pdb'
    Assert-Throws { Check (Package @{ 'Bale.exe' = Binary -Utf16 @('/api/v2/playback/start', '/media-fault') }) } "holds debug-server code ('/media-fault')"
    Assert-Throws { Check (Package @{ 'bale_gpu.dll' = Binary -Utf8 @('libass probe: panicked') }) } "holds debug-server code ('libass probe: panicked')"
    # UTF-8 text in the managed image is no route literal; the marker's encoding counts.
    if ((Check (Package @{ 'Bale.exe' = Binary -Utf8 @('/media-fault') })) -notmatch 'PASS') { throw 'Only UTF-16 route literals count in Bale.exe' }
    Assert-Throws { Check (Package @{ 'rav1g.dll' = $null }) } 'rav1g.dll missing from the production package'
    Assert-Throws { Check (Package @{ 'Licenses/rav1g/LICENSE.txt' = $null }) } 'Licenses/rav1g/LICENSE.txt missing'
    Assert-Throws { Check (Package @{ 'Bale.exe' = $null }) } 'Bale.exe missing from the MSIX'

    # The debug-server direction: the capability and every marker must be there.
    $markers = @{
        'AppxManifest.xml' = Manifest -Version '0.1.812.3' -Extra @('inputInjectionBrokered') -Dependency 'Microsoft.UI.Xaml.2.8'
        'Bale.exe' = Binary -Utf16 @('/media-fault', '/audio-out-test', '/playtest-ffmpeg')
        'bale_gpu.dll' = Binary -Utf8 @('Audio passthrough probe: default render endpoint, exclusive-mode', 'libass probe: panicked')
        'rav1g.dll' = $null
    }
    if ((Check (Package $markers) 'true') -notmatch 'debug markers present') { throw 'A debug-server package with its markers must pass' }
    # A debug-server package may carry a TEST rav1g build (CI's rav1g_test_* inputs): the Store scan leaves rav1g.dll's
    # hash to check-rav1g-package.ps1, which takes the test build's values (test-rav1g-checks.ps1).
    $testRav1g = $markers.Clone(); $testRav1g['rav1g.dll'] = 'test build dll'
    foreach ($name in $Rav1gLicenseFiles) { $testRav1g["Licenses/rav1g/$name"] = "text of $name" }
    if ((Check (Package $testRav1g) 'true') -notmatch 'debug markers present') { throw 'A debug-server package with a test rav1g build must pass' }
    $noMarker = $markers.Clone(); $noMarker['Bale.exe'] = Binary -Utf16 @('/media-fault', '/audio-out-test')
    Assert-Throws { Check (Package $noMarker) 'true' } "lacks the debug marker '/playtest-ffmpeg'"
    $noCapability = $markers.Clone(); $noCapability['AppxManifest.xml'] = Manifest -Version '0.1.812.3'
    Assert-Throws { Check (Package $noCapability) 'true' } 'lacks the capabilities: inputInjectionBrokered'
    $universal = $markers.Clone(); $universal['AppxManifest.xml'] = Manifest -Family 'Windows.Universal' -Extra @('inputInjectionBrokered')
    Assert-Throws { Check (Package $universal) 'true' } 'must target Windows.Xbox alone'

    # The WACK summary reads appcert's XML report (REPORT/REQUIREMENTS/REQUIREMENT/TEST/RESULT/MESSAGES).
    $wack = Join-Path $root 'wack'
    New-Item -ItemType Directory $wack | Out-Null
    Set-Content (Join-Path $wack 'wack-report.xml') -Value ('<?xml version="1.0" encoding="utf-8"?><REPORT OVERALL_RESULT="FAIL"><REQUIREMENTS>' +
        '<REQUIREMENT NUMBER="1" TITLE="Package compliance"><TEST INDEX="1" NAME="Package sanity"><RESULT><![CDATA[PASS]]></RESULT><MESSAGES /></TEST>' +
        '<TEST INDEX="2" NAME="Supported API"><RESULT><![CDATA[FAIL]]></RESULT><MESSAGES><MESSAGE TEXT="API Foo in bar.dll is not supported" /></MESSAGES></TEST>' +
        '<TEST INDEX="3" NAME="Debug configuration"><RESULT><![CDATA[NOT RUN]]></RESULT><MESSAGES /></TEST></REQUIREMENT></REQUIREMENTS></REPORT>')
    $summary = & "$PSScriptRoot/run-wack.ps1" -ReportDirectory $wack -SummaryOnly
    if (($summary -join "`n") -cne "WACK: overall FAIL; 3 tests, 1 not passed (appcert exit none)`n  FAIL: Supported API`n    API Foo in bar.dll is not supported") {
        throw "Unexpected WACK summary: $($summary -join ' | ')"
    }

    if ($BaleCoreLibraries) {
        $libraries = @($BaleCoreLibraries.Split(','))
        if ($libraries.Count -ne 2) { throw '-BaleCoreLibraries takes <production>,<debug-server>' }
        & "$PSScriptRoot/check-bale-core-calls.ps1" -LibraryPath $libraries[0] -DebugServer false
        & "$PSScriptRoot/check-bale-core-calls.ps1" -LibraryPath $libraries[1] -DebugServer true
        Assert-Throws { & "$PSScriptRoot/check-bale-core-calls.ps1" -LibraryPath $libraries[1] -DebugServer false } 'still answers the debug call media_fault.set'
        Assert-Throws { & "$PSScriptRoot/check-bale-core-calls.ps1" -LibraryPath $libraries[0] -DebugServer true } 'lacks the debug call media_fault.set'
    }
    Write-Output "Production package and WACK summary checks: fixtures pass$(if ($BaleCoreLibraries) { ', bale_core JSON calls checked both ways' })"
}
finally { Remove-Item $root -Recurse -Force }
