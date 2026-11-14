param([switch]$NativeOnly)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/ffmpeginteropx-wrapper-common.ps1"
$nativePin = Get-Content (Join-Path $PSScriptRoot 'ffmpeg-native.json') -Raw | ConvertFrom-Json
# patch_revision names the reviewed FFmpeg source patch set (scripts/ffmpeg-native-patches); optional.
$patchRevision = "$($nativePin.patch_revision)"
if ($nativePin.sha256 -notmatch '^[0-9a-f]{64}$' -or $nativePin.dav1d_version -notmatch '^\d+\.\d+\.\d+$' -or
    $patchRevision -cnotmatch '^(p[1-9][0-9]*)?$' -or
    $nativePin.version -ne "8.1.2-bale.dav1d.$($nativePin.dav1d_version)$(if ($patchRevision) { ".$patchRevision" })") {
    throw 'Invalid native FFmpeg package pin'
}
if ($nativePin.release_tag -ne "native-ffmpeg-8.1.2-dav1d-$($nativePin.dav1d_version)$(if ($patchRevision) { "-$patchRevision" })") {
    throw 'Inconsistent native release tag'
}
$packages = @([pscustomobject]@{ Id = 'FFmpegInteropX.UWP.FFmpeg'; Pin = $nativePin; Repository = 'Zorro92/bale-for-silo' })
if (-not $NativeOnly) {
    $wrapperPin = Get-WrapperPin
    $packages += [pscustomobject]@{ Id = 'FFmpegInteropX.UWP.Lib'; Pin = $wrapperPin; Repository = $wrapperPin.release_repository }
}
$feed = Join-Path $PSScriptRoot '../.cache/ffmpeg-native'
$cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else {
    Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'
}
foreach ($package in $packages) {
    $pin = $package.Pin
    $asset = "$($package.Id).$($pin.version).nupkg"
    $staging = Join-Path ([System.IO.Path]::GetTempPath()) "bale-native-$([guid]::NewGuid())"
    New-Item -ItemType Directory $staging | Out-Null
    try {
        $download = Join-Path $staging $asset
        Invoke-WebRequest -Uri "https://github.com/$($package.Repository)/releases/download/$($pin.release_tag)/$asset" -OutFile $download
        $actual = (Get-FileHash $download -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $pin.sha256) { throw "$($package.Id) SHA256 mismatch: expected $($pin.sha256), got $actual" }
        New-Item -ItemType Directory -Force $feed | Out-Null
        Move-Item $download (Join-Path $feed $asset) -Force
        # Force both forks to restore from the verified feed, not a stale/tampered extracted cache.
        $cached = Join-Path $cache "$($package.Id.ToLowerInvariant())/$($pin.version)"
        if (Test-Path $cached) { Remove-Item $cached -Recurse -Force }
        Write-Output "Verified $($package.Id) $($pin.version): SHA256 $actual"
    }
    finally { Remove-Item $staging -Recurse -Force }
}
