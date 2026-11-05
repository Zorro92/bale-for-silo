param(
    [Parameter(Mandatory = $true)][byte[]]$Bytes,
    [string]$ExpectedVersion = '1.5.4'
)

$ErrorActionPreference = 'Stop'
# dav1d_version() embeds a NUL-delimited version, optionally with git-describe's suffix.
# Inspect the binary itself, not a NuGet version or a source/configuration claim.
$text = [System.Text.Encoding]::ASCII.GetString($Bytes)
$pattern = '(?:^|\x00)(?<version>' + [regex]::Escape($ExpectedVersion) + '(?:-\d+-g[0-9a-f]+)?)(?=\x00|$)'
$match = [regex]::Match($text, $pattern)
if (-not $match.Success) { throw "avcodec does not embed dav1d $ExpectedVersion" }
Write-Output "Embedded dav1d: $($match.Groups['version'].Value)"
