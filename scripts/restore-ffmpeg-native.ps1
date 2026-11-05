$ErrorActionPreference = 'Stop'
$pin = Get-Content (Join-Path $PSScriptRoot 'ffmpeg-native.json') -Raw | ConvertFrom-Json
if ($pin.sha256 -notmatch '^[0-9a-f]{64}$' -or $pin.dav1d_version -notmatch '^\d+\.\d+\.\d+$' -or
    $pin.version -ne "8.1.2-bale.dav1d.$($pin.dav1d_version)") {
    throw 'Invalid native FFmpeg package pin'
}
if ($pin.release_tag -ne "native-ffmpeg-8.1.2-dav1d-$($pin.dav1d_version)") { throw 'Inconsistent native release tag' }
$asset = "FFmpegInteropX.UWP.FFmpeg.$($pin.version).nupkg"
$feed = Join-Path $PSScriptRoot '../.cache/ffmpeg-native'
$staging = Join-Path ([System.IO.Path]::GetTempPath()) "bale-native-$([guid]::NewGuid())"
New-Item -ItemType Directory $staging | Out-Null
try {
    $download = Join-Path $staging $asset
    Invoke-WebRequest -Uri "https://github.com/Zorro92/bale-for-silo/releases/download/$($pin.release_tag)/$asset" -OutFile $download
    $actual = (Get-FileHash $download -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $pin.sha256) { throw "Native FFmpeg SHA256 mismatch: expected $($pin.sha256), got $actual" }
    New-Item -ItemType Directory -Force $feed | Out-Null
    Move-Item $download (Join-Path $feed $asset) -Force
    # CI restores a broad NuGet cache first. Force this one package to come from the verified feed,
    # rather than letting NuGet reuse a potentially stale/tampered extracted native package.
    if ($env:NUGET_PACKAGES) {
        $cached = Join-Path $env:NUGET_PACKAGES "ffmpeginteropx.uwp.ffmpeg/$($pin.version)"
        if (Test-Path $cached) { Remove-Item $cached -Recurse -Force }
    }
    Write-Output "Verified native FFmpeg $($pin.version): SHA256 $actual"
}
finally { Remove-Item $staging -Recurse -Force }
