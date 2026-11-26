<#
Restores the pinned rav1g.dll (GPU AV1 decoder, Zorro92/rav1g releases) and its licence texts into .cache/rav1g
before the Windows build, verified by SHA256 (scripts/rav1g.json). Every build decodes AV1 with it through the
FFmpegInteropX fork, which loads rav1g.dll from the package root (LoadPackagedLibrary).

An empty pin ("release_tag": null) restores nothing: the build then packages no rav1g.dll, the library check reports
rav1g unavailable and AV1 stays on dav1d. Releases are immutable; never repin a tag to new hashes.

-TestTag with -TestDllSha256, -TestHeaderSha256 and -TestLicensesSha256 (CI's manual rav1g_test_* inputs) restores
that release of Zorro92/rav1g instead of the pin, verified the same way, for a debug-server package only
(-DebugServer true); scripts/rav1g.json stays unchanged. The log then says "TEST rav1g build from tag <tag>".

-AssetDirectory reads the release assets from a local folder instead of GitHub (tests).
#>
param(
    [string]$PinPath = (Join-Path $PSScriptRoot 'rav1g.json'),
    [string]$OutDirectory = (Join-Path $PSScriptRoot '../.cache/rav1g'),
    [string]$AssetDirectory,
    # The package this restore is for (CI's matrix.debug_server): a test build needs 'true'.
    [ValidateSet('true', 'false')][string]$DebugServer,
    [string]$TestTag,
    [string]$TestDllSha256,
    [string]$TestHeaderSha256,
    [string]$TestLicensesSha256
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/rav1g-common.ps1"
$pin = Get-Rav1gBuildPin -PinPath $PinPath -DebugServer $DebugServer -TestTag $TestTag -TestDllSha256 $TestDllSha256 `
    -TestHeaderSha256 $TestHeaderSha256 -TestLicensesSha256 $TestLicensesSha256
# Always start clean: a stale DLL from an earlier pin must never reach the package.
if (Test-Path $OutDirectory) { Remove-Item $OutDirectory -Recurse -Force }
if (-not $pin) {
    Write-Output 'rav1g: no release pinned (scripts/rav1g.json release_tag is null); packaging no rav1g.dll'
    return
}
if ($pin.test) {
    Write-Output "TEST rav1g build from tag $($pin.release_tag) ($($pin.release_repository)) instead of the scripts/rav1g.json pin: debug-server package only"
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
        else { Invoke-WebRequest -Uri (Get-Rav1gAssetUrl $pin $asset.Name) -OutFile $path }
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
    if ($pin.test) {
        Write-Output "TEST rav1g build from tag $($pin.release_tag) verified (interface $($pin.abi_version)): rav1g.dll SHA256 $($pin.dll_sha256)"
    }
    else {
        Write-Output "Verified rav1g $($pin.version) ($($pin.release_tag), interface $($pin.abi_version)): rav1g.dll SHA256 $($pin.dll_sha256)"
    }
}
finally { Remove-Item $staging -Recurse -Force }
