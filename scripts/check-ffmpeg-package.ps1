param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [string]$WrapperPackagePath,
    [string]$ExpectedWrapperPackageSha256
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path $PackagePath))
try {
    $entry = $archive.GetEntry('AppxManifest.xml')
    if (-not $entry) { throw 'Package is missing AppxManifest.xml' }
    $reader = [System.IO.StreamReader]::new($entry.Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $ns = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $ns.AddNamespace('a', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $servers = $manifest.SelectNodes('/a:Package/a:Extensions/a:Extension/a:InProcessServer[a:Path="FFmpegInteropX.dll"]', $ns)
    if ($servers.Count -ne 1) { throw 'Expected one FFmpegInteropX InProcessServer registration' }
    $classes = @($servers[0].SelectNodes('a:ActivatableClass', $ns) | ForEach-Object { $_.ActivatableClassId })
    [xml]$template = Get-Content (Join-Path $PSScriptRoot '../src/Bale.Xbox/Build/FFmpegManifest.xslt') -Raw
    $expected = @($template.SelectNodes('//*[local-name()="ActivatableClass"]') | ForEach-Object { $_.ActivatableClassId })
    if ($classes.Count -ne $expected.Count) { throw 'FFmpegInteropX activation class count does not match' }
    foreach ($class in $expected) {
        if ($classes -notcontains $class) { throw "Missing activation class $class" }
        Write-Output "Activation: $class"
    }
    foreach ($dll in @('FFmpegInteropX.dll', 'avcodec-62.dll', 'avdevice-62.dll', 'avfilter-11.dll',
        'avformat-62.dll', 'avutil-60.dll', 'swresample-6.dll', 'swscale-9.dll')) {
        $native = $archive.GetEntry($dll)
        if (-not $native -or $native.Length -eq 0) { throw "Missing native DLL $dll" }
        Write-Output "Native DLL: $dll ($($native.Length) bytes)"
    }
    $codec = $archive.GetEntry('avcodec-62.dll').Open()
    $bytes = [System.IO.MemoryStream]::new()
    try {
        $codec.CopyTo($bytes)
        & "$PSScriptRoot/check-dav1d-version.ps1" -Bytes $bytes.ToArray()
    }
    finally { $codec.Dispose(); $bytes.Dispose() }
    foreach ($license in @('Licenses/FFmpegInteropX.txt', 'Licenses/FFmpeg/ffmpeg.txt', 'Licenses/FFmpeg/dav1d.txt',
        'Licenses/FFmpeg/SOURCES.txt', 'Licenses/FFmpegInteropX/SOURCES.txt')) {
        if (-not $archive.GetEntry($license)) { throw "Missing license/provenance $license" }
    }
    # Compare the shipped wrapper and provenance with the independently hash-pinned nupkg,
    # not the extracted NuGet cache that packaging could accidentally have reused.
    $pin = Get-Content (Join-Path $PSScriptRoot 'ffmpeginteropx-wrapper.json') -Raw | ConvertFrom-Json
    if (-not $WrapperPackagePath) {
        $WrapperPackagePath = Join-Path $PSScriptRoot "../.cache/ffmpeg-native/FFmpegInteropX.UWP.Lib.$($pin.version).nupkg"
    }
    if (-not $ExpectedWrapperPackageSha256) { $ExpectedWrapperPackageSha256 = $pin.sha256 }
    $actual = (Get-FileHash $WrapperPackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $ExpectedWrapperPackageSha256) { throw 'Wrapper package SHA256 mismatch' }
    $wrapper = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path $WrapperPackagePath))
    try {
        $payload = @{
            'FFmpegInteropX.dll' = 'runtimes/win-x64/native/FFmpegInteropX.dll'
            'Licenses/FFmpegInteropX/SOURCES.txt' = 'SOURCES.txt'
        }
        foreach ($name in $payload.Keys) {
            $entry = $wrapper.GetEntry($payload[$name])
            if (-not $entry -or $entry.Length -eq 0) { throw "Wrapper package is missing $($payload[$name])" }
            $expectedStream = $entry.Open()
            $shippedStream = $archive.GetEntry($name).Open()
            try {
                $expectedHash = (Get-FileHash -InputStream $expectedStream -Algorithm SHA256).Hash
                $shippedHash = (Get-FileHash -InputStream $shippedStream -Algorithm SHA256).Hash
                if ($shippedHash -ne $expectedHash) { throw "MSIX $name does not match the pinned wrapper package" }
                Write-Output "Wrapper payload: $name SHA256 $($shippedHash.ToLowerInvariant())"
            }
            finally { $expectedStream.Dispose(); $shippedStream.Dispose() }
        }
    }
    finally { $wrapper.Dispose() }
    Write-Output 'FFmpegInteropX activation, pinned wrapper payload, native packaging and dav1d 1.5.4: PASS'
}
finally { $archive.Dispose() }
