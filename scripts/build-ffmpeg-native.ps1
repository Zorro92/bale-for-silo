param(
    [Parameter(Mandatory = $true)][string]$SourcePath,
    [Parameter(Mandatory = $true)][string]$UpstreamRepository,
    [Parameter(Mandatory = $true)][string]$UpstreamRef,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Dav1dTag = '1.5.4'
)

$ErrorActionPreference = 'Stop'
$SourcePath = (Resolve-Path $SourcePath).Path
$version = "8.1.2-bale.dav1d.$Dav1dTag"
$tag = "native-ffmpeg-8.1.2-dav1d-$Dav1dTag"
$output = Join-Path $SourcePath 'Output/NuGet'
New-Item -ItemType Directory -Force $output | Out-Null

function Invoke-Git {
    param([string]$Directory, [string[]]$GitArguments)
    $result = & git -C $Directory @GitArguments
    if ($LASTEXITCODE -ne 0) { throw "git failed: $($GitArguments[0])" }
    return $result
}

function Get-SubmodulePins {
    $pins = @{}
    foreach ($line in (Invoke-Git $SourcePath @('submodule', 'status', '--recursive'))) {
        if ($line -notmatch '^[ +]([0-9a-f]{40}) ([^ ]+)') { throw "Uninitialized or conflicted submodule: $line" }
        $pins[$Matches[2]] = $Matches[1]
    }
    return $pins
}

$upstreamCommit = Invoke-Git $SourcePath @('rev-parse', 'HEAD')
$before = Get-SubmodulePins
# 8.1.2 identifies the native FFmpeg ABI/source, independently of the interop wrapper.
if ($before['Libs/ffmpeg'] -ne '38b88335f99e76ed89ff3c93f877fdefce736c13') {
    throw 'This package version requires the upstream 8.1.2 FFmpeg revision'
}
$originalDav1d = $before['Libs/dav1d']
$dav1d = Join-Path $SourcePath 'Libs/dav1d'
Invoke-Git $dav1d @('fetch', 'origin', "refs/tags/${Dav1dTag}:refs/tags/${Dav1dTag}")
$dav1dCommit = Invoke-Git $dav1d @('rev-parse', "refs/tags/$Dav1dTag`^{commit}")
if ($Dav1dTag -eq '1.5.4' -and $dav1dCommit -ne '54706fc6bc0cdecab7e9593974a4039cc038fca7') {
    throw 'The dav1d 1.5.4 tag no longer resolves to the reviewed commit'
}
Invoke-Git $dav1d @('checkout', '--detach', $dav1dCommit)
$after = Get-SubmodulePins
if ($before.Count -ne $after.Count) { throw 'The submodule set changed' }
foreach ($path in $before.Keys) {
    if ($path -ne 'Libs/dav1d' -and $before[$path] -ne $after[$path]) {
        throw "Unexpected submodule change: $path"
    }
}
# No configure/build-script patches: only the dav1d gitlink differs from the selected ref.
Invoke-Git $SourcePath @('diff', '--exit-code', 'HEAD', '--', 'Build', 'Libs/build-scripts', 'Build-FFmpeg.ps1')

# Upstream runs relative tool installers and calls exit on failure; isolate its process.
Push-Location $SourcePath
try {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./Build-FFmpeg.ps1 `
        -WindowsTargets UWP -Platforms x64 -Configuration Release -SharedOrStatic shared `
        -Gpl disable -Encoders disable -Devices disable -Programs disable -DisableParallelBuilds
    if ($LASTEXITCODE -ne 0) { throw 'Upstream UWP x64 FFmpeg build failed' }
}
finally { Pop-Location }

$native = Join-Path $SourcePath 'Output/FFmpegUWP/x64'
& "$PSScriptRoot/check-dav1d-version.ps1" `
    -Bytes ([System.IO.File]::ReadAllBytes((Join-Path $native 'bin/avcodec-62.dll'))) -ExpectedVersion $Dav1dTag
$config = Get-Content (Join-Path $SourcePath 'Intermediate/FFmpegUWP/x64/int/ffmpeg/config.h') -Raw
foreach ($disabled in @('GPL', 'VERSION3', 'NONFREE', 'LIBX264', 'LIBX265')) {
    if ($config -notmatch "(?m)^#define CONFIG_$disabled 0\s*$") { throw "CONFIG_$disabled is not disabled" }
}
$avutil = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes((Join-Path $native 'bin/avutil-60.dll')))
foreach ($flag in @('--disable-gpl', '--disable-version3', '--disable-encoders', '--enable-shared', '--enable-libdav1d', 'LGPL version 2.1 or later')) {
    if (-not $avutil.Contains($flag)) { throw "Missing native configuration evidence: $flag" }
}
foreach ($flag in @('--enable-gpl', '--enable-version3', '--enable-nonfree', '--enable-libx264', '--enable-libx265')) {
    if ($avutil.Contains($flag)) { throw "Forbidden native configuration: $flag" }
}
Invoke-Git $SourcePath @('diff', '--exit-code', 'HEAD', '--', 'Build', 'Libs/build-scripts', 'Build-FFmpeg.ps1')

$sources = @(
    "Package: FFmpegInteropX.UWP.FFmpeg $version",
    "Release: $tag",
    "FFmpegInteropX repository: https://github.com/$UpstreamRepository",
    "Requested ref: $UpstreamRef",
    "FFmpegInteropX commit: $upstreamCommit",
    "Bale build scripts commit: $env:GITHUB_SHA",
    "dav1d tag: $Dav1dTag",
    "dav1d commit: $dav1dCommit",
    "Original dav1d commit: $originalDav1d",
    '',
    'All submodule revisions (only dav1d changed from the selected FFmpegInteropX ref):'
)
foreach ($path in ($after.Keys | Sort-Object)) { $sources += "$path $($after[$path])" }
$sources += @('', 'Upstream submodule URLs:', (Get-Content (Join-Path $SourcePath '.gitmodules') -Raw),
    'Build: Build-FFmpeg.ps1 -WindowsTargets UWP -Platforms x64 -Configuration Release -SharedOrStatic shared -Gpl disable -Encoders disable -Devices disable -Programs disable -DisableParallelBuilds',
    'Compiler/SDK/tool defaults and FFmpegConfig.sh are unchanged from the selected upstream ref.',
    'x264, x265 and libvpx are checked out at their original pins but NOT built, linked or distributed.',
    'Packaging only: omit x86/ARM64 file globs from a copy of the upstream nuspec; add SOURCES.txt.',
    'Transport only: official GitHub mirrors replace FFmpeg/VideoLAN clone URLs; revisions are unchanged.',
    'dav1d is statically linked into avcodec; FFmpeg DLLs remain shared. Wrapper 2.1.0 is not rebuilt.')
$sources | Set-Content (Join-Path $output 'SOURCES.txt') -Encoding utf8
Copy-Item (Join-Path $SourcePath 'Intermediate/FFmpegUWP/x64/int/ffmpeg/ffbuild/config.log') (Join-Path $output 'ffmpeg-config.log')

# Keep upstream metadata, license/header/native assets and MSBuild targets. Its other-architecture
# file globs cannot match an intentionally x64-only build; this is not a decoder/config change.
[xml]$spec = Get-Content (Join-Path $SourcePath 'Build/FFmpegInteropX.UWP.FFmpeg.nuspec') -Raw
foreach ($file in @($spec.package.files.file)) {
    if ($file.src -match '\\(x86|ARM64)\\') { [void]$file.ParentNode.RemoveChild($file) }
}
$provenance = $spec.CreateElement('file', $spec.DocumentElement.NamespaceURI)
$provenance.SetAttribute('src', '..\Output\NuGet\SOURCES.txt')
$provenance.SetAttribute('target', '')
[void]$spec.package.files.AppendChild($provenance)
$nuspec = Join-Path $SourcePath 'Build/Bale.UWP.FFmpeg.nuspec'
$spec.Save($nuspec)
& nuget.exe pack $nuspec -Version $version `
    -Properties "id=FFmpegInteropX.UWP.FFmpeg;repositoryUrl=https://git.ffmpeg.org/ffmpeg.git;repositoryCommit=$($after['Libs/ffmpeg']);winsdk=10.0.17763.0;NoWarn=NU5128" `
    -OutputDirectory $output
if ($LASTEXITCODE -ne 0) { throw 'Native NuGet packaging failed' }
$package = Join-Path $output "FFmpegInteropX.UWP.FFmpeg.$version.nupkg"
$hash = (Get-FileHash $package -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([System.IO.Path]::GetFileName($package))" | Set-Content "$package.sha256" -Encoding ascii

# Distribute corresponding source alongside the DLL package, without build outputs, downloaded
# tools, .git credentials or unused GPL encoder sources. All built dependency sources are retained.
$sourceArchive = Join-Path $output 'ffmpeg-native-sources.tar.gz'
& tar.exe -czf $sourceArchive --exclude=.git --exclude=./Output --exclude=./Intermediate --exclude=./Tools `
    --exclude=./Libs/x264 --exclude=./Libs/x265 --exclude=./Libs/libvpx -C $SourcePath .
if ($LASTEXITCODE -ne 0) { throw 'Corresponding source archive failed' }
Write-Output "Package SHA256: $hash"
if ($env:GITHUB_OUTPUT) {
    "release_tag=$tag" >> $env:GITHUB_OUTPUT
    "package_sha256=$hash" >> $env:GITHUB_OUTPUT
}
