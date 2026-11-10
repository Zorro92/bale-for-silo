#Requires -Version 7
<#
.SYNOPSIS
Builds libass with FreeType, FriBidi and HarfBuzz as static x64 libraries for native/bale-gpu.

.DESCRIPTION
Every source and tool is pinned with its SHA256 in scripts/libass-native.json. The C/C++ code is
compiled for the UWP app partition (WINAPI_FAMILY_APP), so libass uses its WinRT DirectWrite font
provider, against the static CRT (/MT) that bale_gpu.dll links. Output:
  <OutDir>\lib\{ass,freetype,fribidi,harfbuzz}.lib
  <OutDir>\licenses\{libass,freetype,fribidi,harfbuzz}.txt
  <OutDir>\SOURCES.txt
Set BALE_LIBASS_DIR=<OutDir> for cargo. Needs Windows, Visual Studio with the C++ x64 tools, Python 3.
#>
param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\.libass')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$pinPath = Join-Path $PSScriptRoot 'libass-native.json'
$pin = Get-Content $pinPath -Raw | ConvertFrom-Json
$OutDir = [IO.Path]::GetFullPath($OutDir)
$work = Join-Path ([IO.Path]::GetTempPath()) "bale-libass-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory $work | Out-Null
$downloads = New-Item -ItemType Directory (Join-Path $work 'downloads')

function Get-Verified([string]$Url, [string]$Sha256) {
    $file = Join-Path $downloads ([IO.Path]::GetFileName(([uri]$Url).AbsolutePath))
    Invoke-WebRequest -Uri $Url -OutFile $file -MaximumRetryCount 3 -RetryIntervalSec 5
    $actual = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256) { throw "SHA256 mismatch for $([IO.Path]::GetFileName($file)): got $actual" }
    $file
}

function Invoke-Checked([string]$What, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit $LASTEXITCODE)" }
}

# MSVC x64 environment (cl, link, lib, Windows SDK paths) for meson, imported from vcvars64.bat.
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio with the C++ x64 tools is required' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
cmd /c "`"$vcvars`" >nul && set" | ForEach-Object {
    if ($_ -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2]) }
}
if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) { throw 'vcvars64.bat did not put cl.exe on PATH' }

# Pinned build tools: nasm (libass's SIMD rasterizer and blur), meson and ninja in a private venv.
$nasmZip = Get-Verified $pin.tools.nasm.url $pin.tools.nasm.sha256
Expand-Archive $nasmZip -DestinationPath (Join-Path $work 'nasm')
$nasmDir = (Get-ChildItem (Join-Path $work 'nasm') -Recurse -Filter nasm.exe | Select-Object -First 1).DirectoryName
$venv = Join-Path $work 'venv'
Invoke-Checked 'python -m venv' { python -m venv $venv }
$requirements = Join-Path $work 'requirements.txt'
@(
    "meson==$($pin.tools.meson.version) --hash=sha256:$($pin.tools.meson.wheel_sha256)"
    "ninja==$($pin.tools.ninja.version) --hash=sha256:$($pin.tools.ninja.wheel_sha256)"
) | Set-Content $requirements
Invoke-Checked 'pip install meson ninja' {
    & "$venv\Scripts\python.exe" -m pip install --disable-pip-version-check --no-deps --only-binary :all: --require-hashes -r $requirements
}
$env:PATH = "$venv\Scripts;$nasmDir;$env:PATH"

# Sources: libass is extracted; the three dependencies become meson subprojects that meson extracts
# from packagecache after checking the same pinned hashes.
$tar = Join-Path $env:SystemRoot 'System32\tar.exe'
$libassTarball = Get-Verified $pin.sources.libass.url $pin.sources.libass.sha256
Invoke-Checked 'extract libass' { & $tar -xf $libassTarball -C $work }
$source = Join-Path $work "libass-$($pin.sources.libass.version)"
$packageCache = New-Item -ItemType Directory -Force (Join-Path $source 'subprojects\packagecache')
foreach ($name in 'freetype2', 'fribidi', 'harfbuzz') {
    $dep = $pin.sources.$name
    $tarball = Get-Verified $dep.url $dep.sha256
    Copy-Item $tarball $packageCache
    @(
        '[wrap-file]'
        "directory = $($dep.directory)"
        "source_url = $($dep.url)"
        "source_filename = $([IO.Path]::GetFileName($tarball))"
        "source_hash = $($dep.sha256)"
        ''
        '[provide]'
        "dependency_names = $name"
    ) | Set-Content (Join-Path $source "subprojects\$name.wrap")
}

# UWP app partition for all C/C++ code; meson adds /MT from b_vscrt.
$env:CFLAGS = '-DWINAPI_FAMILY=WINAPI_FAMILY_APP -D_WIN32_WINNT=0x0A00'
$env:CXXFLAGS = $env:CFLAGS
$build = Join-Path $work 'build'
$options = @(
    '--buildtype=release', '-Db_ndebug=true', '-Db_vscrt=mt', '-Ddefault_library=static',
    '--wrap-mode=nodownload', '--force-fallback-for=freetype2,fribidi,harfbuzz',
    '-Ddirectwrite=enabled', '-Dfontconfig=disabled', '-Dcoretext=disabled', '-Dasm=enabled',
    '-Dlibunibreak=disabled', '-Drequire-system-font-provider=true',
    '-Dtest=disabled', '-Dcompare=disabled', '-Dprofile=disabled', '-Dfuzz=disabled', '-Dcheckasm=disabled',
    # FreeType: stdio font streams (no Win32 mmap ftsystem.c), its own zlib for WOFF/gzip, nothing else.
    '-Dfreetype2:mmap=disabled', '-Dfreetype2:zlib=internal', '-Dfreetype2:bzip2=disabled',
    '-Dfreetype2:png=disabled', '-Dfreetype2:brotli=disabled', '-Dfreetype2:harfbuzz=disabled',
    '-Dfreetype2:tests=disabled',
    '-Dfribidi:docs=false', '-Dfribidi:bin=false', '-Dfribidi:tests=false',
    '-Dharfbuzz:glib=disabled', '-Dharfbuzz:gobject=disabled', '-Dharfbuzz:cairo=disabled',
    '-Dharfbuzz:chafa=disabled', '-Dharfbuzz:png=disabled', '-Dharfbuzz:zlib=disabled',
    '-Dharfbuzz:icu=disabled', '-Dharfbuzz:freetype=disabled', '-Dharfbuzz:raster=disabled',
    '-Dharfbuzz:vector=disabled', '-Dharfbuzz:gpu=disabled', '-Dharfbuzz:gpu_demo=disabled',
    '-Dharfbuzz:subset=disabled', '-Dharfbuzz:tests=disabled', '-Dharfbuzz:introspection=disabled',
    '-Dharfbuzz:docs=disabled', '-Dharfbuzz:utilities=disabled'
)
Invoke-Checked 'meson setup' { meson setup $build $source @options }
Invoke-Checked 'meson compile' { meson compile -C $build }

$lib = New-Item -ItemType Directory -Force (Join-Path $OutDir 'lib')
$licenses = New-Item -ItemType Directory -Force (Join-Path $OutDir 'licenses')
foreach ($name in 'ass', 'freetype', 'fribidi', 'harfbuzz') {
    # meson names static libraries lib<name>.a even with MSVC; rustc wants <name>.lib.
    $found = @(Get-ChildItem $build -Recurse -File | Where-Object { $_.Name -in "lib$name.a", "$name.lib" })
    if ($found.Count -ne 1) { throw "expected one static library for $name, found $($found.Count)" }
    Copy-Item $found[0].FullName (Join-Path $lib "$name.lib") -Force
}

# The static CRT must match bale_gpu.dll (+crt-static): no object may ask for the DLL CRT.
$directives = & dumpbin.exe /nologo /directives (Get-ChildItem $lib -Filter *.lib).FullName
if ($LASTEXITCODE -ne 0) { throw 'dumpbin /directives failed' }
if ($directives -match '(?i)/DEFAULTLIB:"?(msvcrt|msvcprt|vcruntime|ucrt)\.lib') {
    throw 'libass libraries must use the static CRT (/MT)'
}
if (-not ($directives -match '(?i)/DEFAULTLIB:"?libcmt')) { throw 'libass libraries do not reference LIBCMT' }

$subprojects = Join-Path $source 'subprojects'
Copy-Item (Join-Path $source $pin.sources.libass.license_file) (Join-Path $licenses 'libass.txt') -Force
foreach ($pair in @(('freetype2', 'freetype'), ('fribidi', 'fribidi'), ('harfbuzz', 'harfbuzz'))) {
    $dep = $pin.sources.($pair[0])
    Copy-Item (Join-Path $subprojects "$($dep.directory)\$($dep.license_file)") (Join-Path $licenses "$($pair[1]).txt") -Force
}

$sources = @('Bale libass build: static x64 libraries, UWP app partition (WINAPI_FAMILY_APP), static CRT (/MT).',
    "Pin revision $($pin.revision); built by scripts/build-libass-native.ps1 from scripts/libass-native.json.", '')
foreach ($name in 'libass', 'freetype2', 'fribidi', 'harfbuzz') {
    $dep = $pin.sources.$name
    $sources += "$name $($dep.version) ($($dep.license))"
    $sources += "  source: $($dep.url)"
    $sources += "  sha256: $($dep.sha256)"
}
$sources += ''
$sources += "Tools: nasm $($pin.tools.nasm.version), meson $($pin.tools.meson.version), ninja $($pin.tools.ninja.version), MSVC $env:VCToolsVersion"
$sources += ''
$sources += 'meson options:'
foreach ($option in $options) { $sources += "  $option" }
$sources += "  CFLAGS/CXXFLAGS: $env:CFLAGS"
$sources | Set-Content (Join-Path $OutDir 'SOURCES.txt')

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem $lib | ForEach-Object { Write-Output ("{0,-14} {1,10:N0} bytes" -f $_.Name, $_.Length) }
