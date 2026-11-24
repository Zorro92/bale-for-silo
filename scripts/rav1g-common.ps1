$ErrorActionPreference = 'Stop'

# The exports the FFmpegInteropX fork resolves from rav1g.dll (Source/BaleRav1g.h, BaleRav1gExports). A library
# without every one of them makes the fork fall back to dav1d, so CI refuses to package it.
$Rav1gExports = @('rav1g_abi_version', 'rav1g_version', 'rav1g_peek_stream_info', 'rav1g_device_create',
    'rav1g_device_d3d12_device', 'rav1g_settings_default', 'rav1g_decoder_create', 'rav1g_decoder_decode',
    'rav1g_decoder_drain', 'rav1g_decoder_configuration', 'rav1g_decoder_output_fence', 'rav1g_decoder_destroy')

# The interface version the pinned wrapper is built against: static_assert in BaleRav1g.h (bale.78 onward: 3,
# rav1g 0.8's display tone-map parity; bale.74 to bale.77: 2; bale.44 to bale.73: 1).
$Rav1gWrapperAbiVersion = 3

# The licence texts the release's rav1g-licenses.zip carries (rav1g's build.yml release job). They ship in the
# package as Licenses/rav1g/<path>.
$Rav1gLicenseFiles = @('LICENSE.txt', 'libav1/LICENSE', 'libav1/PATENTS', 'third_party/rav1d/COPYING',
    'third_party/rav1d/doc/PATENTS', 'third_party/rust-crates/NOTICES.txt')

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
