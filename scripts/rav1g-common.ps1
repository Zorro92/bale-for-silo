$ErrorActionPreference = 'Stop'

# The exports the FFmpegInteropX fork resolves from rav1g.dll (Source/BaleRav1g.h, BaleRav1gExports). A library
# without every one of them makes the fork fall back to dav1d, so CI refuses to package it.
$Rav1gExports = @('rav1g_abi_version', 'rav1g_version', 'rav1g_peek_stream_info', 'rav1g_device_create',
    'rav1g_device_create_with_settings', 'rav1g_device_d3d12_device', 'rav1g_settings_default', 'rav1g_decoder_create',
    'rav1g_decoder_decode', 'rav1g_decoder_drain', 'rav1g_decoder_flush', 'rav1g_decoder_configuration',
    'rav1g_decoder_output_fence', 'rav1g_decoder_destroy')

# The interface version the pinned wrapper is built against: static_assert in BaleRav1g.h (bale.86 onward: 4,
# rav1g 0.9's decoder flush and prepare bits; bale.78 to bale.85: 3, rav1g 0.8's display tone-map parity;
# bale.74 to bale.77: 2; bale.44 to bale.73: 1).
$Rav1gWrapperAbiVersion = 4

# The licence texts the release's rav1g-licenses.zip carries (rav1g's build.yml release job). They ship in the
# package as Licenses/rav1g/<path>.
$Rav1gLicenseFiles = @('LICENSE.txt', 'libav1/LICENSE', 'libav1/PATENTS', 'third_party/rav1d/COPYING',
    'third_party/rav1d/doc/PATENTS', 'third_party/rust-crates/NOTICES.txt')

# Where a test build comes from (CI's manual rav1g_test_* inputs): the releases the pin names.
$Rav1gTestRepository = 'Zorro92/rav1g'

<#
The pin, or $null for an empty pin (release_tag null: no rav1g.dll is packaged). A non-empty pin must be complete
and consistent; its repository, tag and asset names enter a download URL.
#>
function Get-Rav1gPin {
    param([string]$Path = (Join-Path $PSScriptRoot 'rav1g.json'))
    $pin = Get-Content $Path -Raw | ConvertFrom-Json
    if ($null -eq $pin.release_tag) { return $null }
    if ($pin.release_repository -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9][A-Za-z0-9._-]*\z' -or
        $pin.version -cnotmatch '\A\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?\z' -or
        $pin.release_tag -cne "v$($pin.version)" -or
        $pin.source_commit -cnotmatch '\A[0-9a-f]{40}\z' -or
        $pin.abi_version -isnot [long] -or $pin.abi_version -ne $Rav1gWrapperAbiVersion -or
        $pin.dll_asset -cne 'rav1g.dll' -or $pin.header_asset -cne 'rav1g.h' -or
        $pin.licenses_asset -cne 'rav1g-licenses.zip' -or
        $pin.dll_sha256 -cnotmatch '\A[0-9a-f]{64}\z' -or
        $pin.header_sha256 -cnotmatch '\A[0-9a-f]{64}\z' -or
        $pin.licenses_sha256 -cnotmatch '\A[0-9a-f]{64}\z') {
        throw 'Invalid or inconsistent rav1g pin (scripts/rav1g.json)'
    }
    return $pin
}

<#
A test build of rav1g in place of the pin, for a debug-server package only (CI's manual rav1g_test_* inputs;
scripts/rav1g.json is never edited for one): release $Tag of $Rav1gTestRepository, verified by its three SHA256s and the
wrapper's interface version exactly as a pinned release is. The tag enters a download URL, so it must be a plain tag
name; the hashes may be in either case. A production build (-DebugServer other than 'true') refuses it.
#>
function Get-Rav1gTestPin {
    param([string]$Tag, [string]$DebugServer, [string]$DllSha256, [string]$HeaderSha256, [string]$LicensesSha256)
    $Tag = "$Tag".Trim()
    if ($Tag -cnotmatch '\A[0-9A-Za-z][0-9A-Za-z._+-]{0,99}\z') {
        throw 'The rav1g test tag must be a release tag name (letters, digits, ".", "_", "+" or "-"; at most 100)'
    }
    if ($DebugServer -cne 'true') {
        throw "TEST rav1g build from tag $Tag refused: test builds are for debug-server packages only; production packages always take scripts/rav1g.json"
    }
    $hashes = [ordered]@{ dll = $DllSha256; header = $HeaderSha256; licenses = $LicensesSha256 }
    foreach ($name in @($hashes.Keys)) {
        $hashes[$name] = "$($hashes[$name])".Trim().ToLowerInvariant()
        if ($hashes[$name] -cnotmatch '\A[0-9a-f]{64}\z') {
            throw "TEST rav1g build from tag ${Tag}: rav1g_test_$($name)_sha256 must be that asset's SHA256 (64 hex digits)"
        }
    }
    return [pscustomobject]@{
        version = $Tag; release_repository = $Rav1gTestRepository; release_tag = $Tag; abi_version = $Rav1gWrapperAbiVersion
        dll_asset = 'rav1g.dll'; dll_sha256 = $hashes.dll
        header_asset = 'rav1g.h'; header_sha256 = $hashes.header
        licenses_asset = 'rav1g-licenses.zip'; licenses_sha256 = $hashes.licenses
        test = $true
    }
}

<#
What a build packages: the pin (Get-Rav1gPin; $null for an empty one), or with -TestTag that test build
(Get-Rav1gTestPin). Test hashes without a tag are refused, so a half-filled dispatch never builds the pin unnoticed.
#>
function Get-Rav1gBuildPin {
    param([string]$PinPath = (Join-Path $PSScriptRoot 'rav1g.json'), [string]$DebugServer, [string]$TestTag,
        [string]$TestDllSha256, [string]$TestHeaderSha256, [string]$TestLicensesSha256)
    if ("$TestTag".Trim()) {
        return Get-Rav1gTestPin -Tag $TestTag -DebugServer $DebugServer -DllSha256 $TestDllSha256 `
            -HeaderSha256 $TestHeaderSha256 -LicensesSha256 $TestLicensesSha256
    }
    if ("$TestDllSha256$TestHeaderSha256$TestLicensesSha256".Trim()) {
        throw 'rav1g test SHA256 values were given without a test tag (rav1g_test_tag)'
    }
    return Get-Rav1gPin $PinPath
}

<# The download URL of one release asset of a pin or a test build. #>
function Get-Rav1gAssetUrl {
    param($Pin, [string]$Asset)
    return "https://github.com/$($Pin.release_repository)/releases/download/$($Pin.release_tag)/$Asset"
}

<# Extracts exactly the expected licence files; anything else in the archive (or a path outside it) is refused. #>
function Expand-Rav1gLicenses {
    param([string]$Archive, [string]$Destination)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $files = @($zip.Entries | Where-Object { -not $_.FullName.EndsWith('/') })
        $names = @($files | ForEach-Object { $_.FullName } | Sort-Object)
        $expected = @($Rav1gLicenseFiles | Sort-Object)
        if (Compare-Object $names $expected -CaseSensitive) {
            throw "rav1g-licenses.zip must hold exactly: $($Rav1gLicenseFiles -join ', ')"
        }
        foreach ($entry in $files) {
            $target = Join-Path $Destination $entry.FullName
            New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target)
        }
    }
    finally { $zip.Dispose() }
}
