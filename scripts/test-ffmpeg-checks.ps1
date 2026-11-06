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

# Exercise the real MSIX checker with tiny ZIP fixtures: package labels alone cannot prove
# that the updated avcodec, fork wrapper and corresponding provenance actually shipped.
Add-Type -AssemblyName System.IO.Compression.FileSystem
[xml]$template = Get-Content (Join-Path $PSScriptRoot '../src/Bale.Xbox/Build/FFmpegManifest.xslt') -Raw
$extensions = $template.SelectSingleNode('//*[local-name()="Extensions"]').OuterXml
$fixtureManifest = '<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">' + $extensions + '</Package>'
$fixture = Join-Path ([System.IO.Path]::GetTempPath()) "bale-msix-check-$([guid]::NewGuid())"
New-Item -ItemType Directory $fixture | Out-Null
$package = Join-Path $fixture 'fixture.msix'
$wrapper = Join-Path $fixture 'wrapper.nupkg'
function New-ArchiveFixture {
    param([string]$Path, [hashtable]$Files)
    $zip = [System.IO.Compression.ZipFile]::Open($Path, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $Files.Keys) {
            $stream = $zip.CreateEntry($name).Open()
            try { $stream.Write($Files[$name], 0, $Files[$name].Length) }
            finally { $stream.Dispose() }
        }
    }
    finally { $zip.Dispose() }
}
function New-PackageFixture {
    param([string]$Version, [string]$WrapperDll = 'fixture', [string]$WrapperSources = 'fixture', [switch]$OmitWrapperSources)
    $files = @{
        'AppxManifest.xml' = [System.Text.Encoding]::UTF8.GetBytes($fixtureManifest)
        'avcodec-62.dll' = [System.Text.Encoding]::ASCII.GetBytes("prefix`0$Version`0suffix")
        'FFmpegInteropX.dll' = [System.Text.Encoding]::ASCII.GetBytes($WrapperDll)
        'Licenses/FFmpegInteropX/SOURCES.txt' = [System.Text.Encoding]::ASCII.GetBytes($WrapperSources)
    }
    foreach ($name in @('avdevice-62.dll', 'avfilter-11.dll', 'avformat-62.dll',
        'avutil-60.dll', 'swresample-6.dll', 'swscale-9.dll', 'Licenses/FFmpegInteropX.txt',
        'Licenses/FFmpeg/ffmpeg.txt', 'Licenses/FFmpeg/dav1d.txt', 'Licenses/FFmpeg/SOURCES.txt')) {
        $files[$name] = [System.Text.Encoding]::ASCII.GetBytes('fixture')
    }
    if ($OmitWrapperSources) { $files.Remove('Licenses/FFmpegInteropX/SOURCES.txt') }
    New-ArchiveFixture $package $files
}
try {
    New-ArchiveFixture $wrapper @{
        'runtimes/win-x64/native/FFmpegInteropX.dll' = [System.Text.Encoding]::ASCII.GetBytes('fixture')
        'SOURCES.txt' = [System.Text.Encoding]::ASCII.GetBytes('fixture')
    }
    $check = @{ PackagePath = $package; WrapperPackagePath = $wrapper; ExpectedWrapperPackageSha256 = (Get-FileHash $wrapper -Algorithm SHA256).Hash }
    New-PackageFixture '1.5.4-0-g54706fc'
    $result = & "$PSScriptRoot/check-ffmpeg-package.ps1" @check
    if ($result -notcontains 'Embedded dav1d: 1.5.4-0-g54706fc') { throw 'MSIX checker did not inspect avcodec' }
    if (-not ($result -like 'Wrapper payload: FFmpegInteropX.dll SHA256 *')) { throw 'MSIX checker did not compare the wrapper DLL' }
    if (-not ($result -like 'Wrapper payload: Licenses/FFmpegInteropX/SOURCES.txt SHA256 *')) { throw 'MSIX checker did not compare wrapper provenance' }
    foreach ($case in @(
        @{ Version = '1.0.0-0-g99172b1'; Error = 'avcodec does not embed dav1d 1.5.4' },
        @{ WrapperDll = 'old wrapper'; Error = 'MSIX FFmpegInteropX.dll does not match the pinned wrapper package' },
        @{ WrapperSources = 'old provenance'; Error = 'MSIX Licenses/FFmpegInteropX/SOURCES.txt does not match the pinned wrapper package' },
        @{ OmitWrapperSources = $true; Error = 'Missing license/provenance Licenses/FFmpegInteropX/SOURCES.txt' }
    )) {
        Remove-Item $package
        $expectedError = $case.Error
        $case.Remove('Error')
        if (-not $case.ContainsKey('Version')) { $case.Version = '1.5.4' }
        New-PackageFixture @case
        $rejected = $false
        try { & "$PSScriptRoot/check-ffmpeg-package.ps1" @check | Out-Null }
        catch {
            if ($_.Exception.Message -ne $expectedError) { throw }
            $rejected = $true
        }
        if (-not $rejected) { throw "MSIX checker did not reject: $expectedError" }
    }
    Remove-Item $package
    New-PackageFixture '1.5.4'
    # A matching DLL from an unreviewed nupkg is not sufficient either.
    $check.ExpectedWrapperPackageSha256 = '0' * 64
    $rejected = $false
    try { & "$PSScriptRoot/check-ffmpeg-package.ps1" @check | Out-Null }
    catch {
        if ($_.Exception.Message -ne 'Wrapper package SHA256 mismatch') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'MSIX checker accepted an unreviewed wrapper package' }
}
finally { Remove-Item $fixture -Recurse -Force }
Write-Output 'PowerShell syntax, dav1d binary-version, wrapper hash and MSIX fixture checks: PASS'
