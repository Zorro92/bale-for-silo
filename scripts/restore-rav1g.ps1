<#
Restores the pinned rav1g.dll (GPU AV1 decoder, Zorro92/rav1g releases) and its licence texts into .cache/rav1g
before the Windows build, verified by SHA256 (scripts/rav1g.json). The debug av1-decoder switch uses it through
the FFmpegInteropX fork, which loads rav1g.dll from the package root (LoadPackagedLibrary).

An empty pin ("release_tag": null) restores nothing: the build then packages no rav1g.dll, the debug switch reports
rav1g unavailable and the fork falls back to dav1d. Releases are immutable; never repin a tag to new hashes.

-AssetDirectory reads the release assets from a local folder instead of GitHub (tests).
#>
param(
    [string]$PinPath = (Join-Path $PSScriptRoot 'rav1g.json'),
    [string]$OutDirectory = (Join-Path $PSScriptRoot '../.cache/rav1g'),
    [string]$AssetDirectory
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/rav1g-common.ps1"
$pin = Get-Rav1gPin $PinPath
# Always start clean: a stale DLL from an earlier pin must never reach the package.
if (Test-Path $OutDirectory) { Remove-Item $OutDirectory -Recurse -Force }
if (-not $pin) {
    Write-Output 'rav1g: no release pinned (scripts/rav1g.json release_tag is null); packaging no rav1g.dll'
    return
}

$staging = Join-Path ([System.IO.Path]::GetTempPath()) "bale-rav1g-$([guid]::NewGuid())"
New-Item -ItemType Directory $staging | Out-Null
try {
    $files = @{}
    foreach ($asset in @(
            @{ Name = $pin.dll_asset; Sha = $pin.dll_sha256 },
            @{ Name = $pin.header_asset; Sha = $pin.header_sha256 },
            @{ Name = $pin.licenses_asset; Sha = $pin.licenses_sha256 })) {
        $path = Join-Path $staging $asset.Name
        if ($AssetDirectory) { Copy-Item (Join-Path $AssetDirectory $asset.Name) $path }
        else {
            Invoke-WebRequest -Uri "https://github.com/$($pin.release_repository)/releases/download/$($pin.release_tag)/$($asset.Name)" -OutFile $path
        }
        $actual = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -cne $asset.Sha) { throw "rav1g $($asset.Name) SHA256 mismatch: expected $($asset.Sha), got $actual" }
        $files[$asset.Name] = $path
    }

    # The fork is written against one interface version (BaleRav1g.h static_assert); the release's header states its own.
    $header = Get-Content $files[$pin.header_asset] -Raw
    if ($header -notmatch '(?m)^#define RAV1G_ABI_VERSION (\d+)\s*$' -or [int]$Matches[1] -ne $pin.abi_version) {
        throw "rav1g.h does not declare RAV1G_ABI_VERSION $($pin.abi_version)"
    }

    $licenses = Join-Path $staging 'licenses'
    Expand-Rav1gLicenses $files[$pin.licenses_asset] $licenses

    New-Item -ItemType Directory -Force $OutDirectory | Out-Null
    Move-Item $files[$pin.dll_asset] (Join-Path $OutDirectory 'rav1g.dll')
    Move-Item $licenses (Join-Path $OutDirectory 'licenses')
    Write-Output "Verified rav1g $($pin.version) ($($pin.release_tag), interface $($pin.abi_version)): rav1g.dll SHA256 $($pin.dll_sha256)"
}
finally { Remove-Item $staging -Recurse -Force }
