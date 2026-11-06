param([switch]$NativeOnly)

$ErrorActionPreference = 'Stop'
$nativePin = Get-Content (Join-Path $PSScriptRoot 'ffmpeg-native.json') -Raw | ConvertFrom-Json
if ($nativePin.sha256 -notmatch '^[0-9a-f]{64}$' -or $nativePin.dav1d_version -notmatch '^\d+\.\d+\.\d+$' -or
    $nativePin.version -ne "8.1.2-bale.dav1d.$($nativePin.dav1d_version)") {
    throw 'Invalid native FFmpeg package pin'
}
if ($nativePin.release_tag -ne "native-ffmpeg-8.1.2-dav1d-$($nativePin.dav1d_version)") { throw 'Inconsistent native release tag' }
$packages = @([pscustomobject]@{ Id = 'FFmpegInteropX.UWP.FFmpeg'; Pin = $nativePin })
if (-not $NativeOnly) {
    $wrapperPin = Get-Content (Join-Path $PSScriptRoot 'ffmpeginteropx-wrapper.json') -Raw | ConvertFrom-Json
    if ($wrapperPin.sha256 -notmatch '^[0-9a-f]{64}$' -or $wrapperPin.version -cnotmatch '^2\.1\.0-bale\.(0|[1-9]\d*)$') {
        throw 'Invalid FFmpegInteropX wrapper package pin'
    }
    if ($wrapperPin.release_tag -ne "wrapper-ffmpeginteropx-$($wrapperPin.version)") { throw 'Inconsistent wrapper release tag' }
    $packages += [pscustomobject]@{ Id = 'FFmpegInteropX.UWP.Lib'; Pin = $wrapperPin }
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
        Invoke-WebRequest -Uri "https://github.com/Zorro92/bale-for-silo/releases/download/$($pin.release_tag)/$asset" -OutFile $download
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
