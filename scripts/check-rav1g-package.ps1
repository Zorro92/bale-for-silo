<#
Checks the built MSIX against scripts/rav1g.json. rav1g is the default AV1 decoder in every build (owner, 2026-11-26), so
debug-server and production builds with a pinned release alike must carry rav1g.dll unchanged (SHA256 of the pin), its
licence texts under Licenses/rav1g/, and a Microsoft.VCLibs.140.00 package dependency (rav1g.dll links the app CRT,
vcruntime140_app and the UCRT API sets, from VCLibs; the build adds the dependency for FFmpegInteropX today). With
-Dumpbin it also checks the exports the fork resolves and that the DLL imports no desktop CRT. An empty pin must carry no
rav1g.dll (AV1 then stays on dav1d at 1080p60). -DebugServer names the build in the result.
#>
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][ValidateSet('true', 'false')][string]$DebugServer,
    [string]$PinPath = (Join-Path $PSScriptRoot 'rav1g.json'),
    [string]$Dumpbin
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/rav1g-common.ps1"
$pin = Get-Rav1gPin $PinPath
Add-Type -AssemblyName System.IO.Compression.FileSystem
$inspection = Join-Path ([System.IO.Path]::GetTempPath()) "bale-rav1g-check-$([guid]::NewGuid())"
New-Item -ItemType Directory $inspection | Out-Null
try {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $dll = $zip.GetEntry('rav1g.dll')
        $licenses = @($zip.Entries | Where-Object { $_.FullName.StartsWith('Licenses/rav1g/', [StringComparison]::Ordinal) })
        $build = if ($DebugServer -eq 'true') { 'debug-server build' } else { 'production build' }
        if (-not $pin) {
            if ($dll -or $licenses) { throw 'rav1g.dll belongs only in builds with a pinned release' }
            Write-Output "rav1g: none packaged (no release pinned, $build)"
            return
        }
        if (-not $dll) { throw "rav1g.dll missing from the MSIX ($build)" }
        foreach ($name in $Rav1gLicenseFiles) {
            if (-not $zip.GetEntry("Licenses/rav1g/$name")) { throw "Licenses/rav1g/$name missing from the MSIX" }
        }
        $manifestEntry = $zip.GetEntry('AppxManifest.xml')
        if (-not $manifestEntry) { throw 'AppxManifest.xml missing from the MSIX' }
        $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $dependencies = @($manifest.SelectNodes('//*[local-name()="PackageDependency"]') | ForEach-Object { $_.GetAttribute('Name') })
        if ($dependencies -cnotcontains 'Microsoft.VCLibs.140.00') {
            throw 'rav1g.dll needs the app CRT: the MSIX must declare a Microsoft.VCLibs.140.00 package dependency'
        }
        $packaged = Join-Path $inspection 'rav1g.dll'
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($dll, $packaged)
    }
    finally { $zip.Dispose() }

    $actual = (Get-FileHash $packaged -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne $pin.dll_sha256) { throw "Packaged rav1g.dll SHA256 $actual does not match the pin $($pin.dll_sha256)" }

    if ($Dumpbin) {
        $exports = & $Dumpbin /exports $packaged
        if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect packaged rav1g.dll exports' }
        foreach ($symbol in $Rav1gExports) {
            if (-not ($exports | Select-String -Pattern "\s$([regex]::Escape($symbol))\s*$" -Quiet)) {
                throw "Packaged rav1g.dll is missing export $symbol"
            }
        }
        $dependents = & $Dumpbin /dependents $packaged
        if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect packaged rav1g.dll dependents' }
        # The desktop CRT is not in the console's app container; only the app CRT (*_app.dll, VCLibs) is.
        if ($dependents -match '(?i)^\s*(vcruntime140(_1)?|msvcp140(_\d+)?|ucrtbase)d?\.dll\s*$') {
            throw 'Packaged rav1g.dll imports the desktop CRT'
        }
    }
    Write-Output "Packaged rav1g.dll $($pin.version) ($build): SHA256 matches the pin, licences and VCLibs dependency present$(if ($Dumpbin) { ", $($Rav1gExports.Count) exports present" })"
}
finally { Remove-Item $inspection -Recurse -Force }
