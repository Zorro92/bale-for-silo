$ErrorActionPreference = 'Stop'

function Get-WrapperPin {
    param([string]$Path = (Join-Path $PSScriptRoot 'ffmpeginteropx-wrapper.json'))
    $pin = Get-Content $Path -Raw | ConvertFrom-Json
    # Repository, tag and version enter the release URL; accept only owner/name and our version scheme.
    # Build-tool and upstream-base pins belong to the fork; Bale pins the released package and source.
    if ($pin.release_repository -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9][A-Za-z0-9._-]*\z' -or
        $pin.version -cnotmatch '\A2\.1\.0-bale\.(0|[1-9]\d*)\z' -or
        $pin.release_tag -cne "wrapper-ffmpeginteropx-$($pin.version)" -or
        $pin.source_commit -cnotmatch '\A[0-9a-f]{40}\z' -or
        $pin.sha256 -cnotmatch '\A[0-9a-f]{64}\z' -or
        $pin.nuget_content_hash -cnotmatch '\A[A-Za-z0-9+/]{86}==\z') {
        throw 'Invalid or inconsistent FFmpegInteropX wrapper package pin'
    }
    return $pin
}
