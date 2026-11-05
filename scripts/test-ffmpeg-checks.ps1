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

# Exercise the real MSIX checker with tiny ZIP fixtures: a new NuGet/package label is not enough
# if an old avcodec DLL accidentally survives restore or packaging.
Add-Type -AssemblyName System.IO.Compression.FileSystem
[xml]$template = Get-Content (Join-Path $PSScriptRoot '../src/Bale.Xbox/Build/FFmpegManifest.xslt') -Raw
$extensions = $template.SelectSingleNode('//*[local-name()="Extensions"]').OuterXml
$fixtureManifest = '<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">' + $extensions + '</Package>'
$fixture = Join-Path ([System.IO.Path]::GetTempPath()) "bale-msix-check-$([guid]::NewGuid())"
New-Item -ItemType Directory $fixture | Out-Null
$package = Join-Path $fixture 'fixture.msix'
function New-PackageFixture {
    param([string]$Version)
    $files = @{
        'AppxManifest.xml' = [System.Text.Encoding]::UTF8.GetBytes($fixtureManifest)
        'avcodec-62.dll' = [System.Text.Encoding]::ASCII.GetBytes("prefix`0$Version`0suffix")
    }
    foreach ($name in @('FFmpegInteropX.dll', 'avdevice-62.dll', 'avfilter-11.dll', 'avformat-62.dll',
        'avutil-60.dll', 'swresample-6.dll', 'swscale-9.dll', 'Licenses/FFmpegInteropX.txt',
        'Licenses/FFmpeg/ffmpeg.txt', 'Licenses/FFmpeg/dav1d.txt', 'Licenses/FFmpeg/SOURCES.txt')) {
        $files[$name] = [System.Text.Encoding]::ASCII.GetBytes('fixture')
    }
    $zip = [System.IO.Compression.ZipFile]::Open($package, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $files.Keys) {
            $stream = $zip.CreateEntry($name).Open()
            try { $stream.Write($files[$name], 0, $files[$name].Length) }
            finally { $stream.Dispose() }
        }
    }
    finally { $zip.Dispose() }
}
try {
    New-PackageFixture '1.5.4-0-g54706fc'
    $result = & "$PSScriptRoot/check-ffmpeg-package.ps1" -PackagePath $package
    if ($result -notcontains 'Embedded dav1d: 1.5.4-0-g54706fc') { throw 'MSIX checker did not inspect avcodec' }
    Remove-Item $package
    New-PackageFixture '1.0.0-0-g99172b1'
    $rejected = $false
    try { & "$PSScriptRoot/check-ffmpeg-package.ps1" -PackagePath $package | Out-Null }
    catch {
        if ($_.Exception.Message -ne 'avcodec does not embed dav1d 1.5.4') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'MSIX checker accepted the old avcodec DLL' }
}
finally { Remove-Item $fixture -Recurse -Force }
Write-Output 'PowerShell syntax, dav1d binary-version and MSIX fixture checks: PASS'
