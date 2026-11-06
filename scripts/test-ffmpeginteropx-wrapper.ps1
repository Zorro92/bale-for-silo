$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/ffmpeginteropx-wrapper-common.ps1"
$null = Get-WrapperPin
foreach ($file in (Get-ChildItem $PSScriptRoot -Filter '*.ps1')) {
    $tokens = $null
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors) { throw "PowerShell syntax error in $($file.Name): $parseErrors" }
}

function Assert-WrapperReject {
    param([scriptblock]$Action, [string]$Message)
    $rejected = $false
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notlike "$Message*") { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw "Expected wrapper rejection: $Message" }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$fixture = Join-Path ([System.IO.Path]::GetTempPath()) "bale-wrapper-check-$([guid]::NewGuid())"
New-Item -ItemType Directory $fixture | Out-Null
try {
    $repo = Join-Path $fixture 'repo'
    $patches = Join-Path $fixture 'patches'
    $native = Join-Path $fixture 'native'
    New-Item -ItemType Directory $repo, $patches, $native, (Join-Path $repo 'Source') | Out-Null
    Invoke-WrapperGit $repo @('init', '--quiet')
    "one`n" | Set-Content (Join-Path $repo 'Source/test.cpp') -NoNewline
    Invoke-WrapperGit $repo @('add', '.')
    Invoke-WrapperGit $repo @('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '--quiet', '-m', 'Fixture baseline')
    if (@(Add-WrapperPatches $repo $patches).Count) { throw 'Empty patch series did not work' }
    $patchTemplate = @'
diff --git a/Source/test.cpp b/Source/test.cpp
--- a/Source/test.cpp
+++ b/Source/test.cpp
@@ -1 +1 @@
-{0}
+{1}
'@
    $patchTemplate = $patchTemplate.Replace("`r`n", "`n") + "`n"
    ($patchTemplate -f 'two', 'three') | Set-Content (Join-Path $patches '0002-second.patch') -NoNewline
    ($patchTemplate -f 'one', 'two') | Set-Content (Join-Path $patches '0001-first.patch') -NoNewline
    $applied = @(Add-WrapperPatches $repo $patches)
    if (($applied.Name -join ',') -ne '0001-first.patch,0002-second.patch' -or
        (Get-Content (Join-Path $repo 'Source/test.cpp') -Raw).Trim() -ne 'three') {
        throw 'Patch series was not applied in ordinal order'
    }
    foreach ($record in $applied) {
        if ($record.Sha256 -ne (Get-FileHash (Join-Path $patches $record.Name) -Algorithm SHA256).Hash.ToLowerInvariant()) {
            throw 'Patch provenance hash differs from the applied patch'
        }
    }
    Invoke-WrapperGit $repo @('reset', '--hard', '--quiet', 'HEAD')
    Remove-Item (Join-Path $patches '*.patch')
    $badPatch = ($patchTemplate -f 'one', 'two').Replace('test.cpp', 'FFmpegInteropX.idl')
    $badPatch | Set-Content (Join-Path $patches '0001-idl.patch') -NoNewline
    Assert-WrapperReject { Add-WrapperPatches $repo $patches } 'Wrapper patches may only change Source/'
    Remove-Item (Join-Path $patches '*.patch')
    ($patchTemplate -f 'does-not-match', 'two') | Set-Content (Join-Path $patches '0001-rejected.patch') -NoNewline
    Assert-WrapperReject { Add-WrapperPatches $repo $patches } 'git failed: apply'
    # Confirm rejection left no changes, and clear the expected native failure exit code for CI.
    Invoke-WrapperGit $repo @('diff', '--exit-code', 'HEAD')

    # Metadata fixtures need no Windows SDK or managed-projection dependencies.
    $winmd = Join-Path $native 'FFmpegInteropX.winmd'
    $differentWinmd = Join-Path $fixture 'different.winmd'
    Add-Type -TypeDefinition 'namespace WrapperFixture { public class MediaSource {} internal class Hidden {} }' -OutputAssembly $winmd
    Add-Type -TypeDefinition 'namespace WrapperFixture { public class ChangedMediaSource {} }' -OutputAssembly $differentWinmd
    $null = Compare-WrapperWinmd $winmd $winmd
    Assert-WrapperReject { Compare-WrapperWinmd $winmd $differentWinmd } 'WinMD public type mismatch'
    'built x64 DLL' | Set-Content (Join-Path $native 'FFmpegInteropX.dll')
    'built x64 PDB' | Set-Content (Join-Path $native 'FFmpegInteropX.pdb')
    $sources = Join-Path $fixture 'SOURCES.txt'
    'fixture provenance' | Set-Content $sources
    $package = Join-Path $fixture 'original.nupkg'
    $files = @{
        'FFmpegInteropX.UWP.Lib.nuspec' = '<package><metadata><id>FFmpegInteropX.UWP.Lib</id><version>2.1.0</version></metadata></package>'
        '[Content_Types].xml' = '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="dll" ContentType="application/octet" /></Types>'
        'package/services/metadata/core-properties/fixture.psmdcp' = '<coreProperties><version>2.1.0</version></coreProperties>'
        '.signature.p7s' = 'invalidated fixture signature'
        'lib/net6.0-windows10.0.17763.0/FFmpegInteropX.DotNet.dll' = 'managed projection must not change'
        'lib/uap10/FFmpegInteropX.xml' = 'documentation must not change'
        'build/native/FFmpegInteropX.UWP.Lib.targets' = 'targets must not change'
        'runtimes/win-x86/native/FFmpegInteropX.dll' = 'x86 must not change'
        'runtimes/win-x64/native/FFmpegInteropX.dll' = 'original x64 DLL'
        'runtimes/win-arm64/native/FFmpegInteropX.dll' = 'ARM64 must not change'
    }
    $zip = [System.IO.Compression.ZipFile]::Open($package, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $files.Keys) {
            $stream = $zip.CreateEntry($name).Open()
            $bytes = [System.Text.Encoding]::UTF8.GetBytes($files[$name])
            try { $stream.Write($bytes, 0, $bytes.Length) }
            finally { $stream.Dispose() }
        }
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $winmd, 'lib/uap10/FFmpegInteropX.winmd')
    }
    finally { $zip.Dispose() }
    $arguments = @{
        OriginalPackagePath = $package
        OriginalSha256 = (Get-FileHash $package -Algorithm SHA256).Hash.ToLowerInvariant()
        NativeOutputPath = $native
        Version = '2.1.0-bale.0'
        SourcesPath = $sources
        OutputPath = Join-Path $fixture 'output'
    }
    $hash = New-WrapperPackage @arguments
    $resultPath = Join-Path $arguments.OutputPath 'FFmpegInteropX.UWP.Lib.2.1.0-bale.0.nupkg'
    if ($hash -ne (Get-FileHash $resultPath -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Incorrect package SHA256' }
    $result = [System.IO.Compression.ZipFile]::OpenRead($resultPath)
    try {
        if ($result.GetEntry('.signature.p7s')) { throw 'Repack retained the invalid NuGet signature' }
        foreach ($name in @('lib/uap10/FFmpegInteropX.winmd', 'runtimes/win-x64/native/FFmpegInteropX.dll',
            'runtimes/win-x64/native/FFmpegInteropX.pdb', 'SOURCES.txt')) {
            $extracted = Join-Path $fixture 'extracted'
            Export-WrapperZipEntry $result $name $extracted
            $expected = switch ($name) {
                'SOURCES.txt' { $sources }
                'lib/uap10/FFmpegInteropX.winmd' { $winmd }
                default { Join-Path $native ([System.IO.Path]::GetFileName($name)) }
            }
            if ((Get-FileHash $extracted).Hash -ne (Get-FileHash $expected).Hash) { throw "Repack payload differs: $name" }
        }
        foreach ($name in $files.Keys) {
            if ($name -eq '.signature.p7s' -or $name -eq '[Content_Types].xml' -or $name.EndsWith('.nuspec') -or
                $name.EndsWith('.psmdcp') -or $name -eq 'runtimes/win-x64/native/FFmpegInteropX.dll') { continue }
            $reader = [System.IO.StreamReader]::new($result.GetEntry($name).Open())
            try { if ($reader.ReadToEnd() -cne $files[$name]) { throw "Original payload changed: $name" } }
            finally { $reader.Dispose() }
        }
        $extracted = Join-Path $fixture 'spec.xml'
        Export-WrapperZipEntry $result 'FFmpegInteropX.UWP.Lib.nuspec' $extracted
        [xml]$spec = Get-Content $extracted -Raw
        if ($spec.package.metadata.id -ne 'FFmpegInteropX.UWP.Lib' -or $spec.package.metadata.version -ne '2.1.0-bale.0') {
            throw 'Incorrect repack identity'
        }
        Export-WrapperZipEntry $result '[Content_Types].xml' $extracted
        [xml]$content = Get-Content $extracted -Raw
        if ('pdb' -notin $content.Types.Default.Extension -or 'txt' -notin $content.Types.Default.Extension) {
            throw 'Repack content types omit new files'
        }
    }
    finally { $result.Dispose() }
    $arguments.OriginalSha256 = '0' * 64
    Assert-WrapperReject { New-WrapperPackage @arguments } 'Original wrapper SHA256 mismatch'
    $arguments.OriginalSha256 = (Get-FileHash $package -Algorithm SHA256).Hash.ToLowerInvariant()
    Copy-Item $differentWinmd $winmd -Force
    Assert-WrapperReject { New-WrapperPackage @arguments } 'WinMD public type mismatch'
}
finally { Remove-Item $fixture -Recurse -Force }
Write-Output 'Wrapper pins, empty/ordered/rejected patches, IDL guard, WinMD, hash and repack fixture checks: PASS'
