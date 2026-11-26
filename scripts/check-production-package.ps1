<#
Microsoft Store rules for the built MSIX (docs/store-readiness.md, "Automated checks"). A production package
(-DebugServer false) must declare only the Store's capabilities (never the debug server's inputInjectionBrokered),
target Windows.Xbox alone, carry revision 0, hold no Microsoft.UI.Xaml (WinUI 2 is the debug server's WebView2 test), no
.pdb and none of the debug markers below, and ship rav1g.dll with its licences (the release AV1 decoder since
2026-11-26). A debug-server package (-DebugServer true) must hold every debug marker and the capability: that proves on
every push that this scan would see them, although production packages are built only on request.
#>
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][ValidateSet('true', 'false')][string]$DebugServer
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/rav1g-common.ps1"

# Package.appxmanifest; justified for Partner Center in docs/store-readiness.md.
$StoreCapabilities = @('internetClient', 'privateNetworkClientServer', 'hevcPlayback')
$DebugCapability = 'inputInjectionBrokered'
# Strings only debug-server code holds: debug server routes (UTF-16 string literals in the Native AOT image,
# Services/DebugServer.cs) and report text of bale_gpu's debug probes (UTF-8, Cargo feature debug-probes).
$DebugMarkers = @(
    @{ File = 'Bale.exe'; Encoding = 'utf-16'; Text = '/media-fault' }
    @{ File = 'Bale.exe'; Encoding = 'utf-16'; Text = '/audio-out-test' }
    @{ File = 'Bale.exe'; Encoding = 'utf-16'; Text = '/playtest-ffmpeg' }
    @{ File = 'bale_gpu.dll'; Encoding = 'utf-8'; Text = 'Audio passthrough probe: default render endpoint' }
    @{ File = 'bale_gpu.dll'; Encoding = 'utf-8'; Text = 'libass probe: panicked' }
)

function Read-Entry([System.IO.Compression.ZipArchive]$Zip, [string]$Name) {
    $entry = $Zip.GetEntry($Name)
    if (-not $entry) { throw "$Name missing from the MSIX" }
    $stream = $entry.Open()
    $bytes = [System.IO.MemoryStream]::new()
    try { $stream.CopyTo($bytes); return , $bytes.ToArray() } finally { $stream.Dispose(); $bytes.Dispose() }
}

# Byte search through Latin-1, which maps every byte to one char, so String.IndexOf does the scan.
$latin1 = [System.Text.Encoding]::Latin1
function Test-Marker([string]$Text, [hashtable]$Marker) {
    $pattern = if ($Marker.Encoding -eq 'utf-16') { [System.Text.Encoding]::Unicode.GetBytes($Marker.Text) } else { [System.Text.Encoding]::UTF8.GetBytes($Marker.Text) }
    return $Text.IndexOf($latin1.GetString($pattern), [StringComparison]::Ordinal) -ge 0
}

$production = $DebugServer -eq 'false'
$build = if ($production) { 'production' } else { 'debug-server' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path $PackagePath).Path)
try {
    [xml]$manifest = [System.Text.Encoding]::UTF8.GetString((Read-Entry $zip 'AppxManifest.xml'))

    $capabilities = @($manifest.SelectNodes('//*[local-name()="Capabilities"]/*') | ForEach-Object { $_.GetAttribute('Name') })
    $expected = if ($production) { $StoreCapabilities } else { $StoreCapabilities + $DebugCapability }
    if ($production -and $capabilities -contains $DebugCapability) {
        throw "The production manifest declares $DebugCapability (debug server only; the Store rejects it)"
    }
    $unexpected = @($capabilities | Where-Object { $expected -cnotcontains $_ })
    if ($unexpected) { throw "The $build manifest declares capabilities outside the allow-list: $($unexpected -join ', ')" }
    $missing = @($expected | Where-Object { $capabilities -cnotcontains $_ })
    if ($missing) { throw "The $build manifest lacks the capabilities: $($missing -join ', ')" }

    $families = @($manifest.SelectNodes('//*[local-name()="TargetDeviceFamily"]') | ForEach-Object { $_.GetAttribute('Name') })
    if ($families.Count -ne 1 -or $families[0] -cne 'Windows.Xbox') {
        throw "The $build manifest must target Windows.Xbox alone, not: $($families -join ', ')"
    }

    $identity = $manifest.SelectSingleNode('/*[local-name()="Package"]/*[local-name()="Identity"]')
    $version = [version]$identity.GetAttribute('Version')
    if ($production -and $version.Revision -ne 0) {
        throw "Production package version $($version): the Store needs revision 0 (ci.yml Stamp version)"
    }

    $names = @($zip.Entries | ForEach-Object { $_.FullName })
    if ($production) {
        $winui = @($names | Where-Object { $_ -match '(?i)(^|/)Microsoft\.UI\.Xaml[^/]*\.dll$' })
        $dependencies = @($manifest.SelectNodes('//*[local-name()="PackageDependency"]') | ForEach-Object { $_.GetAttribute('Name') } |
            Where-Object { $_ -like 'Microsoft.UI.Xaml*' })
        if ($winui -or $dependencies) { throw "The production package carries WinUI 2 (debug server only): $(@($winui + $dependencies) -join ', ')" }
        $symbols = @($names | Where-Object { $_ -match '(?i)\.pdb$' })
        if ($symbols) { throw "The production package carries symbol files: $($symbols -join ', ')" }
    }

    $files = @{}
    foreach ($marker in $DebugMarkers) {
        if (-not $files.ContainsKey($marker.File)) { $files[$marker.File] = $latin1.GetString((Read-Entry $zip $marker.File)) }
        $found = Test-Marker $files[$marker.File] $marker
        if ($production -and $found) { throw "The production $($marker.File) holds debug-server code ('$($marker.Text)')" }
        if (-not $production -and -not $found) {
            throw "The debug-server $($marker.File) lacks the debug marker '$($marker.Text)': update the markers in this script"
        }
    }

    if ($production) {
        $rav1g = $zip.GetEntry('rav1g.dll')
        if (-not $rav1g -or $rav1g.Length -eq 0) { throw 'rav1g.dll missing from the production package (the release AV1 decoder)' }
        foreach ($name in $Rav1gLicenseFiles) {
            if (-not $zip.GetEntry("Licenses/rav1g/$name")) { throw "Licenses/rav1g/$name missing from the production package" }
        }
    }
}
finally { $zip.Dispose() }

if ($production -and $identity.GetAttribute('Publisher') -ceq 'CN=Bale Development') {
    Write-Output 'Identity: the development identity (Bale.ClientForSilo, CN=Bale Development). A Store upload needs the reserved one: set the BALE_STORE_* repository variables (docs/store-readiness.md).'
}
if ($production) {
    Write-Output "Production package $($version): capabilities $($capabilities -join ', '); Windows.Xbox; revision 0; no WinUI 2, symbols or debug markers; rav1g.dll and $($Rav1gLicenseFiles.Count) licences: PASS"
}
else {
    Write-Output "Debug-server package $($version): Windows.Xbox, $DebugCapability and all $($DebugMarkers.Count) debug markers present (the production scan sees them): PASS"
}
