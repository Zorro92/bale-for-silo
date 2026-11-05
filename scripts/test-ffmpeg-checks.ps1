$ErrorActionPreference = 'Stop'
foreach ($file in (Get-ChildItem $PSScriptRoot -Filter '*.ps1')) {
    $tokens = $null
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors) { throw "PowerShell syntax error in $($file.Name): $parseErrors" }
}

foreach ($version in @('1.5.4', '1.5.4-0-g54706fc')) {
    $bytes = [System.Text.Encoding]::ASCII.GetBytes("prefix`0$version`0suffix")
    $result = & "$PSScriptRoot/check-dav1d-version.ps1" -Bytes $bytes
    if ($result -ne "Embedded dav1d: $version") { throw 'Incorrect dav1d binary-version diagnostic' }
}
foreach ($version in @('1.0.0-0-g99172b1', '11.5.4', '1.5.40', '1.5.4-dev', 'prefix1.5.4', 'no version')) {
    $bytes = [System.Text.Encoding]::ASCII.GetBytes("prefix`0$version`0suffix")
    $rejected = $false
    try { & "$PSScriptRoot/check-dav1d-version.ps1" -Bytes $bytes | Out-Null }
    catch {
        if ($_.Exception.Message -ne 'avcodec does not embed dav1d 1.5.4') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw "Accepted an incorrect dav1d version: $version" }
}
Write-Output 'PowerShell syntax and dav1d binary-version checks: PASS'
