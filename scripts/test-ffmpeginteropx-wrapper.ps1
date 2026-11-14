$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/ffmpeginteropx-wrapper-common.ps1"
$pin = Get-WrapperPin

function Assert-WrapperReject {
    param([scriptblock]$Action, [string]$Message)
    $rejected = $false
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notlike "$Message*") { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw "Expected wrapper rejection: $Message" }
}

$fixture = Join-Path ([System.IO.Path]::GetTempPath()) "bale-wrapper-check-$([guid]::NewGuid())"
New-Item -ItemType Directory $fixture | Out-Null
$previousCache = $env:NUGET_PACKAGES
try {
    $pinPath = Join-Path $fixture 'pin.json'
    $valid = $pin | ConvertTo-Json | ConvertFrom-Json -AsHashtable
    foreach ($field in $valid.Keys) {
        $invalid = @{} + $valid
        $invalid.Remove($field)
        $invalid | ConvertTo-Json | Set-Content $pinPath
        Assert-WrapperReject { Get-WrapperPin $pinPath } 'Invalid or inconsistent FFmpegInteropX wrapper package pin'
    }
    foreach ($case in @(
        @{ version = '2.1.0-bale.023' },
        @{ version = '2.1.0-bale.24' }, # Tag must agree too.
        @{ version = "2.1.0-bale.23`n" },
        @{ release_tag = 'wrapper-ffmpeginteropx-2.1.0-bale.0' },
        @{ source_commit = 'main' },
        @{ sha256 = 'not-a-sha256' },
        @{ nuget_content_hash = 'not-a-content-hash' },
        @{ nuget_content_hash = ('a' * 85) + '==' }
    )) {
        $invalid = @{} + $valid
        foreach ($field in $case.Keys) { $invalid[$field] = $case[$field] }
        $invalid | ConvertTo-Json | Set-Content $pinPath
        Assert-WrapperReject { Get-WrapperPin $pinPath } 'Invalid or inconsistent FFmpegInteropX wrapper package pin'
    }
    foreach ($repository in @('', 'owner', 'owner/name/extra', '../name', 'owner/../name',
        'https://github.com/owner/name', 'owner/name?query', 'owner/name#fragment', 'owner\name', "owner/name`n")) {
        $invalid = @{} + $valid
        $invalid.release_repository = $repository
        $invalid | ConvertTo-Json | Set-Content $pinPath
        Assert-WrapperReject { Get-WrapperPin $pinPath } 'Invalid or inconsistent FFmpegInteropX wrapper package pin'
    }

    # Exercise the real restore script with offline downloads and isolated feed/extraction caches.
    # Changing only the wrapper repository must leave native downloads in Bale's releases.
    $scripts = Join-Path $fixture 'scripts'
    New-Item -ItemType Directory $scripts | Out-Null
    foreach ($name in @('restore-ffmpeg-native.ps1', 'ffmpeginteropx-wrapper-common.ps1', 'ffmpeg-native.json')) {
        Copy-Item (Join-Path $PSScriptRoot $name) $scripts
    }
    $payload = Join-Path $fixture 'download.nupkg'
    [IO.File]::WriteAllText($payload, 'fixture package bytes')
    $hash = (Get-FileHash $payload -Algorithm SHA256).Hash.ToLowerInvariant()
    $nativePinPath = Join-Path $scripts 'ffmpeg-native.json'
    $nativePin = Get-Content $nativePinPath -Raw | ConvertFrom-Json
    $nativePin.sha256 = $hash
    $nativePin | ConvertTo-Json | Set-Content $nativePinPath
    $wrapperPinPath = Join-Path $scripts 'ffmpeginteropx-wrapper.json'
    $valid.sha256 = $hash
    $valid.release_repository = 'Fixture-Owner/wrapper.repo'
    $valid | ConvertTo-Json | Set-Content $wrapperPinPath
    $env:NUGET_PACKAGES = Join-Path $fixture 'nuget-cache'
    $cachedWrapper = Join-Path $env:NUGET_PACKAGES "ffmpeginteropx.uwp.lib/$($pin.version)"
    $cachedNative = Join-Path $env:NUGET_PACKAGES "ffmpeginteropx.uwp.ffmpeg/$($nativePin.version)"
    New-Item -ItemType Directory $cachedWrapper, $cachedNative | Out-Null
    $downloads = [System.Collections.Generic.List[string]]::new()
    function Invoke-WebRequest {
        param([string]$Uri, [string]$OutFile)
        $downloads.Add($Uri)
        Copy-Item $payload $OutFile
    }
    $restore = Join-Path $scripts 'restore-ffmpeg-native.ps1'
    & $restore | Out-Null
    $nativeUrl = "https://github.com/Zorro92/bale-for-silo/releases/download/$($nativePin.release_tag)/FFmpegInteropX.UWP.FFmpeg.$($nativePin.version).nupkg"
    $wrapperUrl = "https://github.com/$($valid.release_repository)/releases/download/$($valid.release_tag)/FFmpegInteropX.UWP.Lib.$($pin.version).nupkg"
    if ($downloads.Count -ne 2 -or $downloads[0] -cne $nativeUrl -or $downloads[1] -cne $wrapperUrl) {
        throw 'Restore did not select the native and wrapper release repositories independently'
    }
    if ((Test-Path $cachedWrapper) -or (Test-Path $cachedNative)) { throw 'Restore trusted an extracted package cache' }
    $feed = Join-Path $fixture '.cache/ffmpeg-native'
    foreach ($package in (Get-ChildItem $feed -Filter '*.nupkg')) {
        if ((Get-FileHash $package.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) {
            throw 'Verified feed differs from the downloaded package'
        }
    }
    if (@(Get-ChildItem $feed -Filter '*.nupkg').Count -ne 2) { throw 'Restore did not populate both feed packages' }

    $downloads.Clear()
    # An unreviewed wrapper must not overwrite the verified feed or clear its extracted cache.
    $valid.sha256 = '0' * 64
    $valid | ConvertTo-Json | Set-Content $wrapperPinPath
    New-Item -ItemType Directory $cachedWrapper | Out-Null
    Assert-WrapperReject { & $restore } 'FFmpegInteropX.UWP.Lib SHA256 mismatch'
    $restoredWrapper = Join-Path $feed "FFmpegInteropX.UWP.Lib.$($pin.version).nupkg"
    if ((Get-FileHash $restoredWrapper -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash -or
        -not (Test-Path $cachedWrapper)) { throw 'Failed wrapper verification mutated the feed or extracted cache' }

    $downloads.Clear()
    $valid.release_repository = 'owner/name/extra'
    $valid | ConvertTo-Json | Set-Content $wrapperPinPath
    Assert-WrapperReject { & $restore } 'Invalid or inconsistent FFmpegInteropX wrapper package pin'
    if ($downloads.Count) { throw 'Restore downloaded before validating the wrapper pin' }
    & $restore -NativeOnly | Out-Null
    if ($downloads.Count -ne 1 -or $downloads[0] -cne $nativeUrl) { throw 'NativeOnly restored a wrapper' }
}
finally {
    $env:NUGET_PACKAGES = $previousCache
    Remove-Item $fixture -Recurse -Force
}
Write-Output 'Wrapper release pins, repository selection, SHA256 rejection and verified-feed restore fixtures: PASS'
