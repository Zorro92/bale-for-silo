param([Parameter(Mandatory = $true)][string]$PackagePath)

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
        'Licenses/FFmpeg/SOURCES.txt')) {
        if (-not $archive.GetEntry($license)) { throw "Missing license/provenance $license" }
    }
    Write-Output 'FFmpegInteropX activation, native packaging and dav1d 1.5.4: PASS'
}
finally { $archive.Dispose() }
