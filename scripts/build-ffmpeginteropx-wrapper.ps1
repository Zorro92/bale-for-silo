param(
    [Parameter(Mandatory = $true)][string]$SourcePath,
    [Parameter(Mandatory = $true)][ValidatePattern('^2\.1\.0-bale\.(0|[1-9]\d*)$')][string]$Version
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/ffmpeginteropx-wrapper-common.ps1"
$pin = Get-WrapperPin
$nativePin = Get-Content (Join-Path $PSScriptRoot 'ffmpeg-native.json') -Raw | ConvertFrom-Json
$SourcePath = (Resolve-Path $SourcePath).Path
$commit = Invoke-WrapperGit $SourcePath @('rev-parse', 'HEAD')
if ($commit -ne $pin.upstream_commit) { throw 'Wrapper source does not match the pinned upstream commit' }
$patches = @(Add-WrapperPatches $SourcePath (Join-Path $PSScriptRoot 'ffmpeginteropx-patches'))
$output = Join-Path $SourcePath 'Output/NuGet'
New-Item -ItemType Directory -Force $output | Out-Null
$staging = Join-Path ([System.IO.Path]::GetTempPath()) "bale-wrapper-build-$([guid]::NewGuid())"
New-Item -ItemType Directory $staging | Out-Null
try {
    # Use the app's verified download path, never upstream's 8.1.2-pre01 packages.config entries.
    & "$PSScriptRoot/restore-ffmpeg-native.ps1"
    $nativePackage = Join-Path $PSScriptRoot "../.cache/ffmpeg-native/FFmpegInteropX.UWP.FFmpeg.$($nativePin.version).nupkg"
    if ((Get-FileHash $nativePackage -Algorithm SHA256).Hash.ToLowerInvariant() -ne $nativePin.sha256) {
        throw 'Verified native FFmpeg package changed before extraction'
    }
    $packages = Join-Path $SourcePath 'packages'
    $nativeDirectory = Join-Path $packages "FFmpegInteropX.UWP.FFmpeg.$($nativePin.version)"
    if (Test-Path $packages) { throw 'Wrapper build requires a fresh packages directory' }
    New-Item -ItemType Directory $nativeDirectory | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($nativePackage, $nativeDirectory)
    & "$PSScriptRoot/check-dav1d-version.ps1" `
        -Bytes ([System.IO.File]::ReadAllBytes((Join-Path $nativeDirectory 'runtimes/win-x64/native/avcodec-62.dll'))) `
        -ExpectedVersion $nativePin.dav1d_version

    # Restore only the three build-tool packages needed by the vcxproj's import guards. In
    # particular, neither Desktop.FFmpeg nor upstream UWP.FFmpeg may be downloaded or restored.
    [xml]$tools = Get-Content (Join-Path $SourcePath 'Source/packages.config') -Raw
    foreach ($item in @($tools.packages.package)) {
        if ($item.id -like 'FFmpegInteropX.*') { [void]$item.ParentNode.RemoveChild($item) }
    }
    $expectedTools = @('Microsoft.Windows.CppWinRT', 'Microsoft.Windows.SDK.BuildTools', 'Microsoft.WindowsAppSDK')
    if (@(Compare-Object $expectedTools @($tools.packages.package.id)).Count) { throw 'Unexpected wrapper build-tool dependencies' }
    $toolsConfig = Join-Path $staging 'packages.config'
    $tools.Save($toolsConfig)
    & nuget.exe restore $toolsConfig -PackagesDirectory $packages -Source https://api.nuget.org/v3/index.json `
        -NonInteractive -NoCache -DirectDownload
    if ($LASTEXITCODE -ne 0) { throw 'Wrapper build-tool NuGet restore failed' }

    # Build only the native WinRT component. Upstream's full entrypoint also rebuilds the
    # managed projection on x64; it must remain the original package's byte-identical payload.
    $project = Join-Path $SourcePath 'Source/FFmpegInteropX.vcxproj'
    $buildLog = Join-Path $output 'wrapper-build.log'
    $msbuildVersion = (& MSBuild.exe -version -nologo) -join ' '
    if ($LASTEXITCODE -ne 0) { throw 'Could not identify MSBuild' }
    & MSBuild.exe $project /m /v:minimal /p:Configuration=Release_UWP /p:Platform=x64 `
        "/p:WindowsTargetPlatformVersion=$($pin.windows_sdk)" "/p:WindowsTargetPlatformMinVersion=$($pin.windows_min_version)" `
        "/p:PlatformToolset=$($pin.platform_toolset)" "/p:LibraryVersionNumber=$($pin.library_version)" `
        "/p:FFmpegPackageVersion=$($nativePin.version)" /fl "/flp:logfile=$buildLog;verbosity=normal"
    if ($LASTEXITCODE -ne 0) { throw 'Upstream Release_UWP x64 FFmpegInteropX native build failed' }
    $nativeOutput = Join-Path $SourcePath 'Output/FFmpegInteropX/x64/Release_UWP'

    $originalName = "$($pin.original_package_id).$($pin.original_package_version).nupkg"
    $original = Join-Path $staging $originalName
    $originalUrl = "https://api.nuget.org/v3-flatcontainer/$($pin.original_package_id.ToLowerInvariant())/$($pin.original_package_version)/$($originalName.ToLowerInvariant())"
    Invoke-WebRequest -Uri $originalUrl -OutFile $original
    if ((Get-FileHash $original -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pin.original_package_sha256) {
        throw 'Downloaded original wrapper package does not match its SHA256 pin'
    }
    $tag = "wrapper-ffmpeginteropx-$Version"
    $sources = @(
        "Package: FFmpegInteropX.UWP.Lib $Version",
        "Release: $tag",
        "FFmpegInteropX repository: https://github.com/$($pin.upstream_repository)",
        "FFmpegInteropX commit: $commit",
        "Bale build scripts commit: $env:GITHUB_SHA",
        "Actions run: https://github.com/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID",
        "Original NuGet: $originalUrl",
        "Original NuGet SHA256: $($pin.original_package_sha256)",
        "Native FFmpeg: FFmpegInteropX.UWP.FFmpeg $($nativePin.version)",
        "Native release: $($nativePin.release_tag)",
        "Native NuGet SHA256: $($nativePin.sha256)",
        "Native NuGet content hash: $($nativePin.nuget_content_hash)",
        "Patch count: $($patches.Count)",
        'Patches in application order (filename SHA256):'
    )
    foreach ($patch in $patches) { $sources += "$($patch.Name) $($patch.Sha256)" }
    if (-not $patches.Count) { $sources += '(none)' }
    $sources += @('',
        "Build: Source/FFmpegInteropX.vcxproj; Release_UWP; x64; PlatformToolset=$($pin.platform_toolset); LibraryVersionNumber=$($pin.library_version)",
        "WindowsTargetPlatformVersion=$($pin.windows_sdk); WindowsTargetPlatformMinVersion=$($pin.windows_min_version)",
        "MSBuild: $msbuildVersion",
        "Runner image: $env:ImageOS $env:ImageVersion",
        'Build-tool packages (unchanged upstream versions):'
    )
    foreach ($item in $tools.packages.package) { $sources += "$($item.id) $($item.version)" }
    $sources += @('',
        'Only the native x64 wrapper is built; no FFmpeg or managed-projection rebuild.',
        'Original winmd, managed projection/XML, targets, docs and x86/ARM64 binaries are retained byte-for-byte.',
        'Repack changes: x64 DLL replaced; x64 PDB added (absent in the original); package version/core-properties updated;',
        'SOURCES.txt and content-type declarations added; invalidated NuGet repository signature removed.',
        'Built/original winmd public type-name sets must match; this is not a complete ABI/member comparison.',
        'Patches may only change Source/*.cpp and Source/*.h; IDL/project/dependency changes are forbidden.',
        'This is a pinned build recipe, not a claim of bit-identical output across runner/compiler revisions.'
    )
    $sourcesPath = Join-Path $output 'SOURCES.txt'
    $sources | Set-Content $sourcesPath -Encoding utf8
    $hash = New-WrapperPackage -OriginalPackagePath $original -OriginalSha256 $pin.original_package_sha256 `
        -NativeOutputPath $nativeOutput -Version $Version -SourcesPath $sourcesPath -OutputPath $output
    # Keep corresponding patched wrapper source, without packages, credentials or build outputs.
    $sourceArchive = Join-Path $output 'ffmpeginteropx-wrapper-sources.tar.gz'
    & tar.exe -czf $sourceArchive --exclude=.git --exclude=./packages --exclude=./Output --exclude=./Intermediate `
        --exclude=./Tools --exclude=./Libs -C $SourcePath .
    if ($LASTEXITCODE -ne 0) { throw 'Wrapper corresponding source archive failed' }
    Write-Output "Package SHA256: $hash"
    if ($env:GITHUB_OUTPUT) {
        "release_tag=$tag" >> $env:GITHUB_OUTPUT
        "package_sha256=$hash" >> $env:GITHUB_OUTPUT
    }
    if ($env:GITHUB_STEP_SUMMARY) {
        @("Package: FFmpegInteropX.UWP.Lib $Version", "Release: $tag", "SHA256: $hash", "Patches: $($patches.Count)",
            (Get-Content (Join-Path $output 'winmd-public-types.txt') -Raw)) -join "`n`n" >> $env:GITHUB_STEP_SUMMARY
    }
}
finally { Remove-Item $staging -Recurse -Force }
